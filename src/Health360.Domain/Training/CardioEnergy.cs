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
    string? DistanceUnit = null);

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
    string? ThresholdMetric = null)
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
            threshold?.Resolution, profile.Inputs.TakesHeartRateLoweringMedication, threshold?.MetricCode);
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
/// at every intensity. No substrate split is produced until a population curve is sourced.
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
    IReadOnlyList<string> Notes);

public static class CardioEnergy
{
    public const int MethodVersion = 1;

    public static IReadOnlyList<CardioActivity> Activities { get; } =
    [
        new("12020", "Jogging, self-selected pace", ZoneSport.Run, 7.5),
        new("12030", "Running, 5.0–5.2 mph (8.0–8.4 km/h)", ZoneSport.Run, 8.5),
        new("12050", "Running, 6.0–6.3 mph (9.7–10.1 km/h)", ZoneSport.Run, 9.3),
        new("01010", "Cycling, under 10 mph (16 km/h), leisure", ZoneSport.Bike, 4),
        new("01030", "Cycling, 12–13.9 mph (19–22 km/h)", ZoneSport.Bike, 8),
        new("01200", "Stationary cycling, general", ZoneSport.Bike, 6.8),
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
        };
        // Copy collections: callers may keep editing the form's arrays after a preview is calculated.
        entry = entry with { ZoneSeconds = entry.ZoneSeconds?.ToArray() };
        zones = zones is null ? null : zones with
        {
            Zones = zones.Zones.ToArray(),
            Inputs = zones.Inputs.Select(i => CardioPhysiology.Snapshot(i)!).ToArray(),
        };
        List<string> notes = ["Rough population estimate, not a confidence interval. Not added to your calorie target.",
            "Fat/carbohydrate estimates are not available yet."];
        var method = "Unavailable";
        EnergyRange? kcal = null;
        var canUseHr = !physiology.TakesHeartRateLoweringMedication && physiology.Sex is not null
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
        if (aerobic is null)
        {
            notes.Add("Intensity split needs a measured threshold and HR that can be compared with it.");
        }

        return new(MethodVersion, entry, activity, physiology, zones, method, kcal, aerobic, anaerobic,
            entry.Distance is { } distance ? UnitCodes.Convert(distance, entry.DistanceUnit!, "m") : null, notes);
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
        if (p.ThresholdHr is not { IsEstimate: false, Value: > 0 } threshold || !double.IsFinite(threshold.Value))
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

    private static EnergyRange? Widen(double low, double high, double fraction) =>
        double.IsFinite(low) && double.IsFinite(high) && double.IsFinite(high * (1 + fraction))
            ? new(low * (1 - fraction), high * (1 + fraction)) : null;

    private static bool Positive(double? value) => value is { } v && double.IsFinite(v) && v > 0;

    private static bool Optional(double? value, double min, double max) =>
        value is null || (double.IsFinite(value.Value) && value >= min && value <= max);
}
