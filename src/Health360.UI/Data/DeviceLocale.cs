using System.Globalization;

namespace Health360.UI.Data;

/// <summary>The device's locale, time zone and currency, which onboarding uses as defaults.</summary>
public interface IDeviceLocale
{
    /// <summary>BCP 47 tag, such as <c>en-GB</c>; unit defaults come from its region (PLAN.md §2.6).</summary>
    string CultureName { get; }

    /// <summary>IANA time zone, such as <c>Europe/London</c>.</summary>
    string TimeZone { get; }

    /// <summary>ISO 4217 code for the locale's region.</summary>
    string Currency { get; }
}

/// <summary>Reads the current culture and local time zone. Needs culture data (not invariant globalization).</summary>
public sealed class DeviceLocale : IDeviceLocale
{
    public string CultureName => CultureInfo.CurrentCulture.Name;

    public string TimeZone => IanaTimeZone(TimeZoneInfo.Local);

    public string Currency => CurrencyFor(CultureInfo.CurrentCulture.Name);

    /// <summary>
    /// The zone's IANA id. Windows reports its own ids ("GMT Standard Time"), which Postgres and the
    /// prototype don't use, so those are converted; UTC when there is no mapping.
    /// </summary>
    public static string IanaTimeZone(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";
    }

    /// <summary>The region's currency; USD when the culture has no region (as the prototype's fallback).</summary>
    public static string CurrencyFor(string cultureName)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            if (culture.IsNeutralCulture || culture.Equals(CultureInfo.InvariantCulture))
            {
                return "USD";
            }

            var code = new RegionInfo(culture.Name).ISOCurrencySymbol;
            return code is { Length: 3 } && code.All(char.IsAsciiLetterUpper) ? code : "USD";
        }
        catch (Exception error) when (error is ArgumentException or CultureNotFoundException)
        {
            return "USD";
        }
    }
}
