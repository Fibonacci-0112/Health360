using FsCheck.Fluent;
using Health360.Domain.Body;
using Health360.Domain.Physiology;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Physiology;

public class EstimatorTests
{
    // Hand calculations from the published equations (docs/calculations.md).
    [Theory]
    [InlineData(Sex.Male, 80, 180, 30, 1781.6)] // 799.2 + 1125 − 147.6 + 166 − 161
    [InlineData(Sex.Female, 60, 165, 40, 1272.85)] // 599.4 + 1031.25 − 196.8 − 161
    public void Mifflin_st_jeor_uses_the_published_coefficients(Sex sex, double kg, double cm, int age, double kcal)
    {
        var estimate = RestingMetabolicRate.MifflinStJeor(sex, kg, cm, age);

        Assert.True(Close(estimate.Value, kcal));
        Assert.Equal("Mifflin-St Jeor", estimate.Method);
    }

    [Fact]
    public void Mifflin_st_jeor_differs_by_sex_by_exactly_166()
    {
        Holds(Gen.Zip(Double(30, 200), Gen.Zip(Double(120, 220), Int(18, 90))), t =>
        {
            var (kg, (cm, age)) = t;
            var male = RestingMetabolicRate.MifflinStJeor(Sex.Male, kg, cm, age).Value;
            var female = RestingMetabolicRate.MifflinStJeor(Sex.Female, kg, cm, age).Value;
            return Close(male - female, 166, 1e-9);
        });
    }

    [Fact]
    public void Katch_mcardle_matches_a_hand_calculation()
    {
        Assert.True(Close(RestingMetabolicRate.KatchMcArdle(60).Value, 1666)); // 370 + 21.6 × 60
    }

    [Fact]
    public void Best_prefers_katch_mcardle_when_fat_free_mass_is_known()
    {
        var ffm = BodyComposition.FatFreeMassKg(80, 25);

        Assert.Equal("Katch-McArdle", RestingMetabolicRate.Best(Sex.Male, 80, 180, 30, ffm)!.Method);
        Assert.Equal("Mifflin-St Jeor", RestingMetabolicRate.Best(Sex.Male, 80, 180, 30, null)!.Method);
        Assert.Null(RestingMetabolicRate.Best(null, 80, 180, 30, null));
        Assert.Null(RestingMetabolicRate.Best(Sex.Male, null, 180, 30, null));
    }

    [Theory]
    [InlineData(40, 180)]
    [InlineData(25, 190.5)]
    public void Tanaka_matches_a_hand_calculation(int age, double bpm)
    {
        Assert.True(Close(MaxHeartRate.Tanaka(age, takesHeartRateLoweringMedication: false)!.Value, bpm));
    }

    [Fact]
    public void Nothing_is_estimated_from_age_when_heart_rate_lowering_medication_is_taken()
    {
        Assert.Null(MaxHeartRate.Tanaka(40, takesHeartRateLoweringMedication: true));
    }

    [Fact]
    public void Rejects_impossible_inputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RestingMetabolicRate.MifflinStJeor(Sex.Male, 0, 180, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestingMetabolicRate.MifflinStJeor(Sex.Male, 80, double.NaN, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestingMetabolicRate.MifflinStJeor(Sex.Male, 80, 180, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RestingMetabolicRate.KatchMcArdle(-5));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaxHeartRate.Tanaka(-1, false));
    }

    [Fact]
    public void Bmi_and_fat_free_mass_are_their_definitions()
    {
        Assert.True(Close(BodyComposition.Bmi(81, 180), 25));
        Assert.True(Close(BodyComposition.FatFreeMassKg(80, 25), 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => BodyComposition.FatFreeMassKg(80, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => BodyComposition.Bmi(80, 0));
    }

    [Theory]
    // A 29 February birthday counts as reached on 28 February in other years (DateOnly.AddYears).
    [InlineData("2000-02-29", "2001-02-27", 0)]
    [InlineData("2000-02-29", "2001-02-28", 1)]
    [InlineData("2000-02-29", "2004-02-29", 4)]
    [InlineData("1990-06-15", "2026-06-14", 35)]
    [InlineData("1990-06-15", "2026-06-15", 36)]
    public void Age_counts_whole_years(string birth, string asOf, int years)
    {
        Assert.Equal(years, Ages.Years(DateOnly.Parse(birth), DateOnly.Parse(asOf)));
    }

    [Fact]
    public void Age_rejects_a_date_before_birth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Ages.Years(new(2000, 1, 2), new(2000, 1, 1)));
    }
}
