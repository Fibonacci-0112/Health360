namespace Health360.Domain.Units;

public enum Dimension
{
    Mass,
    Length,
    Volume,
    Energy,
    Glucose,
    Temperature,
}

/// <summary>
/// A unit of measure, identified by the code stored in the database's unit columns ("kg", "fl_oz", "mg/dL").
/// Ratio units are defined against their dimension's base unit (kg, m, ml, kcal, mmol/L) using the exact
/// <see cref="Factors"/>; temperature is affine and converted separately. Instances are singletons.
/// </summary>
public sealed class Unit
{
    public static readonly Unit Kilogram = new("kg", Dimension.Mass, new(1, 1));
    public static readonly Unit Gram = new("g", Dimension.Mass, new(1, 1000));
    public static readonly Unit Pound = new("lb", Dimension.Mass, new(Factors.KgPerLb, 1));
    public static readonly Unit Ounce = new("oz", Dimension.Mass, new(Factors.GPerOz, 1000));

    public static readonly Unit Metre = new("m", Dimension.Length, new(1, 1));
    public static readonly Unit Centimetre = new("cm", Dimension.Length, new(1, 100));
    public static readonly Unit Kilometre = new("km", Dimension.Length, new(1000, 1));
    public static readonly Unit Inch = new("in", Dimension.Length, new(Factors.CmPerIn, 100));
    public static readonly Unit Foot = new("ft", Dimension.Length, new(Factors.CmPerIn * 12, 100));
    public static readonly Unit Yard = new("yd", Dimension.Length, new(Factors.CmPerIn * 36, 100));
    public static readonly Unit Mile = new("mi", Dimension.Length, new(Factors.MPerMi, 1));

    // US customary kitchen measures: 1 cup = 8 fl oz, 1 tbsp = 1/2 fl oz, 1 tsp = 1/6 fl oz.
    public static readonly Unit Millilitre = new("ml", Dimension.Volume, new(1, 1));
    public static readonly Unit Litre = new("l", Dimension.Volume, new(1000, 1));
    public static readonly Unit FluidOunce = new("fl_oz", Dimension.Volume, new(Factors.MlPerUsFlOz, 1));
    public static readonly Unit Cup = new("cup", Dimension.Volume, new(Factors.MlPerUsFlOz * 8, 1));
    public static readonly Unit Tablespoon = new("tbsp", Dimension.Volume, new(Factors.MlPerUsFlOz, 2));
    public static readonly Unit Teaspoon = new("tsp", Dimension.Volume, new(Factors.MlPerUsFlOz, 6));

    public static readonly Unit Kilocalorie = new("kcal", Dimension.Energy, new(1, 1));
    public static readonly Unit Kilojoule = new("kJ", Dimension.Energy, new(1, Factors.KJPerKcal));

    public static readonly Unit MillimolePerLitre = new("mmol/L", Dimension.Glucose, new(1, 1));
    public static readonly Unit MilligramPerDecilitre = new("mg/dL", Dimension.Glucose, new(1, Factors.GlucoseMgDlPerMmolL));

    public static readonly Unit Celsius = new("C", Dimension.Temperature, null);
    public static readonly Unit Fahrenheit = new("F", Dimension.Temperature, null);

    /// <summary>Every unit, grouped by dimension in <see cref="Dimension"/> order.</summary>
    public static IReadOnlyList<Unit> All { get; } =
    [
        Kilogram, Gram, Pound, Ounce,
        Metre, Centimetre, Kilometre, Inch, Foot, Yard, Mile,
        Millilitre, Litre, FluidOunce, Cup, Tablespoon, Teaspoon,
        Kilocalorie, Kilojoule,
        MillimolePerLitre, MilligramPerDecilitre,
        Celsius, Fahrenheit,
    ];

    private static readonly Dictionary<string, Unit> ByCode = All.ToDictionary(u => u.Code, StringComparer.Ordinal);

    private Unit(string code, Dimension dimension, Ratio? ratio)
    {
        Code = code;
        Dimension = dimension;
        Ratio = ratio;
    }

    /// <summary>The code stored in the database. Case-sensitive ("kJ", "mmol/L").</summary>
    public string Code { get; }

    public Dimension Dimension { get; }

    /// <summary>The ratio to the dimension's base unit; null for temperature.</summary>
    internal Ratio? Ratio { get; }

    public static IReadOnlyList<Unit> OfDimension(Dimension dimension) => [.. All.Where(u => u.Dimension == dimension)];

    public static bool TryParse(string? code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Unit? unit) =>
        ByCode.TryGetValue(code ?? "", out unit);

    public static Unit Parse(string code) =>
        TryParse(code, out var unit) ? unit : throw new FormatException($"Unknown unit: {code}");

    public override string ToString() => Code;
}
