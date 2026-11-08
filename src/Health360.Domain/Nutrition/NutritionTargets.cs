using Health360.Domain.Physiology;
using Health360.Domain.Units;

namespace Health360.Domain.Nutrition;

/// <summary>How a profile's daily targets are set (<c>nutrition_targets.method</c>).</summary>
public enum TargetMethod
{
    /// <summary>From the physiology profile: maintenance energy plus an adjustment, protein from a ratio, fat from a share of energy.</summary>
    Formula,

    /// <summary>Typed in by the user; an explicit override that nothing recomputes.</summary>
    Manual,
}

/// <summary>The mass a protein ratio is multiplied by (<c>nutrition_targets.protein_basis</c>).</summary>
public enum ProteinBasis
{
    /// <summary>The trend body weight.</summary>
    BodyWeight,

    /// <summary>Fat-free mass, which needs a measured body-fat percentage.</summary>
    LeanMass,
}

/// <summary>Why a formula target couldn't be computed, or not completely.</summary>
public enum TargetProblem
{
    /// <summary>No maintenance energy: the profile lacks an RMR (or what estimates one) or an activity level.</summary>
    NoMaintenance,

    /// <summary>Maintenance plus the adjustment is 0 kcal or less.</summary>
    EnergyNotPositive,

    /// <summary>Protein is per body weight and there is no weigh-in.</summary>
    NoBodyWeight,

    /// <summary>Protein is per lean mass and there is no fat-free mass (no measured body fat, or no weigh-in).</summary>
    NoLeanMass,

    /// <summary>Protein and fat take more energy than the target, leaving none for carbohydrate.</summary>
    MacrosExceedEnergy,
}

/// <summary>
/// What a profile's targets are set to (one <c>nutrition_targets</c> row). The formula fields are kept when the
/// method is manual, so switching back restores them. Energy is in kcal; the ratio is as entered with its unit.
/// </summary>
/// <param name="Method">Formula or manual.</param>
/// <param name="EnergyAdjustmentKcal">Added to maintenance energy (negative for a deficit). Formula only.</param>
/// <param name="ProteinRatio">Protein per kg or lb of <paramref name="ProteinBasis"/>, as entered.</param>
/// <param name="ProteinRatioUnit">The unit of <paramref name="ProteinRatio"/>.</param>
/// <param name="ProteinBasis">Body weight or lean mass.</param>
/// <param name="FatPercent">Fat's share of the energy target, in percent.</param>
/// <param name="ManualEnergyKcal">The manual energy target. Required for a manual target.</param>
/// <param name="ManualProteinG">The manual protein target, or none.</param>
/// <param name="ManualFatG">The manual fat target, or none.</param>
/// <param name="ManualCarbohydrateG">The manual carbohydrate target, or none.</param>
public sealed record TargetSettings(
    TargetMethod Method,
    double EnergyAdjustmentKcal,
    double ProteinRatio,
    ProteinRatioUnit ProteinRatioUnit,
    ProteinBasis ProteinBasis,
    double FatPercent,
    double? ManualEnergyKcal = null,
    double? ManualProteinG = null,
    double? ManualFatG = null,
    double? ManualCarbohydrateG = null)
{
    /// <summary>
    /// Protein per kg of body weight before the user chooses: about where Morton et al. (2018) found extra protein
    /// stopped adding lean mass during resistance training (docs/calculations.md, "Nutrition targets").
    /// </summary>
    public const double DefaultProteinGPerKg = 1.6;

    /// <summary>Fat's share of energy before the user chooses. A product default, not a sourced figure.</summary>
    public const double DefaultFatPercent = 30;

    /// <summary>The settings a profile starts with: maintenance, 1.6 g/kg of body weight, 30% of energy from fat.</summary>
    public static TargetSettings Default { get; } = new(
        TargetMethod.Formula, 0, DefaultProteinGPerKg, ProteinRatioUnit.GramsPerKilogram, ProteinBasis.BodyWeight, DefaultFatPercent);
}

