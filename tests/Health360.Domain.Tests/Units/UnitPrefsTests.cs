using Health360.Domain.Units;

namespace Health360.Domain.Tests.Units;

public class UnitPrefsTests
{
    [Theory]
    [InlineData("en-US", BodyWeightUnit.Pound, TemperatureUnit.Fahrenheit, GlucoseUnit.MilligramPerDecilitre)]
    [InlineData("en-GB", BodyWeightUnit.StonePound, TemperatureUnit.Celsius, GlucoseUnit.MillimolePerLitre)]
    [InlineData("de-DE", BodyWeightUnit.Kilogram, TemperatureUnit.Celsius, GlucoseUnit.MillimolePerLitre)]
    [InlineData("en", BodyWeightUnit.Kilogram, TemperatureUnit.Celsius, GlucoseUnit.MillimolePerLitre)]
    public void Locale_defaults(string locale, BodyWeightUnit bodyWeight, TemperatureUnit temperature, GlucoseUnit glucose)
    {
        var p = UnitPrefs.DefaultFor(locale);

        Assert.Equal((bodyWeight, temperature, glucose), (p.BodyWeight, p.Temperature, p.Glucose));
    }

    [Fact]
    public void The_UK_keeps_stone_feet_and_inches_and_miles()
    {
        Assert.Equal(
            UnitPrefs.Metric with
            {
                BodyWeight = BodyWeightUnit.StonePound,
                Height = HeightUnit.FeetInches,
                Distance = DistanceUnit.Mile,
                Pace = PaceUnit.MinutesPerMile,
                ProteinRatio = ProteinRatioUnit.GramsPerPound,
            },
            UnitPrefs.DefaultFor("en-GB"));
    }

    [Fact]
    public void Protein_ratio_follows_the_body_weight_unit()
    {
        Assert.Equal(ProteinRatioUnit.GramsPerPound, UnitPrefs.DefaultFor("en-US").ProteinRatio);
        Assert.Equal(ProteinRatioUnit.GramsPerPound, UnitPrefs.DefaultFor("en-GB").ProteinRatio);
        Assert.Equal(ProteinRatioUnit.GramsPerKilogram, UnitPrefs.DefaultFor("fr-FR").ProteinRatio);
    }

    [Fact]
    public void Australia_and_New_Zealand_default_to_kJ()
    {
        Assert.Equal(EnergyUnit.Kilojoule, UnitPrefs.DefaultFor("en-AU").Energy);
        Assert.Equal(EnergyUnit.Kilojoule, UnitPrefs.DefaultFor("mi-NZ").Energy);
        Assert.Equal(EnergyUnit.Kilocalorie, UnitPrefs.DefaultFor("en-CA").Energy);
    }

    [Theory]
    [InlineData("de-Latn-AT", "AT")]
    [InlineData("en_GB", "GB")]
    [InlineData("es-419", "419")]
    [InlineData("en-us", "US")]
    [InlineData("en", null)]
    [InlineData("", null)]
    public void Parses_region_subtags(string locale, string? region)
    {
        Assert.Equal(region, UnitPrefs.RegionOf(locale));
    }

    [Fact]
    public void Preference_codes_match_the_stored_codes()
    {
        // profiles.unit_prefs already stores these codes (written by the Phase 0 prototype too).
        Assert.Equal(["kg", "lb", "st_lb"], Codes(UnitPrefOptions.BodyWeight));
        Assert.Equal(["kg", "lb"], Codes(UnitPrefOptions.Load));
        Assert.Equal(["cm", "ft_in"], Codes(UnitPrefOptions.Height));
        Assert.Equal(["cm", "in"], Codes(UnitPrefOptions.Circumference));
        Assert.Equal(["km", "mi"], Codes(UnitPrefOptions.Distance));
        Assert.Equal(["m", "yd"], Codes(UnitPrefOptions.ShortDistance));
        Assert.Equal(["min_per_km", "min_per_mi"], Codes(UnitPrefOptions.Pace));
        Assert.Equal(["g", "oz"], Codes(UnitPrefOptions.FoodMass));
        Assert.Equal(["ml", "fl_oz", "cup"], Codes(UnitPrefOptions.Volume));
        Assert.Equal(["kcal", "kJ"], Codes(UnitPrefOptions.Energy));
        Assert.Equal(["C", "F"], Codes(UnitPrefOptions.Temperature));
        Assert.Equal(["mg/dL", "mmol/L"], Codes(UnitPrefOptions.Glucose));
        Assert.Equal(["conventional", "si"], Codes(UnitPrefOptions.LabValues));
        Assert.Equal(["g_per_kg", "g_per_lb"], Codes(UnitPrefOptions.ProteinRatio));
    }

    [Fact]
    public void Every_option_list_covers_its_whole_enum()
    {
        AssertCovers(UnitPrefOptions.BodyWeight);
        AssertCovers(UnitPrefOptions.Load);
        AssertCovers(UnitPrefOptions.Height);
        AssertCovers(UnitPrefOptions.Circumference);
        AssertCovers(UnitPrefOptions.Distance);
        AssertCovers(UnitPrefOptions.ShortDistance);
        AssertCovers(UnitPrefOptions.Pace);
        AssertCovers(UnitPrefOptions.FoodMass);
        AssertCovers(UnitPrefOptions.Volume);
        AssertCovers(UnitPrefOptions.Energy);
        AssertCovers(UnitPrefOptions.Temperature);
        AssertCovers(UnitPrefOptions.Glucose);
        AssertCovers(UnitPrefOptions.LabValues);
        AssertCovers(UnitPrefOptions.ProteinRatio);
    }

    [Fact]
    public void Plain_unit_preferences_use_the_unit_codes()
    {
        Assert.All(Codes(UnitPrefOptions.Energy), c => Assert.Equal(Dimension.Energy, Unit.Parse(c).Dimension));
        Assert.All(Codes(UnitPrefOptions.Temperature), c => Assert.Equal(Dimension.Temperature, Unit.Parse(c).Dimension));
        Assert.All(Codes(UnitPrefOptions.Glucose), c => Assert.Equal(Dimension.Glucose, Unit.Parse(c).Dimension));
    }

    [Fact]
    public void Codes_parse_back_and_unknown_codes_are_rejected()
    {
        Assert.Equal(BodyWeightUnit.StonePound, UnitPrefOptions.BodyWeight.Parse("st_lb"));
        Assert.False(UnitPrefOptions.Energy.TryParse("KJ", out _));
        Assert.Throws<FormatException>(() => UnitPrefOptions.Volume.Parse("gallon"));
    }

    private static string[] Codes<T>(CodeMap<T> map)
        where T : struct, Enum => [.. map.Values.Select(map.ToCode)];

    private static void AssertCovers<T>(CodeMap<T> map)
        where T : struct, Enum
    {
        Assert.Equal(Enum.GetValues<T>().Order(), map.Values.Order());
        Assert.Equal(map.Values.Count, Codes(map).Distinct(StringComparer.Ordinal).Count());
        Assert.All(map.Values, v => Assert.Equal(v, map.Parse(map.ToCode(v))));
    }
}
