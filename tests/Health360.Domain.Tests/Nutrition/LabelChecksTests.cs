using FsCheck.Fluent;
using Health360.Domain.Nutrition;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Nutrition;

public class LabelChecksTests
{
    // A breakfast cereal printed to the FDA rounding rules: 40 g serving, and the same food per 100 g.
    private static readonly LabelValues CerealServing = new(EnergyKcal: 150, Protein: 4, Carbohydrate: 30, Fat: 2.5, Fiber: 4);
    private static readonly LabelValues Cereal100 = new(EnergyKcal: 375, EnergyKj: 1569, Protein: 10, Carbohydrate: 75, Fat: 6, Fiber: 10);

    private static IReadOnlyList<LabelIssue> Check(LabelValues? serving, LabelValues? per100, double? servingSize = 40, FoodBasis basis = FoodBasis.Mass) =>
        LabelChecks.Check(new LabelReading(basis, servingSize, serving, per100));

    [Fact]
    public void A_consistent_label_has_no_issues() => Assert.Empty(Check(CerealServing, Cereal100));

    [Fact]
    public void A_misread_calorie_count_is_flagged_three_ways()
    {
        var issues = Check(CerealServing with { EnergyKcal = 1500 }, Cereal100);

        Assert.Equal(
            [LabelIssueKind.EnergyDoesNotMatchMacros, LabelIssueKind.EnergyTooDense, LabelIssueKind.ColumnsDisagree],
            issues.Select(i => i.Kind));
        Assert.All(issues, i => Assert.Contains(LabelField.EnergyKcal, i.Fields));
        Assert.Equal(LabelColumn.PerServing, issues[0].Column);
        Assert.Null(issues[2].Column);
    }

    [Fact]
    public void Kilojoules_outside_every_conversion_factor_are_flagged()
    {
        // 1069 kJ / 375 kcal = 2.85, below the lowest EU factor ratio (4.0).
        var issue = Assert.Single(Check(null, Cereal100 with { EnergyKj = 1069 }));

        Assert.Equal(LabelIssueKind.KilojoulesDoNotMatchKilocalories, issue.Kind);
        Assert.Equal([LabelField.EnergyKcal, LabelField.EnergyKj], issue.Fields);
    }

    [Theory]
    [InlineData(1500)] // 4.0 × 375
    [InlineData(1625)] // 13/3 × 375
    public void Kilojoules_from_any_eu_factor_pass(double kj) => Assert.Empty(Check(null, Cereal100 with { EnergyKj = kj }));

    [Fact]
    public void Macros_heavier_than_the_food_are_flagged()
    {
        var issue = Assert.Single(Check(null, new LabelValues(Protein: 60, Carbohydrate: 50, Fat: 10)));

        Assert.Equal(LabelIssueKind.MacrosExceedWeight, issue.Kind);
        Assert.Equal(LabelColumn.Per100, issue.Column);
    }

    [Fact]
    public void Alcohol_and_organic_acids_count_towards_the_weight()
    {
        var issue = Assert.Single(Check(null, new LabelValues(Protein: 0, Carbohydrate: 0, Fat: 0, Alcohol: 101)));

        Assert.Equal(LabelIssueKind.MacrosExceedWeight, issue.Kind);
        Assert.Contains(LabelField.Alcohol, issue.Fields);
        Assert.Equal(LabelIssueKind.MacrosExceedWeight, Assert.Single(Check(null, new LabelValues(Fat: 60, OrganicAcids: 50))).Kind);
    }

    [Fact]
    public void Volume_labels_skip_the_weight_checks() =>
        Assert.Empty(Check(null, new LabelValues(Protein: 60, Carbohydrate: 50, Fat: 10), basis: FoodBasis.Volume));

    [Fact]
    public void Fiber_lets_the_energy_fall_below_four_kcal_per_gram_of_carbohydrate()
    {
        // A high-fiber cereal whose calories count only digestible carbohydrate (21 CFR 101.9(c)(1)(i)(C)).
        var label = new LabelValues(EnergyKcal: 60, Protein: 3, Carbohydrate: 24, Fat: 1, Fiber: 14);

        Assert.Empty(Check(label, null, servingSize: 30));
        Assert.Equal(LabelIssueKind.EnergyDoesNotMatchMacros, Assert.Single(Check(label with { Fiber = null }, null, servingSize: 30)).Kind);
    }

