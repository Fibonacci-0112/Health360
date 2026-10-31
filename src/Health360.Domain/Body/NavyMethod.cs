namespace Health360.Domain.Body;

/// <summary>
/// A body-fat estimate from the U.S. Navy circumference equations: predicted body density, then percent
/// fat by Siri (1961). <paramref name="StandardError"/> is the equation's standard error of estimate in
/// percent-fat units, as the report gives it, for showing the estimate as a range.
/// </summary>
public sealed record NavyBodyFat(double Density, double PercentFat, double StandardError);

/// <summary>
/// The Navy (NHRC) circumference method, exactly as published by Hodgdon and Beckett (1984). The density
/// equations take centimetres; convert through <c>Units</c> first and never through a rounded display value.
/// Calculation spec and sources: docs/calculations.md.
/// </summary>
public static class NavyMethod
{
    /// <summary>
    /// Men (NHRC Report 84-11, p. 12): neck just below the larynx, abdomen II at the level of the
    /// umbilicus. Null when the inputs can't be used (non-finite or non-positive, abdomen not larger than
    /// neck) or the result isn't a percentage.
    /// </summary>
    public static NavyBodyFat? Male(double heightCm, double neckCm, double abdomenAtUmbilicusCm)
    {
        if (!Positive(heightCm) || !Positive(neckCm) || !Positive(abdomenAtUmbilicusCm))
        {
            return null;
        }

        var circumference = abdomenAtUmbilicusCm - neckCm;
        if (circumference <= 0)
        {
            return null;
        }

        var density = (-0.19077 * Math.Log10(circumference)) + (0.15456 * Math.Log10(heightCm)) + 1.0324;
        return Result(density, MaleStandardError);
    }

    /// <summary>
    /// Women (NHRC Report 84-29, p. 12): neck just below the larynx, abdomen I at the level of minimal
    /// abdominal width (about midway between the xiphoid and the umbilicus), hip at the greatest
    /// protrusion of the gluteal muscles. Null under the same conditions as <see cref="Male"/>.
    /// </summary>
    public static NavyBodyFat? Female(double heightCm, double neckCm, double abdomenAtNarrowestCm, double hipCm)
    {
        if (!Positive(heightCm) || !Positive(neckCm) || !Positive(abdomenAtNarrowestCm) || !Positive(hipCm))
        {
            return null;
        }

        var circumference = abdomenAtNarrowestCm + hipCm - neckCm;
        if (circumference <= 0)
        {
            return null;
        }

        var density = (-0.35004 * Math.Log10(circumference)) + (0.22100 * Math.Log10(heightCm)) + 1.29579;
        return Result(density, FemaleStandardError);
    }

    /// <summary>Siri (1961), as both reports use it: %BF = 100 × (4.95 / density − 4.50).</summary>
    public static double SiriPercentFat(double density) => 100 * ((4.95 / density) - 4.50);

    /// <summary>Standard error of estimate, men: 3.52 percent-fat units (Report 84-11, p. 12).</summary>
    public const double MaleStandardError = 3.52;

    /// <summary>Standard error of estimate, women: 3.72 percent-fat units (Report 84-29, p. 12).</summary>
    public const double FemaleStandardError = 3.72;

    private static NavyBodyFat? Result(double density, double standardError)
    {
        var percent = SiriPercentFat(density);
        return double.IsFinite(percent) && percent is >= 0 and < 100
            ? new NavyBodyFat(density, percent, standardError)
            : null;
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
