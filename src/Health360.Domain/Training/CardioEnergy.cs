using Health360.Domain.Physiology;
using Health360.Domain.Units;

namespace Health360.Domain.Training;

/// <summary>A small, verified subset of the 2024 Adult Compendium; codes and sources in calculations.md.</summary>
public sealed record CardioActivity(string Code, string Name, ZoneSport Sport, double Mets);

/// <summary>The session as entered. Zone times are seconds, in the displayed zones' order.</summary>
public sealed record CardioEntry(
    string ActivityCode,
    double DurationSeconds,
    double? AverageHr = null,
    double? MaxHr = null,
    IReadOnlyList<double>? ZoneSeconds = null,
    double? Rpe = null,
    bool Intervals = false,
    double? Distance = null,
    string? DistanceUnit = null,
    double? Elevation = null,
    string? ElevationUnit = null,
    DateTimeOffset? EndedAt = null);

/// <summary>The exact zones used, including their source, detached from future profile edits.</summary>
public sealed record CardioZones(
    string Method,
    IReadOnlyList<HeartRateZone> Zones,
    IReadOnlyList<Resolution> Inputs,
    string? StoredId = null)
{
    public static CardioZones? From(HeartRateZones? zones) =>
        zones is null ? null : new(zones.Method, [.. zones.Zones], [.. zones.Inputs.Select(i => CardioPhysiology.Snapshot(i)!)]);
}

/// <summary>Resolved physiology used by one estimate, preserving measured/estimated provenance.</summary>
public sealed record CardioPhysiology(
    double? WeightKg,
    Sex? Sex,
    int? AgeYears,
    Resolution? Vo2Max = null,
    Resolution? MaxHr = null,
    Resolution? RestingHr = null,
    Resolution? ThresholdHr = null,
    bool TakesHeartRateLoweringMedication = false,
    string? ThresholdMetric = null,
    Resolution? LowerThresholdHr = null)
{
    public static CardioPhysiology From(PhysiologyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var threshold = profile.LowerThreshold is not null ? profile.UpperThreshold : null;
        threshold ??= StandaloneThreshold(profile.Inputs, MetricCodes.Vt2Hr)
            ?? StandaloneThreshold(profile.Inputs, MetricCodes.Lt2Hr)
            ?? profile.UpperThreshold
            ?? profile.Lthr;
        return new(profile.WeightKg, profile.Inputs.Sex, profile.AgeYears, profile.Vo2Max?.Resolution,
            profile.MaxHr?.Resolution, profile.RestingHr?.Resolution,
            threshold?.Resolution, profile.Inputs.TakesHeartRateLoweringMedication, threshold?.MetricCode, profile.LowerThreshold?.Resolution);
    }

    private static ProfileValue? StandaloneThreshold(PhysiologyInputs inputs, string metricCode)
    {
        var measurements = inputs.Measurements.Where(m => m.MetricCode == metricCode).Select(m => m.Measured);
        var pin = inputs.Pins is not null && inputs.Pins.TryGetValue(metricCode, out var value) ? value : null;
        return Resolver.Resolve(measurements, null, pin) is { } resolution
            ? new ProfileValue(metricCode, resolution, false)
            : null;
    }

    /// <summary>Only the winning input belongs in a workout snapshot, not unrelated measurement history.</summary>
    internal static Resolution? Snapshot(Resolution? value) =>
        value is null ? null : value with { PassedOver = [], EstimatePassedOver = null };
}

/// <summary>A rough estimate range, never a statistical confidence interval.</summary>
public sealed record EnergyRange(double Low, double High);

/// <summary>
/// Versioned, immutable-by-storage snapshot. Time above threshold describes intensity; both energy systems work
/// at every intensity. Fuel ranges retain their source and method alongside the session inputs.
/// </summary>
public sealed record CardioEstimate(
    int Version,
    CardioEntry Entry,
    CardioActivity Activity,
    CardioPhysiology Physiology,
    CardioZones? Zones,
    string Method,
    EnergyRange? Kcal,
    double? AerobicSeconds,
    double? AnaerobicSeconds,
    double? DistanceMetres,
    IReadOnlyList<string> Notes,
    IntensityMinutes? Intensity = null,
    FuelEstimate? Fuel = null,
    double? ElevationMetres = null);

