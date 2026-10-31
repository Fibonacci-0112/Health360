using Health360.Domain.Body;

namespace Health360.Domain.Physiology;

/// <summary>A stored measurement of one metric: <see cref="Measured.Value"/> is the canonical value.</summary>
public sealed record MetricValue(string MetricCode, Measured Measured);

/// <summary>
/// Everything the body and physiology screens compute from: the profile's fields and every stored measurement.
/// <paramref name="AsOf"/> is today as a local date. <paramref name="ActivityRating"/> is the PA-R code (0–7) for
/// the Jackson VO₂max estimate, null when not asked. <paramref name="Pins"/> holds the user's pins by metric code.
/// </summary>
public sealed record PhysiologyInputs(
    DateOnly AsOf,
    IReadOnlyList<MetricValue> Measurements,
    Sex? Sex = null,
    DateOnly? BirthDate = null,
    double? HeightCm = null,
    ActivityLevel? ActivityLevel = null,
    bool TakesHeartRateLoweringMedication = false,
    int? ActivityRating = null,
    IReadOnlyDictionary<string, Pin>? Pins = null);

/// <summary>
/// One resolved value of the profile: the resolver's result for <paramref name="MetricCode"/>, and whether a
/// measured value is old enough for a "retest?" flag (PLAN.md §2.8).
/// </summary>
public sealed record ProfileValue(string MetricCode, Resolution Resolution, bool RetestSuggested);

/// <summary>
/// The body and physiology profile as of one day (PLAN.md §2.8, docs/calculations.md): the weight trend, the
/// resolved values with their sources, the estimates that fill gaps, maintenance energy and the inputs zones are
/// derived from. A pure function of <see cref="PhysiologyInputs"/>, so entering or deleting a measurement just means
/// building it again.
/// </summary>
public sealed record PhysiologyProfile
{
    private PhysiologyProfile(PhysiologyInputs inputs) => Inputs = inputs;

    public PhysiologyInputs Inputs { get; }

    /// <summary>Whole years on <see cref="PhysiologyInputs.AsOf"/>; null without a birth date (or one after it).</summary>
    public int? AgeYears { get; private init; }

    /// <summary>The smoothed weight trend, oldest first (every weigh-in, whatever its source).</summary>
    public IReadOnlyList<TrendPoint> WeightTrend { get; private init; } = [];

    /// <summary>The newest trend weight, which the formulas use as body weight. Null with no weigh-ins.</summary>
    public double? WeightKg => WeightTrend.Count > 0 ? WeightTrend[^1].TrendKg : null;

    /// <summary>BMI from the trend weight and the profile height.</summary>
    public double? Bmi { get; private init; }

    /// <summary>The body-fat percentage in use (measured values only; the Navy estimate is shown, not stored).</summary>
    public ProfileValue? BodyFat { get; private init; }

    /// <summary>Fat-free mass from the trend weight and <see cref="BodyFat"/>. It is as measured or estimated as the %BF.</summary>
    public double? FatFreeMassKg { get; private init; }

    /// <summary>The Navy-method estimate from the newest neck, waist (and, for women, hip) measurements.</summary>
    public NavyBodyFat? Navy { get; private init; }

    public ProfileValue? Rmr { get; private init; }

    /// <summary>Resolved RMR × the activity level's PAL. Null without an RMR or an activity level.</summary>
    public MaintenanceEnergy? Maintenance { get; private init; }

    public ProfileValue? MaxHr { get; private init; }

    public ProfileValue? RestingHr { get; private init; }

    public ProfileValue? Lthr { get; private init; }

    public ProfileValue? Vo2Max { get; private init; }

    /// <summary>LT1 or VT1 heart rate, whichever pair <see cref="ZoneInputs"/> uses.</summary>
    public ProfileValue? LowerThreshold { get; private init; }

    /// <summary>LT2 or VT2 heart rate, whichever pair <see cref="ZoneInputs"/> uses.</summary>
    public ProfileValue? UpperThreshold { get; private init; }

    public ZoneInputs ZoneInputs { get; private init; } = new();

    /// <summary>Heart-rate zones for <paramref name="sport"/> from the best basis the profile allows (null when none).</summary>
    public HeartRateZones? ZonesFor(ZoneSport sport) => HeartRateZoneRules.Derive(sport, ZoneInputs);

    public static PhysiologyProfile Build(PhysiologyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var byMetric = inputs.Measurements
            .GroupBy(m => m.MetricCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Measured).ToList(), StringComparer.Ordinal);
        List<Measured> Of(string code) => byMetric.TryGetValue(code, out var values) ? values : [];
        Pin? PinOf(string code) => inputs.Pins is not null && inputs.Pins.TryGetValue(code, out var pin) ? pin : null;

        int? age = inputs.BirthDate is { } birth && birth <= inputs.AsOf ? Ages.Years(birth, inputs.AsOf) : null;
        double? height = inputs.HeightCm is { } h && double.IsFinite(h) && h > 0 ? h : null;
        // The table allows 0 kg, the trend needs positive weights: an unusable synced weigh-in is skipped, not fatal.
        var trend = Body.WeightTrend.Compute(Of(MetricCodes.Weight)
            .Where(m => double.IsFinite(m.Value) && m.Value > 0)
            .Select(m => new WeighIn(m.MeasuredOn, m.Value)));
        double? weight = trend.Count > 0 ? trend[^1].TrendKg : null;
        double? bmi = weight is { } w && height is { } hc ? BodyComposition.Bmi(w, hc) : null;

