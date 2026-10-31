using Health360.Domain.Physiology;

namespace Health360.Domain.Tests.Physiology;

public class ActivityLevelTests
{
    [Fact]
    public void Levels_tile_the_fao_bands_and_use_their_midpoints()
    {
        // FAO/WHO/UNU (2004), Table 5.3: 1.40–1.69, 1.70–1.99, 2.00–2.40.
        var bands = Enum.GetValues<ActivityLevel>().Select(ActivityLevels.Pal).ToList();

        Assert.Equal(1.40, bands[0].Low);
        Assert.Equal(2.40, bands[^1].High);
        Assert.All(bands.Zip(bands.Skip(1)), p => Assert.Equal(p.First.High, p.Second.Low));
        Assert.All(bands, b => Assert.Equal((b.Low + b.High) / 2, b.Value, 12));

        // Band edges fall on FAO's: the first two levels share the sedentary-or-light band, Moderate is the
        // whole moderately active band, and the last two share the vigorous band.
        Assert.Equal(1.70, ActivityLevels.Pal(ActivityLevel.Moderate).Low);
        Assert.Equal(2.00, ActivityLevels.Pal(ActivityLevel.Moderate).High);
    }

    [Theory]
    [InlineData(ActivityLevel.Sedentary, "sedentary", 1.475)]
    [InlineData(ActivityLevel.Light, "light", 1.625)]
    [InlineData(ActivityLevel.Moderate, "moderate", 1.85)]
    [InlineData(ActivityLevel.Active, "active", 2.10)]
    [InlineData(ActivityLevel.VeryActive, "very_active", 2.30)]
    public void Codes_round_trip_and_map_to_their_pal(ActivityLevel level, string code, double pal)
    {
        Assert.Equal(code, ActivityLevels.Code(level));
        Assert.True(ActivityLevels.TryParse(code, out var parsed));
        Assert.Equal(level, parsed);
        Assert.Equal(pal, ActivityLevels.Pal(level).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Moderate")]
    public void Unknown_codes_dont_parse(string? code)
    {
        Assert.False(ActivityLevels.TryParse(code, out _));
    }
}
