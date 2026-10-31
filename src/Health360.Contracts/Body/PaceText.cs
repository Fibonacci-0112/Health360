using System.Globalization;

namespace Health360.Contracts.Body;

/// <summary>Pace as typed and shown: minutes and seconds (<c>4:30</c>) per km or mile, stored as seconds.</summary>
public static class PaceText
{
    /// <summary>
    /// Reads <c>m:ss</c> (seconds 0–59, may have decimals) or a plain number of minutes. Null when the text isn't a
    /// pace above zero.
    /// </summary>
    public static double? ParseSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        double seconds;
        if (parts.Length == 1)
        {
            if (!TryNumber(parts[0], out var minutes))
            {
                return null;
            }

            seconds = minutes * 60;
        }
        else if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var whole)
            && TryNumber(parts[1], out var rest) && rest < 60 && parts[1].Length >= 2)
        {
            seconds = (whole * 60) + rest;
        }
        else
        {
            return null;
        }

        return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
    }

    /// <summary><c>m:ss</c>, with the seconds rounded to whole numbers for display.</summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Pace must be finite and not negative");
        }

        var total = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}:{total % 60:00}");
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
}
