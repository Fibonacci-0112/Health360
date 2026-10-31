using FsCheck.Fluent;
using Health360.Domain.Body;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Body;

public class NavyMethodTests
{
    // Hand calculations (computed independently of this code) from the printed equations and Siri (1961).
    [Fact]
    public void Male_matches_a_hand_calculation()
    {
        var result = NavyMethod.Male(heightCm: 178, neckCm: 38, abdomenAtUmbilicusCm: 90)!;

        Assert.True(Close(result.Density, 1.052862957691652));
        Assert.True(Close(result.PercentFat, 20.14665715399667));
        Assert.Equal(3.52, result.StandardError);
    }

    [Fact]
    public void Female_matches_a_hand_calculation()
    {
        var result = NavyMethod.Female(heightCm: 165, neckCm: 33, abdomenAtNarrowestCm: 75, hipCm: 100)!;

        Assert.True(Close(result.Density, 1.0324669396034283));
        Assert.True(Close(result.PercentFat, 29.434237565156366));
        Assert.Equal(3.72, result.StandardError);
    }

    [Fact]
    public void Siri_is_as_printed()
    {
        // Report 84-11, Table 1: %BF = 100[(4.95/Body Density) − 4.50].
        Assert.True(Close(NavyMethod.SiriPercentFat(1.1), 0));
        Assert.True(Close(NavyMethod.SiriPercentFat(1.0), 45));
    }

    // Report 84-11, Appendix A: % fat by (abdomen II − neck) and height, all in inches, rounded to whole
    // percent. Every cell checked here matches the equation, including those whose exact value is near .5.
    [Theory]
    [InlineData(60.0, 11.0, 3)]
    [InlineData(60.0, 15.0, 13)]
    [InlineData(60.0, 20.0, 24)]
    [InlineData(60.0, 25.0, 33)]
    [InlineData(60.0, 30.0, 40)]
    [InlineData(64.5, 11.0, 1)]
    [InlineData(64.5, 15.0, 11)]
    [InlineData(64.5, 20.0, 22)]
    [InlineData(64.5, 25.0, 30)]
    [InlineData(64.5, 30.0, 37)]
    [InlineData(65.0, 12.0, 3)]
    [InlineData(65.0, 20.0, 22)]
    [InlineData(65.0, 28.0, 34)]
    [InlineData(65.0, 32.0, 40)]
    [InlineData(67.0, 18.0, 17)]
    [InlineData(67.0, 24.0, 27)]
    [InlineData(69.5, 12.0, 1)]
    [InlineData(69.5, 20.0, 20)]
    [InlineData(69.5, 25.0, 28)]
    [InlineData(69.5, 30.0, 35)]
    [InlineData(69.5, 34.0, 40)]
    public void Male_reproduces_the_published_table(double heightIn, double circumferenceIn, int tablePercent)
    {
        // The equation only sees abdomen − neck, so any neck gives the same result.
        var neckIn = 15.0;
        var result = NavyMethod.Male(Cm(heightIn), Cm(neckIn), Cm(neckIn + circumferenceIn))!;

        Assert.Equal(tablePercent, Math.Round(result.PercentFat, MidpointRounding.AwayFromZero));
    }

    // Report 84-29, Appendix A, by (abdomen I + hip − neck) and height in inches. This table does not
    // reproduce the printed equation exactly: it runs up to about 1.9 points lower at the lean end and is
    // not always monotonic (docs/calculations.md). The app follows the printed equation; this pins how far
    // apart the two are.
    [Theory]
    [InlineData(58.0, 34.5, 1)]
    [InlineData(58.0, 40.0, 11)]
    [InlineData(58.0, 50.0, 27)]
    [InlineData(58.0, 58.5, 38)]
    [InlineData(58.0, 64.5, 45)]
    [InlineData(60.0, 45.0, 18)]
    [InlineData(60.0, 50.0, 25)]
    [InlineData(60.0, 55.0, 32)]
    [InlineData(61.0, 35.5, 0)]
    [InlineData(62.5, 36.0, 0)]
    [InlineData(62.5, 50.0, 23)]
    [InlineData(62.5, 62.0, 38)]
    [InlineData(62.5, 68.0, 45)]
    [InlineData(63.0, 45.0, 16)]
    [InlineData(63.0, 58.5, 34)]
    [InlineData(67.5, 38.0, 1)]
    [InlineData(67.5, 50.0, 20)]
    [InlineData(67.5, 58.5, 31)]
    public void Female_stays_within_two_points_of_the_published_table(double heightIn, double circumferenceIn,
        int tablePercent)
    {
        var neckIn = 13.0;
        var result = NavyMethod.Female(Cm(heightIn), Cm(neckIn), Cm(circumferenceIn / 2), Cm((circumferenceIn / 2) + neckIn))!;

        Assert.InRange(result.PercentFat - tablePercent, -2.0, 2.0);
    }

    [Fact]
    public void Inches_and_centimetres_give_the_same_result()
    {
        Holds(Gen.Zip(Double(150, 200), Gen.Zip(Double(30, 45), Double(10, 60))), t =>
        {
            var (height, (neck, extra)) = t;
            var metric = NavyMethod.Male(height, neck, neck + extra);
            var imperial = NavyMethod.Male(
                Cm(Conversion.Convert(height, Unit.Centimetre, Unit.Inch)),
                Cm(Conversion.Convert(neck, Unit.Centimetre, Unit.Inch)),
                Cm(Conversion.Convert(neck + extra, Unit.Centimetre, Unit.Inch)));
            return (metric, imperial) switch
            {
                (null, null) => true,
                ({ } m, { } i) => Close(m.PercentFat, i.PercentFat, 1e-9),
                _ => false,
            };
        });
    }

    [Fact]
    public void More_abdomen_means_more_fat_and_more_height_means_less()
    {
        Holds(Gen.Zip(Double(150, 200), Double(15, 40)), t =>
        {
            var (height, extra) = t;
            var baseline = NavyMethod.Male(height, 38, 38 + extra);
            var wider = NavyMethod.Male(height, 38, 38 + extra + 1);
            var taller = NavyMethod.Male(height + 1, 38, 38 + extra);
            return baseline is null || ((wider is null || wider.PercentFat > baseline.PercentFat)
                && (taller is null || taller.PercentFat < baseline.PercentFat));
        });
    }

    [Theory]
    [InlineData(178, 38, 38)] // abdomen not larger than neck
    [InlineData(178, 40, 38)]
    [InlineData(0, 38, 90)]
    [InlineData(178, -1, 90)]
    [InlineData(double.NaN, 38, 90)]
    [InlineData(178, 38, double.PositiveInfinity)]
    [InlineData(178, 38, 39)] // a negative percentage
    public void Male_rejects_unusable_inputs(double height, double neck, double abdomen)
    {
        Assert.Null(NavyMethod.Male(height, neck, abdomen));
    }

    [Theory]
    [InlineData(165, 33, 0, 100)]
    [InlineData(165, 200, 75, 100)] // abdomen + hip not larger than neck
    [InlineData(165, 33, 75, double.NaN)]
    public void Female_rejects_unusable_inputs(double height, double neck, double abdomen, double hip)
    {
        Assert.Null(NavyMethod.Female(height, neck, abdomen, hip));
    }

    private static double Cm(double inches) => Conversion.Convert(inches, Unit.Inch, Unit.Centimetre);
}
