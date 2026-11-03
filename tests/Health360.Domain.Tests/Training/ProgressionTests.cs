using FsCheck.Fluent;
using Health360.Domain.Training;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Training;

public class ProgressionTests
{
    private static WorkSet[] Sets(double load, params int[] reps) => [.. reps.Select(r => new WorkSet(load, r))];

    [Fact]
    public void Linear_adds_the_increment_when_every_set_hits_the_target()
    {
        var next = Progression.LinearProgression(100, 5, Sets(100, 5, 5, 5), 2.5, 0);

        Assert.Equal(new ProgressionSuggestion(102.5, 5, 5, ProgressionAction.Increase, 0), next);
    }

    [Fact]
    public void Linear_counts_extra_reps_and_heavier_sets_as_success() =>
        Assert.Equal(ProgressionAction.Increase, Progression.LinearProgression(100, 5, [new(100, 7), new(105, 5)], 2.5, 1).Action);

    [Theory]
    [InlineData(100, 5, 5, 4)] // one set short
    public void Linear_repeats_and_counts_a_miss(double load, int a, int b, int c)
    {
        var next = Progression.LinearProgression(load, 5, Sets(load, a, b, c), 2.5, 0);

        Assert.Equal(new ProgressionSuggestion(100, 5, 5, ProgressionAction.Repeat, 1), next);
    }

    [Fact]
    public void A_set_lighter_than_prescribed_is_a_miss() =>
        Assert.Equal(ProgressionAction.Repeat, Progression.LinearProgression(100, 5, [new(100, 5), new(95, 5)], 2.5, 0).Action);

    // Default policy: deload after 3 misses in a row by about 10%, in whole increments (at least one).
    [Theory]
    [InlineData(100, 2.5, 90)] // 10 / 2.5 = 4 increments
    [InlineData(225, 5, 200)] // 22.5 / 5 = 4.5, rounded away from zero to 5 increments
    [InlineData(20, 2.5, 17.5)] // 2 / 2.5 = 0.8, at least one increment
    public void The_third_miss_in_a_row_deloads(double load, double increment, double expected)
    {
        var next = Progression.LinearProgression(load, 5, Sets(load, 5, 4), increment, 2);

        Assert.Equal(new ProgressionSuggestion(expected, 5, 5, ProgressionAction.Deload, 0), next);
    }

    [Fact]
    public void A_deload_never_reaches_zero()
    {
        var next = Progression.LinearProgression(2.5, 5, Sets(2.5, 3), 2.5, 2);

        Assert.Equal(new ProgressionSuggestion(2.5, 5, 5, ProgressionAction.Repeat, 3), next);
    }

    [Fact]
    public void Deloads_can_be_turned_off()
    {
        var next = Progression.LinearProgression(100, 5, Sets(100, 4), 2.5, 9, new DeloadPolicy(FailuresBeforeDeload: 0));

        Assert.Equal(new ProgressionSuggestion(100, 5, 5, ProgressionAction.Repeat, 10), next);
    }

    [Fact]
    public void Double_progression_climbs_reps_then_load()
    {
        // A golden sequence: 8–12 reps, +2.5 kg.
        var s1 = Progression.DoubleProgression(60, 8, 12, Sets(60, 10, 9, 8), 2.5, 0);
        Assert.Equal(new ProgressionSuggestion(60, 8, 12, ProgressionAction.Repeat, 0), s1);

        var s2 = Progression.DoubleProgression(s1.Load, 8, 12, Sets(60, 12, 12, 11), 2.5, s1.ConsecutiveFailures);
        Assert.Equal(new ProgressionSuggestion(60, 8, 12, ProgressionAction.Repeat, 0), s2);

        var s3 = Progression.DoubleProgression(s2.Load, 8, 12, Sets(60, 12, 12, 12), 2.5, s2.ConsecutiveFailures);
        Assert.Equal(new ProgressionSuggestion(62.5, 8, 12, ProgressionAction.Increase, 0), s3);

        var s4 = Progression.DoubleProgression(s3.Load, 8, 12, Sets(62.5, 9, 8, 7), 2.5, s3.ConsecutiveFailures);
        Assert.Equal(new ProgressionSuggestion(62.5, 8, 12, ProgressionAction.Repeat, 1), s4);
    }

    [Fact]
    public void Within_the_range_keeps_the_miss_count()
    {
        var next = Progression.DoubleProgression(60, 8, 12, Sets(60, 10, 10), 2.5, 2);

        Assert.Equal(new ProgressionSuggestion(60, 8, 12, ProgressionAction.Repeat, 2), next);
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Progression.LinearProgression(100, 5, [], 2.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progression.LinearProgression(0, 5, Sets(0, 5), 2.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progression.LinearProgression(100, 5, Sets(100, 5), 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progression.LinearProgression(100, 5, Sets(100, 5), 2.5, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progression.DoubleProgression(100, 12, 8, Sets(100, 5), 2.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Progression.LinearProgression(100, 5, Sets(100, -1), 2.5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Progression.LinearProgression(100, 5, Sets(100, 5), 2.5, 0, new DeloadPolicy(Fraction: 1)));
    }

    [Fact]
    public void The_same_session_in_lb_and_kg_gets_the_same_suggestion()
    {
        Holds(Gen.Zip(Double(20, 300), Gen.Zip(Gen.Elements(2.5, 5.0), Gen.Zip(Int(3, 6), Gen.Zip(Int(0, 7), Int(0, 3))))), t =>
        {
            var (lb, (incLb, (target, (reps, failures)))) = t;
            var kg = Conversion.Convert(lb, Unit.Pound, Unit.Kilogram);
            var incKg = Conversion.Convert(incLb, Unit.Pound, Unit.Kilogram);

            var inLb = Progression.LinearProgression(lb, target, Sets(lb, reps, target), incLb, failures);
            var inKg = Progression.LinearProgression(kg, target, Sets(kg, reps, target), incKg, failures);

            return inLb.Action == inKg.Action
                && inLb.ConsecutiveFailures == inKg.ConsecutiveFailures
                && Quantity.CanonicalEquals(Conversion.Convert(inLb.Load, Unit.Pound, Unit.Kilogram), inKg.Load);
        });
    }

    [Fact]
    public void Suggestions_stay_positive_and_on_the_increment_grid()
    {
        Holds(Gen.Zip(Int(1, 200), Gen.Zip(Gen.Elements(1.0, 2.5, 5.0), Gen.Zip(Int(0, 7), Int(0, 5)))), t =>
        {
            var (steps, (inc, (reps, failures))) = t;
            var load = steps * inc;
            var next = Progression.LinearProgression(load, 5, Sets(load, reps), inc, failures);
            var onGrid = Math.Abs((next.Load / inc) - Math.Round(next.Load / inc)) < 1e-9;
            return next.Load > 0 && onGrid && next.Load <= load + inc;
        });
    }
}
