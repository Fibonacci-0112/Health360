namespace Health360.UI.Components;

/// <summary>Display names for the unit preference types and their codes (the prototype's UnitPrefsEditor labels).</summary>
public static class UnitLabels
{
    public static IReadOnlyDictionary<string, string> Types { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["bodyWeight"] = "Body weight",
        ["load"] = "Lifting load",
        ["height"] = "Height",
        ["circumference"] = "Circumference",
        ["distance"] = "Distance",
        ["shortDistance"] = "Track and swim",
        ["pace"] = "Pace",
        ["foodMass"] = "Food mass",
        ["volume"] = "Volume",
        ["energy"] = "Energy",
        ["temperature"] = "Temperature",
        ["glucose"] = "Blood glucose",
        ["labValues"] = "Lab values",
        ["proteinRatio"] = "Protein target",
    };

    /// <summary>Codes not listed show as they are (kg, lb, cm, mmol/L...).</summary>
    public static IReadOnlyDictionary<string, string> Options { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["st_lb"] = "st + lb",
        ["ft_in"] = "ft + in",
        ["min_per_km"] = "min/km",
        ["min_per_mi"] = "min/mi",
        ["fl_oz"] = "fl oz",
        ["cup"] = "cups",
        ["C"] = "°C",
        ["F"] = "°F",
        ["conventional"] = "US conventional",
        ["si"] = "SI",
        ["g_per_kg"] = "g/kg",
        ["g_per_lb"] = "g/lb",
    };
}
