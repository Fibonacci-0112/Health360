using Health360.UI.Data;

namespace Health360.UI.Tests.Data;

public class DeviceLocaleTests
{
    [Theory]
    [InlineData("en-US", "USD")]
    [InlineData("en-GB", "GBP")]
    [InlineData("de-DE", "EUR")]
    [InlineData("sv-SE", "SEK")]
    [InlineData("en-AU", "AUD")]
    [InlineData("ja-JP", "JPY")]
    public void CurrencyComesFromTheRegion(string culture, string currency) => Assert.Equal(currency, DeviceLocale.CurrencyFor(culture));

    [Theory]
    [InlineData("en")]
    [InlineData("")]
    [InlineData("xx-not-a-culture-zz")]
    public void NoRegionMeansUsd(string culture) => Assert.Equal("USD", DeviceLocale.CurrencyFor(culture));

    [Fact]
    public void IanaZonesAreKept() =>
        Assert.Equal("Europe/London", DeviceLocale.IanaTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));

    [Fact]
    public void WindowsZonesBecomeIana() =>
        // Windows reports its own ids; Postgres and the prototype store IANA ones.
        Assert.Equal("Europe/London", DeviceLocale.IanaTimeZone(TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")));

    [Fact]
    public void AZoneWithNoMappingIsUtc() =>
        Assert.Equal("UTC", DeviceLocale.IanaTimeZone(TimeZoneInfo.CreateCustomTimeZone("Nowhere Standard Time", TimeSpan.FromHours(3), "Nowhere", "Nowhere")));

    [Fact]
    public void TheDevicesOwnValuesAreUsable()
    {
        var locale = new DeviceLocale();

        Assert.Equal(3, locale.Currency.Length);
        Assert.False(string.IsNullOrEmpty(locale.TimeZone));
    }
}
