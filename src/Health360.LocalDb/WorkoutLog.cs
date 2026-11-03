using System.Runtime.CompilerServices;
using FluentValidation;
using Health360.Contracts.Training;
using Health360.Domain.Training;
using Health360.Domain.Units;
using PowerSync.Common.Client;
using PowerSync.Common.DB;

namespace Health360.LocalDb;

/// <summary>One <c>workout_sessions</c> row as the Train tab lists it.</summary>
public sealed class SessionRow
{
    public string Id { get; set; } = "";

    public string? TemplateId { get; set; }

    public string? TemplateName { get; set; }

    public string Kind { get; set; } = "";

    public string StartedAt { get; set; } = "";

    /// <summary>Null while the session is in progress.</summary>
    public string? EndedAt { get; set; }

    public string LocalDate { get; set; } = "";

    public long CompletedSets { get; set; }
}

/// <summary>One set in the logger, as entered, with what the logger computed for it.</summary>
public sealed record LoggedSet(
    string Id,
    int Position,
    string SetType,
    int? Reps,
    double? Weight,
    string? WeightUnit,
    double? WeightKg,
    double? Rpe,
    int? Rir,
    bool Completed,
    double? E1rmKg,
    bool IsPr,
    double? DurationSeconds = null,
    double? Distance = null,
    string? DistanceUnit = null);

/// <summary>One exercise of a session with its sets, in order.</summary>
public sealed record LoggedExercise(
    string Id,
    string ExerciseId,
    string Name,
    string TrackingType,
    int Position,
    IReadOnlyList<LoggedSet> Sets)
{
    public bool TracksLoad => TrackingType == "weight_reps";

    /// <summary>Whether a set of it is counted in reps (weight and reps, or reps only).</summary>
    public bool TracksReps => TrackingType is "weight_reps" or "reps";

    /// <summary>Whether a set of it records a time (time only, or distance and time).</summary>
    public bool TracksTime => TrackingType is "duration" or "distance_duration";

    public bool TracksDistance => TrackingType == "distance_duration";
}

/// <summary>One <c>personal_records</c> row with its exercise's name.</summary>
public sealed class PersonalRecordRow
{
    public string Id { get; set; } = "";

    public string ExerciseId { get; set; } = "";

    public string ExerciseName { get; set; } = "";

    /// <summary><c>e1rm</c>, <c>weight</c>, <c>reps</c> or <c>volume</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>kg, or a rep count for <c>reps</c>.</summary>
    public double Value { get; set; }

    public double WeightKg { get; set; }

    public long Reps { get; set; }

    public string? E1rmFormula { get; set; }

    public string SetLogId { get; set; } = "";

    public string AchievedAt { get; set; } = "";

    public string LocalDate { get; set; } = "";
}

/// <summary>The best e1RM of an exercise on one day.</summary>
public sealed class E1rmPoint
{
    public string LocalDate { get; set; } = "";

    public double E1rmKg { get; set; }
}

/// <summary>
/// The live workout logger's data (<c>workout_sessions</c>, <c>session_exercises</c>, <c>set_logs</c> and
/// <c>personal_records</c>, all U) in the local database. Every change is written at once, so a session survives the
/// app being killed and works offline; it uploads when online. Loads are kept as entered; <c>weight_kg</c> and
/// <c>e1rm_kg</c> are computed exactly as the server's trigger recomputes them, and PRs compare canonical kg
/// (<see cref="PersonalRecords"/>).
/// </summary>
public static class WorkoutLog
{
    /// <summary>The formula the logger estimates e1RM with (docs/calculations.md).</summary>
    public const OneRepMaxFormula DefaultFormula = OneRepMaxFormula.Epley;

    private const string SessionColumns =
        "ws.id AS Id, ws.template_id AS TemplateId, t.name AS TemplateName, ws.kind AS Kind, ws.started_at AS StartedAt, " +
        "ws.ended_at AS EndedAt, ws.local_date AS LocalDate, " +
        "(SELECT count(*) FROM set_logs s JOIN session_exercises se ON se.id = s.session_exercise_id " +
        "WHERE se.session_id = ws.id AND s.completed_at IS NOT NULL) AS CompletedSets";

    private const string ActiveSql =
        $"SELECT {SessionColumns} FROM workout_sessions ws LEFT JOIN workout_templates t ON t.id = ws.template_id " +
        "WHERE ws.profile_id = ? AND ws.ended_at IS NULL ORDER BY ws.started_at DESC, ws.id DESC LIMIT 1";

    private const string SetValueColumns =
        "set_type AS SetType, reps AS Reps, weight AS Weight, weight_unit AS WeightUnit, duration_seconds AS DurationSeconds, " +
        "distance AS Distance, distance_unit AS DistanceUnit";

