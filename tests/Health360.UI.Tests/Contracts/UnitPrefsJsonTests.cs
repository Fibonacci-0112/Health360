using Health360.Contracts.Profiles;
using Health360.Domain.Units;

namespace Health360.UI.Tests.Contracts;

public class UnitPrefsJsonTests
{
    // What the Phase 0 prototype wrote (JSON.stringify(METRIC_PREFS)), so stored rows still read back.
    private const string MetricJson =
        """{"bodyWeight":"kg","load":"kg","height":"cm","circumference":"cm","distance":"km","shortDistance":"m","pace":"min_per_km","foodMass":"g","volume":"ml","energy":"kcal","temperature":"C","glucose":"mmol/L","labValues":"si","proteinRatio":"g_per_kg"}""";

    [Fact]
    public void WritesThePrototypesFormat() => Assert.Equal(MetricJson, UnitPrefsJson.Serialize(UnitPrefs.Metric));

    [Fact]
    public void ListsEveryTypeWithTheDomainsCodes()
    {
        Assert.Equal(
            ["bodyWeight", "load", "height", "circumference", "distance", "shortDistance", "pace", "foodMass", "volume", "energy", "temperature", "glucose", "labValues", "proteinRatio"],
            UnitPrefsJson.Fields.Select(f => f.Key));
        Assert.Equal(["kg", "lb", "st_lb"], UnitPrefsJson.Fields[0].Codes);
        Assert.Equal(["ml", "fl_oz", "cup"], UnitPrefsJson.Fields.Single(f => f.Key == "volume").Codes);
    }

    public static TheoryData<string> Locales() => ["en-US", "en-GB", "de-DE", "en-AU", "ja-JP", "fr-CA"];

    [Theory]
    [MemberData(nameof(Locales))]
    public void EveryLocaleDefaultRoundTrips(string locale)
    {
        var prefs = UnitPrefs.DefaultFor(locale);

        Assert.Equal(prefs, UnitPrefsJson.Parse(UnitPrefsJson.Serialize(prefs), UnitPrefs.Metric));
    }

    [Fact]
    public void EveryChoiceOfEveryTypeRoundTrips()
    {
        foreach (var field in UnitPrefsJson.Fields)
        {
            foreach (var code in field.Codes)
            {
                var prefs = field.With(UnitPrefs.UsCustomary, code)!;
                var parsed = UnitPrefsJson.Parse(UnitPrefsJson.Serialize(prefs), UnitPrefs.Metric);
                Assert.Equal(code, field.CodeOf(parsed));
                Assert.Equal(prefs, parsed);
            }
        }
    }

    [Fact]
    public void TypesNotSavedYetComeFromTheLocale()
    {
        var parsed = UnitPrefsJson.Parse("""{"load":"lb"}""", UnitPrefs.DefaultFor("en-GB"));

        Assert.Equal(UnitPrefs.DefaultFor("en-GB") with { Load = LoadUnit.Pound }, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("\"kg\"")]
    [InlineData("""{"load":"stone"}""")]
    [InlineData("""{"load":null}""")]
    [InlineData("""{"load":1}""")]
    [InlineData("""{"bodyWeight":"lb","load":"stone"}""")]
    public void AnythingInvalidFallsBackToTheLocaleDefaultsWhole(string? json)
    {
        var fallback = UnitPrefs.DefaultFor("en-AU");

        Assert.Equal(fallback, UnitPrefsJson.Parse(json, fallback));
    }

    [Fact]
    public void UnknownKeysAreIgnored() =>
        Assert.Equal(UnitPrefs.Metric with { Pace = PaceUnit.MinutesPerMile },
            UnitPrefsJson.Parse("""{"pace":"min_per_mi","future":"x"}""", UnitPrefs.Metric));

    [Fact]
    public void ProteinRatioFollowsBodyWeightWhileOnItsDefault()
    {
        var bodyWeight = UnitPrefsJson.Fields[0];

        var next = UnitPrefsJson.Choose(UnitPrefs.Metric, bodyWeight, "lb");

        Assert.Equal((BodyWeightUnit.Pound, ProteinRatioUnit.GramsPerPound), (next.BodyWeight, next.ProteinRatio));
    }

    [Fact]
    public void ProteinRatioTheUserChoseStays()
    {
        var bodyWeight = UnitPrefsJson.Fields[0];
        var chosen = UnitPrefs.Metric with { ProteinRatio = ProteinRatioUnit.GramsPerPound };

        var next = UnitPrefsJson.Choose(chosen, bodyWeight, "st_lb");

        Assert.Equal(ProteinRatioUnit.GramsPerPound, next.ProteinRatio);
        Assert.Equal(ProteinRatioUnit.GramsPerPound, UnitPrefsJson.Choose(chosen, UnitPrefsJson.Fields[1], "lb").ProteinRatio);
    }

    [Fact]
    public void ChoosingAnUnknownCodeThrows() =>
        Assert.Throws<ArgumentException>(() => UnitPrefsJson.Choose(UnitPrefs.Metric, UnitPrefsJson.Fields[1], "stone"));
}
