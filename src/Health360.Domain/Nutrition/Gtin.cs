namespace Health360.Domain.Nutrition;

/// <summary>
/// A GTIN (EAN-8, UPC-A, EAN-13 or GTIN-14) as scanned. Different sources write the same product with different
/// lengths (a UPC-A has no leading zero where the EAN-13 has one), so lookups compare the 14-digit form.
/// </summary>
public static class Gtin
{
    /// <summary>
    /// The 14-digit form of <paramref name="code"/>, left-padded with zeros, or null when it is not 8, 12, 13 or 14
    /// digits or its check digit is wrong (a misread scan).
    /// </summary>
    public static string? Normalize(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var digits = code.Trim();
        if (digits.Length is not (8 or 12 or 13 or 14))
        {
            return null;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            if (digits[i] is < '0' or > '9')
            {
                return null;
            }

            // Counting from the right, the check digit has weight 1, then 3, 1, 3, ...
            if (i < digits.Length - 1)
            {
                var weight = (digits.Length - 1 - i) % 2 == 1 ? 3 : 1;
                sum += (digits[i] - '0') * weight;
            }
        }

        var check = (10 - (sum % 10)) % 10;
        return check == digits[^1] - '0' ? digits.PadLeft(14, '0') : null;
    }
}
