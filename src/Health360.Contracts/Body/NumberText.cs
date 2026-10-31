using System.Globalization;

namespace Health360.Contracts.Body;

/// <summary>A number as typed into a form field.</summary>
public static class NumberText
{
    /// <summary>
    /// Reads a plain decimal number, with a point or a comma as the decimal separator ("72.5" or "72,5"), and an
    /// optional leading minus (T-scores and temperatures can be negative). No thousands separators, exponents,
    /// NaN or infinity. Null when the text isn't such a number.
    /// </summary>
    public static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }
}
