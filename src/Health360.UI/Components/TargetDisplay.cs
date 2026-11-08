using System.Globalization;
using Health360.Domain.Nutrition;
using Health360.Domain.Physiology;
using Health360.Domain.Units;

namespace Health360.UI.Components;

/// <summary>How nutrition targets and what they were computed from are shown (docs/calculations.md, "Nutrition targets").</summary>
public static class TargetDisplay
{
    /// <summary>A target in the macro's unit: energy in the user's energy unit, the rest in grams.</summary>
    public static string Amount(Nutrient nutrient, double value, EnergyUnit energy) =>
        nutrient == Nutrient.Energy ? MetricDisplay.Energy(value, energy) : MetricDisplay.Format(value, "g");

    /// <summary>The day's target for one macro, or null when it isn't set or couldn't be computed.</summary>
    public static double? Target(DailyTargets targets, Nutrient nutrient) => nutrient.Code switch
    {
        "energy" => targets.EnergyKcal,
        "protein" => targets.ProteinG,
        "carbohydrate" => targets.CarbohydrateG,
        _ => targets.FatG,
    };

    /// <summary>What the targets were computed from, for the line under them.</summary>
    public static string Basis(DailyTargets targets, TargetSettings settings, UnitPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(prefs);
        if (targets.Method == TargetMethod.Manual)
        {
            return "Set by you. They stay as they are until you change them.";
        }

        var parts = new List<string>();
        if (targets.Maintenance is { } maintenance)
        {
            var adjustment = settings.EnergyAdjustmentKcal switch
            {
                > 0 => $" + {MetricDisplay.Energy(settings.EnergyAdjustmentKcal, prefs.Energy)}",
                < 0 => $" − {MetricDisplay.Energy(-settings.EnergyAdjustmentKcal, prefs.Energy)}",
                _ => "",
            };
            parts.Add($"Estimate: maintenance {MetricDisplay.Energy(maintenance.Kcal, prefs.Energy)} (RMR " +
                $"{MetricDisplay.Energy(maintenance.Rmr.Value, prefs.Energy)}, {RmrSource(maintenance.Rmr)}, × activity " +
                $"{maintenance.Pal.ToString("0.###", CultureInfo.InvariantCulture)}){adjustment}.");
        }

        var ratio = $"{settings.ProteinRatio.ToString("0.##", CultureInfo.CurrentCulture)} {RatioUnitName(settings.ProteinRatioUnit)}";
        var basis = settings.ProteinBasis == ProteinBasis.LeanMass ? "lean mass" : "body weight";
        parts.Add(targets.ReferenceMassKg is { } mass
            ? $"Protein {ratio} of {basis} ({MetricDisplay.BodyWeight(mass, prefs.BodyWeight)})."
            : $"Protein {ratio} of {basis}.");
        parts.Add($"Fat {settings.FatPercent.ToString("0.#", CultureInfo.CurrentCulture)}% of energy; carbohydrate is the rest.");
        return string.Join(" ", parts);
    }

    /// <summary>What to do about each problem, in the order they were found.</summary>
    public static string Problem(TargetProblem problem) => problem switch
    {
        TargetProblem.NoMaintenance =>
            "There's no maintenance estimate yet. It needs your RMR, or your weight, height, birth date and sex for " +
            "calculations to estimate it, and your activity level.",
        TargetProblem.EnergyNotPositive => "Maintenance with your adjustment is 0 or less. Make the adjustment smaller.",
        TargetProblem.NoBodyWeight => "Log a weigh-in to set protein from your body weight.",
        TargetProblem.NoLeanMass =>
            "Protein from lean mass needs a measured body-fat percentage and a weigh-in. Log them, or base protein on body weight.",
        TargetProblem.MacrosExceedEnergy =>
            "Protein and fat take more energy than the target, so nothing is left for carbohydrate. Lower one of them.",
        _ => "",
    };

    public static string RatioUnitName(ProteinRatioUnit unit) => unit == ProteinRatioUnit.GramsPerPound ? "g/lb" : "g/kg";

    private static string RmrSource(Resolution rmr) => rmr.Measurement is { } measured
        ? $"{MetricDisplay.SourceName(MeasurementSources.Code(measured.Source))}, {MetricDisplay.Date(measured.MeasuredOn)}"
        : $"{rmr.Estimate?.Method ?? "estimated"} estimate";
}
