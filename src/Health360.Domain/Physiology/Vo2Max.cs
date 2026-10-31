namespace Health360.Domain.Physiology;

/// <summary>Non-exercise VO₂max estimates (ml/kg/min). Sources: docs/calculations.md.</summary>
public static class Vo2Max
{
    /// <summary>The highest code on the NASA/JSC physical activity scale (PA-R), which runs 0–7.</summary>
    public const int MaxActivityCode = 7;

    /// <summary>
    /// Jackson et al. (1990), N-Ex BMI model II (no resting HR): 56.363 + 1.921 × PA-R − 0.381 × age
    /// − 0.754 × BMI + 10.987 × sex (male 1, female 0). SE 5.70 ml/kg/min. Null if the result isn't positive.
    /// </summary>
    public static Estimate? JacksonBmi(Sex sex, int activityCode, int ageYears, double bmi)
    {
        Check(activityCode, ageYears);
        Positive(bmi, nameof(bmi));
        var value = 56.363 + (1.921 * activityCode) - (0.381 * ageYears) - (0.754 * bmi) + (10.987 * Male(sex));
        return value > 0 ? new Estimate(value, EstimateMethods.JacksonBmi, 5.70) : null;
    }

    /// <summary>
    /// Jackson et al. (1990), N-Ex %fat model II (no resting HR): 50.513 + 1.589 × PA-R − 0.289 × age
    /// − 0.552 × %fat + 5.863 × sex (male 1, female 0). SE 5.35 ml/kg/min. Null if the result isn't positive.
    /// </summary>
    public static Estimate? JacksonPercentFat(Sex sex, int activityCode, int ageYears, double percentFat)
    {
        Check(activityCode, ageYears);
        if (!double.IsFinite(percentFat) || percentFat < 0 || percentFat >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentFat), percentFat, "Must be in [0, 100)");
        }

        var value = 50.513 + (1.589 * activityCode) - (0.289 * ageYears) - (0.552 * percentFat) + (5.863 * Male(sex));
        return value > 0 ? new Estimate(value, EstimateMethods.JacksonPercentFat, 5.35) : null;
    }

    /// <summary>
    /// The Jackson model to use: %fat when body fat is known (the paper's more accurate model), otherwise BMI.
    /// Null when sex, the activity code, age, or both body-composition inputs are missing.
    /// </summary>
    public static Estimate? Jackson(Sex? sex, int? activityCode, int? ageYears, double? bmi, double? percentFat) =>
        sex is not { } s || activityCode is not { } pa || ageYears is not { } age ? null
        : percentFat is { } fat ? JacksonPercentFat(s, pa, age, fat)
        : bmi is { } b ? JacksonBmi(s, pa, age, b)
        : null;

    private static int Male(Sex sex) => sex switch
    {
        Sex.Male => 1,
        Sex.Female => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(sex), sex, "Unknown sex"),
    };

    private static void Check(int activityCode, int ageYears)
    {
        if (activityCode is < 0 or > MaxActivityCode)
        {
            throw new ArgumentOutOfRangeException(nameof(activityCode), activityCode, "PA-R runs 0–7");
        }

        if (ageYears < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ageYears), ageYears, "Age can't be negative");
        }
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive number");
        }
    }
}