public sealed record IntensityMinutes(double LowSeconds, double ModerateSeconds, double HighSeconds, string Method);
public sealed record FuelEstimate(EnergyRange FatKcal, EnergyRange CarbKcal, EnergyRange FatGrams, EnergyRange CarbGrams, string Method);

public static class CardioEnergy
{
    public const int MethodVersion = 2;

    public static IReadOnlyList<CardioActivity> Activities { get; } =
    [
        new("12020", "Jogging, self-selected pace", ZoneSport.Run, 7.5),
        new("12030", "Running, 5.0–5.2 mph (8.0–8.4 km/h)", ZoneSport.Run, 8.5),
        new("12050", "Running, 6.0–6.3 mph (9.7–10.1 km/h)", ZoneSport.Run, 9.3),
        new("01010", "Cycling, under 10 mph (16 km/h), leisure", ZoneSport.Bike, 4),
        new("01030", "Cycling, 12–13.9 mph (19–22 km/h)", ZoneSport.Bike, 8),
        new("01200", "Stationary cycling, general", ZoneSport.Bike, 6.8),
        new("17151", "Walking, under 2 mph (3.2 km/h)", ZoneSport.Other, 2.3),
        new("17190", "Walking, moderate pace, level", ZoneSport.Other, 3.8),
        new("17200", "Walking, brisk pace, level", ZoneSport.Other, 4.8),
        new("17080", "Hiking, cross country", ZoneSport.Other, 6),
        new("02048", "Elliptical, moderate effort", ZoneSport.Other, 5),
        new("02049", "Elliptical, vigorous effort", ZoneSport.Other, 9),
        new("02071", "Rowing machine, under 100 watts", ZoneSport.Row, 5),
        new("02072", "Rowing machine, 100–149 watts", ZoneSport.Row, 7.5),
        new("02073", "Rowing machine, 150–199 watts", ZoneSport.Row, 11),
        new("18240", "Swimming freestyle, slow, recreational", ZoneSport.Swim, 5.8),
        new("18230", "Swimming freestyle, fast, vigorous", ZoneSport.Swim, 9.8),
        new("18255", "Swimming backstroke, recreational", ZoneSport.Swim, 4.8),
        new("18265", "Swimming breaststroke, recreational", ZoneSport.Swim, 5.3),
        new("18100", "Kayaking, moderate effort", ZoneSport.Other, 5),
        new("02054", "Strength training, multiple exercises, 8–15 reps", ZoneSport.Other, 3.5),
    ];

    /// <summary>Input errors for manual logging. Kept here so UI and local writes enforce the same invariants.</summary>
    public static IReadOnlyList<string> Errors(CardioEntry entry, CardioZones? zones)
    {
        ArgumentNullException.ThrowIfNull(entry);
        List<string> errors = [];
        if (zones is not null && (zones.Zones is null || zones.Inputs is null || zones.Inputs.Any(i => i is null) || zones.Zones.Count is < 2 or > 10
            || zones.Zones.Any(z => z is null)
            || zones.Zones.Where((z, i) => !double.IsFinite(z.LowerBpm) || z.LowerBpm < 0
                || (i > 0 && z.LowerBpm <= zones.Zones[i - 1].LowerBpm)
                || z.UpperBpm != (i + 1 < zones.Zones.Count ? zones.Zones[i + 1].LowerBpm : (double?)null)).Any()))
        {
            return ["The zone boundaries must be finite and strictly ascending."];
        }
        if (!Activities.Any(a => a.Code == entry.ActivityCode))
        {
            errors.Add("Choose an activity.");
        }

        if (!double.IsFinite(entry.DurationSeconds) || entry.DurationSeconds is <= 0 or > 86400)
        {
            errors.Add("Enter a duration above 0 and up to 24 hours.");
        }

        if (!Optional(entry.AverageHr, 1, 300) || !Optional(entry.MaxHr, 1, 300)
            || entry.AverageHr > entry.MaxHr)
        {
            errors.Add("Heart rate must be from 1 to 300 bpm, with max at least average.");
        }

        if (!Optional(entry.Rpe, 0, 10))
        {
            errors.Add("RPE runs from 0 to 10.");
        }

        if (!Optional(entry.Distance, 0, 1_000_000)
            || (entry.Distance is not null && entry.DistanceUnit is not ("m" or "km" or "mi" or "yd")))
        {
            errors.Add("Enter a distance of 0 or more and choose its unit.");
        }

        if (!Optional(entry.Elevation, 0, 20000) || (entry.Elevation is not null && entry.ElevationUnit is not ("m" or "ft")))
        {
            errors.Add("Enter elevation gain from 0 to 20,000, in m or ft.");
        }

        if (entry.ZoneSeconds is { } times)
        {
            if (zones is null || times.Count != zones.Zones.Count || times.Any(t => !double.IsFinite(t) || t < 0)
                || Math.Abs(times.Sum() - entry.DurationSeconds) > 0.01)
            {
                errors.Add("Zone times must match the displayed zones and add up to the session duration.");
            }
            else if (entry.MaxHr is { } max && times.Where((t, i) => t > 0 && zones.Zones[i].LowerBpm > max).Any())
            {
                errors.Add("Time in a zone cannot be above the session's max heart rate.");
            }
            else if (entry.AverageHr is { } average && !AverageWithinZoneBounds(average, entry, times, zones))
            {
                errors.Add("Average heart rate must be within the time-weighted range of the entered zones.");
            }
        }

        return errors;
    }

