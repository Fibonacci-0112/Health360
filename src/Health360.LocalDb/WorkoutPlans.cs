using System.Globalization;
using FluentValidation;
using Health360.Contracts.Training;
using PowerSync.Common.Client;
using PowerSync.Common.DB;

namespace Health360.LocalDb;

/// <summary>One <c>workout_templates</c> row, with how many exercises it has.</summary>
public sealed class TemplateRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public long ExerciseCount { get; set; }
}

/// <summary>One exercise of a template, with the exercise's name and tracking type.</summary>
public sealed class TemplateExerciseRow
{
    public string Id { get; set; } = "";

    public string ExerciseId { get; set; } = "";

    public string ExerciseName { get; set; } = "";

    public string TrackingType { get; set; } = "";

    public long Position { get; set; }

    /// <summary>JSON text; see <see cref="SetScheme"/>.</summary>
    public string? SetScheme { get; set; }

    public long? RestSeconds { get; set; }

    /// <summary>JSON text; see <see cref="ProgressionRule"/>.</summary>
    public string? Progression { get; set; }

    public SetScheme? Scheme => Contracts.Training.SetScheme.Parse(SetScheme);

    public ProgressionRule? Rule => ProgressionRule.Parse(Progression);
}

/// <summary>One <c>programs</c> row.</summary>
public sealed class ProgramRow
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string? LocationId { get; set; }

    public string StartsOn { get; set; } = "";

    public string? EndsOn { get; set; }
}

/// <summary>
/// A workout planned on a day: from a weekly program entry or a dated one. <see cref="SessionId"/> is the session that
/// did it: a dated entry's own, or for a weekly entry a session of its template on that day.
/// </summary>
public sealed record PlannedWorkout(
    DateOnly Date,
    string ScheduledId,
    string TemplateId,
    string TemplateName,
    string? ProgramId,
    string? SessionId);

