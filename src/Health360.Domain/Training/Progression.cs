using Health360.Domain.Units;

namespace Health360.Domain.Training;

/// <summary>One work set as performed. Load is in the lifting unit the increment is set in.</summary>
public sealed record WorkSet(double Load, int Reps);

/// <summary>Which rule a template exercise progresses by.</summary>
public enum ProgressionKind
{
    /// <summary>One rep target (the top of the range); add the increment when every set reaches it.</summary>
    LinearProgression,

    /// <summary>Reps climb through a range at a fixed load, then the load goes up.</summary>
    DoubleProgression,
}

public enum ProgressionAction
{
    /// <summary>Every set met the target: add the increment.</summary>
    Increase,

    /// <summary>The target wasn't met: keep the load.</summary>
    Repeat,

    /// <summary>The target was missed too many sessions in a row: reduce the load.</summary>
    Deload,
}

/// <summary>
/// The next session's prescription. <see cref="ConsecutiveFailures"/> is passed back for the session after;
/// <see cref="Progression.Replay"/> does that over the history, so nothing is stored. It resets to 0 on any session
/// that isn't a miss, and on a deload.
/// </summary>
public sealed record ProgressionSuggestion(
    double Load,
    int MinReps,
    int MaxReps,
    ProgressionAction Action,
    int ConsecutiveFailures);

/// <summary>
/// When to deload: after <see cref="FailuresBeforeDeload"/> missed sessions in a row, take off about
/// <see cref="Fraction"/> of the load in whole increments (at least one). 0 failures turns deloads off.
/// </summary>
public sealed record DeloadPolicy(int FailuresBeforeDeload = 3, double Fraction = 0.10)
{
    public static DeloadPolicy Default { get; } = new();
}

/// <summary>
/// Load suggestions for the next session (docs/calculations.md, Progression). Loads and the increment share
/// one unit, the lifting unit the increment was set in (PLAN.md §2.6); converting a set logged in another
/// unit is the caller's job, through <c>Health360.Domain/Units</c>.
/// </summary>
public static class Progression
{
    /// <summary>
    /// Linear progression: every set reaches <paramref name="targetReps"/> at the prescribed load or more,
    /// then the load goes up by <paramref name="increment"/>.
    /// </summary>
    public static ProgressionSuggestion LinearProgression(
        double prescribedLoad,
        int targetReps,
        IReadOnlyList<WorkSet> sets,
        double increment,
        int consecutiveFailures,
        DeloadPolicy? deload = null) =>
        Next(prescribedLoad, targetReps, targetReps, sets, increment, consecutiveFailures, deload);

    /// <summary>
    /// Double progression: reps climb within <paramref name="minReps"/>–<paramref name="maxReps"/> at a fixed
    /// load. When every set reaches the top of the range the load goes up and the reps start again from the
    /// bottom. A session counts as missed when any set falls below the bottom of the range.
    /// </summary>
    public static ProgressionSuggestion DoubleProgression(
        double prescribedLoad,
        int minReps,
        int maxReps,
        IReadOnlyList<WorkSet> sets,
        double increment,
        int consecutiveFailures,
        DeloadPolicy? deload = null) =>
        Next(prescribedLoad, minReps, maxReps, sets, increment, consecutiveFailures, deload);

