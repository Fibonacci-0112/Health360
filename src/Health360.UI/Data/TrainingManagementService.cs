using Health360.Contracts.Training;
using Health360.LocalDb;

namespace Health360.UI.Data;

public sealed partial class WorkoutService
{
    public Task<HouseholdChoice[]> HouseholdsAsync() => db.GetAll<HouseholdChoice>("SELECT h.id AS Id, h.name AS Name FROM households h " +
        "JOIN household_members m ON m.household_id = h.id WHERE m.profile_id = ? ORDER BY h.name", [RequireProfile()]);
    public sealed class HouseholdChoice { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    public Task<string> CreateCustomExerciseAsync(string householdId, string name, string trackingType, IReadOnlyList<string> equipment, IReadOnlyList<string> muscles) =>
        ExerciseLibrary.AddCustomAsync(db, householdId, name, trackingType, equipment, muscles, clock);
    public Task<TrainingLocationRow[]> LocationsAsync() => TrainingLocations.ListAsync(db);
    public Task<string> CreateLocationAsync(string householdId, TrainingLocationInput input) => TrainingLocations.AddAsync(db, householdId, input, clock);
    public Task UpdateLocationAsync(string id, TrainingLocationInput input) => TrainingLocations.UpdateAsync(db, id, input, clock);
    public Task DeleteLocationAsync(string id) => TrainingLocations.DeleteAsync(db, id);
    public Task<ProgramRow[]> ProgramsAsync() => WorkoutPlans.ProgramsAsync(db, RequireProfile());
    public Task<string> CreateProgramAsync(string name, string? locationId, DateOnly start, DateOnly? end) =>
        WorkoutPlans.CreateProgramAsync(db, RequireProfile(), name, locationId, start, end, clock);
    public Task UpdateProgramAsync(string id, string name, string? locationId, DateOnly start, DateOnly? end) =>
        WorkoutPlans.UpdateProgramAsync(db, RequireProfile(), id, name, locationId, start, end, clock);
    public Task DeleteProgramAsync(string id) => WorkoutPlans.DeleteProgramAsync(db, RequireProfile(), id);
    public Task<ScheduleEntryRow[]> ScheduleAsync(string? programId) => WorkoutPlans.ScheduleEntriesAsync(db, RequireProfile(), programId);
    public Task<string> ScheduleWeeklyAsync(string programId, string templateId, DayOfWeek day) =>
        WorkoutPlans.ScheduleWeeklyAsync(db, RequireProfile(), programId, templateId, day, clock);
    public Task<string> ScheduleOnAsync(string templateId, DateOnly day, string? programId = null) =>
        WorkoutPlans.ScheduleOnAsync(db, RequireProfile(), templateId, day, clock, programId);
    public Task DeleteScheduleAsync(string id) => WorkoutPlans.DeleteScheduleAsync(db, RequireProfile(), id);
    private Task stateWrites = Task.CompletedTask;
    public async Task<string?> ReadStateAsync(string key)
    {
        var profile = RequireProfile();
        try { await stateWrites; } catch (Exception e) when (e is not OperationCanceledException) { }
        return profile == ProfileId ? await WorkoutState.ReadAsync(db, profile, key) : null;
    }
    public Task SaveStateAsync(string key, string value)
    {
        var profile = RequireProfile();
        return stateWrites = StateAfterAsync(stateWrites, profile, () => WorkoutState.WriteAsync(db, profile, key, value));
    }
    public Task DeleteStateAsync(string key)
    {
        var profile = RequireProfile();
        return stateWrites = StateAfterAsync(stateWrites, profile, () => WorkoutState.DeleteAsync(db, profile, key));
    }
    private async Task StateAfterAsync(Task previous, string profile, Func<Task> save)
    {
        try { await previous; } catch (Exception e) when (e is not OperationCanceledException) { }
        if (profile == ProfileId) await save();
    }
    public Task SwapExerciseAsync(string id, string replacementId) => WorkoutLog.SwapExerciseAsync(db, RequireProfile(), id, replacementId, LoadUnit, clock);
    public Task<int> RestSecondsAsync(string exerciseId) => WorkoutLog.RestSecondsAsync(db, RequireProfile(), exerciseId);
    public Task<MuscleVolumeRow[]> MuscleVolumeAsync(DateOnly from, DateOnly to) => WorkoutLog.MuscleVolumeAsync(db, RequireProfile(), from, to);
    public Task<E1rmPoint[]> E1rmHistoryAsync(string exerciseId) => WorkoutLog.E1rmHistoryAsync(db, RequireProfile(), exerciseId);
}
