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
            estimate.DistanceMetres), CardioJsonContext.Default.CardioEstimateSummary);

    public static string SerializeTimes(IReadOnlyList<double> times) =>
        JsonSerializer.Serialize(times.ToArray(), CardioJsonContext.Default.DoubleArray);

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
                    summary.Method, summary.Kcal, summary.AerobicSeconds, summary.AnaerobicSeconds, summary.DistanceMetres, [])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(CardioEstimate? value) =>
        value is { Version: CardioEnergy.MethodVersion, Entry: not null, Activity: not null, Physiology: not null, Notes: not null }
        && CardioEnergy.Errors(value.Entry, value.Zones).Count == 0
        && ValidRange(value.Kcal);

    private static bool IsValid(CardioEstimateSummary? value) =>
        value is { Version: CardioEnergy.MethodVersion, Entry: not null, Activity: not null }
        && CardioEnergy.Errors(value.Entry, null).Count == 0
        && ValidRange(value.Kcal);

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
    double? DistanceMetres);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CardioEstimate))]
[JsonSerializable(typeof(CardioEstimateSummary))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class CardioJsonContext : JsonSerializerContext;
