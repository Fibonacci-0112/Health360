using FsCheck.Fluent;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Units;

/// <summary>Compound units, pace, protein ratios and rounding.</summary>
public class DisplayTests
{
    [Fact]
    public void Stone_and_pounds_carry_instead_of_showing_14_lb()
    {
        Assert.Equal(new StoneLb(13, 0), Compound.KgToStoneLb(Compound.StoneLbToKg(new StoneLb(12, 13.97)), 1));
        Assert.Equal(new StoneLb(11, 4), Compound.KgToStoneLb(Compound.StoneLbToKg(new StoneLb(11, 4)), 0));
    }

    [Fact]
    public void Feet_and_inches_carry_instead_of_showing_12_in()
    {
        Assert.Equal(new FeetInches(6, 0), Compound.CmToFeetInches(182.85, 0));
        Assert.Equal(new FeetInches(5, 10), Compound.CmToFeetInches(Compound.FeetInchesToCm(new FeetInches(5, 10)), 0));
    }

    [Fact]
    public void Pounds_and_inches_are_always_within_range()
    {
        Holds(Gen.Zip(Double(0, 400), Int(0, 2)), t =>
        {
            var (v, decimals) = t;
            var s = Compound.KgToStoneLb(v, decimals);
            var f = Compound.CmToFeetInches(v, decimals);
            return s.Pounds is >= 0 and < 14 && f.Inches is >= 0 and < 12;
        });
    }

    [Fact]
    public void Round_trips_through_stone_and_pounds()
    {
        Holds(Gen.Zip(Int(0, 50), Int(0, 13)), t =>
        {
            var (st, lb) = t;
            return Compound.KgToStoneLb(Compound.StoneLbToKg(new StoneLb(st, lb)), 0) == new StoneLb(st, lb);
        });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compound_display_rejects_negative_and_non_finite_values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Compound.KgToStoneLb(value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Compound.CmToFeetInches(value));
    }

    [Fact]
    public void Five_minutes_per_km_is_8_02_7_per_mile()
    {
        Assert.Equal(482.8032, Pace.FromSecondsPerKm(300, PaceUnit.MinutesPerMile), 4);
        Assert.Equal(300, Pace.FromSecondsPerKm(300, PaceUnit.MinutesPerKilometre));
        Assert.Equal(300, Pace.FromDistance(5000, 1500));
    }

    [Fact]
    public void Pace_round_trips()
    {
        Holds(Double(60, 3600), s =>
            Close(Pace.ToSecondsPerKm(Pace.FromSecondsPerKm(s, PaceUnit.MinutesPerMile), PaceUnit.MinutesPerMile), s));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Pace_needs_a_positive_distance(double distanceM)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pace.FromDistance(distanceM, 600));
    }

    [Fact]
    public void One_g_per_lb_is_2_2046_g_per_kg()
    {
        Assert.Equal(2.20462262, ProteinRatio.ToGPerKg(1, ProteinRatioUnit.GramsPerPound), 8);
    }

    [Fact]
    public void Protein_ratio_round_trips_between_g_per_kg_and_g_per_lb()
    {
        Holds(Double(0.1, 5), r =>
            Close(ProteinRatio.ToGPerKg(ProteinRatio.FromGPerKg(r, ProteinRatioUnit.GramsPerPound), ProteinRatioUnit.GramsPerPound), r));
    }

    [Fact]
    public void Switching_the_ratio_unit_keeps_the_daily_gram_target_within_rounding()
    {
        Holds(Gen.Zip(Double(0.8, 3.3), Double(30, 200)), t =>
        {
            var (gPerKg, massKg) = t;
            // The user sees the converted ratio rounded to 2 decimals, as the UI shows it.
            var shownPerLb = Rounding.RoundTo(ProteinRatio.FromGPerKg(gPerKg, ProteinRatioUnit.GramsPerPound), 2);
            var before = ProteinRatio.DailyGrams(gPerKg, ProteinRatioUnit.GramsPerKilogram, massKg);
            var after = ProteinRatio.DailyGrams(shownPerLb, ProteinRatioUnit.GramsPerPound, massKg);
            // ±0.005 g/lb of rounding on up to 200 kg (441 lb) is at most ~2.2 g.
            return Math.Abs(after - before) <= (0.005 * (massKg / 0.45359237)) + 1e-9;
        });
    }

    [Theory]
    [InlineData(1.005, 2, 1.01)]
    [InlineData(-2.5, 0, -3)]
    [InlineData(2.5, 0, 3)]
    [InlineData(1e-7, 2, 0)]
    [InlineData(1234.5678, 1, 1234.6)]
    public void RoundTo_rounds_halves_away_from_zero(double value, int decimals, double expected)
    {
        Assert.Equal(expected, Rounding.RoundTo(value, decimals));
    }

    [Fact]
    public void RoundTo_passes_non_finite_values_through()
    {
        Assert.Equal(double.NaN, Rounding.RoundTo(double.NaN, 2));
        Assert.Equal(double.PositiveInfinity, Rounding.RoundTo(double.PositiveInfinity, 2));
    }
}
