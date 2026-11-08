using Health360.Domain.Nutrition;
using Health360.Domain.Physiology;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Nutrition;

/// <summary>
/// Nutrition targets (docs/calculations.md, "Nutrition targets"). Expected values are hand calculations from the
/// physiology profile's golden fixture: male, 30, 180 cm, moderately active (PAL 1.85), 80 kg, so Mifflin-St Jeor
/// gives 1781.6 kcal and maintenance 3295.96 kcal.
/// </summary>
public class NutritionTargetsTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static int nextId;

    private static MetricValue V(string code, double value, string date, MeasurementSource source = MeasurementSource.Manual) =>
        new(code, new Measured(new Guid(Interlocked.Increment(ref nextId), 0, 0, new byte[8]), value, source, DateOnly.Parse(date)));

    private static readonly MetricValue Weighed = V(MetricCodes.Weight, 80, "2026-10-30");

    private static PhysiologyProfile Sam(params MetricValue[] values) =>
        PhysiologyProfile.Build(new PhysiologyInputs(Today, values, Sex.Male, new DateOnly(1996, 5, 1), 180, ActivityLevel.Moderate));

    private static readonly TargetSettings Cut = TargetSettings.Default with { EnergyAdjustmentKcal = -500 };

    [Fact]
    public void A_formula_target_is_maintenance_plus_the_adjustment_with_protein_fat_and_the_rest_as_carbohydrate()
    {
        var targets = NutritionTargets.Compute(Cut, Sam(Weighed));

        Assert.Equal(TargetMethod.Formula, targets.Method);
        Assert.Empty(targets.Problems);
        // 1781.6 × 1.85 − 500 = 2795.96
        Assert.True(Close(targets.EnergyKcal!.Value, 2795.96));
        // 1.6 g/kg × 80 kg
        Assert.True(Close(targets.ProteinG!.Value, 128));
        Assert.Equal(80, targets.ReferenceMassKg);
        // 2795.96 × 30% ÷ 9 = 93.198667
        Assert.True(Close(targets.FatG!.Value, 2795.96 * 0.30 / 9));
        // (2795.96 − 4 × 128 − 9 × 93.198667) ÷ 4 = 361.293
        Assert.True(Close(targets.CarbohydrateG!.Value, 361.293));
        Assert.True(targets.Maintenance!.Rmr.IsEstimate);
    }

    [Fact]
    public void A_lab_RMR_entered_changes_the_target_at_once_and_deleting_it_falls_back_to_the_estimate()
    {
        var lab = V(MetricCodes.Rmr, 1700, "2026-10-01", MeasurementSource.Lab);

        var withLab = NutritionTargets.Compute(Cut, Sam(Weighed, lab));
        // 1700 × 1.85 − 500 = 2645; fat 88.1667 g; carbohydrate (2645 − 512 − 793.5) ÷ 4 = 334.875
        Assert.True(Close(withLab.EnergyKcal!.Value, 2645));
        Assert.True(Close(withLab.CarbohydrateG!.Value, 334.875));
        Assert.Same(lab.Measured, withLab.Maintenance!.Rmr.Measurement);

        var deleted = NutritionTargets.Compute(Cut, Sam(Weighed));
        Assert.True(Close(deleted.EnergyKcal!.Value, 2795.96));
        Assert.True(deleted.Maintenance!.Rmr.IsEstimate);
    }

    [Fact]
    public void Protein_can_be_per_pound_and_per_lean_mass()
    {
        // 0.8 g/lb = 0.8 / 0.45359237 g/kg, × 80 kg = 141.095847
        var perPound = NutritionTargets.Compute(
            Cut with { ProteinRatio = 0.8, ProteinRatioUnit = ProteinRatioUnit.GramsPerPound }, Sam(Weighed));
        Assert.True(Close(perPound.ProteinG!.Value, 0.8 / 0.45359237 * 80));

        // 25% body fat: 60 kg fat-free mass, 1.6 × 60 = 96 g. The RMR estimate switches to Katch-McArdle (1666).
        var lean = NutritionTargets.Compute(Cut with { ProteinBasis = ProteinBasis.LeanMass },
            Sam(Weighed, V(MetricCodes.BodyFatPercent, 25, "2026-10-30")));
        Assert.Equal(60, lean.ReferenceMassKg);
        Assert.True(Close(lean.ProteinG!.Value, 96));
        Assert.True(Close(lean.EnergyKcal!.Value, (1666 * 1.85) - 500));
    }

    [Fact]
    public void Switching_the_ratio_unit_keeps_the_daily_grams_beyond_rounding()
    {
        var perKg = NutritionTargets.Compute(Cut, Sam(Weighed));
        var asPound = ProteinRatio.FromGPerKg(1.6, ProteinRatioUnit.GramsPerPound);
        var perPound = NutritionTargets.Compute(
            Cut with { ProteinRatio = asPound, ProteinRatioUnit = ProteinRatioUnit.GramsPerPound }, Sam(Weighed));

        Assert.True(Close(perPound.ProteinG!.Value, perKg.ProteinG!.Value));
    }

    [Fact]
    public void Missing_inputs_leave_the_values_they_feed_unknown_and_say_why()
    {
        // No weigh-in: no RMR estimate either, so neither energy nor protein.
        var empty = NutritionTargets.Compute(Cut, Sam());
        Assert.Null(empty.EnergyKcal);
        Assert.Null(empty.ProteinG);
        Assert.Null(empty.FatG);
        Assert.Null(empty.CarbohydrateG);
        Assert.Equal([TargetProblem.NoMaintenance, TargetProblem.NoBodyWeight], empty.Problems);

        // Lean mass without a body-fat measurement: energy and fat, but no protein, so no carbohydrate.
        var noLean = NutritionTargets.Compute(Cut with { ProteinBasis = ProteinBasis.LeanMass }, Sam(Weighed));
        Assert.NotNull(noLean.EnergyKcal);
        Assert.NotNull(noLean.FatG);
        Assert.Null(noLean.ProteinG);
        Assert.Null(noLean.CarbohydrateG);
        Assert.Equal([TargetProblem.NoLeanMass], noLean.Problems);
    }

    [Fact]
    public void Energy_at_or_below_zero_and_macros_beyond_the_energy_are_reported_not_clamped()
    {
        var none = NutritionTargets.Compute(Cut with { EnergyAdjustmentKcal = -4000 }, Sam(Weighed));
        Assert.Null(none.EnergyKcal);
        Assert.Contains(TargetProblem.EnergyNotPositive, none.Problems);

        // 3.0 g/kg × 80 = 240 g (960 kcal) and 70% fat of 1295.96 kcal leave −571 kcal.
        var over = NutritionTargets.Compute(
            Cut with { EnergyAdjustmentKcal = -2000, ProteinRatio = 3, FatPercent = 70 }, Sam(Weighed));
        Assert.NotNull(over.EnergyKcal);
        Assert.Null(over.CarbohydrateG);
        Assert.Equal([TargetProblem.MacrosExceedEnergy], over.Problems);
    }

    [Fact]
    public void A_manual_target_is_an_override_the_profile_does_not_change()
    {
        var manual = Cut with { Method = TargetMethod.Manual, ManualEnergyKcal = 2400, ManualProteinG = 150 };

        var before = NutritionTargets.Compute(manual, Sam(Weighed));
        var withLab = NutritionTargets.Compute(manual, Sam(Weighed, V(MetricCodes.Rmr, 1700, "2026-10-01", MeasurementSource.Lab)));

        foreach (var targets in new[] { before, withLab })
        {
            Assert.Equal(TargetMethod.Manual, targets.Method);
            Assert.Equal(2400, targets.EnergyKcal);
            Assert.Equal(150, targets.ProteinG);
            Assert.Null(targets.FatG);
            Assert.Null(targets.CarbohydrateG);
            Assert.Null(targets.Maintenance);
            Assert.Empty(targets.Problems);
        }
    }
}
