namespace Health360.Domain.Nutrition;

/// <summary>
/// A nutrient the app records, identified by the code stored in the database (<c>nutrients.code</c> and the keys of
/// the nutrient maps), and the fixed unit its amounts are always in. Nutrient amounts are never entered in a user's
/// unit, so they have no unit column: energy is kcal, macronutrients are grams, minerals milligrams, vitamin D
/// micrograms. Instances are singletons.
/// </summary>
public sealed class Nutrient
{
    public static readonly Nutrient Energy = new("energy", NutrientUnit.Kilocalorie);
    public static readonly Nutrient Protein = new("protein", NutrientUnit.Gram);

    /// <summary>Total carbohydrate as the label declares it (on US labels it includes fiber and sugar alcohols).</summary>
    public static readonly Nutrient Carbohydrate = new("carbohydrate", NutrientUnit.Gram);

    public static readonly Nutrient Fat = new("fat", NutrientUnit.Gram);
    public static readonly Nutrient Fiber = new("fiber", NutrientUnit.Gram);
    public static readonly Nutrient Sugars = new("sugars", NutrientUnit.Gram);
    public static readonly Nutrient AddedSugars = new("added_sugars", NutrientUnit.Gram);
    public static readonly Nutrient SugarAlcohols = new("sugar_alcohols", NutrientUnit.Gram);
    public static readonly Nutrient SaturatedFat = new("saturated_fat", NutrientUnit.Gram);
    public static readonly Nutrient TransFat = new("trans_fat", NutrientUnit.Gram);
    public static readonly Nutrient Cholesterol = new("cholesterol", NutrientUnit.Milligram);
    public static readonly Nutrient Sodium = new("sodium", NutrientUnit.Milligram);
    public static readonly Nutrient Potassium = new("potassium", NutrientUnit.Milligram);
    public static readonly Nutrient Calcium = new("calcium", NutrientUnit.Milligram);
    public static readonly Nutrient Iron = new("iron", NutrientUnit.Milligram);
    public static readonly Nutrient VitaminD = new("vitamin_d", NutrientUnit.Microgram);

    /// <summary>
    /// Every nutrient, in display order: the Nutrition Facts label set for now. The full micronutrient set arrives with
    /// the USDA import. The migration seeds the same codes and units into <c>nutrients</c>.
    /// </summary>
    public static IReadOnlyList<Nutrient> All { get; } =
    [
        Energy, Fat, SaturatedFat, TransFat, Cholesterol, Sodium, Carbohydrate, Fiber, Sugars, AddedSugars,
        SugarAlcohols, Protein, VitaminD, Calcium, Iron, Potassium,
    ];

    /// <summary>The nutrients the food and log tables store in their own columns rather than in the nutrient map.</summary>
    public static IReadOnlyList<Nutrient> Macros { get; } = [Energy, Protein, Carbohydrate, Fat];

    private static readonly Dictionary<string, Nutrient> ByCode = All.ToDictionary(n => n.Code, StringComparer.Ordinal);

    private Nutrient(string code, NutrientUnit unit)
    {
        Code = code;
        Unit = unit;
    }

    /// <summary>The database code. Lowercase ASCII, digits and underscores.</summary>
    public string Code { get; }

    public NutrientUnit Unit { get; }

    public static bool TryParse(string? code, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Nutrient? nutrient) =>
        ByCode.TryGetValue(code ?? "", out nutrient);

    public override string ToString() => Code;
}

/// <summary>The fixed unit of a nutrient's amounts.</summary>
public enum NutrientUnit
{
    Kilocalorie,
    Gram,
    Milligram,
    Microgram,
}

public static class NutrientUnits
{
    /// <summary>The code stored in <c>nutrients.unit</c>.</summary>
    public static string Code(NutrientUnit unit) => unit switch
    {
        NutrientUnit.Kilocalorie => "kcal",
        NutrientUnit.Gram => "g",
        NutrientUnit.Milligram => "mg",
        NutrientUnit.Microgram => "ug",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, "Unknown nutrient unit"),
    };
}
