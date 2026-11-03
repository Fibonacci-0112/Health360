using Health360.Contracts.Training;
using Health360.Domain.Training;
using Health360.Domain.Units;
using PowerSync.Common.Client;
using PowerSync.Common.DB;

namespace Health360.LocalDb;

/// <summary>
/// The load a template exercise with a progression rule is planned at, in the rule's unit, and why.
/// <see cref="Action"/> is null when no earlier session counts yet and the template's starting load is used.
/// <see cref="LastReps"/> are the last counted session's work-set reps, in order.
/// </summary>
public sealed record ExerciseSuggestion(
    double Load,
    string Unit,
    int Sets,
    int MinReps,
    int MaxReps,
    ProgressionAction? Action,
    int ConsecutiveFailures,
    ProgressionRule Rule,
    IReadOnlyList<int> LastReps)
{
    /// <summary>The reps planned for work set <paramref name="index"/>: last time's again while repeating a load, else the bottom of the range.</summary>
    public int PlannedReps(int index) =>
        Action == ProgressionAction.Repeat && index < LastReps.Count ? Math.Clamp(LastReps[index], MinReps, MaxReps) : MinReps;
}

/// <summary>
/// Progression suggestions from the workout history (docs/calculations.md, Progression). A template exercise's
/// earlier sessions are those of the same template, finished and started before the session asked about; their
/// completed work sets are converted to the rule's unit and replayed by <see cref="Progression.Replay"/>, so the
/// miss streak comes from the history and two devices agree once they have synced.
/// </summary>
public static class WorkoutProgression
{
    /// <summary>
    /// The suggestion for each exercise of a session in progress that was started from a template, by
    /// <c>session_exercises</c> id. Each session exercise is matched to the template's exercises in order by
    /// exercise; one without a rule, or added by hand, has none. The suggestion is worked out again from the
    /// template and history as they are now, so it is left out unless one of the exercise's work sets is still at
    /// its load: a template edited, or history synced, since the start can't put a note over sets it didn't plan.
    /// A finished session has none.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, ExerciseSuggestion>> ForSessionAsync(
        IPowerSyncDatabase db, string profileId, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        return await db.ReadTransaction(async tx =>
        {
            var suggestions = new Dictionary<string, ExerciseSuggestion>(StringComparer.Ordinal);
            var session = await tx.GetOptional<SessionTemplate>(
                "SELECT template_id AS TemplateId, started_at AS StartedAt, ended_at AS EndedAt FROM workout_sessions WHERE id = ? AND profile_id = ?",
                [sessionId, profileId]).ConfigureAwait(false);
            if (session?.TemplateId is not { } templateId || session.EndedAt is not null)
            {
                return suggestions;
            }

            var planned = await tx.GetAll<TemplateExerciseRow>(WorkoutPlans.TemplateExercisesSql, [templateId, profileId]).ConfigureAwait(false);
            var used = new bool[planned.Length];
            var logged = await tx.GetAll<SessionExercise>(
                "SELECT id AS Id, exercise_id AS ExerciseId FROM session_exercises WHERE session_id = ? AND profile_id = ? ORDER BY position, id",
                [sessionId, profileId]).ConfigureAwait(false);
            var loads = (await tx.GetAll<PlannedLoad>(
                "SELECT s.session_exercise_id AS SessionExerciseId, s.weight AS Weight, s.weight_unit AS WeightUnit FROM set_logs s " +
                "JOIN session_exercises se ON se.id = s.session_exercise_id " +
                "WHERE se.session_id = ? AND s.profile_id = ? AND s.set_type = 'working' AND s.weight IS NOT NULL AND s.weight_unit IS NOT NULL",
                [sessionId, profileId]).ConfigureAwait(false)).ToLookup(l => l.SessionExerciseId, StringComparer.Ordinal);
            foreach (var exercise in logged)
            {
                var index = -1;
                for (var i = 0; i < planned.Length && index < 0; i++)
                {
                    index = !used[i] && planned[i].ExerciseId == exercise.ExerciseId ? i : -1;
                }

                if (index < 0)
                {
                    continue;
                }

                used[index] = true;
                var occurrence = Occurrence(planned, index);
                if (await SuggestAsync(tx, profileId, templateId, planned[index], occurrence, session.StartedAt).ConfigureAwait(false) is { } suggestion
                    && loads[exercise.Id].Any(l => l.WeightUnit == suggestion.Unit && Quantity.CanonicalEquals(l.Weight, suggestion.Load)))
                {
                    suggestions[exercise.Id] = suggestion;
                }
            }

            return suggestions;
        }).ConfigureAwait(false);
    }

    /// <summary>Which entry of its exercise a template row is: 0 for the first, 1 for a second row of the same exercise, …</summary>
    internal static int Occurrence(IReadOnlyList<TemplateExerciseRow> template, int index) =>
        template.Take(index).Count(e => e.ExerciseId == template[index].ExerciseId);

    /// <summary>
    /// The suggestion for one template exercise from sessions started before <paramref name="before"/>; null when it
    /// has no rule, isn't counted in weight and reps, or has neither history nor a starting load. A template may list
    /// an exercise more than once (heavy sets, then back-off sets); <paramref name="occurrence"/> says which entry
    /// this is, and each past session contributes the same entry of that exercise, by position.
    /// </summary>
    internal static async Task<ExerciseSuggestion?> SuggestAsync(
        IDBGetUtils tx, string profileId, string templateId, TemplateExerciseRow exercise, int occurrence, string before)
    {
        if (exercise.Rule is not { } rule || exercise.Scheme is not { } scheme || exercise.TrackingType != "weight_reps")
        {
            return null;
        }

        // The template's finished sessions with this exercise, then every entry of the exercise in them with its
        // counted sets (none, through the outer join, for an entry with only warm-ups or unticked sets). All of them:
        // the replay starts from the template's starting load, so leaving out older sessions would change the result.
        var started = WorkoutLog.Instant("w.started_at");
        var rows = await tx.GetAll<HistorySet>(
            "SELECT ws.id AS SessionId, se.id AS SessionExerciseId, s.weight AS Weight, s.weight_unit AS WeightUnit, s.reps AS Reps " +
            "FROM workout_sessions ws JOIN session_exercises se ON se.session_id = ws.id AND se.profile_id = ws.profile_id AND se.exercise_id = ? " +
            "LEFT JOIN set_logs s ON s.session_exercise_id = se.id AND s.profile_id = ws.profile_id AND s.completed_at IS NOT NULL " +
            "AND s.set_type = 'working' AND s.weight IS NOT NULL AND s.weight_unit IS NOT NULL AND s.reps IS NOT NULL " +
            "WHERE ws.id IN (SELECT w.id FROM workout_sessions w WHERE w.profile_id = ? AND w.template_id = ? AND w.ended_at IS NOT NULL " +
            $"AND {started} < {WorkoutLog.Instant("?")} " +
            "AND EXISTS (SELECT 1 FROM session_exercises x WHERE x.session_id = w.id AND x.exercise_id = ?)) " +
            $"ORDER BY {WorkoutLog.Instant("ws.started_at")}, ws.id, se.position, se.id, s.position, s.id",
            // Instant() repeats its expression three times, so the bound comes three times.
            [exercise.ExerciseId, profileId, templateId, before, before, before, exercise.ExerciseId]).ConfigureAwait(false);

        var sessions = new List<List<WorkSet>>();
        string? sessionId = null;
        string? sessionExerciseId = null;
        var entry = -1;
        foreach (var row in rows)
        {
            if (row.SessionId != sessionId)
            {
                (sessionId, sessionExerciseId, entry) = (row.SessionId, null, -1);
                sessions.Add([]);
            }

            if (row.SessionExerciseId != sessionExerciseId)
            {
                (sessionExerciseId, entry) = (row.SessionExerciseId, entry + 1);
            }

            if (entry == occurrence && row is { Weight: { } weight, WeightUnit: { } unit, Reps: { } reps }
                && UnitCodes.Convert(weight, unit, rule.IncrementUnit) is { } load)
            {
                sessions[^1].Add(new WorkSet(load, (int)reps));
            }
        }

        var kind = rule.Kind == "linear" ? ProgressionKind.LinearProgression : ProgressionKind.DoubleProgression;
        double? start = scheme.Load is { } l && scheme.LoadUnit is { } u ? UnitCodes.Convert(l, u, rule.IncrementUnit) : null;
        var (next, lastCounted) = Progression.ReplayCounted(
            kind, scheme.MinReps, scheme.MaxReps, rule.Increment, start, [.. sessions], new DeloadPolicy(rule.DeloadAfter));
        var minReps = kind == ProgressionKind.LinearProgression ? scheme.MaxReps : scheme.MinReps;
        if (next is not null)
        {
            return new ExerciseSuggestion(
                next.Load, rule.IncrementUnit, scheme.Sets, next.MinReps, next.MaxReps, next.Action, next.ConsecutiveFailures, rule,
                [.. sessions[lastCounted].Select(s => s.Reps)]);
        }

        return start is > 0 ? new ExerciseSuggestion(start.Value, rule.IncrementUnit, scheme.Sets, minReps, scheme.MaxReps, null, 0, rule, []) : null;
    }

    internal sealed class SessionTemplate
    {
        public string? TemplateId { get; set; }

        public string StartedAt { get; set; } = "";

        public string? EndedAt { get; set; }
    }

    internal sealed class PlannedLoad
    {
        public string SessionExerciseId { get; set; } = "";

        public double Weight { get; set; }

        public string WeightUnit { get; set; } = "";
    }

    internal sealed class SessionExercise
    {
        public string Id { get; set; } = "";

        public string ExerciseId { get; set; } = "";
    }

    internal sealed class HistorySet
    {
        public string SessionId { get; set; } = "";

        public string SessionExerciseId { get; set; } = "";

        public double? Weight { get; set; }

        public string? WeightUnit { get; set; }

        public long? Reps { get; set; }
    }
}
