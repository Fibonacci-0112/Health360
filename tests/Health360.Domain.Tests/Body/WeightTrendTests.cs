using FsCheck.Fluent;
using Health360.Domain.Body;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Body;

public class WeightTrendTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    [Fact]
    public void Matches_the_golden_fixture()
    {
        // Computed by hand (independently of this code) from docs/calculations.md: two weigh-ins on Jan 2
        // are averaged, and Jan 3 → Jan 6 is a 3-day gap.
        WeighIn[] weighIns =
        [
            new(Jan1, 80.0),
            new(Jan1.AddDays(1), 79.6), new(Jan1.AddDays(1), 80.4),
            new(Jan1.AddDays(2), 79.2),
            new(Jan1.AddDays(5), 78.9),
            new(Jan1.AddDays(6), 79.5),
        ];

        var trend = WeightTrend.Compute(weighIns);

        Assert.Equal([Jan1, Jan1.AddDays(1), Jan1.AddDays(2), Jan1.AddDays(5), Jan1.AddDays(6)], trend.Select(p => p.LocalDate));
        Assert.Equal([1, 2, 1, 1, 1], trend.Select(p => p.Count));
        double[] expected = [80.0, 80.0, 79.92, 79.64358, 79.629222];
        Assert.All(trend.Zip(expected), t => Assert.True(Close(t.First.TrendKg, t.Second), $"{t.First.TrendKg} != {t.Second}"));
    }

    [Fact]
    public void One_day_is_walkers_recurrence()
    {
        // T = previous + 0.1 × (weight − previous): the new weight gets 0.1, the old trend 0.9.
        Assert.True(Close(WeightTrend.Step(80, 70, 1), 79));
    }

    [Fact]
    public void A_gap_is_the_same_as_seeing_the_new_weight_every_missing_day()
    {
        var daily = WeightTrend.Step(WeightTrend.Step(WeightTrend.Step(80, 77, 1), 77, 1), 77, 1);
        Assert.True(Close(WeightTrend.Step(80, 77, 3), daily));
    }

    [Fact]
    public void Starts_at_the_first_days_mean_and_ignores_input_order()
    {
        var trend = WeightTrend.Compute([new(Jan1.AddDays(1), 81), new(Jan1, 79), new(Jan1, 81)]);

        Assert.Equal(80, trend[0].TrendKg);
        Assert.Equal(80, trend[0].MeanKg);
        Assert.True(Close(trend[1].TrendKg, 80.1));
    }

    [Fact]
    public void Empty_input_gives_an_empty_trend()
    {
        Assert.Empty(WeightTrend.Compute([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Rejects_impossible_weights(double kg)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightTrend.Compute([new(Jan1, kg)]));
    }

    [Fact]
    public void Rejects_a_step_of_less_than_a_day()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WeightTrend.Step(80, 79, 0));
    }

    [Fact]
    public void The_trend_stays_between_the_lowest_and_highest_daily_means()
    {
        Holds(Series(), weighIns =>
        {
            var trend = WeightTrend.Compute(weighIns);
            var low = trend.Min(p => p.MeanKg);
            var high = trend.Max(p => p.MeanKg);
            return trend.All(p => p.TrendKg >= low - 1e-9 && p.TrendKg <= high + 1e-9);
        });
    }

    [Fact]
    public void A_constant_weight_gives_a_flat_trend()
    {
        Holds(Gen.Zip(Double(30, 250), Int(1, 60)), t =>
        {
            var (kg, days) = t;
            var trend = WeightTrend.Compute(Enumerable.Range(0, days).Select(d => new WeighIn(Jan1.AddDays(d * 2), kg)));
            return trend.All(p => Close(p.TrendKg, kg));
        });
    }

    [Fact]
    public void Units_never_change_the_trend()
    {
        // Converting every weigh-in to lb and the trend back gives the same trend, so a unit switch can't
        // move it (PLAN.md §2.6).
        Holds(Series(), weighIns =>
        {
            const double kgPerLb = 0.45359237;
            var kg = WeightTrend.Compute(weighIns);
            var lb = WeightTrend.Compute(weighIns.Select(w => w with { Kg = w.Kg / kgPerLb }));
            return kg.Zip(lb).All(p => Close(p.First.TrendKg, p.Second.TrendKg * kgPerLb, 1e-9));
        });
    }

    private static FsCheck.Gen<WeighIn[]> Series() =>
        Gen.Zip(Double(30, 250), Int(0, 90))
            .Select(t => new WeighIn(Jan1.AddDays(t.Item2), t.Item1))
            .ArrayOf()
            .Where(a => a.Length > 0);
}
