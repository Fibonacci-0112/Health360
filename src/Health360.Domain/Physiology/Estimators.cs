namespace Health360.Domain.Physiology;

/// <summary>
/// A value computed from a published equation. <paramref name="Method"/> names the equation for the
/// "estimated" label and the info view. <paramref name="StandardError"/> is set only when the source gives one.
/// </summary>
public sealed record Estimate(double Value, string Method, double? StandardError = null);

/// <summary>Estimated methods, named as the UI shows them.</summary>
public static class EstimateMethods
{
    public const string MifflinStJeor = "Mifflin-St Jeor";
    public const string KatchMcArdle = "Katch-McArdle";
    public const string Tanaka = "Tanaka";
}

/// <summary>Resting metabolic rate (kcal/day). Sources: docs/calculations.md.</summary>
public static class RestingMetabolicRate
{
    /// <summary>
    /// Mifflin et al. (1990), with the coefficients as published: 9.99 × kg + 6.25 × cm − 4.92 × years
    /// + 166 × sex (male 1, female 0) − 161.
    /// </summary>
    public static Estimate MifflinStJeor(Sex sex, double weightKg, double heightCm, int ageYears)
    {
        Positive(weightKg, nameof(weightKg));
        Positive(heightCm, nameof(heightCm));
        if (ageYears < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ageYears), ageYears, "Age can't be negative");
        }

        var male = sex == Sex.Male ? 1 : 0;
        var kcal = (9.99 * weightKg) + (6.25 * heightCm) - (4.92 * ageYears) + (166 * male) - 161;
        return new Estimate(kcal, EstimateMethods.MifflinStJeor);
    }

    /// <summary>Katch-McArdle: 370 + 21.6 × fat-free mass (kg). Used when fat-free mass is known.</summary>
    public static Estimate KatchMcArdle(double fatFreeMassKg)
    {
        Positive(fatFreeMassKg, nameof(fatFreeMassKg));
        return new Estimate(370 + (21.6 * fatFreeMassKg), EstimateMethods.KatchMcArdle);
    }

    /// <summary>
    /// The estimator PLAN.md §2.8 prescribes: Katch-McArdle when fat-free mass is known, otherwise
    /// Mifflin-St Jeor. Null when neither has its inputs.
    /// </summary>
    public static Estimate? Best(Sex? sex, double? weightKg, double? heightCm, int? ageYears, double? fatFreeMassKg) =>
        fatFreeMassKg is { } ffm ? KatchMcArdle(ffm)
        : sex is { } s && weightKg is { } w && heightCm is { } h && ageYears is { } a ? MifflinStJeor(s, w, h, a)
        : null;

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive number");
        }
    }
}

/// <summary>Maximum heart rate (bpm).</summary>
public static class MaxHeartRate
{
    /// <summary>
    /// Tanaka et al. (2001): 208 − 0.7 × age. Null when the user takes medication that lowers heart rate,
    /// because nothing may be derived from age-predicted max HR then (PLAN.md §2.5).
    /// </summary>
    public static Estimate? Tanaka(int ageYears, bool takesHeartRateLoweringMedication)
    {
        if (ageYears < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ageYears), ageYears, "Age can't be negative");
        }

        return takesHeartRateLoweringMedication ? null : new Estimate(208 - (0.7 * ageYears), EstimateMethods.Tanaka);
    }
}
