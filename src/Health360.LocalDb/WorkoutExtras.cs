using PowerSync.Common.Client;

namespace Health360.LocalDb;

public sealed class MuscleVolumeRow
{
    public string Muscle { get; set; } = "";
    public long Sets { get; set; }
    public double VolumeKg { get; set; }
}

public static partial class WorkoutLog
{
    /// <summary>Completed non-warm-up sets for primary muscles only; each muscle gets the full set, never a guessed fraction.</summary>
    public static Task<MuscleVolumeRow[]> MuscleVolumeAsync(IPowerSyncDatabase db, string profileId, DateOnly from, DateOnly to) =>
        db.GetAll<MuscleVolumeRow>("SELECT muscle.value AS Muscle, count(*) AS Sets, sum(coalesce(s.weight_kg * s.reps, 0)) AS VolumeKg " +
            "FROM set_logs s JOIN session_exercises se ON se.id = s.session_exercise_id AND se.profile_id = s.profile_id " +
            "JOIN workout_sessions ws ON ws.id = se.session_id AND ws.profile_id = s.profile_id " +
            "JOIN exercises e ON e.id = se.exercise_id, json_each(CASE WHEN json_valid(e.primary_muscles) THEN e.primary_muscles ELSE '[]' END) muscle " +
            "WHERE s.profile_id = ? AND s.completed_at IS NOT NULL AND s.set_type <> 'warmup' AND ws.ended_at IS NOT NULL " +
            "AND ws.local_date BETWEEN ? AND ? GROUP BY muscle.value ORDER BY Sets DESC, Muscle",
            [profileId, WorkoutPlans.Day(from), WorkoutPlans.Day(to)]);

    /// <summary>
    /// Swapping never re-labels a completed set or its records. Unfinished sets are replaced using the new exercise's own history.
    /// The replacement keeps the slot's template link, so its superset group and rest stay as planned; progression ignores a link
    /// to a row that names another exercise. Swapping an exercise for itself changes nothing.
    /// </summary>
    public static Task SwapExerciseAsync(IPowerSyncDatabase db, string profileId, string sessionExerciseId, string replacementId, string loadUnit, TimeProvider clock) =>
        db.WriteTransaction(async tx =>
        {
            var row = await tx.GetOptional<SwapRow>(
                "SELECT session_id AS SessionId, exercise_id AS ExerciseId, position AS Position, template_exercise_id AS TemplateExerciseId " +
                "FROM session_exercises WHERE id = ? AND profile_id = ?",
                [sessionExerciseId, profileId]).ConfigureAwait(false) ?? throw new InvalidOperationException("The exercise is no longer on this device.");
            await RequireOpenSessionAsync(tx, profileId, row.SessionId).ConfigureAwait(false);
            if (row.ExerciseId == replacementId)
            {
                return;
            }

            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM exercises WHERE id = ?", [replacementId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The replacement has not synced.");
            if ((await tx.GetAll<WorkoutPlans.IdOnly>("SELECT id AS Id FROM set_logs WHERE session_exercise_id = ? AND completed_at IS NOT NULL", [sessionExerciseId])
                .ConfigureAwait(false)).Length > 0)
            {
                throw new InvalidOperationException("Keep completed sets under their original exercise. Add the replacement as another exercise.");
            }

            await tx.Execute("DELETE FROM set_logs WHERE session_exercise_id = ? AND profile_id = ?", [sessionExerciseId, profileId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM session_exercises WHERE id = ? AND profile_id = ?", [sessionExerciseId, profileId]).ConfigureAwait(false);
            await AddExerciseAsync(tx, profileId, row.SessionId, replacementId, (int)row.Position, null, null, loadUnit, clock, templateExerciseId: row.TemplateExerciseId)
                .ConfigureAwait(false);
        });

    public static async Task<int> RestSecondsAsync(IPowerSyncDatabase db, string profileId, string sessionExerciseId) =>
        (int)((await db.GetOptional<RestRow>("SELECT te.rest_seconds AS Seconds FROM session_exercises se " +
            "LEFT JOIN template_exercises te ON te.id = se.template_exercise_id AND te.profile_id = se.profile_id " +
            "WHERE se.id = ? AND se.profile_id = ?", [sessionExerciseId, profileId]).ConfigureAwait(false))?.Seconds ?? 90);

    public sealed class RestRow { public long? Seconds { get; set; } }
    public sealed class SwapRow
    {
        public string SessionId { get; set; } = "";
        public string ExerciseId { get; set; } = "";
        public long Position { get; set; }
        public string? TemplateExerciseId { get; set; }
    }
}
