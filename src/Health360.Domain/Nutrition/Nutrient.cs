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

    /// <summary>Vitamin A as retinol activity equivalents (µg RAE), the measure the FDA Daily Value uses.</summary>
    public static readonly Nutrient VitaminA = new("vitamin_a", NutrientUnit.Microgram);

    public static readonly Nutrient VitaminC = new("vitamin_c", NutrientUnit.Milligram);

    /// <summary>Vitamin E as alpha-tocopherol.</summary>
    public static readonly Nutrient VitaminE = new("vitamin_e", NutrientUnit.Milligram);

    /// <summary>Vitamin K as phylloquinone (K1), as USDA FoodData Central reports it.</summary>
    public static readonly Nutrient VitaminK = new("vitamin_k", NutrientUnit.Microgram);

    public static readonly Nutrient Thiamin = new("thiamin", NutrientUnit.Milligram);
    public static readonly Nutrient Riboflavin = new("riboflavin", NutrientUnit.Milligram);

    /// <summary>Preformed niacin, as USDA FoodData Central reports it; not niacin equivalents (which add tryptophan).</summary>
    public static readonly Nutrient Niacin = new("niacin", NutrientUnit.Milligram);

    public static readonly Nutrient VitaminB6 = new("vitamin_b6", NutrientUnit.Milligram);

    /// <summary>Folate as dietary folate equivalents (µg DFE), the measure the FDA Daily Value uses.</summary>
    public static readonly Nutrient Folate = new("folate", NutrientUnit.Microgram);

    public static readonly Nutrient VitaminB12 = new("vitamin_b12", NutrientUnit.Microgram);
    public static readonly Nutrient Biotin = new("biotin", NutrientUnit.Microgram);
    public static readonly Nutrient PantothenicAcid = new("pantothenic_acid", NutrientUnit.Milligram);
    public static readonly Nutrient Choline = new("choline", NutrientUnit.Milligram);
    public static readonly Nutrient Phosphorus = new("phosphorus", NutrientUnit.Milligram);
    public static readonly Nutrient Iodine = new("iodine", NutrientUnit.Microgram);
    public static readonly Nutrient Magnesium = new("magnesium", NutrientUnit.Milligram);
    public static readonly Nutrient Zinc = new("zinc", NutrientUnit.Milligram);
    public static readonly Nutrient Selenium = new("selenium", NutrientUnit.Microgram);
    public static readonly Nutrient Copper = new("copper", NutrientUnit.Milligram);
    public static readonly Nutrient Manganese = new("manganese", NutrientUnit.Milligram);
    public static readonly Nutrient Molybdenum = new("molybdenum", NutrientUnit.Microgram);

    /// <summary>Ethyl alcohol. It supplies energy (about 7 kcal/g) that the 4/4/9 macronutrient factors leave out.</summary>
    public static readonly Nutrient Alcohol = new("alcohol", NutrientUnit.Gram);

    public static readonly Nutrient Caffeine = new("caffeine", NutrientUnit.Milligram);

    /// <summary>
    /// Every nutrient, in display order: the Nutrition Facts label set, then the vitamins and minerals with an FDA Daily
    /// Value that USDA FoodData Central reports, then alcohol and caffeine. The migrations seed the same codes, units
    /// and order into <c>nutrients</c>.
    /// </summary>
    public static IReadOnlyList<Nutrient> All { get; } =
    [
        Energy, Fat, SaturatedFat, TransFat, Cholesterol, Sodium, Carbohydrate, Fiber, Sugars, AddedSugars,
        SugarAlcohols, Protein, VitaminD, Calcium, Iron, Potassium,
        VitaminA, VitaminC, VitaminE, VitaminK, Thiamin, Riboflavin, Niacin, VitaminB6, Folate, VitaminB12, Biotin,
        PantothenicAcid, Choline, Phosphorus, Iodine, Magnesium, Zinc, Selenium, Copper, Manganese, Molybdenum,
        Alcohol, Caffeine,
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
