namespace Health360.UI.Components;

/// <summary>Display names for the profile's coded choices, shared by onboarding and Settings.</summary>
public static class ProfileLabels
{
    public static IReadOnlyDictionary<string, string> Sex { get; } = new Dictionary<string, string>
    {
        ["female"] = "Female",
        ["male"] = "Male",
    };

    public static IReadOnlyDictionary<string, string> Activity { get; } = new Dictionary<string, string>
    {
        ["sedentary"] = "Mostly sitting",
        ["light"] = "Light",
        ["moderate"] = "Moderate",
        ["active"] = "Active",
        ["very_active"] = "Very active",
    };
}