    private static bool AverageWithinZoneBounds(double average, CardioEntry entry, IReadOnlyList<double> times, CardioZones zones,
        double? fallbackMax = null)
    {
        var low = 0d;
        var high = 0d;
        var hasUpperBound = true;
        for (var i = 0; i < times.Count; i++)
        {
            if (times[i] == 0)
            {
                continue;
            }

            var zone = zones.Zones[i];
            var fraction = times[i] / entry.DurationSeconds;
            low += fraction * zone.LowerBpm;
            var upper = zone.UpperBpm ?? entry.MaxHr ?? fallbackMax;
            if (entry.MaxHr is { } max && upper is { } zoneUpper)
            {
                upper = Math.Min(zoneUpper, max);
            }

            if (upper is null)
            {
                hasUpperBound = false;
            }
            else
            {
                high += fraction * upper.Value;
            }
        }

        return double.IsFinite(low) && average >= low
            && (!hasUpperBound || (double.IsFinite(high) && average <= high));
    }

    public static CardioEstimate Estimate(CardioEntry entry, CardioPhysiology physiology, CardioZones? zones = null)
    {
        ArgumentNullException.ThrowIfNull(physiology);
        var errors = Errors(entry, zones);
        if (errors.Count > 0)
        {
            throw new ArgumentException(errors[0], nameof(entry));
        }

        var activity = Activities.Single(a => a.Code == entry.ActivityCode);
        physiology = physiology with
        {
            Vo2Max = CardioPhysiology.Snapshot(physiology.Vo2Max),
            MaxHr = CardioPhysiology.Snapshot(physiology.MaxHr),
            RestingHr = CardioPhysiology.Snapshot(physiology.RestingHr),
            ThresholdHr = CardioPhysiology.Snapshot(physiology.ThresholdHr),
            LowerThresholdHr = CardioPhysiology.Snapshot(physiology.LowerThresholdHr),
        };
        // Copy collections: callers may keep editing the form's arrays after a preview is calculated.
        entry = entry with { ZoneSeconds = entry.ZoneSeconds?.ToArray() };
        zones = zones is null ? null : zones with
        {
            Zones = [.. zones.Zones],
            Inputs = [.. zones.Inputs.Select(i => CardioPhysiology.Snapshot(i)!)],
        };
        List<string> notes = ["Rough population estimate, not a confidence interval. Not added to your calorie target.",
            "Fuel shares are a broad population proxy, not measured oxidation. Body-fat change depends on energy balance over days and weeks."];
        var method = "Unavailable";
        EnergyRange? kcal = null;
        var canUseHr = activity.Code != "02054" && !physiology.TakesHeartRateLoweringMedication && physiology.Sex is not null
            && physiology.AgeYears is >= 18 and <= 100 && Positive(physiology.WeightKg);
        var hasVo2 = Positive(physiology.Vo2Max?.Value);

        // Whole-zone bounds avoid claiming a precise HR from a watch's zone total. The open last zone needs a
        // recorded or resolved maximum; with neither, fall back to average HR or METs.
        if (canUseHr && entry.ZoneSeconds is { } times && zones is not null
            && ZoneBounds(entry, physiology, zones) is { } bounds)
        {
            double low = 0, high = 0;
            for (var i = 0; i < times.Count; i++)
            {
                low += Keytel(physiology, bounds[i].Low) * times[i] / 60;
                high += Keytel(physiology, bounds[i].High) * times[i] / 60;
            }

            kcal = Widen(low, high, entry.Intervals ? 0.4 : 0.25);
            method = hasVo2 ? "Keytel (VO₂max), time in zones" : "Keytel, time in zones";
        }
        else if (canUseHr && entry.AverageHr is { } average)
        {
            var value = Keytel(physiology, average) * entry.DurationSeconds / 60;
            kcal = Widen(value, value, entry.Intervals ? 0.5 : 0.3);
            method = hasVo2 ? "Keytel (VO₂max), average HR" : "Keytel, average HR";
            notes.Add("Average HR treats the session as steady state; intervals have a wider range.");
        }
        else if (Positive(physiology.WeightKg))
        {
            var value = activity.Mets * physiology.WeightKg!.Value * entry.DurationSeconds / 3600;
            kcal = Widen(value, value, 0.3);
            method = "2024 Compendium METs";
        }

        if (physiology.TakesHeartRateLoweringMedication)
        {
            notes.Add("Heart-rate-lowering medication: energy uses METs, never the HR equation.");
        }

        if (method.StartsWith("Keytel", StringComparison.Ordinal))
        {
            notes.Add("Keytel was studied in regularly exercising adults aged 18–45, 47–120 kg, during submaximal exercise. Other ages, sizes and hard efforts are extrapolations.");
        }

        if (kcal is null)
        {
            notes.Add("Enter a body weight in Body to estimate energy.");
        }

        var (aerobic, anaerobic) = Split(entry, physiology, zones);
        if (aerobic is not null && physiology.ThresholdHr?.IsEstimate == true)
        {
            notes.Add("Intensity uses an estimated LTHR, not a measured threshold.");
        }
        var intensity = Intensity(entry, physiology, zones);
        if (aerobic is null && intensity?.Method == "RPE categories")
        {
            aerobic = intensity.LowSeconds + intensity.ModerateSeconds;
            anaerobic = intensity.HighSeconds;
            notes.Add("RPE categories are a rough perceived-intensity proxy, not a measured lactate threshold.");
        }
        if (activity.Code == "02054")
        {
            aerobic = anaerobic = null;
            intensity = null;
            notes.Add("Strength is mainly anaerobic and carbohydrate-heavy. HR is unsuitable for estimating lifting energy; no numeric fuel split.");
        }
        if (aerobic is null)
        {
            notes.Add("Intensity split needs a usable threshold and comparable HR, or a steady session with perceived effort.");
        }

        return new(MethodVersion, entry, activity, physiology, zones, method, kcal, aerobic, anaerobic,
            entry.Distance is { } distance ? UnitCodes.Convert(distance, entry.DistanceUnit!, "m") : null, notes, intensity, Fuel(entry, physiology, zones, kcal),
            entry.Elevation is { } gain ? UnitCodes.Convert(gain, entry.ElevationUnit!, "m") : null);
    }

