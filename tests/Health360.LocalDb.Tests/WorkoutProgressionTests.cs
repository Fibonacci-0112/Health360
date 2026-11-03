using Health360.Contracts.Training;
using Health360.Domain.Training;
using Health360.Domain.Units;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>
/// Progression suggestions planned by the logger from the workout history, and editing and deleting templates, in a
/// real PowerSync database.
/// </summary>
public class WorkoutProgressionTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Squat = "60000000-0000-7000-8000-000000000001";
    private const string Bench = "60000000-0000-7000-8000-000000000002";
    private const string Plank = "60000000-0000-7000-8000-000000000003";

    private static readonly ProgressionRule DoubleKg = new("double", 2.5, "kg");
    private static readonly ProgressionRule LinearKg = new("linear", 2.5, "kg");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.Db.WriteTransaction(async tx =>
        {
            foreach (var (id, name, tracking) in new[] { (Squat, "Barbell Squat", "weight_reps"), (Bench, "Bench Press", "weight_reps"), (Plank, "Plank", "reps") })
            {
                await tx.Execute(
                    "INSERT INTO exercises (id, name, tracking_type, equipment, primary_muscles, secondary_muscles) VALUES (?, ?, ?, '[]', '[]', '[]')",
                    [id, name, tracking]);
            }

            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    /// <summary>Starts the template, logs the first exercise's planned sets with these reps (and loads, if given), and finishes.</summary>
    private static async Task<IReadOnlyList<LoggedSet>> LogAsync(
        TestDatabase t, SteppingClock clock, string template, int[] reps, (double Weight, string Unit)[]? loads = null)
    {
        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "kg", clock);
        var planned = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0].Sets;
        var work = planned.Where(s => s.SetType == "working").ToList();
        for (var i = 0; i < reps.Length; i++)
        {
            var set = work[i];
            var (weight, unit) = loads?[i] ?? (set.Weight!.Value, set.WeightUnit!);
            await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(reps[i], weight, unit), clock);
            await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, clock);
        }

        await WorkoutLog.FinishAsync(t.Db, Profile, session, clock);
        clock.Advance(TimeSpan.FromDays(2));
        return planned;
    }

    private static async Task<(LoggedExercise Exercise, ExerciseSuggestion? Suggestion)> StartAsync(TestDatabase t, SteppingClock clock, string template)
    {
        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "kg", clock);
        var exercise = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];
        var suggestions = await WorkoutProgression.ForSessionAsync(t.Db, Profile, session);
        return (exercise, suggestions.GetValueOrDefault(exercise.Id));
    }

    [Fact]
    public async Task Double_progression_starts_at_the_templates_load_and_adds_the_increment_once_the_range_is_topped()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 8, 12, 60, "kg"), 120, DoubleKg)], clock);

        var first = await LogAsync(t, clock, template, [10, 9, 8]);
        Assert.All(first, s => Assert.Equal((8, 60.0, "kg"), (s.Reps!.Value, s.Weight!.Value, s.WeightUnit!)));

        // Within the range: same load, and last time's reps to beat.
        var second = await LogAsync(t, clock, template, [12, 12, 12]);
        Assert.Equal([10, 9, 8], second.Select(s => s.Reps!.Value));
        Assert.All(second, s => Assert.Equal(60.0, s.Weight));

        var (third, suggestion) = await StartAsync(t, clock, template);
        Assert.All(third.Sets, s => Assert.Equal((8, 62.5, "kg", false), (s.Reps!.Value, s.Weight!.Value, s.WeightUnit!, s.Completed)));
        Assert.Equal(3, third.Sets.Count);
        Assert.Equal((62.5, ProgressionAction.Increase, 0), (suggestion!.Load, suggestion.Action, suggestion.ConsecutiveFailures));
    }

    [Fact]
    public async Task Three_misses_in_a_row_bring_a_deload()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5, 100, "kg"), null, LinearKg)], clock);

        await LogAsync(t, clock, template, [5, 5, 4]);
        await LogAsync(t, clock, template, [5, 4, 4]);
        var (_, streak) = await StartAsync(t, clock, template);
        Assert.Equal((100.0, ProgressionAction.Repeat, 2), (streak!.Load, streak.Action, streak.ConsecutiveFailures));
        var open = await WorkoutLog.ActiveSessionAsync(t.Db, Profile);
        await WorkoutLog.DiscardAsync(t.Db, Profile, open!.Id, clock);

        await LogAsync(t, clock, template, [4, 4, 4]);
        var (deloaded, suggestion) = await StartAsync(t, clock, template);

        // calculations.md: third miss at 100 with increment 2.5 → 90.
        Assert.All(deloaded.Sets, s => Assert.Equal((5, 90.0), (s.Reps!.Value, s.Weight!.Value)));
        Assert.Equal((ProgressionAction.Deload, 0), (suggestion!.Action, suggestion.ConsecutiveFailures));
    }

    [Fact]
    public async Task A_set_logged_in_another_unit_counts_at_its_converted_load()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(2, 5, 5, 100, "kg"), null, LinearKg)], clock);
        var lb = UnitCodes.Convert(100, "kg", "lb")!.Value;

        // The same 100 kg, one set entered in lb: not lighter than prescribed, so it isn't a miss.
        await LogAsync(t, clock, template, [5, 5], [(100, "kg"), (lb, "lb")]);

        var (exercise, suggestion) = await StartAsync(t, clock, template);
        Assert.Equal(ProgressionAction.Increase, suggestion!.Action);
        Assert.All(exercise.Sets, s => Assert.Equal((102.5, "kg"), (s.Weight!.Value, s.WeightUnit!)));
    }

    [Fact]
    public async Task Only_finished_sessions_of_the_same_template_and_their_completed_work_sets_count()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(1, 5, 5, 100, "kg"), null, LinearKg)], clock);

        // A heavy squat in a workout without the template doesn't count.
        var other = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        var otherSquat = await WorkoutLog.AddExerciseAsync(t.Db, Profile, other, Squat, "kg", clock);
        var heavy = (await WorkoutLog.ExercisesAsync(t.Db, Profile, other))[0].Sets.Single(s => s.SetType == "working").Id;
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, heavy, new SetEntry(1, 200, "kg"), clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, heavy, clock);
        Assert.NotNull(otherSquat);
        await WorkoutLog.FinishAsync(t.Db, Profile, other, clock);
        clock.Advance(TimeSpan.FromDays(1));

        // A warm-up and an unticked set alongside the one work set.
        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "kg", clock);
        var exercise = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];
        var work = exercise.Sets[0].Id;
        var warmup = await WorkoutLog.AddSetAsync(t.Db, Profile, exercise.Id, "kg", clock);
        var unticked = await WorkoutLog.AddSetAsync(t.Db, Profile, exercise.Id, "kg", clock);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, work, new SetEntry(5, 100, "kg"), clock);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, warmup, new SetEntry(5, 20, "kg", SetType: "warmup"), clock);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, unticked, new SetEntry(1, 50, "kg"), clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, work, clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, warmup, clock);
        await WorkoutLog.FinishAsync(t.Db, Profile, session, clock);
        clock.Advance(TimeSpan.FromDays(1));

        var (next, suggestion) = await StartAsync(t, clock, template);
        Assert.Equal((102.5, ProgressionAction.Increase), (suggestion!.Load, suggestion.Action));
        Assert.Equal([5], suggestion.LastReps);
        Assert.Equal(["warmup", "working"], next.Sets.Select(s => s.SetType));
        Assert.Equal((20.0, 102.5), (next.Sets[0].Weight!.Value, next.Sets[1].Weight!.Value));
    }

    [Fact]
    public async Task Without_a_rule_or_a_start_load_the_logger_plans_as_before()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Mixed",
            [new TemplateExerciseInput(Squat, new SetScheme(2, 5, 5), null, LinearKg), new TemplateExerciseInput(Bench, new SetScheme(2, 8, 12, 50, "kg")),
             new TemplateExerciseInput(Plank, new SetScheme(2, 30, 60), null, DoubleKg)], clock);

        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "kg", clock);

        Assert.Empty(await WorkoutProgression.ForSessionAsync(t.Db, Profile, session));
        var exercises = await WorkoutLog.ExercisesAsync(t.Db, Profile, session);
        Assert.All(exercises[0].Sets, s => Assert.Equal((5, null), (s.Reps!.Value, s.Weight)));
        Assert.All(exercises[1].Sets, s => Assert.Equal((8, 50.0), (s.Reps!.Value, s.Weight!.Value)));
    }

    [Fact]
    public async Task Editing_a_template_keeps_unchanged_rows_and_writes_only_what_changed()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5, 100, "kg"), 180, LinearKg), new TemplateExerciseInput(Bench, new SetScheme(3, 8, 12)),
             new TemplateExerciseInput(Plank, new SetScheme(2, 30, 60))], clock);
        var rows = await WorkoutPlans.TemplateExercisesAsync(t.Db, Profile, template);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), clock, cancellationToken: Ct);

        // Bench moves first and gains a rule, squat moves second unchanged otherwise, plank goes and a new squat row is added.
        await WorkoutPlans.UpdateTemplateAsync(t.Db, Profile, template, "Lower",
            [new TemplateExerciseInput(Bench, new SetScheme(3, 8, 12), 90, DoubleKg, rows[1].Id),
             new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5, 100, "kg"), 180, LinearKg, rows[0].Id),
             new TemplateExerciseInput(Squat, new SetScheme(1, 3, 3))], clock);

        Assert.Equal("Lower", (await WorkoutPlans.TemplateAsync(t.Db, Profile, template))!.Name);
        var edited = await WorkoutPlans.TemplateExercisesAsync(t.Db, Profile, template);
        Assert.Equal([rows[1].Id, rows[0].Id], edited.Take(2).Select(e => e.Id));
        Assert.Equal(DoubleKg, edited[0].Rule);
        Assert.Equal(90L, edited[0].RestSeconds);
        Assert.Equal(new SetScheme(1, 3, 3), edited[2].Scheme);
        Assert.Null(edited[2].Rule);

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        Assert.Equal(
            [("PATCH", "workout_templates"), ("DELETE", "template_exercises"), ("PATCH", "template_exercises"), ("PATCH", "template_exercises"),
             ("PUT", "template_exercises")],
            writer.Writes.Select(w => (w.Op, w.Table)));
        Assert.Equal("double", writer.Writes[2].Data!["progression"]!["kind"]!.GetValue<string>());

        // Writing the same plan again changes nothing.
        await WorkoutPlans.UpdateTemplateAsync(t.Db, Profile, template, "Lower",
            [.. (await WorkoutPlans.TemplateExercisesAsync(t.Db, Profile, template))
                .Select(e => new TemplateExerciseInput(e.ExerciseId, e.Scheme!, (int?)e.RestSeconds, e.Rule, e.Id))], clock);
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task An_edit_naming_a_row_that_is_gone_writes_nothing()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5))], clock);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), clock, cancellationToken: Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutPlans.UpdateTemplateAsync(t.Db, Profile, template, "Renamed",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5), null, null, "70000000-0000-7000-8000-000000000009")], clock));
        await Assert.ThrowsAsync<FluentValidation.ValidationException>(() => WorkoutPlans.UpdateTemplateAsync(t.Db, Profile, template, "Renamed",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5), null, new ProgressionRule("double", 0, "kg"))], clock));

        Assert.Equal("Legs", (await WorkoutPlans.TemplateAsync(t.Db, Profile, template))!.Name);
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task Deleting_a_template_removes_its_plan_and_unlinks_its_sessions()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(1, 5, 5, 100, "kg"), null, LinearKg)], clock);
        await WorkoutPlans.ScheduleOnAsync(t.Db, Profile, template, new DateOnly(2026, 11, 10), clock);
        await LogAsync(t, clock, template, [5]);

        await WorkoutPlans.DeleteTemplateAsync(t.Db, Profile, template, clock);

        Assert.Empty(await WorkoutPlans.TemplatesAsync(t.Db, Profile));
        Assert.Empty(await WorkoutPlans.TemplateExercisesAsync(t.Db, Profile, template));
        Assert.Empty(await WorkoutPlans.PlannedAsync(t.Db, Profile, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
        var session = Assert.Single(await WorkoutLog.RecentSessionsAsync(t.Db, Profile));
        Assert.Equal((null, 1L), (session.TemplateId, session.CompletedSets));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutPlans.DeleteTemplateAsync(t.Db, Profile, template, clock));
    }

    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
