using Health360.Contracts.Body;

namespace Health360.UI.Tests.Contracts;

public class MeasurementInputTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static readonly MetricDefinition Weight = new("weight", "body", ["kg", "lb"], "kg", 0, null);
    private static readonly MetricDefinition BodyFat = new("body_fat_pct", "body", ["%"], "%", 0, 100);
    private static readonly MetricDefinition Temperature = new("body_temperature", "body", ["C", "F"], "C", null, null);

    private static IReadOnlyList<string> Errors(MetricDefinition metric, double? value, string unit, string source = "manual", DateOnly? date = null) =>
        [.. new MeasurementInputValidator(metric, Today).Validate(new MeasurementInput(metric.Code, value, unit, source, date ?? Today))
            .Errors.Select(e => e.ErrorMessage)];

    [Fact]
    public void AValidEntryPasses()
    {
        Assert.Empty(Errors(Weight, 80, "kg"));
        Assert.Empty(Errors(Weight, 0.1, "lb", "lab", Today.AddYears(-3)));
        Assert.Empty(Errors(BodyFat, 0, "%"));
        Assert.Empty(Errors(Temperature, -1, "C"));
    }

    [Fact]
    public void ItSaysWhatIsWrong()
    {
        Assert.Equal(["Enter a number."], Errors(Weight, null, "kg"));
        Assert.Equal(["Enter a number."], Errors(Weight, double.NaN, "kg"));
        Assert.Equal(["The value can't be negative."], Errors(Weight, -0.1, "lb"));
        Assert.Equal(["Weight must be more than 0."], Errors(Weight, 0, "kg"));
        Assert.Equal(["Enter a percentage from 0 to 100."], Errors(BodyFat, 100.5, "%"));
        Assert.Equal(["Choose a unit."], Errors(Weight, 80, "st"));
        Assert.Equal(["Choose where the value came from."], Errors(Weight, 80, "kg", "guess"));
        Assert.Equal(["The date can't be in the future."], Errors(Weight, 80, "kg", date: Today.AddDays(1)));
    }

    [Fact]
    public void LimitsApplyToTheCanonicalValue()
    {
        var max = new MetricDefinition("x", "body", ["kg", "lb"], "kg", 0, 100);

        Assert.Empty(Errors(max, 220, "lb")); // 99.79 kg
        Assert.Single(Errors(max, 221, "lb")); // 100.24 kg
    }

    [Fact]
    public void TheCanonicalValueIsTheServersConversion()
    {
        Assert.Equal(176.5 * 0.45359237, Weight.ToCanonical(176.5, "lb"));
        Assert.Null(Weight.ToCanonical(80, "cm"));
    }

    [Theory]
    [InlineData("4:30", 270.0)]
    [InlineData(" 10:05 ", 605.0)]
    [InlineData("4:30.5", 270.5)]
    [InlineData("5", 300.0)]
    [InlineData("4.5", 270.0)]
    public void PaceReadsMinutesAndSeconds(string text, double seconds) => Assert.Equal(seconds, PaceText.ParseSeconds(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("4:60")]
    [InlineData("4:5")]
    [InlineData("-4:30")]
    [InlineData("0:00")]
    [InlineData("4:30:00")]
    [InlineData("fast")]
    public void PaceRejectsAnythingElse(string? text) => Assert.Null(PaceText.ParseSeconds(text));

    [Theory]
    [InlineData(270.0, "4:30")]
    [InlineData(269.6, "4:30")]
    [InlineData(59.5, "1:00")]
    [InlineData(0.0, "0:00")]
    public void PaceShowsMinutesAndWholeSeconds(double seconds, string text) => Assert.Equal(text, PaceText.Format(seconds));

    [Theory]
    [InlineData("72.5", 72.5)]
    [InlineData("72,5", 72.5)]
    [InlineData(" 80 ", 80.0)]
    [InlineData("-1.5", -1.5)]
    public void NumbersAcceptAPointOrAComma(string text, double value) => Assert.Equal(value, NumberText.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,000.5")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("eighty")]
    public void NumbersRejectAnythingElse(string? text) => Assert.Null(NumberText.Parse(text));
}
