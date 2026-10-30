namespace Health360.Domain.Units;

/// <summary>What a quantity column measures (PLAN.md §2.3, §2.6).</summary>
public enum QuantityKind
{
    BodyWeight,
    Load,
    FoodMass,
    Height,
    Circumference,
    Distance,
    Volume,
    Energy,
    Temperature,
    Glucose,
}

public static class QuantityKinds
{
    /// <summary>
    /// The unit a kind's canonical column is stored in. The canonical value drives math, trends, PRs,
    /// charts and AI prompts.
    /// </summary>
    public static Unit CanonicalUnit(QuantityKind kind) => kind switch
    {
        QuantityKind.BodyWeight or QuantityKind.Load => Unit.Kilogram,
        QuantityKind.FoodMass => Unit.Gram,
        QuantityKind.Height or QuantityKind.Circumference => Unit.Centimetre,
        QuantityKind.Distance => Unit.Metre,
        QuantityKind.Volume => Unit.Millilitre,
        QuantityKind.Energy => Unit.Kilocalorie,
        QuantityKind.Temperature => Unit.Celsius,
        QuantityKind.Glucose => Unit.MillimolePerLitre,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown quantity kind"),
    };

    public static Dimension DimensionOf(QuantityKind kind) => CanonicalUnit(kind).Dimension;

    public static double ToCanonical(QuantityKind kind, double value, Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Dimension != DimensionOf(kind))
        {
            throw new ArgumentException($"{unit} is not a unit of {kind}", nameof(unit));
        }

        return Conversion.Convert(value, unit, CanonicalUnit(kind));
    }
}

/// <summary>A stored quantity: the value exactly as typed, its unit, and the canonical value for math.</summary>
public sealed record Quantity
{
    private Quantity(QuantityKind kind, double value, Unit unit, double canonical)
    {
        Kind = kind;
        Value = value;
        Unit = unit;
        Canonical = canonical;
    }

    public QuantityKind Kind { get; }

    public double Value { get; }

    public Unit Unit { get; }

    public double Canonical { get; }

    /// <summary>Builds the (value, unit, canonical) trio written to the database.</summary>
    public static Quantity Create(QuantityKind kind, double value, Unit unit)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Quantity value must be finite");
        }

        return new Quantity(kind, value, unit, QuantityKinds.ToCanonical(kind, value, unit));
    }

    /// <summary>
    /// The number to show in <paramref name="unit"/>. Shown in the unit it was typed in, the original value
    /// comes back untouched, so 225 lb never turns into 224.99 lb. Otherwise it is converted from the canonical value.
    /// </summary>
    public double DisplayValue(Unit unit) =>
        unit == Unit ? Value : Conversion.Convert(Canonical, QuantityKinds.CanonicalUnit(Kind), unit);

    /// <summary>
    /// Compares two canonical values with a relative tolerance, so a PR logged in lb and repeated in
    /// kg (or the reverse) is never counted as a new record just from conversion rounding.
    /// </summary>
    public static bool CanonicalEquals(double a, double b, double relativeTolerance = 1e-9) =>
        Math.Abs(a - b) <= relativeTolerance * Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1);

    public static bool CanonicalGreater(double a, double b, double relativeTolerance = 1e-9) =>
        a > b && !CanonicalEquals(a, b, relativeTolerance);
}
