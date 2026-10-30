namespace Health360.Domain.Units;

public enum ProteinRatioUnit
{
    GramsPerKilogram,
    GramsPerPound,
}

/// <summary>The canonical protein ratio is g per kg (of body weight or of lean mass).</summary>
public static class ProteinRatio
{
    // 1 g/lb = 1 g per 0.45359237 kg.
    public static double ToGPerKg(double ratio, ProteinRatioUnit unit) =>
        unit == ProteinRatioUnit.GramsPerKilogram ? ratio : ratio / Factors.KgPerLb;

    public static double FromGPerKg(double gPerKg, ProteinRatioUnit unit) =>
        unit == ProteinRatioUnit.GramsPerKilogram ? gPerKg : gPerKg * Factors.KgPerLb;

    /// <summary>Daily protein grams from a ratio and the reference mass in kg (body weight or lean mass).</summary>
    public static double DailyGrams(double ratio, ProteinRatioUnit unit, double referenceMassKg) =>
        ToGPerKg(ratio, unit) * referenceMassKg;
}
