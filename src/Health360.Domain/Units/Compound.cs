namespace Health360.Domain.Units;

public readonly record struct StoneLb(double Stone, double Pounds);

public readonly record struct FeetInches(double Feet, double Inches);

/// <summary>Stone + pounds and feet + inches, the two compound display units.</summary>
public static class Compound
{
    private const int LbPerStone = 14;
    private const int InPerFt = 12;

    /// <summary>
    /// Splits a mass into stone and pounds for display, rounding the pounds to <paramref name="lbDecimals"/>.
    /// If the pounds round up to 14, they carry into the next stone (so never "12 st 14 lb").
    /// </summary>
    public static StoneLb KgToStoneLb(double kg, int lbDecimals = 1)
    {
        if (!(kg >= 0) || double.IsInfinity(kg))
        {
            throw new ArgumentOutOfRangeException(nameof(kg), kg, "Mass must be finite and not negative");
        }

        var totalLb = Rounding.RoundTo(kg / Factors.KgPerLb, lbDecimals);
        var st = Math.Floor(totalLb / LbPerStone);
        var lb = Rounding.RoundTo(totalLb - (st * LbPerStone), lbDecimals);
        return lb >= LbPerStone ? new(st + 1, Rounding.RoundTo(lb - LbPerStone, lbDecimals)) : new(st, lb);
    }

    public static double StoneLbToKg(StoneLb value) => ((value.Stone * LbPerStone) + value.Pounds) * Factors.KgPerLb;

    /// <summary>Splits a length into feet and inches for display, carrying 12 in into the next foot.</summary>
    public static FeetInches CmToFeetInches(double cm, int inDecimals = 0)
    {
        if (!(cm >= 0) || double.IsInfinity(cm))
        {
            throw new ArgumentOutOfRangeException(nameof(cm), cm, "Length must be finite and not negative");
        }

        var totalIn = Rounding.RoundTo(cm / Factors.CmPerIn, inDecimals);
        var ft = Math.Floor(totalIn / InPerFt);
        var inches = Rounding.RoundTo(totalIn - (ft * InPerFt), inDecimals);
        return inches >= InPerFt ? new(ft + 1, Rounding.RoundTo(inches - InPerFt, inDecimals)) : new(ft, inches);
    }

    public static double FeetInchesToCm(FeetInches value) => ((value.Feet * InPerFt) + value.Inches) * Factors.CmPerIn;
}
