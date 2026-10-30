namespace Health360.Domain.Units;

public static class Conversion
{
    /// <summary>Converts between two units of the same dimension. Throws on a dimension mismatch.</summary>
    public static double Convert(double value, Unit from, Unit to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from == to)
        {
            return value;
        }

        if (from.Dimension != to.Dimension)
        {
            throw new ArgumentException($"Cannot convert {from} to {to}", nameof(to));
        }

        if (from.Dimension == Dimension.Temperature)
        {
            return from == Unit.Celsius ? (value * 9 / 5) + 32 : (value - 32) * 5 / 9;
        }

        var a = from.Ratio!.Value;
        var b = to.Ratio!.Value;
        // value × a.Num/a.Den gives the base unit; × b.Den/b.Num leaves it in `to`.
        // Multiplying both numerators first keeps exact pairs (e.g. lb↔kg) to one rounding step.
        // The evaluation order matches packages/domain, so both stacks produce the same doubles.
        return value * a.Num * b.Den / (a.Den * b.Num);
    }
}