    /// <summary>Keytel's printed mixed-model equations, kJ/min converted to kcal/min (4.184 kJ/kcal).</summary>
    private static double Keytel(CardioPhysiology p, double hr)
    {
        var age = p.AgeYears!.Value;
        var kg = p.WeightKg!.Value;
        var kj = Positive(p.Vo2Max?.Value)
            ? p.Sex == Sex.Male
                ? -59.3954 - 36.3781 + (0.271 * age) + (0.394 * kg) + (0.404 * p.Vo2Max!.Value) + (0.634 * hr)
                : -59.3954 + (0.274 * age) + (0.103 * kg) + (0.380 * p.Vo2Max!.Value) + (0.450 * hr)
            : p.Sex == Sex.Male
                ? -55.0969 + (0.6309 * hr) + (0.1988 * kg) + (0.2017 * age)
                : -20.4022 + (0.4472 * hr) - (0.1263 * kg) + (0.074 * age);
        return Math.Max(0, kj / 4.184);
    }

    private static EnergyRange[]? ZoneBounds(CardioEntry entry, CardioPhysiology p, CardioZones zones)
    {
        var result = new EnergyRange[zones.Zones.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var z = zones.Zones[i];
            var high = z.UpperBpm ?? entry.MaxHr ?? p.MaxHr?.Value;
            if (entry.MaxHr is { } max && high is { } h)
            {
                high = Math.Min(h, max);
            }

            if (entry.ZoneSeconds![i] > 0 && (high is null || !double.IsFinite(high.Value) || high < z.LowerBpm))
            {
                return null;
            }

            result[i] = new(z.LowerBpm, Math.Max(z.LowerBpm, high ?? z.LowerBpm));
        }

        // A profile maximum can be estimated or outdated. It must not cap zone energy below the
        // observed average; use the average-HR path instead when the resolved bounds contradict it.
        return entry.AverageHr is { } average
            && !AverageWithinZoneBounds(average, entry, entry.ZoneSeconds!, zones, p.MaxHr?.Value)
                ? null : result;
    }

