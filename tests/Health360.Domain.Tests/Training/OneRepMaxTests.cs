using FsCheck.Fluent;
using Health360.Domain.Training;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Training;

public class OneRepMaxTests
{
    // Hand calculations from the published equations (docs/calculations.md).
    [Theory]
    [InlineData(100, 5, 116.66666666666667)] // 100 × (1 + 5/30)
    [InlineData(225, 8, 285)] // 225 × (1 + 8/30)
    [InlineData(80, 10, 106.66666666666667)] // 80 × (1 + 10/30)
    public void Epley_matches_a_hand_calculation(double load, int reps, double expected) =>
        Assert.True(Close(OneRepMax.Estimate(load, reps, OneRepMaxFormula.Epley)!.Value, expected, 1e-12));

    [Theory]
    [InlineData(100, 5, 112.5)] // 100 × 36 / 32
    [InlineData(225, 8, 279.31034482758622)] // 225 × 36 / 29
    [InlineData(80, 10, 106.66666666666667)] // 80 × 36 / 27: the two equations meet at 10 reps
    public void Brzycki_matches_a_hand_calculation(double load, int reps, double expected) =>
        Assert.True(Close(OneRepMax.Estimate(load, reps, OneRepMaxFormula.Brzycki)!.Value, expected, 1e-12));

    [Theory]
    [InlineData(OneRepMaxFormula.Epley)]
    [InlineData(OneRepMaxFormula.Brzycki)]
    public void A_single_is_its_own_one_rep_max(OneRepMaxFormula formula) =>
        Assert.Equal(142.5, OneRepMax.Estimate(142.5, 1, formula));

    [Theory]
    [InlineData(OneRepMaxFormula.Epley)]
    [InlineData(OneRepMaxFormula.Brzycki)]
    public void Nothing_is_estimated_above_ten_reps(OneRepMaxFormula formula) =>
        Assert.Null(OneRepMax.Estimate(100, OneRepMax.MaxReps + 1, formula));

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-10, 5)]
    [InlineData(double.NaN, 5)]
    [InlineData(double.PositiveInfinity, 5)]
    [InlineData(100, 0)]
    public void Invalid_sets_are_rejected(double load, int reps) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OneRepMax.Estimate(load, reps, OneRepMaxFormula.Epley));

    [Fact]
    public void E1rm_rises_steadily_with_weight_and_reps()
    {
        var formulas = Gen.Elements(OneRepMaxFormula.Epley, OneRepMaxFormula.Brzycki);
        Holds(Gen.Zip(formulas, Gen.Zip(Double(1, 500), Gen.Zip(Double(0.5, 50), Int(1, OneRepMax.MaxReps - 1)))), t =>
        {
            var (formula, (load, (extra, reps))) = t;
            var e = OneRepMax.Estimate(load, reps, formula)!.Value;
            return e >= load
                && OneRepMax.Estimate(load + extra, reps, formula) > e
                && OneRepMax.Estimate(load, reps + 1, formula) > e;
        });
    }

    [Fact]
    public void Switching_units_never_changes_the_estimate()
    {
        var formulas = Gen.Elements(OneRepMaxFormula.Epley, OneRepMaxFormula.Brzycki);
        Holds(Gen.Zip(formulas, Gen.Zip(Double(1, 500), Int(1, OneRepMax.MaxReps))), t =>
        {
            var (formula, (lb, reps)) = t;
            var kg = Conversion.Convert(lb, Unit.Pound, Unit.Kilogram);
            var fromLb = Conversion.Convert(OneRepMax.Estimate(lb, reps, formula)!.Value, Unit.Pound, Unit.Kilogram);
            return Quantity.CanonicalEquals(fromLb, OneRepMax.Estimate(kg, reps, formula)!.Value);
        });
    }
}
