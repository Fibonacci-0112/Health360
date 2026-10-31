namespace Health360.Domain.Physiology;

/// <summary>
/// The activity level onboarding asks for (<c>profiles.activity_level</c>). The five levels split the three
/// lifestyle bands of FAO/WHO/UNU (2004), Table 5.3; see <see cref="ActivityLevels"/>.
/// </summary>
public enum ActivityLevel
{
    Sedentary,
    Light,
    Moderate,
    Active,
    VeryActive,
}

/// <summary>A physical activity level: total energy expenditure as a multiple of basal metabolic rate.</summary>
public sealed record PalBand(double Low, double High, double Value);

public static class ActivityLevels
{
    /// <summary>The stored code, as in <c>profiles.activity_level</c>.</summary>
    public static string Code(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => "sedentary",
        ActivityLevel.Light => "light",
        ActivityLevel.Moderate => "moderate",
        ActivityLevel.Active => "active",
        ActivityLevel.VeryActive => "very_active",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown activity level"),
    };

    public static bool TryParse(string? code, out ActivityLevel level)
    {
        foreach (var candidate in Enum.GetValues<ActivityLevel>())
        {
            if (Code(candidate) == code)
            {
                level = candidate;
                return true;
            }
        }

        level = default;
        return false;
    }

    /// <summary>
    /// The PAL range and the value used for each level. FAO/WHO/UNU (2004), Table 5.3: sedentary or light
    /// 1.40–1.69, active or moderately active 1.70–1.99, vigorous 2.00–2.40. Sedentary and Light split the
    /// first band in half, Moderate is the whole second band, and Active and Very active split the third; each
    /// level uses the midpoint of its range. The split is this app's choice, documented in docs/calculations.md.
    /// </summary>
    public static PalBand Pal(ActivityLevel level) => level switch
    {
        ActivityLevel.Sedentary => new PalBand(1.40, 1.55, 1.475),
        ActivityLevel.Light => new PalBand(1.55, 1.70, 1.625),
        ActivityLevel.Moderate => new PalBand(1.70, 2.00, 1.85),
        ActivityLevel.Active => new PalBand(2.00, 2.20, 2.10),
        ActivityLevel.VeryActive => new PalBand(2.20, 2.40, 2.30),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown activity level"),
    };
}

/// <summary>
/// Maintenance energy: resting metabolic rate × PAL. Always an estimate, even when the RMR was measured,
/// because the activity multiplier is.
/// </summary>
/// <param name="Kcal">Estimated total energy expenditure, kcal/day.</param>
/// <param name="Rmr">The resolved RMR it was computed from, with its source.</param>
/// <param name="Level">The activity level.</param>
/// <param name="Pal">The multiplier used.</param>
public sealed record MaintenanceEnergy(double Kcal, Resolution Rmr, ActivityLevel Level, double Pal)
{
    public static MaintenanceEnergy From(Resolution rmr, ActivityLevel level)
    {
        ArgumentNullException.ThrowIfNull(rmr);
        var pal = ActivityLevels.Pal(level).Value;
        return new MaintenanceEnergy(rmr.Value * pal, rmr, level, pal);
    }
}
