namespace Health360.Domain.Training;

/// <summary>
/// A deliberately broad population proxy, not a metabolic test. Calibration: Romijn et al. (1993), Table 1,
/// five fasted endurance-trained cyclists. Nonprotein gas and energy constants: Péronnet &amp; Massicotte (1991), Table 1.
/// Derivations, sources, interpolation and product range choices are documented in calculations.md.
/// </summary>
public static class CardioSubstrates
{
    public const string Method = "Romijn 1993 population proxy; Péronnet–Massicotte 1991";
    public const double FatKcalPerGram = 9.746;
    public const double CarbKcalPerGram = 3.8683;

    /// <summary>Fat energy share from nonprotein RQ, equations 5–8 of Péronnet &amp; Massicotte (not Lusk's older table).</summary>
    public static double FatFraction(double rq)
    {
        if (!double.IsFinite(rq)) throw new ArgumentOutOfRangeException(nameof(rq));
        if (rq <= 0.7036) return 1;
        if (rq >= 0.9960) return 0;
        var gramsFatPerGramGlucose = (0.7426 - 0.7455 * rq) / (2.0092 * rq - 1.4136);
        return Math.Clamp(1 - CarbKcalPerGram / (CarbKcalPerGram + FatKcalPerGram * gramsFatPerGramGlucose), 0, 1);
    }

    /// <summary>Study-derived RQ at 25/65/85% VO₂max. Outside that range the full end interval is returned.</summary>
    public static EnergyRange RqRange(double lowFractionVo2Max, double highFractionVo2Max, double uncertainty = 0.05)
    {
        if (!double.IsFinite(lowFractionVo2Max) || !double.IsFinite(highFractionVo2Max)
            || lowFractionVo2Max > highFractionVo2Max || !double.IsFinite(uncertainty) || uncertainty < 0)
        {
            throw new ArgumentException("A finite ordered intensity range is required.");
        }

        var low = lowFractionVo2Max < 0.25 ? 0.7036 : RqAt(Math.Min(0.85, lowFractionVo2Max));
        var high = highFractionVo2Max > 0.85 ? 0.9960 : RqAt(Math.Max(0.25, highFractionVo2Max));
        return new(Math.Max(0.7036, low - uncertainty), Math.Min(0.9960, high + uncertainty));
    }

    public static FuelEstimate Allocate(EnergyRange kcal, EnergyRange rq, string method = Method)
    {
        var lowFat = FatFraction(rq.High);
        var highFat = FatFraction(rq.Low);
        var fat = new EnergyRange(kcal.Low * lowFat, kcal.High * highFat);
        var carb = new EnergyRange(kcal.Low * (1 - highFat), kcal.High * (1 - lowFat));
        return new(fat, carb, new(fat.Low / FatKcalPerGram, fat.High / FatKcalPerGram),
            new(carb.Low / CarbKcalPerGram, carb.High / CarbKcalPerGram), method);
    }

    public static double RqAt(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction is < 0.25 or > 0.85) throw new ArgumentOutOfRangeException(nameof(fraction));
        // Table 1's FFA oxidation in µmol/kg/min becomes fat g/kg/min using the study's 860 g/mol triglyceride.
        // The study mean VO₂max is 67 ml/kg/min; gas stoichiometry recovers a nonprotein RQ for each point.
        var a = fraction <= 0.65 ? 0.25 : 0.65;
        var b = fraction <= 0.65 ? 0.65 : 0.85;
        var fa = Rq(a, a == 0.25 ? 26.8 : 42.8);
        var fb = Rq(b, b == 0.65 ? 42.8 : 29.6);
        return fa + (fb - fa) * (fraction - a) / (b - a);
    }

    private static double Rq(double fraction, double ffaMicromoles)
    {
        var fatGrams = ffaMicromoles * 860 / 3_000_000;
        var oxygen = 0.067 * fraction;
        var glucoseGrams = Math.Max(0, (oxygen - 2.0092 * fatGrams) / 0.7455);
        return (1.4136 * fatGrams + 0.7426 * glucoseGrams) / oxygen;
    }
}
