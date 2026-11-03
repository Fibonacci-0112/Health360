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
    /// <summary>How many of the latest sessions are replayed; older ones no longer change the load.</summary>
    public const int SessionsReplayed = 100;

    /// <summary>
    /// The suggestion for each exercise of a session started from a template, by <c>session_exercises</c> id. Each
    /// session exercise is matched to the template's exercises in order by exercise; one without a rule, or added by
    /// hand, has none.
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
                "SELECT template_id AS TemplateId, started_at AS StartedAt FROM workout_sessions WHERE id = ? AND profile_id = ?",
                [sessionId, profileId]).ConfigureAwait(false);
            if (session?.TemplateId is not { } templateId)
            {
                return suggestions;
            }

            var planned = (await tx.GetAll<TemplateExerciseRow>(WorkoutPlans.TemplateExercisesSql, [templateId, profileId]).ConfigureAwait(false)).ToList();
            var logged = await tx.GetAll<SessionExercise>(
                "SELECT id AS Id, exercise_id AS ExerciseId FROM session_exercises WHERE session_id = ? AND profile_id = ? ORDER BY position, id",
                [sessionId, profileId]).ConfigureAwait(false);
            foreach (var exercise in logged)
            {
                var index = planned.FindIndex(p => p.ExerciseId == exercise.ExerciseId);
                if (index < 0)
                {
                    continue;
                }

                var match = planned[index];
                planned.RemoveAt(index);
                if (await SuggestAsync(tx, profileId, templateId, match, session.StartedAt).ConfigureAwait(false) is { } suggestion)
                {
                    suggestions[exercise.Id] = suggestion;
                }
            }

            return suggestions;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The suggestion for one template exercise from sessions started before <paramref name="before"/>; null when it
    /// has no rule, isn't counted in weight and reps, or has neither history nor a starting load.
    /// </summary>
    internal static async Task<ExerciseSuggestion?> SuggestAsync(
        IDBGetUtils tx, string profileId, string templateId, TemplateExerciseRow exercise, string before)
    {
        if (exercise.Rule is not { } rule || exercise.Scheme is not { } scheme || exercise.TrackingType != "weight_reps")
        {
            return null;
        }

        var rows = await tx.GetAll<HistorySet>(
            "SELECT ws.id AS SessionId, se.id AS SessionExerciseId, s.weight AS Weight, s.weight_unit AS WeightUnit, s.reps AS Reps " +
            "FROM set_logs s JOIN session_exercises se ON se.id = s.session_exercise_id JOIN workout_sessions ws ON ws.id = se.session_id " +
            "WHERE s.profile_id = ? AND ws.profile_id = ? AND ws.template_id = ? AND se.exercise_id = ? AND ws.ended_at IS NOT NULL " +
            $"AND {WorkoutLog.Instant("ws.started_at")} < {WorkoutLog.Instant("?")} " +
            "AND s.completed_at IS NOT NULL AND s.set_type = 'working' AND s.weight IS NOT NULL AND s.weight_unit IS NOT NULL AND s.reps IS NOT NULL " +
            $"ORDER BY {WorkoutLog.Instant("ws.started_at")}, ws.id, se.position, se.id, s.position, s.id",
            // Instant() repeats its expression three times, so the bound comes three times.
            [profileId, profileId, templateId, exercise.ExerciseId, before, before, before]).ConfigureAwait(false);

        // One exercise per session: the first, if a session did it twice.
        var sessions = new List<List<WorkSet>>();
        string? sessionId = null;
        string? sessionExerciseId = null;
        foreach (var row in rows)
        {
            if (row.SessionId != sessionId)
            {
                (sessionId, sessionExerciseId) = (row.SessionId, row.SessionExerciseId);
                sessions.Add([]);
            }
            else if (row.SessionExerciseId != sessionExerciseId)
            {
                continue;
            }

            if (UnitCodes.Convert(row.Weight, row.WeightUnit, rule.IncrementUnit) is { } load)
            {
                sessions[^1].Add(new WorkSet(load, (int)row.Reps));
            }
        }

        var replayed = sessions.Where(s => s.Count > 0).TakeLast(SessionsReplayed).ToList();
        var kind = rule.Kind == "linear" ? ProgressionKind.LinearProgression : ProgressionKind.DoubleProgression;
        double? start = scheme.Load is { } l && scheme.LoadUnit is { } u ? UnitCodes.Convert(l, u, rule.IncrementUnit) : null;
        var next = Progression.Replay(
            kind, scheme.MinReps, scheme.MaxReps, rule.Increment, start, [.. replayed], new DeloadPolicy(rule.DeloadAfter));
        var minReps = kind == ProgressionKind.LinearProgression ? scheme.MaxReps : scheme.MinReps;
        if (next is not null)
        {
            return new ExerciseSuggestion(
                next.Load, rule.IncrementUnit, scheme.Sets, next.MinReps, next.MaxReps, next.Action, next.ConsecutiveFailures, rule,
                [.. replayed[^1].Select(s => s.Reps)]);
        }

        return start is > 0 ? new ExerciseSuggestion(start.Value, rule.IncrementUnit, scheme.Sets, minReps, scheme.MaxReps, null, 0, rule, []) : null;
    }

    internal sealed class SessionTemplate
    {
        public string? TemplateId { get; set; }

        public string StartedAt { get; set; } = "";
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

        public double Weight { get; set; }

        public string WeightUnit { get; set; } = "";

        public long Reps { get; set; }
    }
}
