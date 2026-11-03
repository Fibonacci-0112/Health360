using Health360.Domain.Units;

namespace Health360.Domain.Training;

/// <summary>What a personal record is a record of (docs/calculations.md, Personal records).</summary>
public enum RecordKind
{
    /// <summary>The highest estimated one-rep max, in kg.</summary>
    E1rm,

    /// <summary>The heaviest load lifted for at least one rep, in kg.</summary>
    Weight,

    /// <summary>The most reps at one load (a rep count, at the set's load).</summary>
    Reps,

    /// <summary>The largest single-set volume, load × reps, in kg.</summary>
    Volume,
}

/// <summary>
/// One completed set as PR detection reads it: the load in canonical kg (never a rounded display value), the reps,
/// and the e1RM computed from that kg value, if any.
/// </summary>
public sealed record LiftedSet(double WeightKg, int Reps, double? E1rmKg)
{
    /// <summary>Whether the set can count toward a record: a positive, finite load and at least one rep.</summary>
    public bool Counts => double.IsFinite(WeightKg) && WeightKg > 0 && Reps >= 1;

    /// <summary>Load × reps, in kg.</summary>
    public double VolumeKg => WeightKg * Reps;
}

/// <summary>A record a set sets: the kind and its value (kg, or a rep count for <see cref="RecordKind.Reps"/>).</summary>
public sealed record NewRecord(RecordKind Kind, double Value);

/// <summary>
/// Which records a completed set beats, against the profile's earlier completed sets of the same exercise. Compares
/// canonical kg with <see cref="Quantity.CanonicalGreater"/> and <see cref="Quantity.CanonicalEquals"/>, so repeating
/// a lift in another unit is never a record.
/// </summary>
public static class PersonalRecords
{
    /// <summary>
    /// The records <paramref name="set"/> beats, in <see cref="RecordKind"/> order. A record needs an earlier value to
    /// beat: an exercise's first set sets a baseline, not a record, and a rep record needs an earlier set at the same
    /// load. Sets that don't count (no load, no reps; warm-ups are the caller's to leave out) are ignored on both sides.
    /// </summary>
    public static IReadOnlyList<NewRecord> Detect(IReadOnlyList<LiftedSet> earlier, LiftedSet set)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(set);
        if (!set.Counts)
        {
            return [];
        }

        var before = earlier.Where(s => s.Counts).ToList();
        if (before.Count == 0)
        {
            return [];
        }

        var records = new List<NewRecord>();
        var bestE1rm = before.Where(s => s.E1rmKg is { } e && double.IsFinite(e)).Select(s => s.E1rmKg!.Value).DefaultIfEmpty().Max();
        if (set.E1rmKg is { } e1rm && double.IsFinite(e1rm) && bestE1rm > 0 && Quantity.CanonicalGreater(e1rm, bestE1rm))
        {
            records.Add(new NewRecord(RecordKind.E1rm, e1rm));
        }

        if (Quantity.CanonicalGreater(set.WeightKg, before.Max(s => s.WeightKg)))
        {
            records.Add(new NewRecord(RecordKind.Weight, set.WeightKg));
        }

        var atLoad = before.Where(s => Quantity.CanonicalEquals(s.WeightKg, set.WeightKg)).ToList();
        if (atLoad.Count > 0 && set.Reps > atLoad.Max(s => s.Reps))
        {
            records.Add(new NewRecord(RecordKind.Reps, set.Reps));
        }

        if (Quantity.CanonicalGreater(set.VolumeKg, before.Max(s => s.VolumeKg)))
        {
            records.Add(new NewRecord(RecordKind.Volume, set.VolumeKg));
        }

        return records;
    }
}