    [Fact]
    public void Energy_from_organic_acids_and_alcohol_counts_when_printed()
    {
        // Vinegar per 100 ml: 21 kcal, nearly all from about 7 g of acetic acid at 3 kcal/g (Annex XIV).
        var vinegar = new LabelValues(EnergyKcal: 21, Protein: 0, Carbohydrate: 0, Fat: 0);
        // A 5% beer per 100 ml: about 4 g of alcohol at 7 kcal/g plus 3 g of carbohydrate.
        var beer = new LabelValues(EnergyKcal: 40, Protein: 0, Carbohydrate: 3, Fat: 0);

        Assert.Empty(Check(null, vinegar with { OrganicAcids = 7 }, basis: FoodBasis.Volume));
        Assert.Empty(Check(null, beer with { Alcohol = 4 }, basis: FoodBasis.Volume));
        // Unprinted, they can't be counted: the energy is marked suspect for the user to confirm.
        Assert.Equal(LabelIssueKind.EnergyDoesNotMatchMacros, Assert.Single(Check(null, vinegar, basis: FoodBasis.Volume)).Kind);
        Assert.Contains(LabelField.Alcohol, Assert.Single(Check(null, beer with { Alcohol = 0 }, basis: FoodBasis.Volume)).Fields);
    }

    [Fact]
    public void A_zero_calorie_drink_passes() =>
        Assert.Empty(Check(new LabelValues(EnergyKcal: 0, Protein: 0, Carbohydrate: 0, Fat: 0), null, servingSize: 355, basis: FoodBasis.Volume));

    [Fact]
    public void Columns_that_disagree_name_the_fields()
    {
        // A 50 g serving misread for 40 g: 375 kcal/100 g × 50 g is about 188 kcal, not 150. Fat still agrees
        // (6 g/100 g × 49.5 g ≥ 2.72 g, and 2.5 g printed stands for up to 2.75 g).
        var issue = Assert.Single(Check(CerealServing, Cereal100, servingSize: 50));

        Assert.Equal(LabelIssueKind.ColumnsDisagree, issue.Kind);
        Assert.Equal([LabelField.EnergyKcal, LabelField.Protein, LabelField.Carbohydrate, LabelField.Fiber, LabelField.ServingSize], issue.Fields);
    }

    [Fact]
    public void Missing_values_skip_the_checks_that_need_them() =>
        Assert.Empty(Check(new LabelValues(EnergyKcal: 9999), null, servingSize: null));

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_values_are_rejected(double value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(new LabelValues(Fat: value), null));

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void The_serving_size_must_be_positive(double size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(CerealServing, null, servingSize: size));

    // FDA rounding, written out independently of the code under test (21 CFR 101.9(c)).
    private static double RoundKcal(double x) => x < 5 ? 0 : x <= 50 ? 5 * Math.Round(x / 5) : 10 * Math.Round(x / 10);

    private static double RoundFat(double x) => x < 0.5 ? 0 : x < 5 ? 0.5 * Math.Round(x / 0.5) : Math.Round(x);

    private static double RoundGrams(double x) => x < 0.5 ? 0 : Math.Round(x);

    private static LabelValues Printed(double protein, double carbohydrate, double fat)
    {
        var kcal = (4 * protein) + (4 * carbohydrate) + (9 * fat);
        return new(RoundKcal(kcal), Math.Round(kcal * 4.184), RoundGrams(protein), RoundGrams(carbohydrate), RoundFat(fat));
    }

    [Fact]
    public void A_correctly_rounded_label_is_never_flagged()
    {
        var macros = Gen.Zip(Double(0, 100), Gen.Zip(Double(0, 100), Double(0, 100)))
            .Select(t => (P: t.Item1, C: t.Item2.Item1, F: t.Item2.Item2))
            .Select(m => m.P + m.C + m.F is var sum && sum > 100 ? (m.P * 100 / sum, m.C * 100 / sum, m.F * 100 / sum) : m);
        Holds(Gen.Zip(macros, Int(1, 500)), t =>
        {
            var ((p, c, f), serving) = t;
            var per100 = Printed(p, c, f);
            var perServing = Printed(p * serving / 100, c * serving / 100, f * serving / 100);
            return LabelChecks.Check(new LabelReading(FoodBasis.Mass, serving, perServing, per100)).Count == 0;
        });
    }
}
