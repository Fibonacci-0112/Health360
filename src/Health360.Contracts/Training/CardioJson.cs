using System.Text.Json;
using System.Text.Json.Serialization;
using Health360.Domain.Training;

namespace Health360.Contracts.Training;

/// <summary>Versioned cardio snapshots, using generated metadata for the trimmed MAUI client.</summary>
public static class CardioJson
{
    public static string Serialize(CardioEstimate estimate) =>
        JsonSerializer.Serialize(estimate, CardioJsonContext.Default.CardioEstimate);

    public static string SerializeWorkoutSummary(CardioEstimate estimate) =>
        JsonSerializer.Serialize(new CardioEstimateSummary(
            estimate.Version,
            estimate.Entry with { ZoneSeconds = null },
            estimate.Activity,
            estimate.Method.Replace(" (VO₂max)", "", StringComparison.Ordinal),
            estimate.Kcal,
            estimate.AerobicSeconds,
            estimate.AnaerobicSeconds,
            estimate.DistanceMetres, SharedIntensity(estimate), estimate.Fuel, estimate.ElevationMetres), CardioJsonContext.Default.CardioEstimateSummary);

    /// <summary>
    /// Shared only when it follows from shared fields alone (RPE categories with no HR input); otherwise its presence or method
    /// could reveal measured thresholds or heart-rate-lowering medication. Matches private.cardio_workout_summary.
    /// </summary>
    private static IntensityMinutes? SharedIntensity(CardioEstimate estimate) =>
        estimate.Intensity is { Method: "RPE categories" } intensity && estimate.Entry.AverageHr is null && estimate.Entry.ZoneSeconds is null
            ? intensity : null;

    public static string SerializeDraft(CardioDraft draft) => JsonSerializer.Serialize(draft, CardioJsonContext.Default.CardioDraft);
    public static CardioDraft? ParseDraft(string? json)
    {
        try
        {
            var draft = json is null ? null : JsonSerializer.Deserialize(json, CardioJsonContext.Default.CardioDraft);
            return draft is
            {
                ActivityCode: not null, Minutes: not null, Distance: not null, DistanceUnit: not null, AverageHr: not null,
                MaxHr: not null, Rpe: not null, ZoneMinutes: not null, ZoneKey: not null, Elevation: not null, ElevationUnit: not null, EndDate: not null, EndTime: not null
            }
                && draft.ZoneMinutes.Length <= 10 ? draft : null;
        }
        catch (JsonException) { return null; }
    }

    public static string SerializeTimes(IReadOnlyList<double> times) =>
        JsonSerializer.Serialize([.. times], CardioJsonContext.Default.DoubleArray);

    /// <summary>Unknown versions and malformed snapshots aren't shown as current estimates.</summary>
    public static CardioEstimate? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize(json, CardioJsonContext.Default.CardioEstimate);
            if (IsValid(value))
            {
                return value;
            }

            var summary = JsonSerializer.Deserialize(json, CardioJsonContext.Default.CardioEstimateSummary);
            return IsValid(summary)
                ? new CardioEstimate(summary!.Version, summary.Entry, summary.Activity, new(null, null, null), null,
                    summary.Method, summary.Kcal, summary.AerobicSeconds, summary.AnaerobicSeconds, summary.DistanceMetres, [], summary.Intensity, summary.Fuel, summary.ElevationMetres)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(CardioEstimate? value) =>
        value is { Version: >= 1 and <= CardioEnergy.MethodVersion, Entry: not null, Activity: not null, Physiology: not null, Notes: not null }
        && CardioEnergy.Errors(value.Entry, value.Zones).Count == 0
        && ValidRange(value.Kcal) && ValidExtras(value.Entry.DurationSeconds, value.AerobicSeconds, value.AnaerobicSeconds, value.Intensity, value.Fuel);

    private static bool IsValid(CardioEstimateSummary? value) =>
        value is { Version: >= 1 and <= CardioEnergy.MethodVersion, Entry: not null, Activity: not null }
        && CardioEnergy.Errors(value.Entry, null).Count == 0
        && ValidRange(value.Kcal) && ValidExtras(value.Entry.DurationSeconds, value.AerobicSeconds, value.AnaerobicSeconds, value.Intensity, value.Fuel);

    private static bool ValidExtras(double duration, double? aerobic, double? anaerobic, IntensityMinutes? intensity, FuelEstimate? fuel) =>
        (aerobic is null && anaerobic is null || aerobic is >= 0 && anaerobic is >= 0 && double.IsFinite(aerobic.Value)
            && double.IsFinite(anaerobic.Value) && Math.Abs(aerobic.Value + anaerobic.Value - duration) < 0.01)
        && (intensity is null || double.IsFinite(intensity.LowSeconds) && double.IsFinite(intensity.ModerateSeconds)
            && double.IsFinite(intensity.HighSeconds) && intensity.LowSeconds >= 0 && intensity.ModerateSeconds >= 0 && intensity.HighSeconds >= 0
            && Math.Abs(intensity.LowSeconds + intensity.ModerateSeconds + intensity.HighSeconds - duration) < 0.01)
        && (fuel is null || fuel is { FatKcal: not null, CarbKcal: not null, FatGrams: not null, CarbGrams: not null }
            && ValidRange(fuel.FatKcal) && ValidRange(fuel.CarbKcal) && ValidRange(fuel.FatGrams) && ValidRange(fuel.CarbGrams));

    private static bool ValidRange(EnergyRange? value) =>
        value is null || (double.IsFinite(value.Low) && double.IsFinite(value.High) && value.Low >= 0 && value.High >= value.Low);
}

public sealed record CardioEstimateSummary(
    int Version,
    CardioEntry Entry,
    CardioActivity Activity,
    string Method,
    EnergyRange? Kcal,
    double? AerobicSeconds,
    double? AnaerobicSeconds,
    double? DistanceMetres,
    IntensityMinutes? Intensity = null,
    FuelEstimate? Fuel = null,
    double? ElevationMetres = null);

public sealed record CardioDraft(string ActivityCode, string Minutes, string Distance, string DistanceUnit, string AverageHr,
    string MaxHr, string Rpe, bool Intervals, string[] ZoneMinutes, string ZoneKey, string Elevation, string ElevationUnit,
    bool Backdated, string EndDate, string EndTime);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CardioEstimate))]
[JsonSerializable(typeof(CardioEstimateSummary))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(CardioDraft))]
internal sealed partial class CardioJsonContext : JsonSerializerContext;
