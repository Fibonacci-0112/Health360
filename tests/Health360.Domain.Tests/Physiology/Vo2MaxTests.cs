using FsCheck.Fluent;
using Health360.Domain.Physiology;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Physiology;

public class Vo2MaxTests
{
    // Hand calculations from Jackson et al. (1990), p. 866 (docs/calculations.md).
    [Theory]
    [InlineData(Sex.Male, 5, 40, 25, 42.865)] // 56.363 + 9.605 − 15.24 − 18.85 + 10.987
    [InlineData(Sex.Female, 3, 35, 22, 32.203)] // 56.363 + 5.763 − 13.335 − 16.588
    public void Jackson_bmi_matches_a_hand_calculation(Sex sex, int pa, int age, double bmi, double expected)
    {
        var estimate = Vo2Max.JacksonBmi(sex, pa, age, bmi)!;

        Assert.True(Close(estimate.Value, expected, 1e-9), $"{estimate.Value}");
        Assert.Equal("Jackson (BMI)", estimate.Method);
        Assert.Equal(5.70, estimate.StandardError);
    }

    [Theory]
    [InlineData(Sex.Male, 5, 40, 20, 41.721)] // 50.513 + 7.945 − 11.56 − 11.04 + 5.863
    [InlineData(Sex.Female, 4, 30, 28, 32.743)] // 50.513 + 6.356 − 8.67 − 15.456
    public void Jackson_percent_fat_matches_a_hand_calculation(Sex sex, int pa, int age, double fat, double expected)
    {
        var estimate = Vo2Max.JacksonPercentFat(sex, pa, age, fat)!;

        Assert.True(Close(estimate.Value, expected, 1e-9), $"{estimate.Value}");
        Assert.Equal("Jackson (%fat)", estimate.Method);
        Assert.Equal(5.35, estimate.StandardError);
    }

    [Fact]
    public void The_mens_equations_match_the_papers_male_intercepts()
    {
        // The paper also prints the men's equations with the sex term folded in: 67.350 (BMI) and 56.376 (%fat).
        Holds(Gen.Zip(Int(0, 7), Gen.Zip(Int(18, 70), Gen.Zip(Double(15, 40), Double(5, 45)))), t =>
        {
            var (pa, (age, (bmi, fat))) = t;
            var bmiMale = 67.350 + (1.921 * pa) - (0.381 * age) - (0.754 * bmi);
            var fatMale = 56.376 + (1.589 * pa) - (0.289 * age) - (0.552 * fat);
            return Close(Vo2Max.JacksonBmi(Sex.Male, pa, age, bmi)!.Value, bmiMale, 1e-9)
                && Close(Vo2Max.JacksonPercentFat(Sex.Male, pa, age, fat)!.Value, fatMale, 1e-9);
        });
    }

    [Fact]
    public void Jackson_prefers_the_percent_fat_model_when_body_fat_is_known()
    {
        Assert.Equal("Jackson (%fat)", Vo2Max.Jackson(Sex.Male, 5, 40, 25, 20)!.Method);
        Assert.Equal("Jackson (BMI)", Vo2Max.Jackson(Sex.Male, 5, 40, 25, null)!.Method);
        Assert.Null(Vo2Max.Jackson(Sex.Male, 5, 40, null, null));
        Assert.Null(Vo2Max.Jackson(null, 5, 40, 25, 20));
        Assert.Null(Vo2Max.Jackson(Sex.Male, null, 40, 25, 20));
        Assert.Null(Vo2Max.Jackson(Sex.Male, 5, null, 25, 20));
    }

    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonBmi(Sex.Male, -1, 40, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonBmi(Sex.Male, 8, 40, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonBmi(Sex.Male, 5, -1, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonBmi(Sex.Male, 5, 40, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonBmi(Sex.Male, 5, 40, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonPercentFat(Sex.Male, 5, 40, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Vo2Max.JacksonPercentFat(Sex.Male, 5, 40, -0.1));
    }

    [Fact]
    public void A_result_that_is_not_positive_gives_no_estimate()
    {
        Assert.Null(Vo2Max.JacksonBmi(Sex.Female, 0, 120, 60));
    }
}
