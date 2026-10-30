namespace Health360.Domain.Units;

public enum PaceUnit
{
    MinutesPerKilometre,
    MinutesPerMile,
}

/// <summary>Pace is stored canonically as seconds per kilometre.</summary>
public static class Pace
{
    public static double ToSecondsPerKm(double secondsPerUnit, PaceUnit unit) =>
        unit == PaceUnit.MinutesPerKilometre ? secondsPerUnit : secondsPerUnit * 1000 / Factors.MPerMi;

    public static double FromSecondsPerKm(double secondsPerKm, PaceUnit unit) =>
        unit == PaceUnit.MinutesPerKilometre ? secondsPerKm : secondsPerKm * Factors.MPerMi / 1000;

    /// <summary>Seconds per km from a distance (m) and a duration (s).</summary>
    public static double FromDistance(double distanceM, double durationS)
    {
        if (!(distanceM > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(distanceM), distanceM, "Distance must be positive");
        }

        return durationS * 1000 / distanceM;
    }
}