    private const string SetsSql =
        "SELECT se.id AS SessionExerciseId, se.exercise_id AS ExerciseId, e.name AS ExerciseName, e.tracking_type AS TrackingType, " +
        "se.position AS ExercisePosition, s.id AS SetId, s.position AS SetPosition, s.set_type AS SetType, s.reps AS Reps, " +
        "s.weight AS Weight, s.weight_unit AS WeightUnit, s.weight_kg AS WeightKg, s.rpe AS Rpe, s.rir AS Rir, " +
        "s.completed_at AS CompletedAt, s.e1rm_kg AS E1rmKg, s.is_pr AS IsPr, s.duration_seconds AS DurationSeconds, " +
        "s.distance AS Distance, s.distance_unit AS DistanceUnit " +
        "FROM session_exercises se LEFT JOIN exercises e ON e.id = se.exercise_id " +
        "LEFT JOIN set_logs s ON s.session_exercise_id = se.id AND s.profile_id = se.profile_id " +
        "WHERE se.session_id = ? AND se.profile_id = ? ORDER BY se.position, se.id, s.position, s.id";

    /// <summary>The session in progress (not ended), newest first if there are several; null when there is none.</summary>
    public static async Task<SessionRow?> ActiveSessionAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetOptional<SessionRow>(ActiveSql, [profileId]).ConfigureAwait(false);
    }

    /// <summary>The session in progress, now and after every change (an empty array when there is none).</summary>
    public static IAsyncEnumerable<SessionRow[]> WatchActiveSession(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<SessionRow>(ActiveSql, [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    /// <summary>
    /// Fires now and after every change to anything the Train tab shows: sessions and their sets, templates, programs,
    /// the schedule, records and the exercise library, whether written here or synced from another device. It reads
    /// one row of counts; the caller reloads what it shows.
    /// </summary>
    public static IAsyncEnumerable<ChangeStamp[]> WatchTrainChanges(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<ChangeStamp>(
            "SELECT (SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM workout_sessions WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM set_logs WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM workout_templates WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM template_exercises WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM programs WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM scheduled_workouts WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM personal_records WHERE profile_id = ?) || " +
            "(SELECT count(*) FROM exercises) AS Stamp",
            [profileId, profileId, profileId, profileId, profileId, profileId, profileId],
            new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    /// <summary>The profile's finished sessions, newest first.</summary>
    public static async Task<SessionRow[]> RecentSessionsAsync(IPowerSyncDatabase db, string profileId, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<SessionRow>(
            $"SELECT {SessionColumns} FROM workout_sessions ws LEFT JOIN workout_templates t ON t.id = ws.template_id " +
            "WHERE ws.profile_id = ? AND ws.ended_at IS NOT NULL ORDER BY ws.started_at DESC, ws.id DESC LIMIT ?",
            [profileId, Math.Clamp(limit, 1, 200)]).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a strength session, empty or from a template. Each exercise gets planned sets to accept with one tap: with
    /// a progression rule, last time's warm-ups and the scheme's work sets at the suggested load
    /// (<see cref="WorkoutProgression"/>); otherwise last time's completed sets as entered, or else the template's
    /// scheme, or else one empty set in <paramref name="loadUnit"/>. A dated <paramref name="scheduledId"/> is marked as done by this session.
    /// </summary>
    /// <exception cref="InvalidOperationException">A session is already in progress, or the template isn't on this device.</exception>
    public static async Task<string> StartAsync(
        IPowerSyncDatabase db,
        string profileId,
        string? templateId,
        string loadUnit,
        TimeProvider clock,
        string? scheduledId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        CheckLoadUnit(loadUnit);
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            if (await tx.GetOptional<WorkoutPlans.IdOnly>(
                "SELECT id AS Id FROM workout_sessions WHERE profile_id = ? AND ended_at IS NULL", [profileId]).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("Finish or discard the workout in progress first.");
            }

            TemplateExerciseRow[] exercises = [];
            if (templateId is not null)
            {
                _ = await tx.GetOptional<WorkoutPlans.IdOnly>(
                    "SELECT id AS Id FROM workout_templates WHERE id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The template is not on this device.");
                exercises = await tx.GetAll<TemplateExerciseRow>(WorkoutPlans.TemplateExercisesSql, [templateId, profileId]).ConfigureAwait(false);
            }

            await tx.Execute(
                "INSERT INTO workout_sessions (id, profile_id, template_id, kind, started_at, local_date, created_at, updated_at) " +
                "VALUES (?, ?, ?, 'strength', ?, ?, ?, ?)",
                [id, profileId, templateId, now, Timestamps.LocalDate(clock), now, now]).ConfigureAwait(false);
            for (var i = 0; i < exercises.Length; i++)
            {
                var suggestion = await WorkoutProgression.SuggestAsync(
                    tx, profileId, templateId!, exercises[i], WorkoutProgression.Occurrence(exercises, i), now).ConfigureAwait(false);

                // Linear progression aims for the top of the range, also before there is a load to suggest. A row with a
                // rule follows its own scheme then, not sets copied from an unrelated workout of the exercise.
                var scheme = exercises[i].Scheme is { } s && exercises[i].Rule?.Kind == "linear" ? s with { MinReps = s.MaxReps } : exercises[i].Scheme;
                var ruled = exercises[i].Rule is not null && scheme is not null && exercises[i].TrackingType == "weight_reps";
                await AddExerciseAsync(tx, profileId, id, exercises[i].ExerciseId, i, scheme, suggestion, loadUnit, clock, ruled)
                    .ConfigureAwait(false);
            }

            if (scheduledId is not null)
            {
                await tx.Execute(
                    "UPDATE scheduled_workouts SET session_id = ?, updated_at = ? WHERE id = ? AND profile_id = ? AND scheduled_on IS NOT NULL",
                    [id, now, scheduledId, profileId]).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>Adds an exercise at the end of a session, with planned sets as <see cref="StartAsync"/> describes.</summary>
    public static async Task<string> AddExerciseAsync(
        IPowerSyncDatabase db, string profileId, string sessionId, string exerciseId, string loadUnit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        CheckLoadUnit(loadUnit);
        string id = "";
        await db.WriteTransaction(async tx =>
        {
            await RequireOpenSessionAsync(tx, profileId, sessionId).ConfigureAwait(false);
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM exercises WHERE id = ?", [exerciseId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The exercise is not on this device.");
            var next = await tx.Get<Count>(
                "SELECT coalesce(max(position) + 1, 0) AS N FROM session_exercises WHERE session_id = ? AND profile_id = ?",
                [sessionId, profileId]).ConfigureAwait(false);
            id = await AddExerciseAsync(tx, profileId, sessionId, exerciseId, (int)next.N, null, null, loadUnit, clock).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>Removes an exercise and its sets (and their records) from a session in progress.</summary>
    public static async Task RemoveExerciseAsync(IPowerSyncDatabase db, string profileId, string sessionExerciseId)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.WriteTransaction(async tx =>
        {
            var owner = await tx.GetOptional<SessionOf>(
                "SELECT session_id AS SessionId FROM session_exercises WHERE id = ? AND profile_id = ?", [sessionExerciseId, profileId])
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The exercise is not in this workout.");
            await RequireOpenSessionAsync(tx, profileId, owner.SessionId).ConfigureAwait(false);
            await tx.Execute(
                "DELETE FROM personal_records WHERE profile_id = ? AND set_log_id IN (SELECT id FROM set_logs WHERE session_exercise_id = ?)",
                [profileId, sessionExerciseId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM set_logs WHERE session_exercise_id = ? AND profile_id = ?", [sessionExerciseId, profileId])
                .ConfigureAwait(false);
            await tx.Execute("DELETE FROM session_exercises WHERE id = ? AND profile_id = ?", [sessionExerciseId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Adds a set after the exercise's last, copying its type, reps and load so it takes one tap to accept.</summary>
    public static async Task<string> AddSetAsync(IPowerSyncDatabase db, string profileId, string sessionExerciseId, string loadUnit, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        CheckLoadUnit(loadUnit);
        var id = ExerciseLibrary.NewId(clock);
        await db.WriteTransaction(async tx =>
        {
            var owner = await tx.GetOptional<SessionOf>(
                "SELECT session_id AS SessionId FROM session_exercises WHERE id = ? AND profile_id = ?", [sessionExerciseId, profileId])
                .ConfigureAwait(false) ?? throw new InvalidOperationException("The exercise is not in this workout.");
            await RequireOpenSessionAsync(tx, profileId, owner.SessionId).ConfigureAwait(false);
            var last = await tx.GetOptional<SetValues>(
                $"SELECT position AS Position, {SetValueColumns} FROM set_logs WHERE session_exercise_id = ? AND profile_id = ? ORDER BY position DESC, id DESC LIMIT 1",
                [sessionExerciseId, profileId]).ConfigureAwait(false);
            var values = last is null
                ? new SetValues { SetType = "working", WeightUnit = loadUnit }
                : new SetValues
                {
                    SetType = last.SetType == "warmup" ? "working" : last.SetType,
                    Reps = last.Reps,
                    Weight = last.Weight,
                    WeightUnit = last.WeightUnit,
                    DurationSeconds = last.DurationSeconds,
                    Distance = last.Distance,
                    DistanceUnit = last.DistanceUnit,
                };
            await InsertSetAsync(tx, id, profileId, sessionExerciseId, last is null ? 0 : (int)last.Position + 1, values, clock).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Replaces a set's entry, in a workout still in progress. A completed set is evaluated again: its e1RM, and its
    /// records against the sets completed before it. Returns only records it didn't already hold.
    /// </summary>
    /// <exception cref="ValidationException">The entry fails <see cref="SetEntryValidator"/>; nothing is written.</exception>
    public static Task<IReadOnlyList<NewRecord>> UpdateSetAsync(
        IPowerSyncDatabase db, string profileId, string setId, SetEntry entry, TimeProvider clock, OneRepMaxFormula formula = DefaultFormula) =>
        ChangeSetAsync(db, profileId, setId, _ => entry, clock, formula);

    /// <summary>
    /// Changes one or more fields of a set, applied to the row as it is now in the database, so quick edits of
    /// different fields can't overwrite each other with values read before the other was saved. Evaluated again when
    /// completed, as <see cref="UpdateSetAsync"/>.
    /// </summary>
    /// <exception cref="ValidationException">The changed entry fails <see cref="SetEntryValidator"/>; nothing is written.</exception>
    public static async Task<IReadOnlyList<NewRecord>> ChangeSetAsync(
        IPowerSyncDatabase db,
        string profileId,
        string setId,
        Func<SetEntry, SetEntry> change,
        TimeProvider clock,
        OneRepMaxFormula formula = DefaultFormula)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(clock);
        IReadOnlyList<NewRecord> records = [];
        await db.WriteTransaction(async tx =>
        {
            var state = await RequireSetAsync(tx, profileId, setId).ConfigureAwait(false);
            var current = await tx.Get<StoredSet>(
                $"SELECT {SetValueColumns}, rpe AS Rpe, rir AS Rir, completed_at AS CompletedAt FROM set_logs WHERE id = ? AND profile_id = ?",
                [setId, profileId]).ConfigureAwait(false);
            var entry = change(new SetEntry(
                current.Reps is { } reps ? (int)reps : null,
                current.Weight,
                current.WeightUnit ?? "kg",
                current.Rpe,
                current.Rir is { } rir ? (int)rir : null,
                current.SetType,
                current.DurationSeconds,
                current.Distance,
                current.DistanceUnit));
            new SetEntryValidator().ValidateAndThrow(entry);
            if (current.CompletedAt is not null)
            {
                // A completed set keeps what defines it: clearing a hold's time would leave a done set that says nothing.
                RequireMeasure(state.TrackingType, entry.Reps, entry.DurationSeconds, entry.Distance);
            }

            var weight = entry.Weight;
            double? weightKg = weight is { } w ? UnitCodes.Convert(w, entry.WeightUnit, "kg") : null;
            var distanceUnit = entry.Distance is null ? null : entry.DistanceUnit;
            double? metres = entry.Distance is { } d && distanceUnit is not null ? UnitCodes.Convert(d, distanceUnit, "m") : null;
            await tx.Execute(
                "UPDATE set_logs SET set_type = ?, reps = ?, weight = ?, weight_unit = ?, weight_kg = ?, rpe = ?, rir = ?, " +
                "duration_seconds = ?, distance = ?, distance_unit = ?, distance_m = ?, updated_at = ? WHERE id = ? AND profile_id = ?",
                [entry.SetType, entry.Reps, weight, weight is null ? null : entry.WeightUnit, weightKg, entry.Rpe, entry.Rir,
                 entry.DurationSeconds, entry.Distance, distanceUnit, metres, Timestamps.UtcNow(clock), setId, profileId]).ConfigureAwait(false);
            if (current.CompletedAt is { } completedAt)
            {
                records = await EvaluateAsync(tx, profileId, setId, completedAt, formula, clock).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return records;
    }

    /// <summary>
    /// Ticks a set off: records when it was done, estimates its e1RM (not for a warm-up) and writes a
    /// <c>personal_records</c> row for each record it beats against the sets completed before it. Returns the records it
    /// didn't already hold, for the logger to celebrate. The set's workout must still be in progress.
    /// </summary>
    public static async Task<IReadOnlyList<NewRecord>> CompleteSetAsync(
        IPowerSyncDatabase db, string profileId, string setId, TimeProvider clock, OneRepMaxFormula formula = DefaultFormula)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        IReadOnlyList<NewRecord> records = [];
        await db.WriteTransaction(async tx =>
        {
            var set = await RequireSetAsync(tx, profileId, setId).ConfigureAwait(false);
            RequireMeasure(set.TrackingType, set.Reps, set.DurationSeconds, set.Distance);
            records = await EvaluateAsync(tx, profileId, setId, set.CompletedAt ?? Timestamps.UtcNow(clock), formula, clock).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return records;
    }

    /// <summary>Un-ticks a set: it is planned again, without an estimate or records.</summary>
    public static async Task UncompleteSetAsync(IPowerSyncDatabase db, string profileId, string setId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        await db.WriteTransaction(async tx =>
        {
            _ = await RequireSetAsync(tx, profileId, setId).ConfigureAwait(false);
            await tx.Execute("DELETE FROM personal_records WHERE set_log_id = ? AND profile_id = ?", [setId, profileId]).ConfigureAwait(false);
            await tx.Execute(
                "UPDATE set_logs SET completed_at = NULL, e1rm_formula = NULL, e1rm_kg = NULL, is_pr = 0, updated_at = ? WHERE id = ? AND profile_id = ?",
                [Timestamps.UtcNow(clock), setId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes a set and its records.</summary>
    public static async Task DeleteSetAsync(IPowerSyncDatabase db, string profileId, string setId)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.WriteTransaction(async tx =>
        {
            _ = await RequireSetAsync(tx, profileId, setId).ConfigureAwait(false);
            // The server cascades; locally the record rows go first so their deletes upload before the set's.
            await tx.Execute("DELETE FROM personal_records WHERE set_log_id = ? AND profile_id = ?", [setId, profileId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM set_logs WHERE id = ? AND profile_id = ?", [setId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Ends the session now. Sets still only planned are dropped, then exercises left without sets.</summary>
    public static async Task FinishAsync(IPowerSyncDatabase db, string profileId, string sessionId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        await db.WriteTransaction(async tx =>
        {
            await RequireOpenSessionAsync(tx, profileId, sessionId).ConfigureAwait(false);
            await tx.Execute(
                "DELETE FROM set_logs WHERE profile_id = ? AND completed_at IS NULL " +
                "AND session_exercise_id IN (SELECT id FROM session_exercises WHERE session_id = ?)",
                [profileId, sessionId]).ConfigureAwait(false);
            await tx.Execute(
                "DELETE FROM session_exercises WHERE session_id = ? AND profile_id = ? " +
                "AND NOT EXISTS (SELECT 1 FROM set_logs s WHERE s.session_exercise_id = session_exercises.id)",
                [sessionId, profileId]).ConfigureAwait(false);
            var now = Timestamps.UtcNow(clock);
            await tx.Execute("UPDATE workout_sessions SET ended_at = ?, updated_at = ? WHERE id = ? AND profile_id = ?",
                [now, now, sessionId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes a session with everything logged in it, and unmarks the schedule entry it did.</summary>
    public static async Task DiscardAsync(IPowerSyncDatabase db, string profileId, string sessionId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>(
                "SELECT id AS Id FROM workout_sessions WHERE id = ? AND profile_id = ?", [sessionId, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The workout is not in the local database.");
            const string Sets = "SELECT s.id FROM set_logs s JOIN session_exercises se ON se.id = s.session_exercise_id WHERE se.session_id = ?";
            await tx.Execute($"DELETE FROM personal_records WHERE profile_id = ? AND set_log_id IN ({Sets})", [profileId, sessionId]).ConfigureAwait(false);
            await tx.Execute($"DELETE FROM set_logs WHERE profile_id = ? AND id IN ({Sets})", [profileId, sessionId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM session_exercises WHERE session_id = ? AND profile_id = ?", [sessionId, profileId]).ConfigureAwait(false);
            await tx.Execute("UPDATE scheduled_workouts SET session_id = NULL, updated_at = ? WHERE session_id = ? AND profile_id = ?",
                [Timestamps.UtcNow(clock), sessionId, profileId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM workout_sessions WHERE id = ? AND profile_id = ?", [sessionId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>A session's exercises and sets, in order.</summary>
    public static async Task<IReadOnlyList<LoggedExercise>> ExercisesAsync(IPowerSyncDatabase db, string profileId, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Group(await db.GetAll<FlatSetRow>(SetsSql, [sessionId, profileId]).ConfigureAwait(false));
    }

    /// <summary>A session's exercises and sets, now and after every change.</summary>
    public static async IAsyncEnumerable<IReadOnlyList<LoggedExercise>> Watch(
        PowerSyncDatabase db, string profileId, string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = db.Watch<FlatSetRow>(SetsSql, [sessionId, profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
        await foreach (var batch in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return Group(batch);
        }
    }

    /// <summary>The profile's records, newest first; one exercise's when <paramref name="exerciseId"/> is given.</summary>
    public static async Task<PersonalRecordRow[]> RecordsAsync(IPowerSyncDatabase db, string profileId, string? exerciseId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<PersonalRecordRow>(
            "SELECT pr.id AS Id, pr.exercise_id AS ExerciseId, coalesce(e.name, '') AS ExerciseName, pr.kind AS Kind, pr.value AS Value, " +
            "pr.weight_kg AS WeightKg, pr.reps AS Reps, pr.e1rm_formula AS E1rmFormula, pr.set_log_id AS SetLogId, " +
            "pr.achieved_at AS AchievedAt, pr.local_date AS LocalDate " +
            "FROM personal_records pr LEFT JOIN exercises e ON e.id = pr.exercise_id " +
            "WHERE pr.profile_id = ? AND (? IS NULL OR pr.exercise_id = ?) ORDER BY pr.achieved_at DESC, pr.id",
            [profileId, exerciseId, exerciseId]).ConfigureAwait(false);
    }

    /// <summary>An exercise's best e1RM per day, oldest first (canonical kg).</summary>
    public static async Task<E1rmPoint[]> E1rmHistoryAsync(IPowerSyncDatabase db, string profileId, string exerciseId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<E1rmPoint>(
            "SELECT ws.local_date AS LocalDate, max(s.e1rm_kg) AS E1rmKg FROM set_logs s " +
            "JOIN session_exercises se ON se.id = s.session_exercise_id JOIN workout_sessions ws ON ws.id = se.session_id " +
            "WHERE s.profile_id = ? AND se.exercise_id = ? AND s.e1rm_kg IS NOT NULL GROUP BY ws.local_date ORDER BY ws.local_date",
            [profileId, exerciseId]).ConfigureAwait(false);
    }

    /// <summary>
    /// The e1RM <c>private.e1rm_kg</c> and the <c>set_logs</c> trigger compute: only for a completed set that isn't a
    /// warm-up, from canonical kg; null when the set gives none (no load, no reps, over 10 reps).
    /// </summary>
    public static double? E1rmFor(string setType, bool completed, double? weightKg, int? reps, OneRepMaxFormula formula) =>
        completed && setType != "warmup" && weightKg is { } kg && double.IsFinite(kg) && kg > 0 && reps is >= 1
            ? OneRepMax.Estimate(kg, reps.Value, formula)
            : null;

    /// <summary>
    /// The <c>personal_records</c> row id for a set's record of one kind: a UUIDv8 derived from the set and the kind, like
    /// <see cref="MetricPins.IdFor"/>, so the same record re-evaluated on two devices offline ends as one row (the
    /// upload is an upsert by id), never two.
    /// </summary>
    public static string RecordIdFor(string setId, RecordKind kind) =>
        Domain.Ids.UuidV8.FromName($"health360/personal_records/{Guid.Parse(setId):D}/{KindCode(kind)}").ToString("D");

    /// <summary>The <c>personal_records.kind</c> code of a record.</summary>
    public static string KindCode(RecordKind kind) => kind switch
    {
        RecordKind.E1rm => "e1rm",
        RecordKind.Weight => "weight",
        RecordKind.Reps => "reps",
        RecordKind.Volume => "volume",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown record kind"),
    };

    private static string FormulaCode(OneRepMaxFormula formula) => formula switch
    {
        OneRepMaxFormula.Epley => "epley",
        OneRepMaxFormula.Brzycki => "brzycki",
        _ => throw new ArgumentOutOfRangeException(nameof(formula), formula, "Unknown formula"),
    };

    /// <summary>Marks the set completed at <paramref name="completedAt"/>, and recomputes its e1RM and records.</summary>
    private static async Task<IReadOnlyList<NewRecord>> EvaluateAsync(
        ITransaction tx, string profileId, string setId, string completedAt, OneRepMaxFormula formula, TimeProvider clock)
    {
        var set = await tx.Get<SetForRecords>(
            "SELECT s.set_type AS SetType, s.reps AS Reps, s.weight_kg AS WeightKg, se.exercise_id AS ExerciseId, ws.local_date AS LocalDate " +
            "FROM set_logs s JOIN session_exercises se ON se.id = s.session_exercise_id JOIN workout_sessions ws ON ws.id = se.session_id " +
            "WHERE s.id = ? AND s.profile_id = ?",
            [setId, profileId]).ConfigureAwait(false);
        var reps = set.Reps is { } r ? (int)r : (int?)null;
        var e1rm = E1rmFor(set.SetType, completed: true, set.WeightKg, reps, formula);

        var held = await tx.GetAll<HeldRecord>(
            "SELECT kind AS Kind, value AS Value FROM personal_records WHERE set_log_id = ? AND profile_id = ?", [setId, profileId])
            .ConfigureAwait(false);
        await tx.Execute("DELETE FROM personal_records WHERE set_log_id = ? AND profile_id = ?", [setId, profileId]).ConfigureAwait(false);
        IReadOnlyList<NewRecord> records = [];
        if (set.SetType != "warmup" && set.WeightKg is { } kg && reps is { } count)
        {
            // Only sets completed before this one: a record is against what came before it, so editing an older set
            // never takes away the record it set at the time because of a later, better set.
            var earlier = await tx.GetAll<SetForRecords>(
                "SELECT s.set_type AS SetType, s.reps AS Reps, s.weight_kg AS WeightKg FROM set_logs s " +
                "JOIN session_exercises se ON se.id = s.session_exercise_id " +
                "WHERE s.profile_id = ? AND se.exercise_id = ? AND s.id <> ? AND s.completed_at IS NOT NULL AND s.set_type <> 'warmup' " +
                $"AND s.weight_kg > 0 AND s.reps >= 1 AND ({Instant("s.completed_at")} < (SELECT {Instant("t")} FROM (SELECT ? AS t)) " +
                $"OR ({Instant("s.completed_at")} = (SELECT {Instant("t")} FROM (SELECT ? AS t)) AND s.id < ?))",
                [profileId, set.ExerciseId, setId, completedAt, completedAt, setId]).ConfigureAwait(false);
            // Earlier estimates are recomputed with this formula, so a record never comes from comparing two formulas.
            var lifted = earlier.Select(s => new LiftedSet(s.WeightKg!.Value, (int)s.Reps!.Value, E1rmFor("working", true, s.WeightKg, (int)s.Reps!.Value, formula)))
                .ToList();
            records = PersonalRecords.Detect(lifted, new LiftedSet(kg, count, e1rm));
        }

        var now = Timestamps.UtcNow(clock);
        foreach (var record in records)
        {
            await tx.Execute(
                "INSERT INTO personal_records (id, profile_id, exercise_id, kind, value, weight_kg, reps, e1rm_formula, set_log_id, " +
                "achieved_at, local_date, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                [
                    RecordIdFor(setId, record.Kind), profileId, set.ExerciseId, KindCode(record.Kind), record.Value, set.WeightKg, reps,
                    record.Kind == RecordKind.E1rm ? FormulaCode(formula) : null, setId, completedAt, set.LocalDate, now, now,
                ]).ConfigureAwait(false);
        }

        await tx.Execute(
            "UPDATE set_logs SET completed_at = ?, e1rm_formula = ?, e1rm_kg = ?, is_pr = ?, updated_at = ? WHERE id = ? AND profile_id = ?",
            [completedAt, e1rm is null ? null : FormulaCode(formula), e1rm, records.Count > 0 ? 1 : 0, now, setId, profileId]).ConfigureAwait(false);

        // What's new: a record the set already held with the same value isn't news when the set is edited.
        return [.. records.Where(r => !held.Any(h => h.Kind == KindCode(r.Kind) && Quantity.CanonicalEquals(h.Value, r.Value)))];
    }

    /// <summary>
    /// What defines a done set: its reps for a rep-based exercise (weight and reps, or reps only), its time for a timed
    /// one, and its distance or time for distance and time. A done set without it would say nothing, and once the
    /// workout is finished it could no longer be corrected.
    /// </summary>
    private static void RequireMeasure(string? trackingType, long? reps, double? durationSeconds, double? distance)
    {
        if (trackingType is null)
        {
            // The exercise hasn't synced to this device, so what the set needs is unknown: don't let it through blank.
            throw new InvalidOperationException("This exercise hasn't synced to this device yet. Try again once it has.");
        }

        if (trackingType is "weight_reps" or "reps" && reps is null)
        {
            throw new InvalidOperationException("Enter the reps first.");
        }

        if (trackingType == "duration" && durationSeconds is null)
        {
            throw new InvalidOperationException("Enter the time first.");
        }

        if (trackingType == "distance_duration" && durationSeconds is null && distance is null)
        {
            throw new InvalidOperationException("Enter the distance or the time first.");
        }
    }

    /// <summary>
    /// SQL for a timestamp as an instant (Julian day), so times written here (<c>2026-11-03T18:10:00.000Z</c>) and synced
    /// from Postgres (<c>2026-11-03 18:20:00+00</c>) compare by when they happened, not as text. SQLite reads both once an
    /// hour-only offset is given its minutes.
    /// </summary>
    internal static string Instant(string expression) =>
        $"julianday(CASE WHEN {expression} GLOB '*[+-][0-9][0-9]' THEN {expression} || ':00' ELSE {expression} END)";

    private static async Task<string> AddExerciseAsync(
        ITransaction tx,
        string profileId,
        string sessionId,
        string exerciseId,
        int position,
        SetScheme? scheme,
        ExerciseSuggestion? suggestion,
        string loadUnit,
        TimeProvider clock,
        bool fromSchemeFirst = false)
    {
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await tx.Execute(
            "INSERT INTO session_exercises (id, profile_id, session_id, exercise_id, position, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
            [id, profileId, sessionId, exerciseId, position, now, now]).ConfigureAwait(false);

        // Last time's completed sets, as entered, from the newest other session that has any.
        var last = await tx.GetAll<SetValues>(
            "SELECT s.position AS Position, s.set_type AS SetType, s.reps AS Reps, s.weight AS Weight, s.weight_unit AS WeightUnit, " +
            "s.duration_seconds AS DurationSeconds, s.distance AS Distance, s.distance_unit AS DistanceUnit " +
            "FROM set_logs s WHERE s.profile_id = ? AND s.completed_at IS NOT NULL AND s.session_exercise_id = (" +
            "SELECT se.id FROM session_exercises se JOIN workout_sessions ws ON ws.id = se.session_id " +
            "WHERE se.profile_id = ? AND se.exercise_id = ? AND se.session_id <> ? " +
            "AND EXISTS (SELECT 1 FROM set_logs x WHERE x.session_exercise_id = se.id AND x.completed_at IS NOT NULL) " +
            "ORDER BY ws.started_at DESC, se.position DESC LIMIT 1) ORDER BY s.position, s.id",
            [profileId, profileId, exerciseId, sessionId]).ConfigureAwait(false);

        // With a progression rule: last time's warm-ups as entered, then the work sets at the suggested load.
        IEnumerable<SetValues> planned = suggestion is not null
            ? last.Where(s => s.SetType == "warmup").Concat(Enumerable.Range(0, suggestion.Sets).Select(i => new SetValues
            {
                SetType = "working",
                Reps = suggestion.PlannedReps(i),
                Weight = suggestion.Load,
                WeightUnit = suggestion.Unit,
            }))
            : last.Length > 0 && !fromSchemeFirst
            ? last
            : scheme is not null
                // A row with a rule but no load to suggest yet keeps last time's warm-ups before its scheme's work sets.
                ? last.Where(s => fromSchemeFirst && s.SetType == "warmup").Concat(Enumerable.Range(0, scheme.Sets).Select(_ => new SetValues
                {
                    SetType = "working",
                    Reps = scheme.MinReps,
                    Weight = scheme.Load,
                    WeightUnit = scheme.LoadUnit ?? loadUnit,
                }))
                : [new SetValues { SetType = "working", WeightUnit = loadUnit }];
        var i = 0;
        foreach (var values in planned)
        {
            await InsertSetAsync(tx, ExerciseLibrary.NewId(clock), profileId, id, i++, values, clock).ConfigureAwait(false);
        }

        return id;
    }

    private static async Task InsertSetAsync(ITransaction tx, string id, string profileId, string sessionExerciseId, int position, SetValues values, TimeProvider clock)
    {
        var now = Timestamps.UtcNow(clock);
        var unit = values.Weight is null ? null : values.WeightUnit;
        double? kg = values.Weight is { } w && unit is not null ? UnitCodes.Convert(w, unit, "kg") : null;
        var distanceUnit = values.Distance is null ? null : values.DistanceUnit;
        double? metres = values.Distance is { } d && distanceUnit is not null ? UnitCodes.Convert(d, distanceUnit, "m") : null;
        await tx.Execute(
            "INSERT INTO set_logs (id, profile_id, session_exercise_id, position, set_type, reps, weight, weight_unit, weight_kg, " +
            "duration_seconds, distance, distance_unit, distance_m, is_pr, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, ?)",
            [id, profileId, sessionExerciseId, position, values.SetType, values.Reps, values.Weight, unit, kg, values.DurationSeconds,
             values.Distance, distanceUnit, metres, now, now]).ConfigureAwait(false);
    }

    private static async Task RequireOpenSessionAsync(ITransaction tx, string profileId, string sessionId)
    {
        var session = await tx.GetOptional<SessionState>(
            "SELECT ended_at AS EndedAt FROM workout_sessions WHERE id = ? AND profile_id = ?", [sessionId, profileId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The workout is not in the local database.");
        if (session.EndedAt is not null)
        {
            throw new InvalidOperationException("The workout has ended.");
        }
    }

    /// <summary>The set, which must be in a workout still in progress: a finished workout's sets and records stay as logged.</summary>
    private static async Task<SetState> RequireSetAsync(ITransaction tx, string profileId, string setId)
    {
        var set = await tx.GetOptional<SetState>(
            "SELECT s.completed_at AS CompletedAt, s.reps AS Reps, s.duration_seconds AS DurationSeconds, s.distance AS Distance, " +
            "e.tracking_type AS TrackingType, ws.ended_at AS EndedAt FROM set_logs s " +
            "JOIN session_exercises se ON se.id = s.session_exercise_id JOIN workout_sessions ws ON ws.id = se.session_id " +
            "LEFT JOIN exercises e ON e.id = se.exercise_id WHERE s.id = ? AND s.profile_id = ?",
            [setId, profileId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The set is not in the local database.");
        return set.EndedAt is null ? set : throw new InvalidOperationException("The workout has ended.");
    }

    private static void CheckLoadUnit(string loadUnit)
    {
        if (!WorkoutCodes.LoadUnits.Contains(loadUnit))
        {
            throw new ArgumentException("kg or lb.", nameof(loadUnit));
        }
    }

    private static List<LoggedExercise> Group(FlatSetRow[] rows)
    {
        var exercises = new List<LoggedExercise>();
        foreach (var group in rows.GroupBy(r => r.SessionExerciseId))
        {
            var first = group.First();
            var sets = group.Where(r => r.SetId is not null).Select(r => new LoggedSet(
                r.SetId!,
                (int)(r.SetPosition ?? 0),
                r.SetType ?? "working",
                r.Reps is { } reps ? (int)reps : null,
                r.Weight,
                r.WeightUnit,
                r.WeightKg,
                r.Rpe,
                r.Rir is { } rir ? (int)rir : null,
                r.CompletedAt is not null,
                r.E1rmKg,
                r.IsPr is 1,
                r.DurationSeconds,
                r.Distance,
                r.DistanceUnit)).ToList();
            exercises.Add(new LoggedExercise(
                first.SessionExerciseId, first.ExerciseId, first.ExerciseName ?? "Exercise", first.TrackingType ?? "weight_reps",
                (int)first.ExercisePosition, sets));
        }

        return exercises;
    }

    /// <summary>A summary of the Train tab's tables; only its changing matters.</summary>
    public sealed class ChangeStamp
    {
        public string? Stamp { get; set; }
    }

    internal sealed class FlatSetRow
    {
        public string SessionExerciseId { get; set; } = "";

        public string ExerciseId { get; set; } = "";

        public string? ExerciseName { get; set; }

        public string? TrackingType { get; set; }

        public long ExercisePosition { get; set; }

        public string? SetId { get; set; }

        public long? SetPosition { get; set; }

        public string? SetType { get; set; }

        public long? Reps { get; set; }

        public double? Weight { get; set; }

        public string? WeightUnit { get; set; }

        public double? WeightKg { get; set; }

        public double? Rpe { get; set; }

        public long? Rir { get; set; }

        public string? CompletedAt { get; set; }

        public double? E1rmKg { get; set; }

        public long? IsPr { get; set; }

        public double? DurationSeconds { get; set; }

        public double? Distance { get; set; }

        public string? DistanceUnit { get; set; }
    }

    internal sealed class SetValues
    {
        public long Position { get; set; }

        public string SetType { get; set; } = "working";

        public long? Reps { get; set; }

        public double? Weight { get; set; }

        public string? WeightUnit { get; set; }

        public double? DurationSeconds { get; set; }

        public double? Distance { get; set; }

        public string? DistanceUnit { get; set; }
    }

    internal sealed class SetForRecords
    {
        public string SetType { get; set; } = "";

        public long? Reps { get; set; }

        public double? WeightKg { get; set; }

        public string ExerciseId { get; set; } = "";

        public string LocalDate { get; set; } = "";
    }

    internal sealed class StoredSet
    {
        public string SetType { get; set; } = "working";

        public long? Reps { get; set; }

        public double? Weight { get; set; }

        public string? WeightUnit { get; set; }

        public double? DurationSeconds { get; set; }

        public double? Distance { get; set; }

        public string? DistanceUnit { get; set; }

        public double? Rpe { get; set; }

        public long? Rir { get; set; }

        public string? CompletedAt { get; set; }
    }

    internal sealed class HeldRecord
    {
        public string Kind { get; set; } = "";

        public double Value { get; set; }
    }

    internal sealed class SetState
    {
        public string? CompletedAt { get; set; }

        public long? Reps { get; set; }

        public double? DurationSeconds { get; set; }

        public double? Distance { get; set; }

        public string? TrackingType { get; set; }

        public string? EndedAt { get; set; }
    }

    internal sealed class SessionState
    {
        public string? EndedAt { get; set; }
    }

    internal sealed class SessionOf
    {
        public string SessionId { get; set; } = "";
    }

    internal sealed class Count
    {
        public long N { get; set; }
    }
}