        var bodyFat = Value(MetricCodes.BodyFatPercent, Of(MetricCodes.BodyFatPercent), null, PinOf(MetricCodes.BodyFatPercent));
        double? percentFat = bodyFat?.Resolution.Value is { } pf && double.IsFinite(pf) && pf is >= 0 and < 100 ? pf : null;
        double? fatFree = weight is { } wf && percentFat is { } p ? BodyComposition.FatFreeMassKg(wf, p) : null;

        var rmrEstimate = RestingMetabolicRate.Best(inputs.Sex, weight, height, age, fatFree is > 0 ? fatFree : null);
        var rmr = Value(MetricCodes.Rmr, Of(MetricCodes.Rmr), rmrEstimate, PinOf(MetricCodes.Rmr), measured =>
            Retest.IsSuggested(RetestRule.After12MonthsOrWeightChange, measured.MeasuredOn, inputs.AsOf,
                TrendOn(trend, measured.MeasuredOn), weight));

        var maxEstimate = age is { } a ? MaxHeartRate.Tanaka(a, inputs.TakesHeartRateLoweringMedication) : null;
        var maxHr = Value(MetricCodes.MaxHr, Of(MetricCodes.MaxHr), maxEstimate, PinOf(MetricCodes.MaxHr));
        var restingHr = Value(MetricCodes.RestingHr, Of(MetricCodes.RestingHr), null, PinOf(MetricCodes.RestingHr));

        // Neither the LTHR estimate nor Nes et al. (2011) is specified yet (docs/calculations.md, "Still to specify").
        var lthr = Value(MetricCodes.Lthr, Of(MetricCodes.Lthr), null, PinOf(MetricCodes.Lthr), measured =>
            Retest.IsSuggested(RetestRule.After12Months, measured.MeasuredOn, inputs.AsOf));
        var vo2Estimate = restingHr is null && inputs.ActivityRating is { } pa and >= 0 and <= Physiology.Vo2Max.MaxActivityCode
            ? Physiology.Vo2Max.Jackson(inputs.Sex, pa, age, bmi, percentFat)
            : null;
        var vo2Max = Value(MetricCodes.Vo2Max, Of(MetricCodes.Vo2Max), vo2Estimate, PinOf(MetricCodes.Vo2Max), measured =>
            Retest.IsSuggested(RetestRule.After12Months, measured.MeasuredOn, inputs.AsOf));

        // Lactate thresholds when both are measured, otherwise ventilatory ones.
        var (lowerCode, upperCode) = Of(MetricCodes.Lt1Hr).Count > 0 && Of(MetricCodes.Lt2Hr).Count > 0
            ? (MetricCodes.Lt1Hr, MetricCodes.Lt2Hr)
            : Of(MetricCodes.Vt1Hr).Count > 0 && Of(MetricCodes.Vt2Hr).Count > 0
                ? (MetricCodes.Vt1Hr, MetricCodes.Vt2Hr)
                : (MetricCodes.Lt1Hr, MetricCodes.Lt2Hr);
        var lower = Value(lowerCode, Of(lowerCode), null, PinOf(lowerCode));
        var upper = Value(upperCode, Of(upperCode), null, PinOf(upperCode));

        return new PhysiologyProfile(inputs)
        {
            AgeYears = age,
            WeightTrend = trend,
            Bmi = bmi,
            BodyFat = bodyFat,
            FatFreeMassKg = fatFree,
            Navy = NavyEstimate(inputs.Sex, height, Newest(Of(MetricCodes.Neck)), Newest(Of(MetricCodes.WaistAtNavel)),
                Newest(Of(MetricCodes.WaistAtNarrowest)), Newest(Of(MetricCodes.Hip))),
            Rmr = rmr,
            Maintenance = rmr is not null && inputs.ActivityLevel is { } level ? MaintenanceEnergy.From(rmr.Resolution, level) : null,
            MaxHr = maxHr,
            RestingHr = restingHr,
            Lthr = lthr,
            Vo2Max = vo2Max,
            LowerThreshold = lower,
            UpperThreshold = upper,
            ZoneInputs = new ZoneInputs(maxHr?.Resolution, restingHr?.Resolution, lthr?.Resolution, lower?.Resolution,
                upper?.Resolution, inputs.TakesHeartRateLoweringMedication),
        };
    }

    private static ProfileValue? Value(string code, List<Measured> measured, Estimate? estimate, Pin? pin,
        Func<Measured, bool>? retest = null) =>
        Resolver.Resolve(measured, estimate, pin) is { } resolution
            ? new ProfileValue(code, resolution, resolution.Measurement is { } m && retest is not null && retest(m))
            : null;

    /// <summary>The trend weight on <paramref name="date"/>: the last trend point on or before it.</summary>
    private static double? TrendOn(IReadOnlyList<TrendPoint> trend, DateOnly date) =>
        trend.LastOrDefault(p => p.LocalDate <= date)?.TrendKg;

    /// <summary>The newest value: latest day, then the later id (the later entry), as the resolver orders a tier.</summary>
    private static double? Newest(List<Measured> values) =>
        values.Count == 0 ? null : values.OrderByDescending(m => m.MeasuredOn).ThenByDescending(m => m.Id).First().Value;

    private static NavyBodyFat? NavyEstimate(Sex? sex, double? height, double? neck, double? navel, double? narrowest, double? hip) =>
        sex switch
        {
            Sex.Male when height is { } h && neck is { } n && navel is { } w => NavyMethod.Male(h, n, w),
            Sex.Female when height is { } h && neck is { } n && narrowest is { } w && hip is { } hp => NavyMethod.Female(h, n, w, hp),
            _ => null,
        };
}
