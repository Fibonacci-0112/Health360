namespace Health360.Domain.Units;

/// <summary>
/// Converts between the unit codes stored in <c>measurements.unit</c> and <c>metric_definitions</c>. It mirrors
/// the database's <c>private.convert_unit</c> operation for operation, so a value the app writes has the same
/// canonical value the server's trigger recomputes:
/// <list type="bullet">
/// <item>Ratio and temperature units (<see cref="Unit"/> codes) go through <see cref="Conversion.Convert"/>.</item>
/// <item>Pace in <c>s_per_km</c> and <c>s_per_mi</c> goes through <see cref="Pace"/>.</item>
/// <item>Fixed scientific units (bpm, W, ml/kg/min, %, ...) convert only to themselves.</item>
/// </list>
/// </summary>
public static class UnitCodes
{
    public const string SecondsPerKm = "s_per_km";
    public const string SecondsPerMile = "s_per_mi";

    /// <summary>The value in <paramref name="toCode"/>; null when the two codes can't be converted.</summary>
    public static double? Convert(double value, string fromCode, string toCode)
    {
        ArgumentNullException.ThrowIfNull(fromCode);
        ArgumentNullException.ThrowIfNull(toCode);
        if (fromCode == toCode)
        {
            return value;
        }

        if (fromCode == SecondsPerMile && toCode == SecondsPerKm)
        {
            return Pace.ToSecondsPerKm(value, PaceUnit.MinutesPerMile);
        }

        if (fromCode == SecondsPerKm && toCode == SecondsPerMile)
        {
            return Pace.FromSecondsPerKm(value, PaceUnit.MinutesPerMile);
        }

        return Unit.TryParse(fromCode, out var from) && Unit.TryParse(toCode, out var to) && from.Dimension == to.Dimension
            ? Conversion.Convert(value, from, to)
            : null;
    }
}