/// <summary>
/// Templates, programs and the schedule (<c>workout_templates</c>, <c>template_exercises</c>, <c>programs</c>,
/// <c>scheduled_workouts</c>, all U) in the local database.
/// </summary>
public static partial class WorkoutPlans
{
    /// <summary>The profile's templates, by name.</summary>
    public static async Task<TemplateRow[]> TemplatesAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<TemplateRow>(
            "SELECT t.id AS Id, t.name AS Name, (SELECT count(*) FROM template_exercises e WHERE e.template_id = t.id) AS ExerciseCount " +
            "FROM workout_templates t WHERE t.profile_id = ? ORDER BY t.name COLLATE NOCASE, t.id",
            [profileId]).ConfigureAwait(false);
    }

    /// <summary>One of the profile's templates; null when it isn't on this device.</summary>
    public static async Task<TemplateRow?> TemplateAsync(IPowerSyncDatabase db, string profileId, string templateId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetOptional<TemplateRow>(
            "SELECT t.id AS Id, t.name AS Name, (SELECT count(*) FROM template_exercises e WHERE e.template_id = t.id) AS ExerciseCount " +
            "FROM workout_templates t WHERE t.id = ? AND t.profile_id = ?",
            [templateId, profileId]).ConfigureAwait(false);
    }

    /// <summary>
    /// A template's exercises in order, with each one's scheme, rest and progression rule. An exercise that hasn't
    /// synced yet is left out.
    /// </summary>
    public static async Task<TemplateExerciseRow[]> TemplateExercisesAsync(IPowerSyncDatabase db, string profileId, string templateId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<TemplateExerciseRow>(TemplateExercisesSql, [templateId, profileId]).ConfigureAwait(false);
    }

    /// <summary>A template's exercises (template id, then profile id), as <see cref="TemplateExercisesAsync"/> reads them.</summary>
    internal const string TemplateExercisesSql =
        "SELECT te.id AS Id, te.exercise_id AS ExerciseId, e.name AS ExerciseName, e.tracking_type AS TrackingType, " +
        "te.position AS Position, te.set_scheme AS SetScheme, te.rest_seconds AS RestSeconds, te.progression AS Progression " +
        "FROM template_exercises te JOIN exercises e ON e.id = te.exercise_id " +
        "WHERE te.template_id = ? AND te.profile_id = ? ORDER BY te.position, te.id";

    /// <summary>Adds a template with its exercises, in the order given, in one transaction.</summary>
    /// <exception cref="ArgumentException">The name is empty or too long, a rest time is out of range, or an exercise has an id.</exception>
    /// <exception cref="ValidationException">A set scheme or progression rule fails its validator.</exception>
    /// <exception cref="InvalidOperationException">An exercise isn't on this device.</exception>
    public static async Task<string> CreateTemplateAsync(
        IPowerSyncDatabase db,
        string profileId,
        string name,
        IReadOnlyList<TemplateExerciseInput> exercises,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(exercises);
        ArgumentNullException.ThrowIfNull(clock);
        CheckTemplate(name, exercises);
        if (exercises.Any(e => e.Id is not null))
        {
            throw new ArgumentException("A new template's exercises are new too.", nameof(exercises));
        }

        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO workout_templates (id, profile_id, name, created_at, updated_at) VALUES (?, ?, ?, ?, ?)",
                [id, profileId, name.Trim(), now, now]).ConfigureAwait(false);
            for (var i = 0; i < exercises.Count; i++)
            {
                await InsertTemplateExerciseAsync(tx, profileId, id, exercises[i], i, clock).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Renames a template and replaces its exercises with <paramref name="exercises"/>, in that order, in one
    /// transaction. An exercise with an <see cref="TemplateExerciseInput.Id"/> keeps its row (written only if
    /// something changed), one without is added, and the template's other rows are deleted. Past sessions keep what
    /// was logged; only the next start uses the new plan. <paramref name="loadedIds"/>, when given, are the rows the
    /// edit started from: a row not among them arrived since and was never shown, so the edit is refused.
    /// </summary>
    /// <exception cref="ArgumentException">The name or a rest time is out of range, or an id appears twice.</exception>
    /// <exception cref="ValidationException">A set scheme or progression rule fails its validator.</exception>
    /// <exception cref="InvalidOperationException">
    /// The template isn't on this device, an id isn't one of its exercises (removed meanwhile, here or on another
    /// device), an exercise isn't on this device, or the template has a row whose exercise hasn't synced yet (which
    /// <see cref="TemplateExercisesAsync"/> doesn't list, so the edit can't be complete).
    /// </exception>
    public static async Task UpdateTemplateAsync(
        IPowerSyncDatabase db,
        string profileId,
        string templateId,
        string name,
        IReadOnlyList<TemplateExerciseInput> exercises,
        TimeProvider clock,
        IReadOnlyCollection<string>? loadedIds = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentNullException.ThrowIfNull(exercises);
        ArgumentNullException.ThrowIfNull(clock);
        CheckTemplate(name, exercises);
        var kept = exercises.Where(e => e.Id is not null).Select(e => e.Id!).ToList();
        if (kept.Distinct(StringComparer.Ordinal).Count() != kept.Count)
        {
            throw new ArgumentException("An exercise row appears twice.", nameof(exercises));
        }

        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            var template = await tx.GetOptional<NameOnly>(
                "SELECT name AS Name FROM workout_templates WHERE id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The template is not on this device.");
            var existing = (await tx.GetAll<StoredTemplateExercise>(
                "SELECT te.id AS Id, te.exercise_id AS ExerciseId, te.position AS Position, te.set_scheme AS SetScheme, te.rest_seconds AS RestSeconds, " +
                "te.progression AS Progression, EXISTS (SELECT 1 FROM exercises e WHERE e.id = te.exercise_id) AS OnDevice " +
                "FROM template_exercises te WHERE te.template_id = ? AND te.profile_id = ?",
                [templateId, profileId]).ConfigureAwait(false)).ToDictionary(e => e.Id, StringComparer.Ordinal);
            if (kept.FirstOrDefault(id => !existing.ContainsKey(id)) is not null)
            {
                throw new InvalidOperationException("An exercise was removed from this template meanwhile. Reopen it to see the current plan.");
            }

            // TemplateExercisesAsync leaves out a row whose exercise hasn't synced, so an editor never saw it: leaving it
            // out here is not a request to remove it.
            if (existing.Values.Any(e => e.OnDevice == 0 && !kept.Contains(e.Id, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(HiddenExercises);
            }

            // A row added since the editor loaded (synced from another device) was never shown, so it isn't removed.
            if (loadedIds is not null && existing.Keys.Any(id => !loadedIds.Contains(id, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(TemplateChanged);
            }

            if (template.Name != name.Trim())
            {
                await tx.Execute("UPDATE workout_templates SET name = ?, updated_at = ? WHERE id = ? AND profile_id = ?",
                    [name.Trim(), now, templateId, profileId]).ConfigureAwait(false);
            }

            foreach (var gone in existing.Keys.Except(kept, StringComparer.Ordinal))
            {
                await tx.Execute("DELETE FROM template_exercises WHERE id = ? AND profile_id = ?", [gone, profileId]).ConfigureAwait(false);
            }

            for (var i = 0; i < exercises.Count; i++)
            {
                var exercise = exercises[i];
                if (exercise.Id is null)
                {
                    await InsertTemplateExerciseAsync(tx, profileId, templateId, exercise, i, clock).ConfigureAwait(false);
                    continue;
                }

                var row = existing[exercise.Id];
                if (row.ExerciseId == exercise.ExerciseId && row.Position == i && row.RestSeconds == exercise.RestSeconds
                    && SetScheme.Parse(row.SetScheme) == exercise.Scheme && ProgressionRule.Parse(row.Progression) == exercise.Progression)
                {
                    continue;
                }

                if (row.ExerciseId != exercise.ExerciseId)
                {
                    await RequireExerciseAsync(tx, exercise.ExerciseId).ConfigureAwait(false);
                }

                await tx.Execute(
                    "UPDATE template_exercises SET exercise_id = ?, position = ?, set_scheme = ?, rest_seconds = ?, progression = ?, updated_at = ? " +
                    "WHERE id = ? AND profile_id = ?",
                    [exercise.ExerciseId, i, exercise.Scheme.ToJson(), exercise.RestSeconds, exercise.Progression?.ToJson(), now, exercise.Id, profileId])
                    .ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a template with its exercises and its schedule entries, as the server's cascade does. Sessions done
    /// from it keep everything logged and lose only the link (also as the server does).
    /// </summary>
    /// <exception cref="InvalidOperationException">The template isn't on this device.</exception>
    public static async Task DeleteTemplateAsync(IPowerSyncDatabase db, string profileId, string templateId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<IdOnly>(
                "SELECT id AS Id FROM workout_templates WHERE id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The template is not on this device.");
            await tx.Execute("DELETE FROM template_exercises WHERE template_id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM scheduled_workouts WHERE template_id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false);
            await tx.Execute("UPDATE workout_sessions SET template_id = NULL, updated_at = ? WHERE template_id = ? AND profile_id = ?",
                [now, templateId, profileId]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM workout_templates WHERE id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Why an edit was refused because the template gained an exercise after it was loaded.</summary>
    public const string TemplateChanged =
        "This template was changed on another device while you edited it. Reopen it to see the current plan, then make your changes again.";

    /// <summary>Why a template can't be edited until it has fully synced.</summary>
    public const string HiddenExercises =
        "Some of this template's exercises haven't reached this device yet. Wait for it to sync, then edit the template.";

    private static void CheckTemplate(string name, IReadOnlyList<TemplateExerciseInput> exercises)
    {
        if (!WorkoutCodes.IsName(name))
        {
            throw new ArgumentException("A name of 1 to 120 characters.", nameof(name));
        }

        if (exercises.Count > 50)
        {
            throw new ArgumentException("A template has at most 50 exercises.", nameof(exercises));
        }

        foreach (var exercise in exercises)
        {
            ArgumentNullException.ThrowIfNull(exercise);
            new SetSchemeValidator().ValidateAndThrow(exercise.Scheme);
            if (exercise.Progression is { } rule)
            {
                new ProgressionRuleValidator().ValidateAndThrow(rule);
            }

            if (exercise.RestSeconds is < 0 or > 3600)
            {
                throw new ArgumentException("Rest is 0 to 3600 seconds.", nameof(exercises));
            }
        }
    }

    private static async Task RequireExerciseAsync(ITransaction tx, string exerciseId) =>
        _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM exercises WHERE id = ?", [exerciseId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The exercise is not on this device.");

    private static async Task InsertTemplateExerciseAsync(
        ITransaction tx, string profileId, string templateId, TemplateExerciseInput exercise, int position, TimeProvider clock)
    {
        await RequireExerciseAsync(tx, exercise.ExerciseId).ConfigureAwait(false);
        var now = Timestamps.UtcNow(clock);
        await tx.Execute(
            "INSERT INTO template_exercises (id, profile_id, template_id, exercise_id, position, set_scheme, rest_seconds, progression, " +
            "created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            [ExerciseLibrary.NewId(clock), profileId, templateId, exercise.ExerciseId, position, exercise.Scheme.ToJson(), exercise.RestSeconds,
             exercise.Progression?.ToJson(), now, now]).ConfigureAwait(false);
    }

    /// <summary>The profile's programs, newest start first.</summary>
    public static async Task<ProgramRow[]> ProgramsAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<ProgramRow>(
            "SELECT id AS Id, name AS Name, location_id AS LocationId, starts_on AS StartsOn, ends_on AS EndsOn " +
            "FROM programs WHERE profile_id = ? ORDER BY starts_on DESC, id",
            [profileId]).ConfigureAwait(false);
    }

    /// <summary>Adds a program from <paramref name="startsOn"/> to <paramref name="endsOn"/> (open-ended when null).</summary>
    public static async Task<string> CreateProgramAsync(
        IPowerSyncDatabase db,
        string profileId,
        string name,
        string? locationId,
        DateOnly startsOn,
        DateOnly? endsOn,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        if (!WorkoutCodes.IsName(name))
        {
            throw new ArgumentException("A name of 1 to 120 characters.", nameof(name));
        }

        if (endsOn < startsOn)
        {
            throw new ArgumentException("A program can't end before it starts.", nameof(endsOn));
        }

        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.Execute(
            "INSERT INTO programs (id, profile_id, name, location_id, starts_on, ends_on, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            [id, profileId, name.Trim(), locationId, Day(startsOn), endsOn is { } end ? Day(end) : null, now, now]).ConfigureAwait(false);
        return id;
    }

    /// <summary>Plans a template every <paramref name="weekday"/> of a program.</summary>
    public static async Task<string> ScheduleWeeklyAsync(
        IPowerSyncDatabase db, string profileId, string programId, string templateId, DayOfWeek weekday, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(programId);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentNullException.ThrowIfNull(clock);
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        if (!Enum.IsDefined(weekday)) throw new ArgumentOutOfRangeException(nameof(weekday));
        await db.WriteTransaction(async tx =>
        {
            await RequireScheduleParentsAsync(tx, profileId, templateId, programId).ConfigureAwait(false);
            await tx.Execute(
                "INSERT INTO scheduled_workouts (id, profile_id, program_id, template_id, weekday, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                [id, profileId, programId, templateId, IsoWeekday(weekday), now, now]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>Plans a template on one date, optionally as part of a program.</summary>
    public static async Task<string> ScheduleOnAsync(
        IPowerSyncDatabase db, string profileId, string templateId, DateOnly date, TimeProvider clock, string? programId = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentNullException.ThrowIfNull(clock);
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            await RequireScheduleParentsAsync(tx, profileId, templateId, programId).ConfigureAwait(false);
            await tx.Execute(
                "INSERT INTO scheduled_workouts (id, profile_id, program_id, template_id, scheduled_on, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
                [id, profileId, programId, templateId, Day(date), now, now]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// What is planned from <paramref name="from"/> to <paramref name="to"/> (inclusive), by date: each weekly entry on its
    /// weekday within its program's dates, and each dated entry on its date.
    /// </summary>
    public static async Task<IReadOnlyList<PlannedWorkout>> PlannedAsync(IPowerSyncDatabase db, string profileId, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (to < from || to.DayNumber - from.DayNumber > 366)
        {
            throw new ArgumentOutOfRangeException(nameof(to), "A range of up to a year.");
        }

        var rows = await db.GetAll<ScheduleRow>(
            "SELECT s.id AS Id, s.program_id AS ProgramId, s.template_id AS TemplateId, t.name AS TemplateName, s.weekday AS Weekday, " +
            "s.scheduled_on AS ScheduledOn, s.session_id AS SessionId, p.starts_on AS StartsOn, p.ends_on AS EndsOn " +
            "FROM scheduled_workouts s JOIN workout_templates t ON t.id = s.template_id LEFT JOIN programs p ON p.id = s.program_id " +
            "WHERE s.profile_id = ? AND (s.scheduled_on IS NULL OR (s.scheduled_on >= ? AND s.scheduled_on <= ?))",
            [profileId, Day(from), Day(to)]).ConfigureAwait(false);

        // A weekly entry has no session of its own: it counts as done on a day with a session of its template.
        var sessions = await db.GetAll<SessionOn>(
            "SELECT id AS Id, template_id AS TemplateId, local_date AS LocalDate FROM workout_sessions " +
            "WHERE profile_id = ? AND template_id IS NOT NULL AND local_date >= ? AND local_date <= ? ORDER BY started_at, id",
            [profileId, Day(from), Day(to)]).ConfigureAwait(false);

        var planned = new List<PlannedWorkout>();
        foreach (var row in rows)
        {
            if (row.ScheduledOn is not null)
            {
                if (TryDay(row.ScheduledOn, out var date))
                {
                    planned.Add(new PlannedWorkout(date, row.Id, row.TemplateId, row.TemplateName, row.ProgramId, row.SessionId));
                }

                continue;
            }

            if (row.Weekday is not { } weekday || !TryDay(row.StartsOn, out var starts))
            {
                continue;
            }

            DateOnly? ends = TryDay(row.EndsOn, out var end) ? end : null;
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (IsoWeekday(day.DayOfWeek) == weekday && day >= starts && (ends is null || day <= ends))
                {
                    var done = sessions.FirstOrDefault(x => x.TemplateId == row.TemplateId && x.LocalDate == Day(day))?.Id;
                    planned.Add(new PlannedWorkout(day, row.Id, row.TemplateId, row.TemplateName, row.ProgramId, done));
                }
            }
        }

        return [.. planned.OrderBy(p => p.Date).ThenBy(p => p.TemplateName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ScheduledId)];
    }

    /// <summary>ISO weekday: Monday 1 … Sunday 7, as <c>scheduled_workouts.weekday</c> stores it.</summary>
    public static int IsoWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static bool TryDay(string? text, out DateOnly date) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal sealed class IdOnly
    {
        public string Id { get; set; } = "";
    }

    internal sealed class NameOnly
    {
        public string Name { get; set; } = "";
    }

    internal sealed class StoredTemplateExercise
    {
        public string Id { get; set; } = "";

        public string ExerciseId { get; set; } = "";

        public long Position { get; set; }

        public string? SetScheme { get; set; }

        public long? RestSeconds { get; set; }

        public string? Progression { get; set; }

        public long OnDevice { get; set; }
    }

    internal sealed class SessionOn
    {
        public string Id { get; set; } = "";

        public string TemplateId { get; set; } = "";

        public string LocalDate { get; set; } = "";
    }

    internal sealed class ScheduleRow
    {
        public string Id { get; set; } = "";

        public string? ProgramId { get; set; }

        public string TemplateId { get; set; } = "";

        public string TemplateName { get; set; } = "";

        public long? Weekday { get; set; }

        public string? ScheduledOn { get; set; }

        public string? SessionId { get; set; }

        public string? StartsOn { get; set; }

        public string? EndsOn { get; set; }
    }
}
