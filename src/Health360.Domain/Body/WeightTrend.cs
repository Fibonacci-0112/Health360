namespace Health360.Domain.Body;

/// <summary>One weigh-in: the local day it was taken on and its canonical value.</summary>
public sealed record WeighIn(DateOnly LocalDate, double Kg);

/// <summary>
/// One day of the trend: the mean of that day's weigh-ins (<paramref name="Count"/> of them) and the
/// smoothed trend after taking that day into account.
/// </summary>
public sealed record TrendPoint(DateOnly LocalDate, double MeanKg, int Count, double TrendKg);

/// <summary>
/// The smoothed weight trend: an exponentially weighted moving average, as in Walker's
/// <i>The Hacker's Diet</i>. Each day the trend moves 10% of the way from where it was towards that day's
/// weight. Spec (start value, several weigh-ins in a day, missing days): docs/calculations.md.
/// </summary>
public static class WeightTrend
{
    /// <summary>The fraction of the distance to the new weight the trend moves each day (Walker's 0.1).</summary>
    public const double Smoothing = 0.1;

    /// <summary>
    /// The trend for every day that has a weigh-in, oldest first. It is a pure function of the whole series,
    /// so an edited or deleted weigh-in is handled by computing it again.
    /// </summary>
    public static IReadOnlyList<TrendPoint> Compute(IEnumerable<WeighIn> weighIns)
    {
        ArgumentNullException.ThrowIfNull(weighIns);

        var days = weighIns
            .Select(w => double.IsFinite(w.Kg) && w.Kg > 0
                ? w
                : throw new ArgumentOutOfRangeException(nameof(weighIns), w.Kg, "Weights must be positive numbers"))
            .GroupBy(w => w.LocalDate)
            .Select(g => (Date: g.Key, Mean: g.Average(w => w.Kg), Count: g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

        var points = new List<TrendPoint>(days.Count);
        foreach (var (date, mean, count) in days)
        {
            var trend = points.Count == 0
                ? mean
                : Step(points[^1].TrendKg, mean, date.DayNumber - points[^1].LocalDate.DayNumber);
            points.Add(new TrendPoint(date, mean, count, trend));
        }

        return points;
    }

    /// <summary>
    /// Moves <paramref name="previousTrend"/> towards <paramref name="weightKg"/> over
    /// <paramref name="days"/> days: <c>weight + (previous − weight) × 0.9^days</c>. One day is Walker's
    /// <c>previous + 0.1 × (weight − previous)</c>; a gap counts each missing day as if the new weight had been
    /// seen on it.
    /// </summary>
    public static double Step(double previousTrend, double weightKg, int days)
    {
        if (days < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "Days must be at least 1");
        }

        return weightKg + ((previousTrend - weightKg) * Math.Pow(1 - Smoothing, days));
    }
}
