namespace Health360.Domain.Units;

/// <summary>
/// Exact conversion factors (PLAN.md §2.6). Each <see cref="Unit"/> is defined as a ratio to its
/// dimension's base unit, so definitions such as "1 kcal = 4.184 kJ" appear literally instead of as
/// a pre-divided double. Nothing else in the codebase may hard-code a conversion factor.
/// </summary>
public static class Factors
{
    /// <summary>International avoirdupois pound (1959 agreement).</summary>
    public const double KgPerLb = 0.45359237;

    /// <summary>Avoirdupois ounce = 1/16 lb.</summary>
    public const double GPerOz = 28.349523125;

    /// <summary>International inch (1959 agreement).</summary>
    public const double CmPerIn = 2.54;

    /// <summary>International mile = 5280 ft.</summary>
    public const double MPerMi = 1609.344;

    /// <summary>US customary fluid ounce = 1/128 US gallon (231 in³).</summary>
    public const double MlPerUsFlOz = 29.5735295625;

    /// <summary>Thermochemical calorie.</summary>
    public const double KJPerKcal = 4.184;

    /// <summary>
    /// Glucose only: mg/dL per mmol/L, from the molar mass of C6H12O6 (180.156 g/mol).
    /// Every other lab analyte has its own factor in <c>biomarkers.unit_conversions</c>; never reuse this one.
    /// </summary>
    public const double GlucoseMgDlPerMmolL = 18.0156;
}

/// <summary>value_in_base = value × <see cref="Num"/> / <see cref="Den"/>.</summary>
internal readonly record struct Ratio(double Num, double Den);
