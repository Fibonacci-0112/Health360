using FluentValidation;
using Health360.Contracts.Training;
using Health360.Domain.Training;
using Health360.Domain.Units;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>The workout logger's writes in a real PowerSync database: sessions, sets, e1RM, PRs, and what they upload.</summary>
public class WorkoutLogTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Squat = "60000000-0000-7000-8000-000000000001";
    private const string Plank = "60000000-0000-7000-8000-000000000002";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        // System exercises as the reference stream brings them down: synced, not queued.
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO exercises (id, external_id, name, tracking_type, equipment, primary_muscles, secondary_muscles) " +
                "VALUES (?, 'Barbell_Squat', 'Barbell Squat', 'weight_reps', '[\"barbell\"]', '[\"quadriceps\"]', '[]')", [Squat]);
            await tx.Execute(
                "INSERT INTO exercises (id, external_id, name, tracking_type, equipment, primary_muscles, secondary_muscles) " +
                "VALUES (?, 'Plank', 'Plank', 'reps', '[]', '[\"abdominals\"]', '[]')", [Plank]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    /// <summary>Logs a finished session of one exercise with the given sets, each completed.</summary>
    private static async Task<IReadOnlyList<IReadOnlyList<NewRecord>>> LogSessionAsync(
        TestDatabase t, SteppingClock clock, params (double Weight, string Unit, int Reps, string Type)[] sets)
    {
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        var exercise = await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var planned = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Select(s => s.Id).ToList();
        var records = new List<IReadOnlyList<NewRecord>>();
        for (var i = 0; i < sets.Length; i++)
        {
            var id = i < planned.Count ? planned[i] : await WorkoutLog.AddSetAsync(t.Db, Profile, exercise, "kg", clock);
            var (weight, unit, reps, type) = sets[i];
            await WorkoutLog.UpdateSetAsync(t.Db, Profile, id, new SetEntry(reps, weight, unit, SetType: type), clock);
            records.Add(await WorkoutLog.CompleteSetAsync(t.Db, Profile, id, clock));
            clock.Advance(TimeSpan.FromMinutes(3));
        }

        await WorkoutLog.FinishAsync(t.Db, Profile, session, clock);
        clock.Advance(TimeSpan.FromDays(2));
        return records;
    }

    [Fact]
    public async Task A_session_from_a_template_plans_the_schemes_sets()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs",
            [new TemplateExerciseInput(Squat, new SetScheme(3, 8, 12, 60, "kg"), 120), new TemplateExerciseInput(Plank, new SetScheme(2, 30, 60))], clock);

        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "lb", clock);

        var exercises = await WorkoutLog.ExercisesAsync(t.Db, Profile, session);
        Assert.Equal(["Barbell Squat", "Plank"], exercises.Select(e => e.Name));
        Assert.All(exercises[0].Sets, s => Assert.Equal((8, 60.0, "kg", 60.0, false), (s.Reps!.Value, s.Weight!.Value, s.WeightUnit!, s.WeightKg!.Value, s.Completed)));
        Assert.Equal(3, exercises[0].Sets.Count);
        Assert.All(exercises[1].Sets, s => Assert.Equal((30, null, null), (s.Reps!.Value, s.Weight, s.WeightUnit)));
        var active = await WorkoutLog.ActiveSessionAsync(t.Db, Profile);
        Assert.Equal((session, "Legs", "2026-11-03"), (active!.Id, active.TemplateName, active.LocalDate));
    }

    [Fact]
    public async Task Last_sessions_sets_are_prefilled_as_entered()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (225, "lb", 5, "working"), (225, "lb", 4, "working"));

        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);

        var sets = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets;
        Assert.Equal([(5, 225.0, "lb"), (4, 225.0, "lb")], sets.Select(s => (s.Reps!.Value, s.Weight!.Value, s.WeightUnit!)));
        Assert.All(sets, s => Assert.False(s.Completed));
    }

    [Fact]
    public async Task Completing_a_set_estimates_its_e1rm_from_canonical_kg()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();

        await LogSessionAsync(t, clock, (225, "lb", 8, "working"));

        var set = await t.Db.Get<SetRow>("SELECT weight_kg AS WeightKg, e1rm_kg AS E1rmKg, e1rm_formula AS E1rmFormula, is_pr AS IsPr FROM set_logs", []);
        var kg = 225 * 0.45359237;
        Assert.Equal(kg, set.WeightKg);
        Assert.Equal(OneRepMax.Estimate(kg, 8, OneRepMaxFormula.Epley), set.E1rmKg);
        Assert.Equal(("epley", 0L), (set.E1rmFormula, set.IsPr));
    }

    [Fact]
    public async Task The_first_session_is_a_baseline_and_a_heavier_set_later_is_a_record()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var first = await LogSessionAsync(t, clock, (100, "kg", 5, "working"));
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), clock, cancellationToken: Ct);

        var second = await LogSessionAsync(t, clock, (102.5, "kg", 5, "working"), (100, "kg", 6, "working"));

        Assert.Empty(first.Single());
        Assert.Equal([RecordKind.E1rm, RecordKind.Weight, RecordKind.Volume], second[0].Select(r => r.Kind));
        // 100 × 6 beats the reps done at 100 kg, and its e1RM (120) and volume (600) beat 102.5 × 5's.
        Assert.Equal([RecordKind.E1rm, RecordKind.Reps, RecordKind.Volume], second[1].Select(r => r.Kind));
        var records = await WorkoutLog.RecordsAsync(t.Db, Profile, Squat);
        Assert.Equal(6, records.Length);
        var e1rm = records.Last(r => r.Kind == "e1rm");
        Assert.Equal((102.5 * (1 + (5 / 30.0)), "epley", 102.5, 5L, "Barbell Squat"), (e1rm.Value, e1rm.E1rmFormula, e1rm.WeightKg, e1rm.Reps, e1rm.ExerciseName));
        Assert.Equal((6.0, 100.0), (records.Single(r => r.Kind == "reps").Value, records.Single(r => r.Kind == "reps").WeightKg));
        Assert.Equal(2, await t.Db.Get<long>("SELECT count(*) FROM set_logs WHERE is_pr = 1", []));

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        var record = writer.Writes.First(w => w.Table == "personal_records");
        Assert.Equal("PUT", record.Op);
        var setWrites = writer.Writes.Where(w => w.Table == "set_logs").Select(w => w.Id).ToHashSet();
        Assert.Contains(record.Data!["set_log_id"]!.GetValue<string>(), setWrites);
        Assert.True(writer.Writes.FindIndex(w => w.Table == "set_logs") < writer.Writes.FindIndex(w => w.Table == "personal_records"));
        Assert.Contains(writer.Writes, w => w.Table == "set_logs" && w.Data?["is_pr"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task Repeating_a_lift_in_the_other_unit_is_never_a_record()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (100, "kg", 5, "working"));

        var again = await LogSessionAsync(t, clock, (Conversion.Convert(100, Unit.Kilogram, Unit.Pound), "lb", 5, "working"));

        Assert.Empty(again.Single());
        Assert.Empty(await WorkoutLog.RecordsAsync(t.Db, Profile));
    }

    [Fact]
    public async Task A_warmup_gets_no_estimate_and_sets_no_record()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (60, "kg", 5, "working"));

        var records = await LogSessionAsync(t, clock, (80, "kg", 5, "warmup"));

        Assert.Empty(records.Single());
        Assert.Equal(1, await t.Db.Get<long>("SELECT count(*) FROM set_logs WHERE set_type = 'warmup' AND e1rm_kg IS NULL AND e1rm_formula IS NULL", []));
    }

    [Fact]
    public async Task Unticking_a_set_removes_its_estimate_and_records()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (100, "kg", 5, "working"));
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var set = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(5, 110, "kg"), clock);
        Assert.NotEmpty(await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, clock));

        await WorkoutLog.UncompleteSetAsync(t.Db, Profile, set.Id, clock);

        var after = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        Assert.Equal((false, null, false), (after.Completed, after.E1rmKg, after.IsPr));
        Assert.Empty(await WorkoutLog.RecordsAsync(t.Db, Profile));
    }

    [Fact]
    public async Task Editing_a_completed_set_evaluates_it_again()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (100, "kg", 5, "working"));
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var set = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, clock);

        var records = await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(3, 100, "kg"), clock);

        Assert.Empty(records);
        Assert.Empty(await WorkoutLog.RecordsAsync(t.Db, Profile));
        var after = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        Assert.Equal((true, 3, OneRepMax.Estimate(100, 3, OneRepMaxFormula.Epley)), (after.Completed, after.Reps!.Value, after.E1rmKg));
    }

    [Fact]
    public async Task An_invalid_entry_writes_nothing()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var set = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();

        await Assert.ThrowsAsync<ValidationException>(() => WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(5, -1, "kg"), clock));
        await Assert.ThrowsAsync<ValidationException>(() => WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(5, 20, "st"), clock));

        Assert.Null((await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single().Weight);
    }

    [Fact]
    public async Task Changing_one_field_keeps_the_others_as_they_are_now()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var set = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();

        // Two quick edits from the same rendered row: the second must not write back the weight the row had before.
        await WorkoutLog.ChangeSetAsync(t.Db, Profile, set.Id, e => e with { Weight = 140, WeightUnit = "lb" }, clock);
        await WorkoutLog.ChangeSetAsync(t.Db, Profile, set.Id, e => e with { Reps = 8 }, clock);

        var after = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        Assert.Equal((140.0, "lb", 8, 140 * 0.45359237), (after.Weight!.Value, after.WeightUnit!, after.Reps!.Value, after.WeightKg!.Value));
        await Assert.ThrowsAsync<ValidationException>(() => WorkoutLog.ChangeSetAsync(t.Db, Profile, set.Id, e => e with { Rpe = 11 }, clock));
    }

    [Fact]
    public async Task Only_one_session_is_in_progress()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);

        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock));
    }

    [Fact]
    public async Task Finishing_drops_sets_never_done_and_exercises_left_empty()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        var squat = await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Plank, "kg", clock);
        var done = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0].Sets.Single().Id;
        await WorkoutLog.AddSetAsync(t.Db, Profile, squat, "kg", clock);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, done, new SetEntry(5, 60, "kg"), clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, done, clock);
        clock.Advance(TimeSpan.FromMinutes(40));

        await WorkoutLog.FinishAsync(t.Db, Profile, session, clock);

        var exercises = await WorkoutLog.ExercisesAsync(t.Db, Profile, session);
        Assert.Equal(done, exercises.Single().Sets.Single().Id);
        Assert.Null(await WorkoutLog.ActiveSessionAsync(t.Db, Profile));
        var recent = Assert.Single(await WorkoutLog.RecentSessionsAsync(t.Db, Profile));
        Assert.Equal(("2026-11-03T18:40:00.000Z", 1L), (recent.EndedAt, recent.CompletedSets));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Plank, "kg", clock));
    }

    [Fact]
    public async Task A_session_in_progress_is_still_there_after_a_restart()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);

        var reopened = await t.ReopenAsync();

        Assert.Equal(session, (await WorkoutLog.ActiveSessionAsync(reopened, Profile))!.Id);
        Assert.Single(await WorkoutLog.ExercisesAsync(reopened, Profile, session));
    }

    [Fact]
    public async Task Discarding_deletes_everything_logged_and_queues_the_deletes()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (100, "kg", 5, "working"));
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", clock);
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Squat, "kg", clock);
        var set = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Single().Sets.Single();
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new SetEntry(5, 120, "kg"), clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, clock);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), clock, cancellationToken: Ct);

        await WorkoutLog.DiscardAsync(t.Db, Profile, session, clock);

        Assert.Null(await WorkoutLog.ActiveSessionAsync(t.Db, Profile));
        Assert.Empty(await WorkoutLog.RecordsAsync(t.Db, Profile));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        Assert.Equal(
            ["personal_records", "set_logs", "session_exercises", "workout_sessions"],
            writer.Writes.Where(w => w.Op == "DELETE").Select(w => w.Table).Distinct());
    }

    [Fact]
    public async Task E1rm_history_is_the_best_estimate_per_day()
    {
        await using var t = await OpenAsync();
        var clock = new SteppingClock();
        await LogSessionAsync(t, clock, (100, "kg", 5, "working"), (100, "kg", 3, "working"));
        await LogSessionAsync(t, clock, (105, "kg", 5, "working"));

        var history = await WorkoutLog.E1rmHistoryAsync(t.Db, Profile, Squat);

        Assert.Equal(
            [("2026-11-03", OneRepMax.Estimate(100, 5, OneRepMaxFormula.Epley)!.Value), ("2026-11-05", OneRepMax.Estimate(105, 5, OneRepMaxFormula.Epley)!.Value)],
            history.Select(h => (h.LocalDate, h.E1rmKg)));
    }

    /// <summary>Starts at 18:00 UTC on 3 November 2026 and moves only when told.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 3, 18, 0, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }

    private sealed class SetRow
    {
        public double? WeightKg { get; set; }

        public double? E1rmKg { get; set; }

        public string? E1rmFormula { get; set; }

        public long IsPr { get; set; }
    }
}
