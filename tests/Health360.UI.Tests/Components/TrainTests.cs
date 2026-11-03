using Bunit;
using Health360.Contracts.Training;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>The Train tab and the live logger: starting, logging sets with one tap, PRs, finishing and discarding.</summary>
public sealed class TrainTests : AppTestContext
{
    private const string Squat = "60000000-0000-7000-8000-000000000001";
    private const string Kg = "{\"load\":\"kg\"}";

    private async Task<IRenderedComponent<Routes>> TrainAsync(string unitPrefs = Kg, Func<Task>? seed = null)
    {
        await SignedInAndOnboardedAsync(unitPrefs);
        // A system exercise as the reference stream brings it down.
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO exercises (id, external_id, name, tracking_type, equipment, primary_muscles, secondary_muscles) " +
                "VALUES (?, 'Barbell_Squat', 'Barbell Squat', 'weight_reps', '[\"barbell\"]', '[\"quadriceps\"]', '[]')", [Squat]);
            await tx.Execute(
                "INSERT INTO exercises (id, name, tracking_type, equipment, primary_muscles, secondary_muscles) " +
                "VALUES ('60000000-0000-7000-8000-000000000005', 'Rowing machine', 'distance_duration', '[]', '[]', '[]')");
            await tx.Execute("DELETE FROM ps_crud");
        });
        if (seed is not null)
        {
            await seed();
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("#start-empty"), Wait);
        return cut;
    }

    /// <summary>A finished session of squats, logged as if on an earlier day.</summary>
    private async Task LogEarlierSessionAsync(double weight, string unit, int reps)
    {
        var db = App.Database.Db;
        var session = await WorkoutLog.StartAsync(db, ProfileId, null, unit, App.Clock);
        await WorkoutLog.AddExerciseAsync(db, ProfileId, session, Squat, unit, App.Clock);
        var set = (await WorkoutLog.ExercisesAsync(db, ProfileId, session)).Single().Sets.Single();
        await WorkoutLog.UpdateSetAsync(db, ProfileId, set.Id, new SetEntry(reps, weight, unit), App.Clock);
        await WorkoutLog.CompleteSetAsync(db, ProfileId, set.Id, App.Clock);
        await WorkoutLog.FinishAsync(db, ProfileId, session, App.Clock);
        App.Clock.Now = App.Clock.Now.AddDays(2);
    }

    private async Task AddSquatAsync(IRenderedComponent<Routes> cut)
    {
        cut.WaitForAssertion(() => cut.Find("#exercise-search"), Wait);
        await TypeAsync(cut, "#exercise-search", "squat");
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Add Barbell Squat']"), Wait);
        await ClickAsync(cut, "button[aria-label='Add Barbell Squat']");
        cut.WaitForAssertion(() => cut.Find("h2[id^=ex-]"), Wait);
    }

    [Fact]
    public async Task An_empty_tab_offers_an_empty_workout_and_explains_records()
    {
        var cut = await TrainAsync();

        Assert.Equal("Train", cut.Find("h1").TextContent);
        Assert.Contains("Nothing planned for the next 7 days.", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("None yet.", cut.Find("[aria-labelledby=records-title]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_set_is_logged_as_entered_and_ticking_it_off_saves_it_locally()
    {
        var cut = await TrainAsync("{\"load\":\"lb\"}");
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);

        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in lb']", "225");
        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 reps']", "8");
        await ClickAsync(cut, "button[aria-label='Barbell Squat set 1 done']");

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Barbell Squat set 1 done']").GetAttribute("aria-pressed")), Wait);
        var session = (await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId))!;
        var set = (await WorkoutLog.ExercisesAsync(App.Database.Db, ProfileId, session.Id)).Single().Sets.Single();
        Assert.Equal((225.0, "lb", 8, true), (set.Weight!.Value, set.WeightUnit!, set.Reps!.Value, set.Completed));
        Assert.Equal(225 * 0.45359237, set.WeightKg);
        Assert.NotNull(set.E1rmKg);
    }

    [Fact]
    public async Task Last_times_sets_are_prefilled_so_one_tap_logs_a_record()
    {
        var cut = await TrainAsync(seed: async () =>
        {
            await LogEarlierSessionAsync(100, "kg", 5);
            await LogEarlierSessionAsync(100, "kg", 5);
        });
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);
        cut.WaitForAssertion(() => Assert.Equal("100", cut.Find("input[aria-label='Barbell Squat set 1 weight in kg']").GetAttribute("value")), Wait);
        Assert.Equal("5", cut.Find("input[aria-label='Barbell Squat set 1 reps']").GetAttribute("value"));

        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 reps']", "6");
        await ClickAsync(cut, "button[aria-label='Barbell Squat set 1 done']");

        cut.WaitForAssertion(() => Assert.Contains("New PR on Barbell Squat", cut.Find(".pr-status").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("estimated 1RM 120.0 kg", cut.Find(".pr-status").TextContent, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".badge-pr")), Wait);
    }

    [Fact]
    public async Task A_timed_exercise_logs_distance_and_time_not_reps()
    {
        var cut = await TrainAsync("{\"load\":\"kg\",\"distance\":\"mi\"}");
        await ClickAsync(cut, "#start-empty");
        cut.WaitForAssertion(() => cut.Find("#exercise-search"), Wait);
        await TypeAsync(cut, "#exercise-search", "rowing");
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Add Rowing machine']"), Wait);
        await ClickAsync(cut, "button[aria-label='Add Rowing machine']");
        cut.WaitForAssertion(() => cut.Find("input[aria-label='Rowing machine set 1 time']"), Wait);
        Assert.Empty(cut.FindAll("input[aria-label='Rowing machine set 1 reps']"));

        await ClickAsync(cut, "button[aria-label='Rowing machine set 1 done']");
        cut.WaitForAssertion(() => Assert.Equal("Enter the distance or the time first.", cut.Find("[role=alert]").TextContent), Wait);
        await ChangeAsync(cut, "input[aria-label='Rowing machine set 1 distance in mi']", "1.5");
        await ChangeAsync(cut, "input[aria-label='Rowing machine set 1 time']", "12:30");
        await ClickAsync(cut, "button[aria-label='Rowing machine set 1 done']");

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Rowing machine set 1 done']").GetAttribute("aria-pressed")), Wait);
        Assert.Equal("12:30", cut.Find("input[aria-label='Rowing machine set 1 time']").GetAttribute("value"));
        var session = (await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId))!;
        var set = (await WorkoutLog.ExercisesAsync(App.Database.Db, ProfileId, session.Id)).Single().Sets.Single();
        Assert.Equal((1.5, "mi", 750.0), (set.Distance!.Value, set.DistanceUnit!, set.DurationSeconds!.Value));
    }

    [Fact]
    public async Task A_schedule_that_arrives_after_the_tab_opened_shows_up()
    {
        var cut = await TrainAsync();
        Assert.Contains("Nothing planned for the next 7 days.", cut.Markup, StringComparison.Ordinal);

        var legs = await WorkoutPlans.CreateTemplateAsync(App.Database.Db, ProfileId, "Legs", [], App.Clock);
        await WorkoutPlans.ScheduleOnAsync(App.Database.Db, ProfileId, legs, DateOnly.FromDateTime(App.Clock.Now.UtcDateTime), App.Clock);

        cut.WaitForAssertion(() => Assert.Contains("Legs", cut.Find("[aria-labelledby=planned-title]").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task Typing_saves_without_leaving_the_field_so_a_restart_keeps_it()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);
        var session = (await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId))!;

        // Typed, focus never leaves: each complete value is saved as it is typed, and the text stays as typed.
        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 reps']", "7");
        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in kg']", "102");
        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in kg']", "102.");

        await WaitForSetAsync(session.Id, s => s.Reps == 7 && s.Weight == 102);
        Assert.Equal("102.", cut.Find("input[aria-label='Barbell Squat set 1 weight in kg']").GetAttribute("value"));
        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in kg']", "102.5");
        await WaitForSetAsync(session.Id, s => s.Weight == 102.5);

        // As after a restart: the logger is built again and reads the workout from the database.
        Services.GetRequiredService<NavigationManager>().NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("#resume"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"train/workout/{session.Id}");
        var reopened = cut;
        reopened.WaitForAssertion(() => Assert.Equal("102.5", reopened.Find("input[aria-label='Barbell Squat set 1 weight in kg']").GetAttribute("value")), Wait);
        Assert.Equal("7", reopened.Find("input[aria-label='Barbell Squat set 1 reps']").GetAttribute("value"));
    }

    [Fact]
    public async Task A_half_typed_or_out_of_range_value_waits_until_focus_leaves()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);
        var session = (await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId))!;
        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 RPE']", "8");
        await WaitForSetAsync(session.Id, s => s.Rpe == 8);

        await TypeAsync(cut, "input[aria-label='Barbell Squat set 1 RPE']", "81");

        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.Equal("81", cut.Find("input[aria-label='Barbell Squat set 1 RPE']").GetAttribute("value"));
        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 RPE']", "81");
        cut.WaitForAssertion(() => Assert.Equal("RPE runs from 0 to 10.", cut.Find("[role=alert]").TextContent), Wait);
        await WaitForSetAsync(session.Id, s => s.Rpe == 8);
    }

    [Fact]
    public async Task A_set_without_reps_cannot_be_ticked_off()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);

        await ClickAsync(cut, "button[aria-label='Barbell Squat set 1 done']");

        cut.WaitForAssertion(() => Assert.Equal("Enter the reps first.", cut.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("false", cut.Find("button[aria-label='Barbell Squat set 1 done']").GetAttribute("aria-pressed"));
    }

    private async Task WaitForSetAsync(string sessionId, Func<LoggedSet, bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        LoggedSet? set = null;
        while (DateTime.UtcNow < deadline)
        {
            set = (await WorkoutLog.ExercisesAsync(App.Database.Db, ProfileId, sessionId)).Single().Sets.Single();
            if (condition(set))
            {
                return;
            }

            await Task.Delay(20, Xunit.TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The set never matched; last saved: {set}");
    }

    [Fact]
    public async Task An_invalid_weight_says_why_and_saves_nothing()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);

        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in kg']", "heavy");

        cut.WaitForAssertion(() => Assert.Equal("Enter the weight as a number.", cut.Find("[role=alert]").TextContent), Wait);
        var session = (await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId))!;
        Assert.Null((await WorkoutLog.ExercisesAsync(App.Database.Db, ProfileId, session.Id)).Single().Sets.Single().Weight);
    }

    [Fact]
    public async Task A_workout_in_progress_is_offered_to_resume_and_finishing_lists_it()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);
        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 weight in kg']", "60");
        await ChangeAsync(cut, "input[aria-label='Barbell Squat set 1 reps']", "5");
        await ClickAsync(cut, "button[aria-label='Barbell Squat set 1 done']");
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("button[aria-label='Barbell Squat set 1 done']").GetAttribute("aria-pressed")), Wait);

        Services.GetRequiredService<NavigationManager>().NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("#resume"), Wait);
        Assert.Contains("1 set done", cut.Find("[aria-labelledby=active-title]").TextContent, StringComparison.Ordinal);
        await ClickAsync(cut, "#resume");
        cut.WaitForAssertion(() => cut.Find("#finish"), Wait);
        await ClickAsync(cut, "#finish");

        cut.WaitForAssertion(() => cut.Find("#recent-title"), Wait);
        Assert.Contains("1 set", cut.Find("[aria-labelledby=recent-title]").TextContent, StringComparison.Ordinal);
        Assert.Null(await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task Discarding_asks_first_then_deletes_the_workout()
    {
        var cut = await TrainAsync();
        await ClickAsync(cut, "#start-empty");
        await AddSquatAsync(cut);

        await ClickAsync(cut, "#discard");
        cut.WaitForAssertion(() => cut.Find("#discard-confirm"), Wait);
        await ClickAsync(cut, "#discard-confirm");

        cut.WaitForAssertion(() => cut.Find("#start-empty"), Wait);
        Assert.Null(await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task A_template_starts_with_its_exercises_planned()
    {
        var cut = await TrainAsync(seed: () =>
            WorkoutPlans.CreateTemplateAsync(App.Database.Db, ProfileId, "Legs", [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5, 80, "kg"))], App.Clock));
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Start Legs']"), Wait);

        await ClickAsync(cut, "button[aria-label='Start Legs']");

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tr.set").Count), Wait);
        Assert.Equal("80", cut.Find("input[aria-label='Barbell Squat set 3 weight in kg']").GetAttribute("value"));
    }
}
