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
    public void A_session_within_the_range_ends_the_miss_streak()
    {
        var inRange = Progression.DoubleProgression(60, 8, 12, Sets(60, 10, 10), 2.5, 2);
        Assert.Equal(new ProgressionSuggestion(60, 8, 12, ProgressionAction.Repeat, 0), inRange);

        // Two misses, a session in range, then one more miss: not three in a row, so no deload.
        var miss = Progression.DoubleProgression(60, 8, 12, Sets(60, 7), 2.5, inRange.ConsecutiveFailures);
        Assert.Equal(new ProgressionSuggestion(60, 8, 12, ProgressionAction.Repeat, 1), miss);
    }

    [Fact]
    public void An_off_grid_load_moves_by_whole_increments()
    {
        Assert.Equal(103.5, Progression.LinearProgression(101, 5, Sets(101, 5), 2.5, 0).Load);
        Assert.Equal(91, Progression.LinearProgression(101, 5, Sets(101, 4), 2.5, 2).Load);
    }

    [Fact]
    public void A_load_too_large_to_represent_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Progression.LinearProgression(double.MaxValue, 5, Sets(double.MaxValue, 5), double.MaxValue, 0));

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
    public void A_half_increment_deload_rounds_alike_in_lb_and_kg()
    {
        // 175 lb × 10% / 5 lb = 3.5 increments, rounded to 4: 155 lb. In kg the ratio comes out 3.4999999999999996.
        var kg = Conversion.Convert(175, Unit.Pound, Unit.Kilogram);
        var incKg = Conversion.Convert(5, Unit.Pound, Unit.Kilogram);

        Assert.Equal(155, Progression.LinearProgression(175, 5, Sets(175, 4), 5, 2).Load);
        var inKg = Progression.LinearProgression(kg, 5, Sets(kg, 4), incKg, 2);
        Assert.True(Quantity.CanonicalEquals(Conversion.Convert(155, Unit.Pound, Unit.Kilogram), inKg.Load), $"{inKg.Load}");
    }

    [Fact]
    public void A_ratio_genuinely_below_a_half_is_not_snapped_up()
    {
        // 35 × 0.99999999988 / 10 = 3.4999999996: three increments, not four (which would reach 0 and skip).
        var next = Progression.LinearProgression(35, 5, Sets(35, 4), 10, 2, new DeloadPolicy(Fraction: 0.99999999988));

        Assert.Equal(new ProgressionSuggestion(5, 5, 5, ProgressionAction.Deload, 0), next);
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

    private static IReadOnlyList<WorkSet>[] History(params WorkSet[][] sessions) => sessions;

    [Fact]
    public void Replay_without_history_suggests_nothing() =>
        Assert.Null(Progression.Replay(ProgressionKind.DoubleProgression, 8, 12, 2.5, 60, []));

    [Fact]
    public void Replay_counts_misses_from_the_history_and_deloads_on_the_third()
    {
        // Golden (calculations.md): double 8–12 from 60, increment 2.5.
        var sessions = History(Sets(60, 10, 9, 8), Sets(60, 12, 12, 12), Sets(62.5, 7, 7, 7), Sets(62.5, 7, 7, 6));

        Assert.Equal(new ProgressionSuggestion(62.5, 8, 12, ProgressionAction.Repeat, 2), Progression.Replay(ProgressionKind.DoubleProgression, 8, 12, 2.5, 60, sessions));
        Assert.Equal(
            new ProgressionSuggestion(55, 8, 12, ProgressionAction.Deload, 0),
            Progression.Replay(ProgressionKind.DoubleProgression, 8, 12, 2.5, 60, [.. sessions, Sets(62.5, 7, 6, 6)]));
    }

    [Fact]
    public void Replay_targets_the_top_of_the_range_for_linear()
    {
        var next = Progression.Replay(ProgressionKind.LinearProgression, 3, 5, 2.5, 100, History(Sets(100, 5, 5, 5), Sets(102.5, 5, 5, 5)));

        Assert.Equal(new ProgressionSuggestion(105, 5, 5, ProgressionAction.Increase, 0), next);
    }

    [Fact]
    public void Replay_without_a_start_load_judges_the_first_session_by_its_lightest_set()
    {
        // 90 then 100: judged at 90, every set reached 5, so 92.5.
        var next = Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, null, History([new WorkSet(90, 5), new WorkSet(100, 5)]));

        Assert.Equal(new ProgressionSuggestion(92.5, 5, 5, ProgressionAction.Increase, 0), next);
    }

    [Fact]
    public void Replay_builds_on_a_heavier_load_chosen_by_hand_but_counts_a_lighter_one_as_a_miss()
    {
        // Suggested 102.5 after the first session; the second was all at 110, so it is judged at 110.
        var heavier = Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, 100, History(Sets(100, 5, 5), Sets(110, 5, 5)));
        var lighter = Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, 100, History(Sets(100, 5, 5), Sets(95, 5, 5)));

        Assert.Equal(new ProgressionSuggestion(112.5, 5, 5, ProgressionAction.Increase, 0), heavier);
        Assert.Equal(new ProgressionSuggestion(102.5, 5, 5, ProgressionAction.Repeat, 1), lighter);
    }

    [Fact]
    public void Replay_skips_empty_and_unloaded_sessions()
    {
        var next = Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, null, History([], Sets(0, 5, 5), Sets(50, 5, 5)));

        Assert.Equal(new ProgressionSuggestion(52.5, 5, 5, ProgressionAction.Increase, 0), next);
        Assert.Null(Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, null, History(Sets(0, 5))));
    }

    [Fact]
    public void Replay_matches_stepping_each_session_by_hand()
    {
        Holds(Gen.Zip(Gen.Elements(1.0, 2.5, 5.0), Gen.ListOf(Gen.Zip(Int(0, 14), Int(0, 14)))), t =>
        {
            var (inc, raw) = t;
            var sessions = raw.Take(12).ToList();
            double load = 40;
            var failures = 0;
            var replayed = new List<IReadOnlyList<WorkSet>>();
            foreach (var (a, b) in sessions)
            {
                var sets = Sets(load, a, b);
                replayed.Add(sets);
                var step = Progression.DoubleProgression(load, 8, 12, sets, inc, failures);
                (load, failures) = (step.Load, step.ConsecutiveFailures);
            }

            var next = Progression.Replay(ProgressionKind.DoubleProgression, 8, 12, inc, 40, replayed);
            return sessions.Count == 0 ? next is null : next is not null && next.Load == load && next.ConsecutiveFailures == failures;
        });
    }

    [Fact]
    public void Replay_skips_sessions_done_without_load_rather_than_counting_misses()
    {
        // Three unloaded sessions after a start at 100 would be three misses and a deload if they counted.
        var unloaded = History(Sets(0, 5, 5), Sets(0, 5, 5), Sets(0, 5, 5));

        Assert.Null(Progression.Replay(ProgressionKind.LinearProgression, 5, 5, 2.5, 100, unloaded));
        var (next, last) = Progression.ReplayCounted(ProgressionKind.LinearProgression, 5, 5, 2.5, 100, [Sets(100, 5, 4), .. unloaded]);
        Assert.Equal((new ProgressionSuggestion(100, 5, 5, ProgressionAction.Repeat, 1), 0), (next, last));
    }
}
