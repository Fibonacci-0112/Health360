using Health360.Contracts.Training;
using Health360.Domain.Physiology;
using Health360.Domain.Training;
using Health360.Domain.Units;
using Health360.LocalDb;
using PowerSync.Common.Client;

namespace Health360.UI.Data;

/// <summary>
/// The Train tab's and the logger's access to the signed-in user's workouts in the local database. Every write is
/// local first and uploads when online, so the logger works offline and a session survives the app being killed.
/// </summary>
public sealed class WorkoutService(PowerSyncDatabase db, ProfileService profiles, TimeProvider clock)
{
    /// <summary>The profile's lifting unit code (kg or lb), for new sets with nothing to copy.</summary>
    public string LoadUnit => UnitPrefOptions.Load.ToCode(profiles.UnitPrefs.Load);

    /// <summary>The profile's distance unit code (km or mi), for timed sets with nothing to copy.</summary>
    public string DistanceUnit => UnitPrefOptions.Distance.ToCode(profiles.UnitPrefs.Distance);

    /// <summary>Today on this device.</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public string? ProfileId => profiles.Profile?.Id;

    public Task<SessionRow?> ActiveSessionAsync() => WorkoutLog.ActiveSessionAsync(db, RequireProfile());

    /// <summary>Fires now and after every change to anything the Train tab shows (<see cref="WorkoutLog.WatchTrainChanges"/>).</summary>
    public IAsyncEnumerable<WorkoutLog.ChangeStamp[]> WatchTrainChanges(CancellationToken cancellationToken) =>
        WorkoutLog.WatchTrainChanges(db, RequireProfile(), cancellationToken);

    public Task<SessionRow[]> RecentSessionsAsync(int limit = 5) => WorkoutLog.RecentSessionsAsync(db, RequireProfile(), limit);

    public Task<CardioZones?> CardioZonesAsync(ZoneSport sport, PhysiologyProfile physiology) =>
        CardioLog.ZonesAsync(db, RequireProfile(), sport, Today, physiology);

    public Task<string> SaveCardioAsync(CardioEntry entry, CardioPhysiology physiology, CardioZones? zones) =>
        CardioLog.SaveAsync(db, RequireProfile(), entry, physiology, zones, clock);

    public Task<CardioSessionRow[]> CardioSessionsAsync(DateOnly from, DateOnly to) =>
        CardioLog.SessionsAsync(db, RequireProfile(), from, to);

    public Task<TemplateRow[]> TemplatesAsync() => WorkoutPlans.TemplatesAsync(db, RequireProfile());

    public Task<TemplateRow?> TemplateAsync(string templateId) => WorkoutPlans.TemplateAsync(db, RequireProfile(), templateId);

    public Task<TemplateExerciseRow[]> TemplateExercisesAsync(string templateId) => WorkoutPlans.TemplateExercisesAsync(db, RequireProfile(), templateId);

    public Task<string> CreateTemplateAsync(string name, IReadOnlyList<TemplateExerciseInput> exercises) =>
        WorkoutPlans.CreateTemplateAsync(db, RequireProfile(), name, exercises, clock);

    public Task UpdateTemplateAsync(
        string templateId, string name, IReadOnlyList<TemplateExerciseInput> exercises, IReadOnlyCollection<string>? loadedIds = null) =>
        WorkoutPlans.UpdateTemplateAsync(db, RequireProfile(), templateId, name, exercises, clock, loadedIds);

    public Task DeleteTemplateAsync(string templateId) => WorkoutPlans.DeleteTemplateAsync(db, RequireProfile(), templateId, clock);

    /// <summary>The progression suggestion for each exercise of a session, by session exercise id (<see cref="WorkoutProgression"/>).</summary>
    public Task<IReadOnlyDictionary<string, ExerciseSuggestion>> SuggestionsAsync(string sessionId) =>
        WorkoutProgression.ForSessionAsync(db, RequireProfile(), sessionId);

    public Task<IReadOnlyList<PlannedWorkout>> PlannedAsync(DateOnly from, DateOnly to) => WorkoutPlans.PlannedAsync(db, RequireProfile(), from, to);

    public Task<PersonalRecordRow[]> RecordsAsync() => WorkoutLog.RecordsAsync(db, RequireProfile());

    public Task<string> StartAsync(string? templateId, string? scheduledId = null) =>
        WorkoutLog.StartAsync(db, RequireProfile(), templateId, LoadUnit, clock, scheduledId);

    public IAsyncEnumerable<IReadOnlyList<LoggedExercise>> Watch(string sessionId, CancellationToken cancellationToken) =>
        WorkoutLog.Watch(db, RequireProfile(), sessionId, cancellationToken);

    public Task<ExerciseRow[]> SearchExercisesAsync(string? query) => ExerciseLibrary.SearchAsync(db, query, 8);

    public Task<string> AddExerciseAsync(string sessionId, string exerciseId) =>
        WorkoutLog.AddExerciseAsync(db, RequireProfile(), sessionId, exerciseId, LoadUnit, clock);

    public Task RemoveExerciseAsync(string sessionExerciseId) => WorkoutLog.RemoveExerciseAsync(db, RequireProfile(), sessionExerciseId);

    public Task<string> AddSetAsync(string sessionExerciseId) => WorkoutLog.AddSetAsync(db, RequireProfile(), sessionExerciseId, LoadUnit, clock);

    /// <summary>Changes fields of a set as it is now in the database (<see cref="WorkoutLog.ChangeSetAsync"/>).</summary>
    public Task<IReadOnlyList<NewRecord>> ChangeSetAsync(string setId, Func<SetEntry, SetEntry> change) =>
        WorkoutLog.ChangeSetAsync(db, RequireProfile(), setId, change, clock);

    public Task<IReadOnlyList<NewRecord>> CompleteSetAsync(string setId) => WorkoutLog.CompleteSetAsync(db, RequireProfile(), setId, clock);

    public Task UncompleteSetAsync(string setId) => WorkoutLog.UncompleteSetAsync(db, RequireProfile(), setId, clock);

    public Task DeleteSetAsync(string setId) => WorkoutLog.DeleteSetAsync(db, RequireProfile(), setId);

    public Task FinishAsync(string sessionId) => WorkoutLog.FinishAsync(db, RequireProfile(), sessionId, clock);

    public Task DiscardAsync(string sessionId) => WorkoutLog.DiscardAsync(db, RequireProfile(), sessionId, clock);

    /// <summary>A load in canonical kg shown in the profile's lifting unit ("102.5 kg", "226.0 lb").</summary>
    public string Load(double kg)
    {
        var unit = LoadUnit;
        return Components.MetricDisplay.Format(UnitCodes.Convert(kg, "kg", unit) ?? kg, unit);
    }

    private string RequireProfile() =>
        profiles.Profile?.Id ?? throw new InvalidOperationException("The profile hasn't synced to this device yet.");
}