    private static (double? Aerobic, double? Anaerobic) Split(CardioEntry entry, CardioPhysiology p, CardioZones? zones)
    {
        if (p.ThresholdHr is not { Value: > 0 } threshold || !double.IsFinite(threshold.Value)
            || (p.TakesHeartRateLoweringMedication && threshold.IsEstimate))
        {
            return (null, null);
        }

        double high = 0;
        if (entry.ZoneSeconds is { } times && zones is not null)
        {
            for (var i = 0; i < times.Count; i++)
            {
                if (times[i] == 0)
                {
                    continue;
                }

                var zone = zones.Zones[i];
                if (zone.LowerBpm >= threshold.Value)
                {
                    high += times[i];
                }
                else if (zone.UpperBpm is null || zone.UpperBpm > threshold.Value)
                {
                    // A zone straddles the threshold: there isn't enough information to divide its minutes.
                    return (null, null);
                }
            }
        }
        else if (entry.AverageHr is { } average && !entry.Intervals)
        {
            high = average >= threshold.Value ? entry.DurationSeconds : 0;
        }
        else
        {
            return (null, null);
        }

        return (entry.DurationSeconds - high, high);
    }

    private static FuelEstimate? Fuel(CardioEntry entry, CardioPhysiology p, CardioZones? zones, EnergyRange? kcal)
    {
        if (kcal is null || entry.ActivityCode == "02054") return null;
        var canUseHr = !p.TakesHeartRateLoweringMedication && Positive(p.MaxHr?.Value);
        EnergyRange? RqForHr(double low, double high)
        {
            if (!canUseHr) return null;
            double Fraction(double hr) => Positive(p.RestingHr?.Value) && p.MaxHr!.Value > p.RestingHr!.Value
                && p.Vo2Max?.Value is > 3.5 and var vo2 && double.IsFinite(vo2)
                    ? (((hr - p.RestingHr.Value) / (p.MaxHr.Value - p.RestingHr.Value)) * (vo2 - 3.5) + 3.5) / vo2
                    : hr / p.MaxHr!.Value;
            return CardioSubstrates.RqRange(Fraction(low), Fraction(high), entry.Intervals ? 0.08 : 0.05);
        }

        if (canUseHr && entry.ZoneSeconds is { } times && zones is not null && ZoneBounds(entry, p, zones) is { } bounds)
        {
            var fuels = new List<FuelEstimate>();
            for (var i = 0; i < times.Count; i++)
            {
                if (times[i] == 0) continue;
                // Allocate the same whole-zone energy bounds used above, rather than treating zone time as energy share.
                var zoneEnergy = p.Sex is not null && p.AgeYears is >= 18 and <= 100 && Positive(p.WeightKg)
                    ? Widen(Keytel(p, bounds[i].Low) * times[i] / 60, Keytel(p, bounds[i].High) * times[i] / 60, entry.Intervals ? 0.4 : 0.25)
                    : new EnergyRange(kcal.Low * times[i] / entry.DurationSeconds, kcal.High * times[i] / entry.DurationSeconds);
                if (zoneEnergy is null) return null;
                fuels.Add(CardioSubstrates.Allocate(zoneEnergy, RqForHr(bounds[i].Low, bounds[i].High)!));
            }
            EnergyRange Sum(Func<FuelEstimate, EnergyRange> select) => new(fuels.Sum(f => select(f).Low), fuels.Sum(f => select(f).High));
            return new(Sum(f => f.FatKcal), Sum(f => f.CarbKcal), Sum(f => f.FatGrams), Sum(f => f.CarbGrams), CardioSubstrates.Method + "; time in zones");
        }
        if (canUseHr && entry.AverageHr is { } average)
        {
            return CardioSubstrates.Allocate(kcal, RqForHr(average, average)!, CardioSubstrates.Method + "; average HR assumes steady state");
        }
        if (entry.Rpe is { } rpe && !entry.Intervals)
        {
            // Broad category mapping, an explicit product assumption rather than an individual RPE→VO₂ equation.
            var low = rpe < 4 ? 0 : rpe < 6 ? 0.46 : 0.64;
            var high = rpe < 4 ? 0.46 : rpe < 6 ? 0.64 : 1;
            return CardioSubstrates.Allocate(kcal, CardioSubstrates.RqRange(low, high, 0.08),
                CardioSubstrates.Method + "; rough RPE category proxy");
        }
        return null;
    }

