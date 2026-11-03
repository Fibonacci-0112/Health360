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
    /// <c>session_exercises</c> id. Each session exercise is matched to the template row it was planned from
    /// (<c>template_exercise_id</c>); one without a rule, or added by hand, has none. The suggestion is worked out again from the
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
                "SELECT id AS Id, exercise_id AS ExerciseId, template_exercise_id AS TemplateExerciseId FROM session_exercises " +
                "WHERE session_id = ? AND profile_id = ? ORDER BY position, id",
                [sessionId, profileId]).ConfigureAwait(false);
            var loads = (await tx.GetAll<PlannedLoad>(
                "SELECT s.session_exercise_id AS SessionExerciseId, s.weight AS Weight, s.weight_unit AS WeightUnit FROM set_logs s " +
                "JOIN session_exercises se ON se.id = s.session_exercise_id " +
                "WHERE se.session_id = ? AND s.profile_id = ? AND s.set_type = 'working' AND s.weight IS NOT NULL AND s.weight_unit IS NOT NULL",
                [sessionId, profileId]).ConfigureAwait(false)).ToLookup(l => l.SessionExerciseId, StringComparer.Ordinal);
            var rows = await RowIdsAsync(tx, profileId, templateId).ConfigureAwait(false);

            // Entries linked to a row that still names their exercise claim it first; only then may an entry with no
            // live link (none, or one to a row removed from the template) take a row, and only when the template
            // lists its exercise once.
            var matches = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var exercise in logged)
            {
                var linked = exercise.TemplateExerciseId is { } link ? Array.FindIndex(planned, p => p.Id == link) : -1;
                if (linked >= 0 && planned[linked].ExerciseId == exercise.ExerciseId && !used[linked])
                {
                    (used[linked], matches[exercise.Id]) = (true, linked);
                }
            }

            foreach (var exercise in logged)
            {
                // A link to a row still in the template (whether or not its exercise has synced here) is never a fallback.
                if (exercise.TemplateExerciseId is { } link && rows.Contains(link))
                {
                    continue;
                }

                var index = Unambiguous(planned, exercise.ExerciseId) ? Array.FindIndex(planned, p => p.ExerciseId == exercise.ExerciseId) : -1;
                if (index >= 0 && !used[index])
                {
                    (used[index], matches[exercise.Id]) = (true, index);
                }
            }

            foreach (var exercise in logged)
            {
                if (matches.TryGetValue(exercise.Id, out var index)
                    && await SuggestAsync(tx, profileId, templateId, planned, index, session.StartedAt).ConfigureAwait(false) is { } suggestion
                    && loads[exercise.Id].Any(l => AtLoad(suggestion, l.Weight, l.WeightUnit)))
                {
                    suggestions[exercise.Id] = suggestion;
                }
            }

            return suggestions;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a suggestion still describes an exercise as logged now: one of its work sets is at the suggested load
    /// and unit. The logger checks each update with it, so a note goes once every planned set has been changed.
    /// </summary>
    public static bool StillPlanned(ExerciseSuggestion suggestion, LoggedExercise exercise)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentNullException.ThrowIfNull(exercise);
        return exercise.Sets.Any(s => s.SetType == "working" && s.Weight is { } w && s.WeightUnit is { } u && AtLoad(suggestion, w, u));
    }

    private static bool AtLoad(ExerciseSuggestion suggestion, double weight, string unit) =>
        unit == suggestion.Unit && Quantity.CanonicalEquals(weight, suggestion.Load);

    /// <summary>
    /// Every row of the template, including those whose exercise hasn't synced here (which
    /// <see cref="WorkoutPlans.TemplateExercisesSql"/> leaves out), so such a row is never taken for one removed.
    /// </summary>
    private static async Task<HashSet<string>> RowIdsAsync(IDBGetUtils tx, string profileId, string templateId) =>
        (await tx.GetAll<WorkoutPlans.IdOnly>(
            "SELECT id AS Id FROM template_exercises WHERE template_id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false))
            .Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a template lists the exercise only once.</summary>
    internal static bool Unambiguous(IReadOnlyList<TemplateExerciseRow> template, string exerciseId) =>
        template.Count(e => e.ExerciseId == exerciseId) == 1;

    /// <summary>
    /// The suggestion for one template exercise from sessions started before <paramref name="before"/>; null when it
    /// has no rule, isn't counted in weight and reps, or has neither history nor a starting load. Each past session
    /// contributes its entry planned from this template row (<c>session_exercises.template_exercise_id</c>), so a
    /// template that lists an exercise twice (heavy sets, then back-off sets) keeps two histories however its rows
    /// are reordered. A session without such an entry contributes its first entry of the exercise with no live link
    /// (none, or to a row since removed, as after removing an exercise and adding it back) only when the template
    /// lists the exercise once; otherwise nothing.
    /// </summary>
    internal static async Task<ExerciseSuggestion?> SuggestAsync(
        IDBGetUtils tx, string profileId, string templateId, IReadOnlyList<TemplateExerciseRow> template, int index, string before)
    {
        var exercise = template[index];
        var unambiguous = Unambiguous(template, exercise.ExerciseId);
        if (exercise.Rule is not { } rule || exercise.Scheme is not { } scheme || exercise.TrackingType != "weight_reps")
        {
            return null;
        }

        // Read only for a row that progresses, and only when an unlinked entry could stand in for it.
        var live = unambiguous ? await RowIdsAsync(tx, profileId, templateId).ConfigureAwait(false) : [];

        // The template's finished sessions with this exercise, then every entry of the exercise in them with its
        // counted sets (none, through the outer join, for an entry with only warm-ups or unticked sets). All of them:
        // the replay starts from the template's starting load, so leaving out older sessions would change the result.
        var started = WorkoutLog.Instant("w.started_at");
        var rows = await tx.GetAll<HistorySet>(
            "SELECT ws.id AS SessionId, se.id AS SessionExerciseId, se.template_exercise_id AS TemplateExerciseId, s.weight AS Weight, s.weight_unit AS WeightUnit, s.reps AS Reps " +
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
        foreach (var session in rows.GroupBy(r => r.SessionId, StringComparer.Ordinal))
        {
            // The rows come in replay order, so groups do too; within one, entries come by position.
            var entries = session.GroupBy(r => r.SessionExerciseId, StringComparer.Ordinal).ToList();
            var entry = entries.FirstOrDefault(e => e.First().TemplateExerciseId == exercise.Id)
                ?? (unambiguous ? entries.FirstOrDefault(e => e.First().TemplateExerciseId is not { } l || !live.Contains(l)) : null);
            sessions.Add([.. (entry ?? Enumerable.Empty<HistorySet>())
                .Where(r => r.Weight is not null && r.WeightUnit is not null && r.Reps is not null)
                .Select(r => (Load: UnitCodes.Convert(r.Weight!.Value, r.WeightUnit!, rule.IncrementUnit), Reps: (int)r.Reps!.Value))
                .Where(r => r.Load is not null)
                .Select(r => new WorkSet(r.Load!.Value, r.Reps))]);
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

        public string? TemplateExerciseId { get; set; }
    }

    internal sealed class HistorySet
    {
        public string SessionId { get; set; } = "";

        public string SessionExerciseId { get; set; } = "";

        public string? TemplateExerciseId { get; set; }

        public double? Weight { get; set; }

        public string? WeightUnit { get; set; }

        public long? Reps { get; set; }
    }
}
