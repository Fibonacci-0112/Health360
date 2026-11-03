namespace Health360.Domain.Training;

/// <summary>The published equation an estimated one-rep max (e1RM) comes from.</summary>
public enum OneRepMaxFormula
{
    Epley,
    Brzycki,
}

/// <summary>Estimated one-rep max from a set taken to fatigue. Sources: docs/calculations.md.</summary>
public static class OneRepMax
{
    /// <summary>
    /// The most reps the app estimates from. Both equations agree within about 4% up to 10 reps and
    /// diverge beyond; this is a product limit, not a figure from either paper.
    /// </summary>
    public const int MaxReps = 10;

    /// <summary>
    /// The e1RM in the load's own unit (pass canonical kg to compare across units), or null above
    /// <see cref="MaxReps"/>. A single is its own one-rep max: it was lifted, not estimated.
    /// Epley (1985): load × (1 + reps / 30). Brzycki (1993): load × 36 / (37 − reps).
    /// </summary>
    public static double? Estimate(double load, int reps, OneRepMaxFormula formula)
    {
        if (!double.IsFinite(load) || load <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(load), load, "Must be a positive number");
        }

        if (reps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(reps), reps, "At least one rep");
        }

        if (reps > MaxReps)
        {
            return null;
        }

        if (reps == 1)
        {
            return load;
        }

        return formula switch
        {
            OneRepMaxFormula.Epley => load * (1 + (reps / 30.0)),
            OneRepMaxFormula.Brzycki => load * 36 / (37 - reps),
            _ => throw new ArgumentOutOfRangeException(nameof(formula), formula, "Unknown formula"),
        };
    }
}