    /// <summary>
    /// The suggestion for the next session, found by applying the rule to each past session in order, oldest first, so
    /// the miss streak comes from the history itself and nothing needs storing. Each session is judged against the load
    /// the previous one suggested; when nothing was suggested yet (no <paramref name="startLoad"/>), or every work set
    /// was heavier than suggested, it is judged against its lightest work set, so a load chosen by hand is built on.
    /// Linear progression targets <paramref name="maxReps"/>. Sessions without work sets, or at a load of 0, are skipped.
    /// Null when no session counts: the caller plans the start load, if any.
    /// </summary>
    public static ProgressionSuggestion? Replay(
        ProgressionKind kind,
        int minReps,
        int maxReps,
        double increment,
        double? startLoad,
        IReadOnlyList<IReadOnlyList<WorkSet>> sessions,
        DeloadPolicy? deload = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Positive(increment, nameof(increment));
        if (minReps < 1 || maxReps < minReps)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReps), maxReps, "Reps need 1 ≤ min ≤ max");
        }

        if (startLoad is { } start && (!double.IsFinite(start) || start < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(startLoad), startLoad, "Can't be negative");
        }

        var min = kind == ProgressionKind.LinearProgression ? maxReps : minReps;
        double? prescribed = startLoad is > 0 ? startLoad : null;
        var failures = 0;
        ProgressionSuggestion? last = null;
        foreach (var sets in sessions)
        {
            if (sets is null || sets.Count == 0)
            {
                continue;
            }

            var lightest = sets.Min(s => s.Load);
            var load = prescribed is { } p && !(lightest > p && !Quantity.CanonicalEquals(lightest, p)) ? p : lightest;
            if (!double.IsFinite(load) || load <= 0 || Quantity.CanonicalEquals(load, 0))
            {
                continue;
            }

            last = Next(load, min, maxReps, sets, increment, failures, deload);
            prescribed = last.Load;
            failures = last.ConsecutiveFailures;
        }

        return last;
    }

    private static ProgressionSuggestion Next(
        double load,
        int minReps,
        int maxReps,
        IReadOnlyList<WorkSet> sets,
        double increment,
        int consecutiveFailures,
        DeloadPolicy? deload)
    {
        Check(load, minReps, maxReps, sets, increment, consecutiveFailures);
        deload ??= DeloadPolicy.Default;
        CheckPolicy(deload);

        bool AtLoad(WorkSet set) => set.Load >= load || Quantity.CanonicalEquals(set.Load, load);

        if (sets.All(s => AtLoad(s) && s.Reps >= maxReps))
        {
            return new(Finite(load + increment), minReps, maxReps, ProgressionAction.Increase, 0);
        }

        var missed = sets.Any(s => !AtLoad(s) || s.Reps < minReps);
        if (!missed)
        {
            // Within the range but not yet at the top (double progression only): not a miss, so the streak ends.
            return new(load, minReps, maxReps, ProgressionAction.Repeat, 0);
        }

        var failures = consecutiveFailures + 1;
        if (deload.FailuresBeforeDeload > 0 && failures >= deload.FailuresBeforeDeload)
        {
            // Snap the ratio to 12 decimals first (conversion noise is ~1e-15), so 3.4999999999999996 from a unit conversion still
            // counts as the half it is and the same session deloads alike in lb and kg.
            var ratio = Math.Round(load * deload.Fraction / increment, 12);
            var steps = Math.Max(1, Math.Round(ratio, MidpointRounding.AwayFromZero));
            var reduced = Finite(load - (steps * increment));
            if (reduced > 0 && !Quantity.CanonicalEquals(reduced, 0))
            {
                return new(reduced, minReps, maxReps, ProgressionAction.Deload, 0);
            }
        }

        return new(load, minReps, maxReps, ProgressionAction.Repeat, failures);
    }

    private static void Check(
        double load,
        int minReps,
        int maxReps,
        IReadOnlyList<WorkSet> sets,
        double increment,
        int consecutiveFailures)
    {
        Positive(load, nameof(load));
        Positive(increment, nameof(increment));
        if (minReps < 1 || maxReps < minReps)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReps), maxReps, "Reps need 1 ≤ min ≤ max");
        }

        if (consecutiveFailures < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailures), consecutiveFailures, "Can't be negative");
        }

        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
        {
            throw new ArgumentException("A session needs at least one work set", nameof(sets));
        }

        foreach (var set in sets)
        {
            if (!double.IsFinite(set.Load) || set.Load < 0 || set.Reps < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sets), set, "Loads and reps can't be negative");
            }
        }
    }

    private static void CheckPolicy(DeloadPolicy deload)
    {
        if (deload.FailuresBeforeDeload < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deload), deload, "Failures can't be negative");
        }

        if (!double.IsFinite(deload.Fraction) || deload.Fraction <= 0 || deload.Fraction >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(deload), deload, "Fraction must be in (0, 1)");
        }
    }

    private static double Finite(double load) =>
        double.IsFinite(load) ? load : throw new ArgumentOutOfRangeException(nameof(load), load, "Load out of range");

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive number");
        }
    }
}
