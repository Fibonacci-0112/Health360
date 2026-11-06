using Health360.Contracts.Training;
using Health360.Domain.Physiology;
using Health360.Domain.Training;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

public sealed class TrainingCompletionTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Household = "0190a0b0-0000-7000-8000-0000000000a1";
    private const string Exercise = "60000000-0000-7000-8000-000000000001";
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock Clock = new(Now);

    [Fact]
    public async Task An_hour_workout_reopens_mid_session_then_uploads_every_completed_set_and_volume()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("""INSERT INTO exercises (id, name, tracking_type, primary_muscles) VALUES (?, 'Squat', 'weight_reps', '["quadriceps","glutes"]')""", [Exercise]);
        await t.Db.Execute("DELETE FROM ps_crud");
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", new FixedClock(Now.AddHours(-1)));
        var exercise = await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Exercise, "kg", Clock);
        var set = Assert.Single(Assert.Single(await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Sets);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new(5, 100, "kg"), new FixedClock(Now.AddMinutes(-40)));
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, new FixedClock(Now.AddMinutes(-40)));
        await t.ReopenAsync();
        Assert.Equal(session, (await WorkoutLog.ActiveSessionAsync(t.Db, Profile))!.Id);
        var second = await WorkoutLog.AddSetAsync(t.Db, Profile, exercise, "kg", Clock);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, second, new(5, 225, "lb"), Clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, second, Clock);
        await WorkoutLog.FinishAsync(t.Db, Profile, session, Clock, new CardioPhysiology(80, Sex.Male, 30));
        var finished = Assert.Single(await WorkoutLog.RecentSessionsAsync(t.Db, Profile));
        Assert.Equal(2, finished.CompletedSets);
        Assert.NotNull(CardioJson.Parse(finished.EnergyEstimate));
        var volume = await WorkoutLog.MuscleVolumeAsync(t.Db, Profile, new(2026, 11, 5), new(2026, 11, 5));
        Assert.Equal(2, volume.Length);
        Assert.All(volume, m => { Assert.Equal(2, m.Sets); Assert.Equal(500 + 225 * 0.45359237 * 5, m.VolumeKg, 9); });
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, writer.Writes.Count(w => w.Table == "set_logs" && w.Data?["completed_at"] is not null));
    }

    [Fact]
    public async Task Strength_energy_runs_to_the_last_completed_set_not_to_a_late_finish()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO exercises (id, name, tracking_type) VALUES (?, 'Squat', 'weight_reps')", [Exercise]);
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", new FixedClock(Now.AddHours(-10)));
        await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Exercise, "kg", Clock);
        var set = Assert.Single(Assert.Single(await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).Sets);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, set.Id, new(5, 100, "kg"), Clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, set.Id, new FixedClock(Now.AddHours(-9)));
        await WorkoutLog.FinishAsync(t.Db, Profile, session, Clock, new CardioPhysiology(80, Sex.Male, 30));
        var estimate = CardioJson.Parse(Assert.Single(await WorkoutLog.RecentSessionsAsync(t.Db, Profile)).EnergyEstimate)!;
        Assert.Equal(3600, estimate.Entry.DurationSeconds, 6);
        Assert.Equal((196, 364), (Math.Round(estimate.Kcal!.Low, 6), Math.Round(estimate.Kcal.High, 6)));

        var empty = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", new FixedClock(Now.AddHours(-1)));
        await WorkoutLog.FinishAsync(t.Db, Profile, empty, Clock, new CardioPhysiology(80, Sex.Male, 30));
        Assert.Null((await WorkoutLog.RecentSessionsAsync(t.Db, Profile)).Single(s => s.Id == empty).EnergyEstimate);
    }

    [Fact]
    public async Task Swapping_keeps_the_slots_superset_and_rest_and_a_self_swap_changes_nothing()
    {
        await using var t = await TestDatabase.OpenAsync();
        foreach (var id in new[] { Exercise, Household, Profile })
            await t.Db.Execute("INSERT INTO exercises (id, name, tracking_type) VALUES (?, 'Exercise', 'weight_reps')", [id]);
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "A",
            [new TemplateExerciseInput(Exercise, new SetScheme(2, 8, 12, Superset: "A"), 45), new TemplateExerciseInput(Household, new SetScheme(2, 8, 12, Superset: "A"), 45)], Clock);
        var session = await WorkoutLog.StartAsync(t.Db, Profile, template, "kg", Clock);
        var first = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, first.Sets[0].Id, new(7, 40, "kg"), Clock);
        var before = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];

        await WorkoutLog.SwapExerciseAsync(t.Db, Profile, first.Id, Exercise, "kg", Clock);
        var unchanged = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];
        Assert.Equal((before.Id, before.ExerciseId), (unchanged.Id, unchanged.ExerciseId));
        Assert.Equal(before.Sets, unchanged.Sets);
        Assert.Equal(7, unchanged.Sets[0].Reps);

        await WorkoutLog.SwapExerciseAsync(t.Db, Profile, first.Id, Profile, "kg", Clock);
        var swapped = (await WorkoutLog.ExercisesAsync(t.Db, Profile, session))[0];
        Assert.Equal((Profile, "A"), (swapped.ExerciseId, swapped.Superset));
        Assert.Equal(45, await WorkoutLog.RestSecondsAsync(t.Db, Profile, swapped.Id));
    }

    [Fact]
    public async Task Swapping_an_unstarted_exercise_cannot_relabel_completed_sets_or_another_owners_data()
    {
        await using var t = await TestDatabase.OpenAsync();
        foreach (var id in new[] { Exercise, Household })
            await t.Db.Execute("INSERT INTO exercises (id, name, tracking_type) VALUES (?, 'Exercise', 'weight_reps')", [id]);
        var session = await WorkoutLog.StartAsync(t.Db, Profile, null, "kg", Clock);
        var id1 = await WorkoutLog.AddExerciseAsync(t.Db, Profile, session, Exercise, "kg", Clock);
        await WorkoutLog.SwapExerciseAsync(t.Db, Profile, id1, Household, "kg", Clock);
        var swapped = Assert.Single(await WorkoutLog.ExercisesAsync(t.Db, Profile, session));
        Assert.Equal(Household, swapped.ExerciseId);
        await WorkoutLog.UpdateSetAsync(t.Db, Profile, swapped.Sets[0].Id, new(5, 100, "kg"), Clock);
        await WorkoutLog.CompleteSetAsync(t.Db, Profile, swapped.Sets[0].Id, Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutLog.SwapExerciseAsync(t.Db, Profile, swapped.Id, Exercise, "kg", Clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutLog.SwapExerciseAsync(t.Db, "other", swapped.Id, Exercise, "kg", Clock));
        Assert.Equal(Household, Assert.Single(await WorkoutLog.ExercisesAsync(t.Db, Profile, session)).ExerciseId);
    }

    [Fact]
    public async Task Local_drafts_survive_reopen_are_never_uploaded_and_clear_with_account_data()
    {
        await using var t = await TestDatabase.OpenAsync();
        await WorkoutState.WriteAsync(t.Db, Profile, "cardio", "unfinished text");
        await t.ReopenAsync();
        Assert.Equal("unfinished text", await WorkoutState.ReadAsync(t.Db, Profile, "cardio"));
        Assert.Null(await WorkoutState.ReadAsync(t.Db, "other", "cardio"));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
        await t.Db.DisconnectAndClear(clearLocal: true);
        Assert.Null(await WorkoutState.ReadAsync(t.Db, Profile, "cardio"));
    }

    [Fact]
    public async Task Backdated_cardio_saves_the_actual_date_elevation_and_immutable_v2_snapshot()
    {
        await using var t = await TestDatabase.OpenAsync();
        var entry = new CardioEntry("18240", 3600, Rpe: 5, Elevation: 25, ElevationUnit: "ft", EndedAt: Now.AddDays(-2));
        await CardioLog.SaveAsync(t.Db, Profile, entry, new(80, null, null), null, Clock);
        await t.ReopenAsync();
        var saved = Assert.Single(await CardioLog.SessionsAsync(t.Db, Profile, new(2026, 11, 3), new(2026, 11, 3))).Estimate!;
        Assert.Equal(2, saved.Version);
        Assert.Equal(entry, saved.Entry);
        Assert.NotNull(saved.Fuel);
        await Assert.ThrowsAsync<ArgumentException>(() => CardioLog.SaveAsync(t.Db, Profile, entry with { EndedAt = Now.AddDays(1) }, new(80, null, null), null, Clock));
    }

    [Fact]
    public async Task Missing_or_foreign_schedule_parents_never_enter_the_queue()
    {
        await using var t = await TestDatabase.OpenAsync();
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "A", [], Clock);
        var program = await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "A", null, new(2026, 11, 1), null, Clock);
        await t.Db.Execute("DELETE FROM ps_crud");
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutPlans.ScheduleWeeklyAsync(t.Db, "other", program, template, DayOfWeek.Monday, Clock));
        await WorkoutPlans.DeleteTemplateAsync(t.Db, Profile, template, Clock);
        await t.Db.Execute("DELETE FROM ps_crud");
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkoutPlans.ScheduleOnAsync(t.Db, Profile, template, new(2026, 11, 5), Clock));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task Editing_locations_programs_and_schedules_preserves_logged_history()
    {
        await using var t = await TestDatabase.OpenAsync();
        var location = await TrainingLocations.AddAsync(t.Db, Household, new("Home", [], [], 20, "kg"), Clock);
        await TrainingLocations.UpdateAsync(t.Db, location, new("Garage", ["barbell"], [new(45, "lb", 4)], 45, "lb"), Clock);
        var template = await WorkoutPlans.CreateTemplateAsync(t.Db, Profile, "A", [], Clock);
        var program = await WorkoutPlans.CreateProgramAsync(t.Db, Profile, "Base", location, new(2026, 11, 1), null, Clock);
        await WorkoutPlans.UpdateProgramAsync(t.Db, Profile, program, "Peak", location, new(2026, 11, 2), new(2026, 12, 1), Clock);
        var schedule = await WorkoutPlans.ScheduleWeeklyAsync(t.Db, Profile, program, template, DayOfWeek.Monday, Clock);
        Assert.Equal(schedule, Assert.Single(await WorkoutPlans.ScheduleEntriesAsync(t.Db, Profile, program)).Id);
        await WorkoutPlans.DeleteScheduleAsync(t.Db, "other", schedule);
        Assert.Single(await WorkoutPlans.ScheduleEntriesAsync(t.Db, Profile, program));
        await WorkoutPlans.DeleteProgramAsync(t.Db, Profile, program);
        Assert.Empty(await WorkoutPlans.ScheduleEntriesAsync(t.Db, Profile, program));
        Assert.Single(await WorkoutPlans.TemplatesAsync(t.Db, Profile));
        Assert.Equal(45 * 0.45359237, Assert.Single(await TrainingLocations.ListAsync(t.Db)).BarWeightKg);
    }
}
