namespace Health360.Domain.Physiology;

/// <summary>What a zone set was derived from (<c>zone_sets.basis</c>, except <c>custom</c>, which the user types in).</summary>
public enum ZoneBasis
{
    /// <summary>Lab thresholds: LT1/VT1 and LT2/VT2 heart rates (Friel for run and bike, Seiler and Kjerland otherwise).</summary>
    Lab,

    /// <summary>A measured or test-derived lactate threshold heart rate (Friel's functional threshold HR).</summary>
    Lthr,

    /// <summary>Heart rate reserve (Karvonen), banded as in Garber et al. (2011), Table 4.</summary>
    HeartRateReserve,

    /// <summary>Percent of maximum heart rate: Friel for run and bike, Garber et al. (2011), Table 4, otherwise.</summary>
    PercentOfMax,
}

/// <summary>The sport a zone set is for (<c>zone_sets.sport</c>). Friel's zones cover run and bike only.</summary>
public enum ZoneSport
{
    All,
    Run,
    Bike,
    Swim,
    Row,
    Other,
}

/// <summary>
/// One heart-rate zone. <paramref name="LowerBpm"/> is inclusive; <paramref name="UpperBpm"/> is the next zone's
/// lower bound (exclusive), or null for the last zone, which has no far end.
/// </summary>
public sealed record HeartRateZone(string Name, double LowerBpm, double? UpperBpm);

/// <summary>
/// Heart-rate zones derived from the physiology profile, with the basis and the resolved values they came from
/// (for the info view). <see cref="Boundaries"/> is what <c>zone_sets.boundaries</c> stores for metric
/// <c>hr</c>: each zone's lower bound in bpm, zone 1 first, strictly ascending.
/// </summary>
public sealed record HeartRateZones(
    ZoneBasis Basis,
    ZoneSport Sport,
    string Method,
    IReadOnlyList<HeartRateZone> Zones,
    IReadOnlyList<Resolution> Inputs)
{
    public IReadOnlyList<double> Boundaries => [.. Zones.Select(z => z.LowerBpm)];

    /// <summary>
    /// The 0-based index of the zone <paramref name="bpm"/> falls in, using the <c>zone_sets</c> rule: zone i holds
    /// values ≥ its lower bound and below the next zone's. Null when the value is below zone 1 or not finite.
    /// </summary>
    public int? ZoneOf(double bpm)
    {
        if (!double.IsFinite(bpm) || bpm < Zones[0].LowerBpm)
        {
            return null;
        }

        var index = 0;
        while (index + 1 < Zones.Count && bpm >= Zones[index + 1].LowerBpm)
        {
            index++;
        }

        return index;
    }
}

/// <summary>
/// The resolved values zones can be derived from. Each is the resolver's result for its metric, so it carries
/// whether it is measured or estimated. <paramref name="LowerThreshold"/> is LT1 or VT1 heart rate and
/// <paramref name="UpperThreshold"/> LT2 or VT2 heart rate; the caller picks which pair to pass.
/// </summary>
public sealed record ZoneInputs(
    Resolution? MaxHr = null,
    Resolution? RestingHr = null,
    Resolution? Lthr = null,
    Resolution? LowerThreshold = null,
    Resolution? UpperThreshold = null,
    bool TakesHeartRateLoweringMedication = false);

/// <summary>Heart-rate zones, best basis first (PLAN.md §2.8). Sources and boundaries: docs/calculations.md.</summary>
public static class HeartRateZoneRules
{
    public const string FrielLactateMethod = "Friel (LT1 and LT2)";
    public const string FrielThresholdMethod = "Friel (threshold HR)";
    public const string FrielMaxMethod = "Friel (% of max HR)";
    public const string SeilerMethod = "Three zones from lab thresholds (Seiler)";
    public const string KarvonenMethod = "Karvonen (heart rate reserve)";
    public const string PercentOfMaxMethod = "% of max HR";

    /// <summary>
    /// Friel (2025), FTHR column, run and bike: Z1 &lt; 78, Z2 78–86, Z3 87–93, Z4 94–99, Z5a 100–102, Z5b &gt; 102.
    /// Each zone starts at the first number of its own range; "more than 102" starts at 102.
    /// </summary>
    public static IReadOnlyList<(string Name, double Percent)> FrielThreshold { get; } =
        [("1", 0), ("2", 78), ("3", 87), ("4", 94), ("5a", 100), ("5b", 102)];

    /// <summary>
    /// Friel (2025), MHR column, run and bike: Z1 &lt; 72, Z2 73–82, Z3 83–87, Z4 88–92, Z5a and Z5b together
    /// 92–100. Each zone starts at the first number of its own range.
    /// </summary>
    public static IReadOnlyList<(string Name, double Percent)> FrielMax { get; } =
        [("1", 0), ("2", 73), ("3", 83), ("4", 88), ("5", 92)];

    /// <summary>Garber et al. (2011), Table 4, %HRR: very light &lt; 30, light 30–39, moderate 40–59, vigorous 60–89, near-maximal ≥ 90.</summary>
    public static IReadOnlyList<(string Name, double Percent)> GarberReserve { get; } =
        [("Very light", 0), ("Light", 30), ("Moderate", 40), ("Vigorous", 60), ("Near-maximal to maximal", 90)];

    /// <summary>Garber et al. (2011), Table 4, %HRmax: very light &lt; 57, light 57–63, moderate 64–76, vigorous 77–95, near-maximal ≥ 96.</summary>
    public static IReadOnlyList<(string Name, double Percent)> GarberMax { get; } =
        [("Very light", 0), ("Light", 57), ("Moderate", 64), ("Vigorous", 77), ("Near-maximal to maximal", 96)];

