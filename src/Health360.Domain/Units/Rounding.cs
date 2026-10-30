namespace Health360.Domain.Units;

public static class Rounding
{
    /// <summary>2^-52, JavaScript's <c>Number.EPSILON</c>. Not <see cref="double.Epsilon"/>, the smallest subnormal.</summary>
    private const double MachineEpsilon = 2.220446049250313e-16;

    /// <summary>
    /// Rounds half away from zero to <paramref name="decimals"/> places, for presentation only. The (1 + ε)
    /// nudge stops binary representation from rounding halves down (1.005 × 100 is 100.49999999999999).
    /// </summary>
    public static double RoundTo(double value, int decimals = 0)
    {
        if (!double.IsFinite(value))
        {
            return value;
        }

        var factor = Math.Pow(10, decimals);
        var sign = value < 0 ? -1 : 1;
        return sign * Math.Round(Math.Abs(value) * factor * (1 + MachineEpsilon), MidpointRounding.AwayFromZero) / factor;
    }
}
