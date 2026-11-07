using Health360.Domain.Units;

namespace Health360.Domain.Nutrition;

/// <summary>
/// What a food's nutrient values are per: 100 g (solids, and most labels) or 100 ml (drinks labelled per volume).
/// Stored in <c>foods.basis</c> as the base unit's code.
/// </summary>
public enum FoodBasis
{
    Mass,
    Volume,
}

public static class FoodBases
{
    /// <summary>The unit an amount of food is converted to before scaling its per-100 values: g or ml.</summary>
    public static Unit BaseUnit(FoodBasis basis) => basis switch
    {
        FoodBasis.Mass => Unit.Gram,
        FoodBasis.Volume => Unit.Millilitre,
        _ => throw new ArgumentOutOfRangeException(nameof(basis), basis, "Unknown food basis"),
    };

    /// <summary>The database code: "g" or "ml".</summary>
    public static string Code(FoodBasis basis) => BaseUnit(basis).Code;

    public static bool TryParse(string? code, out FoodBasis basis)
    {
        switch (code)
        {
            case "g":
                basis = FoodBasis.Mass;
                return true;
            case "ml":
                basis = FoodBasis.Volume;
                return true;
            default:
                basis = default;
                return false;
        }
    }
}

/// <summary>A named measure of one food, such as "1 slice" or "1 cup", and how much of the food's basis it holds.</summary>
public sealed record FoodPortion
{
    public FoodPortion(string name, double baseAmount)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A portion needs a name", nameof(name));
        }

        if (!double.IsFinite(baseAmount) || baseAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseAmount), baseAmount, "Must be a positive amount");
        }

        Name = name;
        BaseAmount = baseAmount;
    }

    public string Name { get; }

    /// <summary>Grams (or ml, for a volume-basis food) in one portion.</summary>
    public double BaseAmount { get; }
}

/// <summary>Turns an amount of food as entered into grams or millilitres of the food's basis.</summary>
public static class FoodAmount
{
    /// <summary>
    /// <paramref name="quantity"/> in <paramref name="unit"/>, converted to the food's base unit through
    /// <see cref="Conversion"/>. A mass unit only converts for a mass-basis food and a volume unit for a volume-basis
    /// one: a cup of flour is a portion, since grams per cup depend on the food.
    /// </summary>
    public static double ToBase(double quantity, Unit unit, FoodBasis basis)
    {
        ArgumentNullException.ThrowIfNull(unit);
        CheckQuantity(quantity);
        var baseUnit = FoodBases.BaseUnit(basis);
        if (unit.Dimension != baseUnit.Dimension)
        {
            throw new ArgumentException($"{unit} can't measure a food listed per 100 {baseUnit}; use a portion", nameof(unit));
        }

        return Conversion.Convert(quantity, unit, baseUnit);
    }

    /// <summary><paramref name="count"/> portions: count × the portion's base amount.</summary>
    public static double ToBase(double count, FoodPortion portion)
    {
        ArgumentNullException.ThrowIfNull(portion);
        CheckQuantity(count);
        return count * portion.BaseAmount;
    }

    private static void CheckQuantity(double quantity)
    {
        if (!double.IsFinite(quantity) || quantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Must be a finite, non-negative amount");
        }
    }
}
