using FluentValidation;
using Health360.Contracts.Training;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Templates, programs, the schedule, training locations and the exercise library in a real PowerSync database.</summary>
public class WorkoutPlansTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Household = "0190a0b0-0000-7000-8000-0000000000a1";
    private const string Squat = "60000000-0000-7000-8000-000000000001";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 3, 18, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.Db.WriteTransaction(async tx =>
        {
            foreach (var (id, name) in new[] { (Squat, "Barbell Squat"), ("60000000-0000-7000-8000-000000000002", "Front Squat"),
                ("60000000-0000-7000-8000-000000000003", "Bench Press"), ("60000000-0000-7000-8000-000000000004", "Squat_Jump 100%") })
            {
                await tx.Execute(
                    "INSERT INTO exercises (id, name, tracking_type, equipment, primary_muscles, secondary_muscles) VALUES (?, ?, 'weight_reps', '[]', '[]', '[]')",
                    [id, name]);
            }

            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    [Fact]
    public async Task A_template_keeps_its_exercises_in_order_with_their_schemes()
    {
        await using var t = await OpenAsync();

        var id = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, " Legs ",
            [new TemplateExerciseInput(Squat, new SetScheme(5, 5, 5, 100, "kg"), 180), new TemplateExerciseInput("60000000-0000-7000-8000-000000000002", new SetScheme(3, 8, 12))],
            Clock);

        var template = Assert.Single(await WorkoutPlans.TemplatesAsync(t.Db, Profile));
        Assert.Equal((id, "Legs", 2L), (template.Id, template.Name, template.ExerciseCount));
        var exercises = await WorkoutPlans.TemplateExercisesAsync(t.Db, Profile, id);
        Assert.Equal(["Barbell Squat", "Front Squat"], exercises.Select(e => e.ExerciseName));
        Assert.Equal(new SetScheme(5, 5, 5, 100, "kg"), exercises[0].Scheme);
        Assert.Equal(180L, exercises[0].RestSeconds);

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);
        Assert.Equal(["workout_templates", "template_exercises", "template_exercises"], writer.Writes.Select(w => w.Table));
        Assert.Equal(5, writer.Writes[1].Data!["set_scheme"]!["sets"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_template_with_a_bad_scheme_or_missing_exercise_writes_nothing()
    {
        await using var t = await OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [new TemplateExerciseInput(Squat, new SetScheme(3, 12, 8))], Clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [new TemplateExerciseInput("nope", new SetScheme(3, 8, 12))], Clock));

        Assert.Empty(await WorkoutPlans.TemplatesAsync(t.Db, Profile));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task Weekly_entries_repeat_within_their_programs_dates_and_dated_ones_land_on_their_day()
    {
        await using var t = await OpenAsync();
        var legs = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [], Clock);
        var push = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Push", [], Clock);
        // Monday 2 November to Sunday 15 November.
        var program = await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "Base", null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 15), Clock);
        await WorkoutPlans.ScheduleWeeklyAsync(t.Db, Profile, program, legs, DayOfWeek.Monday, Clock);
        await WorkoutPlans.ScheduleWeeklyAsync(t.Db, Profile, program, push, DayOfWeek.Sunday, Clock);
        await WorkoutPlans.ScheduleOnAsync(t.Db, Profile, push, new DateOnly(2026, 11, 4), Clock);

        var planned = await WorkoutPlans.PlannedAsync(t.Db, Profile, new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 22));

        Assert.Equal(
            [("2026-11-02", "Legs"), ("2026-11-04", "Push"), ("2026-11-08", "Push"), ("2026-11-09", "Legs"), ("2026-11-15", "Push")],
            planned.Select(p => (p.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), p.TemplateName)));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);
        Assert.Equal([1L, 7L], writer.Writes.Where(w => w.Data?["weekday"] is not null).Select(w => w.Data!["weekday"]!.GetValue<long>()));
    }

    [Fact]
    public async Task Starting_a_dated_entry_marks_it_done_and_discarding_unmarks_it()
    {
        await using var t = await OpenAsync();
        var legs = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5))], Clock);
        var entry = await WorkoutPlans.ScheduleOnAsync(t.Db, Profile, legs, new DateOnly(2026, 11, 3), Clock);

        var session = await WorkoutLog.StartAsync(t.Db, Profile, legs, "kg", Clock, entry);

        var day = new DateOnly(2026, 11, 3);
        Assert.Equal(session, Assert.Single(await WorkoutPlans.PlannedAsync(t.Db, Profile, day, day)).SessionId);
        await WorkoutLog.DiscardAsync(t.Db, Profile, session, Clock);
        Assert.Null(Assert.Single(await WorkoutPlans.PlannedAsync(t.Db, Profile, day, day)).SessionId);
    }

    [Fact]
    public async Task A_weekly_entry_is_done_on_a_day_with_a_session_of_its_template()
    {
        await using var t = await OpenAsync();
        var legs = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "Legs", [new TemplateExerciseInput(Squat, new SetScheme(3, 5, 5))], Clock);
        var program = await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "Base", null, new DateOnly(2026, 11, 2), null, Clock);
        // 3 November 2026 is a Tuesday.
        await WorkoutPlans.ScheduleWeeklyAsync(t.Db, Profile, program, legs, DayOfWeek.Tuesday, Clock);
        var day = new DateOnly(2026, 11, 3);
        Assert.Null(Assert.Single(await WorkoutPlans.PlannedAsync(t.Db, Profile, day, day)).SessionId);

        var session = await WorkoutLog.StartAsync(t.Db, Profile, legs, "kg", Clock);

        Assert.Equal(session, Assert.Single(await WorkoutPlans.PlannedAsync(t.Db, Profile, day, day)).SessionId);
        Assert.Null(Assert.Single(await WorkoutPlans.PlannedAsync(t.Db, Profile, day.AddDays(7), day.AddDays(7))).SessionId);
    }

    [Fact]
    public async Task A_program_cannot_end_before_it_starts() =>
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var t = await OpenAsync();
            await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "Base", null, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 1), Clock);
        });

    [Fact]
    public async Task A_location_keeps_plates_in_their_own_units_and_the_bar_in_kg_too()
    {
        await using var t = await OpenAsync();
        var input = new TrainingLocationInput("Garage", ["barbell", "dumbbell"],
            [new PlateInput(45, "lb", 4), new PlateInput(1.25, "kg", 2)], 45, "lb");

        var id = await TrainingLocations.AddAsync(t.Db, Household, input, Clock);

        var location = Assert.Single(await TrainingLocations.ListAsync(t.Db));
        Assert.Equal((id, "Garage", 45 * 0.45359237), (location.Id, location.Name, location.BarWeightKg!.Value));
        Assert.Equal(input.Plates, location.Plates);
        Assert.Equal(["barbell", "dumbbell"], location.EquipmentCodes);
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);
        var data = writer.Writes.Single().Data!;
        Assert.Equal("""[{"weight":45,"unit":"lb","count":4},{"weight":1.25,"unit":"kg","count":2}]""", data["plate_inventory"]!.ToJsonString());
        Assert.Equal("""["barbell","dumbbell"]""", data["equipment"]!.ToJsonString());
    }

    [Theory]
    [InlineData(0.0, "kg", 2)]
    [InlineData(20.0, "st", 2)]
    [InlineData(20.0, "kg", 0)]
    public async Task A_location_with_a_bad_plate_writes_nothing(double weight, string unit, int count)
    {
        await using var t = await OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            TrainingLocations.AddAsync(t.Db, Household, new TrainingLocationInput("Gym", [], [new PlateInput(weight, unit, count)], null, null), Clock));

        Assert.Empty(await TrainingLocations.ListAsync(t.Db));
    }

    [Fact]
    public async Task Deleting_a_location_leaves_its_programs_without_one()
    {
        await using var t = await OpenAsync();
        var location = await TrainingLocations.AddAsync(t.Db, Household, new TrainingLocationInput("Gym", [], [], 20, "kg"), Clock);
        await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "Base", location, new DateOnly(2026, 11, 2), null, Clock);

        await TrainingLocations.DeleteAsync(t.Db, location);

        Assert.Null(Assert.Single(await WorkoutPlans.ProgramsAsync(t.Db, Profile)).LocationId);
    }

    [Fact]
    public async Task Search_puts_names_starting_with_the_text_first_and_treats_wildcards_as_text()
    {
        await using var t = await OpenAsync();

        Assert.Equal(["Squat_Jump 100%", "Barbell Squat", "Front Squat"], (await ExerciseLibrary.SearchAsync(t.Db, "squat")).Select(e => e.Name));
        Assert.Equal(["Squat_Jump 100%"], (await ExerciseLibrary.SearchAsync(t.Db, "100%")).Select(e => e.Name));
        Assert.Equal(["Squat_Jump 100%"], (await ExerciseLibrary.SearchAsync(t.Db, "t_j")).Select(e => e.Name));
        Assert.Equal(4, (await ExerciseLibrary.SearchAsync(t.Db, "")).Length);
    }

    [Fact]
    public async Task A_custom_exercise_belongs_to_the_household()
    {
        await using var t = await OpenAsync();

        var id = await ExerciseLibrary.AddCustomAsync(t.Db, Household, "Band pull-apart", "reps", ["band"], ["rear_delts"], Clock);

        var row = (await ExerciseLibrary.GetAsync(t.Db, id))!;
        Assert.Equal((Household, true, false), (row.HouseholdId, row.IsCustom, row.TracksLoad));
        Assert.Equal(["band"], row.EquipmentCodes);
        await Assert.ThrowsAsync<ArgumentException>(() => ExerciseLibrary.AddCustomAsync(t.Db, Household, "X", "reps", ["Bar bell"], [], Clock));
    }
}