    /// <summary>Friel's zones cover running and cycling only; other sports use Seiler and Garber.</summary>
    public static bool UsesFriel(ZoneSport sport) => sport is ZoneSport.Run or ZoneSport.Bike;

    /// <summary>The stored code, as in <c>zone_sets.basis</c>.</summary>
    public static string Code(ZoneBasis basis) => basis switch
    {
        ZoneBasis.Lab => "lab",
        ZoneBasis.Lthr => "lthr",
        ZoneBasis.HeartRateReserve => "hrr",
        ZoneBasis.PercentOfMax => "max_hr",
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Unknown zone basis"),
    };

    /// <summary>The stored code, as in <c>zone_sets.sport</c>.</summary>
    public static string Code(ZoneSport sport) => sport switch
    {
        ZoneSport.All => "all",
        ZoneSport.Run => "run",
        ZoneSport.Bike => "bike",
        ZoneSport.Swim => "swim",
        ZoneSport.Row => "row",
        ZoneSport.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(sport), sport, "Unknown sport"),
    };

    /// <summary>
    /// Derives zones from the best basis the inputs allow:
    /// <list type="number">
    /// <item>Lab thresholds (LT1/VT1 and LT2/VT2), both measured: Friel's lactate column for run and bike,
    /// Seiler's three zones for other sports.</item>
    /// <item>A measured or test-derived LTHR (Friel's FTHR), run and bike only.</item>
    /// <item>Heart rate reserve (Garber bands), when max HR and a measured resting HR are known.</item>
    /// <item>% of max HR: Friel's MHR column for run and bike, Garber's bands for other sports.</item>
    /// </list>
    /// Estimated LTHR, thresholds and resting HR never set zones. When the heart-rate-lowering medication flag is
    /// set, an estimated (age-predicted) max HR is not used either. A basis whose boundaries wouldn't be finite and
    /// strictly ascending is skipped. Null when no basis applies.
    /// </summary>
    public static HeartRateZones? Derive(ZoneSport sport, ZoneInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var friel = UsesFriel(sport);

        var lower = Measured(inputs.LowerThreshold);
        var upper = Measured(inputs.UpperThreshold);
        if (lower is not null && upper is not null)
        {
            // Friel: Z2 is the 10 bpm below LT1, Z4 the 10 bpm above LT2, Z5 everything higher.
            var lab = friel
                ? Valid(Zones(["1", "2", "3", "4", "5"], [0, lower.Value - 10, lower.Value, upper.Value, upper.Value + 10]))
                : Valid(Zones(["1", "2", "3"], [0, lower.Value, upper.Value]));
            if (lab is not null)
            {
                return new HeartRateZones(ZoneBasis.Lab, sport, friel ? FrielLactateMethod : SeilerMethod, lab, [lower, upper]);
            }
        }

        var lthr = Measured(inputs.Lthr);
        if (friel && lthr is not null && Valid(Percent(FrielThreshold, lthr.Value / 100, 0)) is { } threshold)
        {
            return new HeartRateZones(ZoneBasis.Lthr, sport, FrielThresholdMethod, threshold, [lthr]);
        }

        var max = inputs.MaxHr is { } m && (!m.IsEstimate || !inputs.TakesHeartRateLoweringMedication) && Positive(m.Value)
            ? m
            : null;
        if (max is null)
        {
            return null;
        }

        // Karvonen: target = resting + fraction × (max − resting).
        var resting = Measured(inputs.RestingHr);
        if (resting is not null && Valid(Percent(GarberReserve, (max.Value - resting.Value) / 100, resting.Value)) is { } reserve)
        {
            return new HeartRateZones(ZoneBasis.HeartRateReserve, sport, KarvonenMethod, reserve, [max, resting]);
        }

        return Valid(Percent(friel ? FrielMax : GarberMax, max.Value / 100, 0)) is { } percentOfMax
            ? new HeartRateZones(ZoneBasis.PercentOfMax, sport, friel ? FrielMaxMethod : PercentOfMaxMethod, percentOfMax, [max])
            : null;
    }

    // What zone_sets accepts for metric hr: finite, non-negative, strictly ascending lower bounds. A basis whose
    // bounds fail (LT1 ≥ LT2, LT1 ≤ 10 bpm, resting ≥ max, or a value so large the arithmetic overflows) is skipped.
    private static List<HeartRateZone>? Valid(List<HeartRateZone> zones) =>
        zones.All(z => double.IsFinite(z.LowerBpm) && z.LowerBpm >= 0)
        && zones.Zip(zones.Skip(1)).All(p => p.Second.LowerBpm > p.First.LowerBpm)
            ? zones
            : null;

    private static Resolution? Measured(Resolution? resolution) =>
        resolution is { IsEstimate: false } r && Positive(r.Value) ? r : null;

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    // Lower bound of each zone = offset + percent × scale, so zone 1 (percent 0) starts at the offset.
    private static List<HeartRateZone> Percent(IReadOnlyList<(string Name, double Percent)> table, double scale, double offset) =>
        Zones([.. table.Select(t => t.Name)], [.. table.Select(t => offset + (t.Percent * scale))]);

    private static List<HeartRateZone> Zones(IReadOnlyList<string> names, IReadOnlyList<double> lowers) =>
        [.. names.Select((name, i) => new HeartRateZone(name, lowers[i], i + 1 < lowers.Count ? lowers[i + 1] : null))];
}