    private static IntensityMinutes? Intensity(CardioEntry entry, CardioPhysiology p, CardioZones? zones)
    {
        if (p.LowerThresholdHr is { IsEstimate: false, Value: > 0 } lower
            && p.ThresholdHr is { IsEstimate: false } upper && upper.Value > lower.Value
            && ((entry.ZoneSeconds is not null && zones is not null) || (entry.AverageHr is not null && !entry.Intervals)))
        {
            double low = 0, moderate = 0, high = 0;
            if (entry.ZoneSeconds is { } times && zones is not null)
            {
                for (var i = 0; i < times.Count; i++)
                {
                    if (times[i] == 0) continue;
                    var zone = zones.Zones[i];
                    if (zone.LowerBpm >= upper.Value) high += times[i];
                    else if (zone.LowerBpm >= lower.Value && zone.UpperBpm <= upper.Value) moderate += times[i];
                    else if (zone.UpperBpm <= lower.Value) low += times[i];
                    else return null;
                }
            }
            else if (entry.AverageHr is { } hr && !entry.Intervals)
            {
                if (hr >= upper.Value) high = entry.DurationSeconds;
                else if (hr >= lower.Value) moderate = entry.DurationSeconds;
                else low = entry.DurationSeconds;
            }
            else
            {
                return null;
            }

            return new(low, moderate, high, "Measured LT1/LT2 HR");
        }

        // A single RPE is not a time distribution for an interval workout.
        if ((p.TakesHeartRateLoweringMedication || (entry.AverageHr is null && entry.ZoneSeconds is null)) && entry.Rpe is { } rpe && !entry.Intervals)
        {
            return new(rpe < 4 ? entry.DurationSeconds : 0, rpe is >= 4 and < 6 ? entry.DurationSeconds : 0,
                rpe >= 6 ? entry.DurationSeconds : 0, "RPE categories");
        }
        return null;
    }

    private static EnergyRange? Widen(double low, double high, double fraction) =>
        double.IsFinite(low) && double.IsFinite(high) && double.IsFinite(high * (1 + fraction))
            ? new(low * (1 - fraction), high * (1 + fraction)) : null;

    private static bool Positive(double? value) => value is { } v && double.IsFinite(v) && v > 0;

    private static bool Optional(double? value, double min, double max) =>
        value is null || (double.IsFinite(value.Value) && value >= min && value <= max);
}
