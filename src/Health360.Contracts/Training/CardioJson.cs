using System.Text.Json;
using System.Text.Json.Serialization;
using Health360.Domain.Training;

namespace Health360.Contracts.Training;

/// <summary>Versioned cardio snapshots, using generated metadata for the trimmed MAUI client.</summary>
public static class CardioJson
{
    public static string Serialize(CardioEstimate estimate) =>
        JsonSerializer.Serialize(estimate, CardioJsonContext.Default.CardioEstimate);

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
            return value is { Version: CardioEnergy.MethodVersion, Entry: not null, Activity: not null, Physiology: not null, Notes: not null }
                && CardioEnergy.Errors(value.Entry, value.Zones).Count == 0
                && (value.Kcal is null || (double.IsFinite(value.Kcal.Low) && double.IsFinite(value.Kcal.High)
                    && value.Kcal.Low >= 0 && value.Kcal.High >= value.Kcal.Low)) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CardioEstimate))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class CardioJsonContext : JsonSerializerContext;
