namespace Health360.Domain.Physiology;

/// <summary>Where a measured value came from (<c>measurements.source</c>).</summary>
public enum MeasurementSource
{
    Lab,
    FieldTest,
    Device,
    Manual,
}

/// <summary>The resolver's order, best first. A device reading and a manual entry rank the same.</summary>
public enum SourceTier
{
    Lab,
    FieldTest,
    DeviceOrManual,
    Estimate,
}

/// <summary>A measured value of one metric for one profile.</summary>
public sealed record Measured(Guid Id, double Value, MeasurementSource Source, DateOnly MeasuredOn, string? Method = null)
{
    public SourceTier Tier => Source switch
    {
        MeasurementSource.Lab => SourceTier.Lab,
        MeasurementSource.FieldTest => SourceTier.FieldTest,
        MeasurementSource.Device or MeasurementSource.Manual => SourceTier.DeviceOrManual,
        _ => throw new ArgumentOutOfRangeException(nameof(Source), Source, "Unknown source"),
    };
}

/// <summary>
/// The user's explicit choice for one metric: a specific measurement, or the estimate even when measured
/// values exist. A pin that can't be honoured (the measurement was deleted, or there's no estimate) is
/// ignored and reported, never silently replaced.
/// </summary>
public sealed record Pin
{
    private Pin(Guid? measurementId) => MeasurementId = measurementId;

    /// <summary>The pinned measurement, or null for a pin to the estimate.</summary>
    public Guid? MeasurementId { get; }

    public static Pin ToMeasurement(Guid id) => new(id);

    public static Pin ToEstimate { get; } = new((Guid?)null);
}

/// <summary>
/// The value in use for one metric and why: exactly one of <paramref name="Measurement"/> and
/// <paramref name="Estimate"/> is set. <paramref name="PassedOver"/> lists the other measured values, best
/// first, and <paramref name="EstimatePassedOver"/> the estimate when a measured value won, for the info view.
/// </summary>
public sealed record Resolution(
    double Value,
    SourceTier Tier,
    Measured? Measurement,
    Estimate? Estimate,
    bool Pinned,
    bool PinUnavailable,
    IReadOnlyList<Measured> PassedOver,
    Estimate? EstimatePassedOver)
{
    public bool IsEstimate => Estimate is not null;
}

/// <summary>
/// Picks the value to use (PLAN.md §2.8): a pin first, then lab, field test, device or manual, then the
/// estimate. Within a tier the newest measurement wins; on the same day, the later id (UUIDv7, so the later
/// entry). Old measured values still beat estimates; <see cref="Retest"/> flags them instead.
/// </summary>
public static class Resolver
{
    /// <returns>The resolution, or null when there's neither a measured value nor an estimate.</returns>
    public static Resolution? Resolve(IEnumerable<Measured> measured, Estimate? estimate, Pin? pin = null)
    {
        ArgumentNullException.ThrowIfNull(measured);

        var ranked = measured
            .OrderBy(m => m.Tier)
            .ThenByDescending(m => m.MeasuredOn)
            .ThenByDescending(m => m.Id)
            .ToList();

        if (pin is not null)
        {
            if (pin.MeasurementId is { } id && ranked.FirstOrDefault(m => m.Id == id) is { } chosen)
            {
                return FromMeasurement(chosen, ranked, estimate, pinned: true, pinUnavailable: false);
            }

            if (pin.MeasurementId is null && estimate is not null)
            {
                return new Resolution(estimate.Value, SourceTier.Estimate, null, estimate, Pinned: true,
                    PinUnavailable: false, ranked, null);
            }
        }

        var pinUnavailable = pin is not null;
        if (ranked.Count > 0)
        {
            return FromMeasurement(ranked[0], ranked, estimate, pinned: false, pinUnavailable);
        }

        return estimate is null
            ? null
            : new Resolution(estimate.Value, SourceTier.Estimate, null, estimate, Pinned: false, pinUnavailable, [], null);
    }

    private static Resolution FromMeasurement(Measured chosen, List<Measured> ranked, Estimate? estimate, bool pinned,
        bool pinUnavailable) =>
        new(chosen.Value, chosen.Tier, chosen, null, pinned, pinUnavailable, [.. ranked.Where(m => m != chosen)], estimate);
}

/// <summary>When an old measured value gets a "retest?" flag (PLAN.md §2.8). Product rules, not clinical ones.</summary>
public enum RetestRule
{
    /// <summary>VO₂max, LTHR, DEXA: after 12 months.</summary>
    After12Months,

    /// <summary>Measured RMR: after 12 months, or once body weight has changed by 5% or more since the test.</summary>
    After12MonthsOrWeightChange,
}

public static class Retest
{
    public const double WeightChangeFraction = 0.05;

    /// <param name="rule">The metric's rule.</param>
    /// <param name="measuredOn">The test date.</param>
    /// <param name="asOf">Today, as a local date.</param>
    /// <param name="weightThenKg">Trend weight on the test date, when known.</param>
    /// <param name="weightNowKg">Trend weight now, when known.</param>
    public static bool IsSuggested(RetestRule rule, DateOnly measuredOn, DateOnly asOf, double? weightThenKg = null,
        double? weightNowKg = null)
    {
        if (asOf >= measuredOn.AddMonths(12))
        {
            return true;
        }

        return rule == RetestRule.After12MonthsOrWeightChange
            && weightThenKg is > 0 and var then
            && weightNowKg is > 0 and var now
            && Math.Abs(now - then) / then >= WeightChangeFraction;
    }
}
