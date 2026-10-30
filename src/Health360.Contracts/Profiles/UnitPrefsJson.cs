using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Health360.Domain.Units;

namespace Health360.Contracts.Profiles;

/// <summary>One measurement type in <c>profiles.unit_prefs</c>: its JSON key, its codes and how to read and set it.</summary>
public sealed class UnitPrefField
{
    private readonly Func<UnitPrefs, string> get;
    private readonly Func<UnitPrefs, string, UnitPrefs?> set;

    private UnitPrefField(string key, IReadOnlyList<string> codes, Func<UnitPrefs, string> get, Func<UnitPrefs, string, UnitPrefs?> set)
    {
        Key = key;
        Codes = codes;
        this.get = get;
        this.set = set;
    }

    /// <summary>The key in <c>unit_prefs</c>, the same as in the prototype's <c>UnitPrefs</c> type.</summary>
    public string Key { get; }

    /// <summary>The stored codes, in the order the editor lists them.</summary>
    public IReadOnlyList<string> Codes { get; }

    public string CodeOf(UnitPrefs prefs) => get(prefs);

    /// <summary>The preferences with this type set to <paramref name="code"/>; null if the code isn't one of <see cref="Codes"/>.</summary>
    public UnitPrefs? With(UnitPrefs prefs, string? code) => code is null ? null : set(prefs, code);

    internal static UnitPrefField Create<T>(string key, CodeMap<T> codes, Func<UnitPrefs, T> get, Func<UnitPrefs, T, UnitPrefs> set)
        where T : struct, Enum =>
        new(
            key,
            [.. codes.Values.Select(codes.ToCode)],
            prefs => codes.ToCode(get(prefs)),
            (prefs, code) => codes.TryParse(code, out var value) ? set(prefs, value) : null);
}

/// <summary>
/// Reads and writes <c>profiles.unit_prefs</c> (PLAN.md §2.6) in the prototype's format: an object of
/// codes keyed like <c>{"bodyWeight":"kg","load":"lb",...}</c>, the same codes as <c>UNIT_PREF_OPTIONS</c>.
/// </summary>
public static class UnitPrefsJson
{
    /// <summary>Every measurement type, in the order onboarding and Settings list them.</summary>
    public static IReadOnlyList<UnitPrefField> Fields { get; } =
    [
        UnitPrefField.Create("bodyWeight", UnitPrefOptions.BodyWeight, p => p.BodyWeight, (p, v) => p with { BodyWeight = v }),
        UnitPrefField.Create("load", UnitPrefOptions.Load, p => p.Load, (p, v) => p with { Load = v }),
        UnitPrefField.Create("height", UnitPrefOptions.Height, p => p.Height, (p, v) => p with { Height = v }),
        UnitPrefField.Create("circumference", UnitPrefOptions.Circumference, p => p.Circumference, (p, v) => p with { Circumference = v }),
        UnitPrefField.Create("distance", UnitPrefOptions.Distance, p => p.Distance, (p, v) => p with { Distance = v }),
        UnitPrefField.Create("shortDistance", UnitPrefOptions.ShortDistance, p => p.ShortDistance, (p, v) => p with { ShortDistance = v }),
        UnitPrefField.Create("pace", UnitPrefOptions.Pace, p => p.Pace, (p, v) => p with { Pace = v }),
        UnitPrefField.Create("foodMass", UnitPrefOptions.FoodMass, p => p.FoodMass, (p, v) => p with { FoodMass = v }),
        UnitPrefField.Create("volume", UnitPrefOptions.Volume, p => p.Volume, (p, v) => p with { Volume = v }),
        UnitPrefField.Create("energy", UnitPrefOptions.Energy, p => p.Energy, (p, v) => p with { Energy = v }),
        UnitPrefField.Create("temperature", UnitPrefOptions.Temperature, p => p.Temperature, (p, v) => p with { Temperature = v }),
        UnitPrefField.Create("glucose", UnitPrefOptions.Glucose, p => p.Glucose, (p, v) => p with { Glucose = v }),
        UnitPrefField.Create("labValues", UnitPrefOptions.LabValues, p => p.LabValues, (p, v) => p with { LabValues = v }),
        UnitPrefField.Create("proteinRatio", UnitPrefOptions.ProteinRatio, p => p.ProteinRatio, (p, v) => p with { ProteinRatio = v }),
    ];

    public static string Serialize(UnitPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in Fields)
            {
                writer.WriteString(field.Key, field.CodeOf(prefs));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Stored preferences, with <paramref name="fallback"/> (the locale defaults) for any type not saved
    /// yet. If anything stored is invalid (an unknown code, not an object, not JSON) the whole fallback is
    /// used, as the prototype's <c>parseUnitPrefs</c> does.
    /// </summary>
    public static UnitPrefs Parse(string? json, UnitPrefs fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (string.IsNullOrEmpty(json))
        {
            return fallback;
        }

        JsonObject? stored;
        try
        {
            stored = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return fallback;
        }

        if (stored is null)
        {
            return fallback;
        }

        var prefs = fallback;
        foreach (var field in Fields)
        {
            if (!stored.TryGetPropertyValue(field.Key, out var node))
            {
                continue;
            }

            string? code = null;
            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                code = text;
            }

            if (field.With(prefs, code) is not { } next)
            {
                return fallback;
            }

            prefs = next;
        }

        return prefs;
    }

    /// <summary>
    /// The editor's change: sets one type, and keeps the protein ratio following the body-weight unit
    /// while it is still on the matching default (as the prototype's <c>UnitPrefsEditor</c> does).
    /// </summary>
    public static UnitPrefs Choose(UnitPrefs prefs, UnitPrefField field, string code)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        ArgumentNullException.ThrowIfNull(field);
        var next = field.With(prefs, code) ?? throw new ArgumentException($"{code} is not a {field.Key} unit", nameof(code));
        if (field.Key == "bodyWeight" && prefs.ProteinRatio == UnitPrefs.ProteinRatioDefault(prefs.BodyWeight))
        {
            next = next with { ProteinRatio = UnitPrefs.ProteinRatioDefault(next.BodyWeight) };
        }

        return next;
    }
}