/// <summary>
/// The day's targets, with what they were computed from. A value is null when it can't be computed or wasn't set;
/// <see cref="Problems"/> says why. Formula targets are estimates, because maintenance energy always is.
/// </summary>
/// <param name="Method">How the targets were set.</param>
/// <param name="EnergyKcal">Energy target, kcal/day.</param>
/// <param name="ProteinG">Protein target, g/day.</param>
/// <param name="FatG">Fat target, g/day.</param>
/// <param name="CarbohydrateG">Carbohydrate target, g/day: the energy protein and fat leave.</param>
/// <param name="Maintenance">The maintenance energy a formula target starts from.</param>
/// <param name="ReferenceMassKg">The body weight or lean mass protein was computed from.</param>
/// <param name="Problems">What kept a formula value from being computed.</param>
public sealed record DailyTargets(
    TargetMethod Method,
    double? EnergyKcal,
    double? ProteinG,
    double? FatG,
    double? CarbohydrateG,
    MaintenanceEnergy? Maintenance,
    double? ReferenceMassKg,
    IReadOnlyList<TargetProblem> Problems);

/// <summary>Daily energy and macronutrient targets (docs/calculations.md, "Nutrition targets").</summary>
public static class NutritionTargets
{
    /// <summary>The general factors, kcal per gram: protein and carbohydrate 4, fat 9 (21 CFR 101.9(c)(1)(i); docs/calculations.md, "Label checks").</summary>
    public const double ProteinKcalPerG = 4;

    public const double CarbohydrateKcalPerG = 4;

    public const double FatKcalPerG = 9;

    /// <summary>
    /// The targets from <paramref name="settings"/> and the physiology profile. A formula target is computed from the
    /// profile every time, so a lab RMR entered or deleted changes it at once:
    /// energy = maintenance + adjustment; protein = ratio (g/kg) × body weight or lean mass;
    /// fat = energy × share ÷ 9; carbohydrate = (energy − 4 × protein − 9 × fat) ÷ 4.
    /// </summary>
    public static DailyTargets Compute(TargetSettings settings, PhysiologyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        if (settings.Method == TargetMethod.Manual)
        {
            return new DailyTargets(TargetMethod.Manual, settings.ManualEnergyKcal, settings.ManualProteinG, settings.ManualFatG,
                settings.ManualCarbohydrateG, null, null, []);
        }

        var problems = new List<TargetProblem>();
        var maintenance = profile.Maintenance;
        double? energy = null;
        if (maintenance is null)
        {
            problems.Add(TargetProblem.NoMaintenance);
        }
        else if (maintenance.Kcal + settings.EnergyAdjustmentKcal is var kcal && kcal > 0)
        {
            energy = kcal;
        }
        else
        {
            problems.Add(TargetProblem.EnergyNotPositive);
        }

        var mass = settings.ProteinBasis == ProteinBasis.LeanMass ? profile.FatFreeMassKg : profile.WeightKg;
        mass = mass is { } m && double.IsFinite(m) && m > 0 ? m : null;
        double? protein = mass is { } reference
            ? ProteinRatio.DailyGrams(settings.ProteinRatio, settings.ProteinRatioUnit, reference)
            : null;
        if (mass is null)
        {
            problems.Add(settings.ProteinBasis == ProteinBasis.LeanMass ? TargetProblem.NoLeanMass : TargetProblem.NoBodyWeight);
        }

        double? fat = energy is { } e ? e * settings.FatPercent / 100 / FatKcalPerG : null;
        double? carbohydrate = null;
        if (energy is { } total && protein is { } p && fat is { } f)
        {
            var left = total - (ProteinKcalPerG * p) - (FatKcalPerG * f);
            if (left >= 0)
            {
                carbohydrate = left / CarbohydrateKcalPerG;
            }
            else
            {
                problems.Add(TargetProblem.MacrosExceedEnergy);
            }
        }

        return new DailyTargets(TargetMethod.Formula, energy, protein, fat, carbohydrate, maintenance, mass, problems);
    }
}
