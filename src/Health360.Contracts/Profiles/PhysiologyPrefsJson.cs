using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.Contracts.Profiles;

/// <summary>The physiology settings in <c>profiles.physiology_prefs</c> (PLAN.md §2.3) that the app reads so far.</summary>
/// <param name="TakesHeartRateLoweringMedication">
/// Set when the user takes medication that lowers heart rate (a beta blocker, for example). Nothing is then derived
/// from age-predicted max HR (PLAN.md §2.5).
/// </param>
public sealed record PhysiologyPrefs(bool TakesHeartRateLoweringMedication);

/// <summary>
/// Reads and updates <c>profiles.physiology_prefs</c>, a JSON object. Keys the app doesn't know (the pins arrive
/// later) are kept as they are when one setting changes.
/// </summary>
public static class PhysiologyPrefsJson
{
    public const string MedicationKey = "heartRateLoweringMedication";

    /// <summary>The settings; anything missing, not an object or not JSON reads as off.</summary>
    public static PhysiologyPrefs Parse(string? json) =>
        new(Read(json)?[MedicationKey] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag);

    /// <summary>The stored JSON with the medication flag set to <paramref name="value"/>.</summary>
    public static string WithMedication(string? json, bool value)
    {
        var stored = Read(json) ?? [];
        stored[MedicationKey] = value;
        return stored.ToJsonString();
    }

    private static JsonObject? Read(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
