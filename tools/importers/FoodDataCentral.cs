using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Health360.Domain.Nutrition;

namespace Health360.Importers;

/// <summary>A named measure of a catalogue food, in grams (USDA values are per 100 g).</summary>
public sealed record ImportedPortion(string Name, double Grams);

/// <summary>
/// One catalogue food, mapped to the columns of <c>public.foods</c>: values per 100 g, null or absent when the dataset
/// doesn't report them (unknown, not 0).
/// </summary>
public sealed record ImportedFood(
    string ExternalId,
    string Name,
    double? Energy100,
    double? Protein100,
    double? Carbohydrate100,
    double? Fat100,
    IReadOnlyDictionary<string, double> Nutrients100,
    IReadOnlyList<ImportedPortion> Portions);

/// <summary>The foods to import from one FoodData Central file, the entries left out, and portions dropped.</summary>
public sealed record ParsedFoods(
    FoodDataCentral.Dataset Dataset,
    IReadOnlyList<ImportedFood> Foods,
    IReadOnlyList<SkippedEntry> Skipped,
    int PortionsDropped);

/// <summary>
/// USDA FoodData Central (fdc.nal.usda.gov, public domain): the JSON downloads of Foundation Foods
/// (<c>FoundationFoods</c>), SR Legacy (<c>SRLegacyFoods</c>) and FNDDS survey foods (<c>SurveyFoods</c>). Each food has
/// an <c>fdcId</c>, a <c>description</c>, <c>foodNutrients</c> per 100 g and <c>foodPortions</c> with gram weights.
/// Branded foods are not imported: they are looked up through the FoodData Central API (docs/FEATURES.md).
/// </summary>
/// <remarks>
/// Nutrients are matched by FoodData Central's nutrient id, and the unit it reports must be the nutrient's own
/// (docs/calculations.md, USDA FoodData Central import). Energy is the dataset's "Energy" in kcal (id 1008); Foundation
/// Foods often give only Atwater energies, so the specific-factor value (2048) comes next and the general-factor one
/// (2047) last. Total sugars are id 2000, or 1063 in Foundation Foods. Folate is folate DFE (1190) only: Foundation
/// Foods report total folate, which is a different measure, so their folate stays unknown.
/// </remarks>
public static partial class FoodDataCentral
{
    /// <summary>A FoodData Central download this importer reads.</summary>
    public sealed record Dataset(string RootProperty, string Code, string Name);

    public static readonly Dataset Foundation = new("FoundationFoods", "usda-fdc-foundation", "Foundation Foods");
    public static readonly Dataset SrLegacy = new("SRLegacyFoods", "usda-fdc-sr-legacy", "SR Legacy");
    public static readonly Dataset Survey = new("SurveyFoods", "usda-fdc-survey", "FNDDS survey foods");

    public static IReadOnlyList<Dataset> Datasets { get; } = [Foundation, SrLegacy, Survey];

    /// <summary>The <c>foods.source</c> of every row this importer writes.</summary>
    public const string Source = "usda_fdc";

    public const string Attribution =
        "U.S. Department of Agriculture, Agricultural Research Service. FoodData Central (fdc.nal.usda.gov). Public domain (CC0 1.0)";

    private const int MaxNameLength = 200;
    private const int MaxPortionNameLength = 80;
    private const int MaxPortions = 50;
    private const double MaxPortionGrams = 100000;

    /// <summary>Energy in kcal, by FoodData Central nutrient id, most preferred first.</summary>
    private static readonly int[] EnergyIds = [1008, 2048, 2047];

    /// <summary>Every other nutrient by FoodData Central nutrient id, most preferred first.</summary>
    private static readonly (Nutrient Nutrient, int[] Ids)[] NutrientIds =
    [
        (Nutrient.Protein, [1003]),
        (Nutrient.Fat, [1004]),
        (Nutrient.Carbohydrate, [1005]), // by difference: total carbohydrate as US labels declare it
        (Nutrient.SaturatedFat, [1258]),
        (Nutrient.TransFat, [1257]),
        (Nutrient.Cholesterol, [1253]),
        (Nutrient.Sodium, [1093]),
        (Nutrient.Fiber, [1079]),
        (Nutrient.Sugars, [2000, 1063]),
        (Nutrient.VitaminD, [1114]), // D2 + D3 in µg, not the IU entry
        (Nutrient.Calcium, [1087]),
        (Nutrient.Iron, [1089]),
        (Nutrient.Potassium, [1092]),
        (Nutrient.VitaminA, [1106]), // RAE, not IU
        (Nutrient.VitaminC, [1162]),
        (Nutrient.VitaminE, [1109]), // alpha-tocopherol
        (Nutrient.VitaminK, [1185]), // phylloquinone
        (Nutrient.Thiamin, [1165]),
        (Nutrient.Riboflavin, [1166]),
        (Nutrient.Niacin, [1167]),
        (Nutrient.VitaminB6, [1175]),
        (Nutrient.Folate, [1190]), // DFE
        (Nutrient.VitaminB12, [1178]),
        (Nutrient.Biotin, [1176]),
        (Nutrient.PantothenicAcid, [1170]),
        (Nutrient.Choline, [1180]),
        (Nutrient.Phosphorus, [1091]),
        (Nutrient.Iodine, [1100]),
        (Nutrient.Magnesium, [1090]),
        (Nutrient.Zinc, [1095]),
        (Nutrient.Selenium, [1103]),
        (Nutrient.Copper, [1098]),
        (Nutrient.Manganese, [1101]),
        (Nutrient.Molybdenum, [1102]),
        (Nutrient.Alcohol, [1018]),
        (Nutrient.Caffeine, [1057]),
    ];

    private static readonly Dictionary<int, Nutrient> UnitById = BuildUnits();

    /// <summary>
    /// Reads one download. A food is skipped, and reported, when the table would reject it or its values can't be
    /// trusted: no usable id or name, a repeated id, no energy, a nutrient in an unexpected unit, listed twice with
    /// different amounts or negative, or more than 100 g of a macronutrient per 100 g. A portion without a positive
    /// gram weight, a name that is too long or repeated, FNDDS's coding defaults ("Quantity not specified", "Guideline
    /// amount per …"), or past the 50th is dropped
    /// and counted.
    /// </summary>
    /// <exception cref="JsonException">The file isn't one of the FoodData Central downloads above.</exception>
    public static ParsedFoods Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Expected a FoodData Central JSON download (an object with one array of foods).");
        }

        if (root.TryGetProperty("BrandedFoods", out _))
        {
            throw new JsonException("Branded foods are looked up through the FoodData Central API, not imported.");
        }

        var dataset = Datasets.FirstOrDefault(d => root.TryGetProperty(d.RootProperty, out _))
            ?? throw new JsonException(
                $"Expected one of {string.Join(", ", Datasets.Select(d => d.RootProperty))} at the top of the file.");
        var array = root.GetProperty(dataset.RootProperty);
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException($"Expected {dataset.RootProperty} to be an array of foods.");
        }

        var foods = new List<ImportedFood>();
        var skipped = new List<SkippedEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var portionsDropped = 0;
        var index = 0;
        foreach (var entry in array.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.Object ? FdcId(entry) : null;
            var food = Map(entry, id, out var reason, ref portionsDropped);
            if (food is null)
            {
                skipped.Add(new SkippedEntry(index, id, reason!));
            }
            else if (!seen.Add(food.ExternalId))
            {
                skipped.Add(new SkippedEntry(index, id, "repeated id"));
            }
            else
            {
                foods.Add(food);
            }

            index++;
        }

        return new ParsedFoods(dataset, foods, skipped, portionsDropped);
    }

    private static ImportedFood? Map(JsonElement entry, string? id, out string? reason, ref int portionsDropped)
    {
        reason = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            reason = "not an object";
            return null;
        }

        if (id is null)
        {
            reason = "fdcId missing or not a positive integer";
            return null;
        }

        var name = Text(entry, "description") is { } description ? Spaces().Replace(description.Trim(), " ") : null;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
        {
            reason = "description missing or too long";
            return null;
        }

        var amounts = ReadNutrients(entry, out reason);
        if (amounts is null)
        {
            return null;
        }

        var energy = First(amounts, EnergyIds);
        if (energy is null)
        {
            reason = "energy missing";
            return null;
        }

        double? protein = null, carbohydrate = null, fat = null;
        var others = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (nutrient, ids) in NutrientIds)
        {
            var amount = First(amounts, ids);
            if (amount is null)
            {
                continue;
            }

            if (nutrient == Nutrient.Protein)
            {
                protein = amount;
            }
            else if (nutrient == Nutrient.Carbohydrate)
            {
                carbohydrate = amount;
            }
            else if (nutrient == Nutrient.Fat)
            {
                fat = amount;
            }
            else
            {
                others[nutrient.Code] = amount.Value;
            }
        }

        // foods_mass_macros: 100 g of food can't hold more than 100 g of one macronutrient.
        if (protein > 100 || carbohydrate > 100 || fat > 100)
        {
            reason = "macronutrient over 100 g per 100 g";
            return null;
        }

        var portions = ReadPortions(entry, ref portionsDropped);
        return new ImportedFood(id, name, energy, protein, carbohydrate, fat, others, portions);
    }

    /// <summary>
    /// The amounts of the nutrients this importer maps, by FoodData Central nutrient id. An entry without an amount is
    /// unknown and left out. Null, with the reason, when an amount can't be trusted.
    /// </summary>
    private static Dictionary<int, double>? ReadNutrients(JsonElement entry, out string? reason)
    {
        reason = null;
        var amounts = new Dictionary<int, double>();
        if (!entry.TryGetProperty("foodNutrients", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            reason = "foodNutrients missing";
            return null;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("nutrient", out var nutrient) || nutrient.ValueKind != JsonValueKind.Object
                || !nutrient.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt32(out var nutrientId)
                || !UnitById.TryGetValue(nutrientId, out var mapped))
            {
                continue;
            }

            if (!item.TryGetProperty("amount", out var amountElement) || amountElement.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!SameUnit(Text(nutrient, "unitName"), mapped.Unit))
            {
                reason = $"nutrient {nutrientId} not in {NutrientUnits.Code(mapped.Unit)}";
                return null;
            }

            if (amountElement.ValueKind != JsonValueKind.Number
                || !amountElement.TryGetDouble(out var amount) || !double.IsFinite(amount) || amount < 0)
            {
                reason = $"nutrient {nutrientId} amount not a non-negative number";
                return null;
            }

            if (amounts.TryGetValue(nutrientId, out var earlier) && earlier != amount)
            {
                reason = $"nutrient {nutrientId} listed twice";
                return null;
            }

            amounts[nutrientId] = amount;
        }

        return amounts;
    }

    private static List<ImportedPortion> ReadPortions(JsonElement entry, ref int dropped)
    {
        var portions = new List<ImportedPortion>();
        if (!entry.TryGetProperty("foodPortions", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return portions;
        }

        var ordered = list.EnumerateArray()
            .Select((item, position) => (item, position))
            .OrderBy(p => p.item.ValueKind == JsonValueKind.Object
                && p.item.TryGetProperty("sequenceNumber", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var n)
                ? n : int.MaxValue)
            .ThenBy(p => p.position);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (item, _) in ordered)
        {
            var portion = item.ValueKind == JsonValueKind.Object ? MapPortion(item) : null;
            if (portion is null || portions.Count == MaxPortions || !names.Add(portion.Name))
            {
                dropped++;
                continue;
            }

            portions.Add(portion);
        }

        return portions;
    }

    /// <summary>
    /// A portion's name from how the dataset describes it: "1 cup", "1 serving, 1/2 cup" (Foundation Foods: a count,
    /// a unit and a modifier), "1 cup, chopped" (SR Legacy: a count and a modifier), "1 meatball" (FNDDS: a
    /// description).
    /// </summary>
    private static ImportedPortion? MapPortion(JsonElement item)
    {
        if (!item.TryGetProperty("gramWeight", out var weight) || weight.ValueKind != JsonValueKind.Number
            || !weight.TryGetDouble(out var grams) || !double.IsFinite(grams) || grams <= 0 || grams > MaxPortionGrams)
        {
            return null;
        }

        var count = Number(item, "amount") ?? Number(item, "value");
        var unit = item.TryGetProperty("measureUnit", out var measure) && measure.ValueKind == JsonValueKind.Object
            ? Clean(Text(measure, "name"))
            : null;
        if (string.Equals(unit, "undetermined", StringComparison.OrdinalIgnoreCase))
        {
            unit = null;
        }

        var description = Clean(Text(item, "portionDescription"));
        var modifier = Clean(Text(item, "modifier"));
        string? name;
        if (count is not { } n || n <= 0 || !double.IsFinite(n))
        {
            // FNDDS: the description says it all; its modifier is a code.
            name = description;
        }
        else
        {
            var extra = modifier ?? description;
            var number = n.ToString("0.###", CultureInfo.InvariantCulture);
            name = unit is null
                ? extra is null ? null : $"{number} {extra}"
                : extra is null ? $"{number} {unit}" : $"{number} {unit}, {extra}";
        }

        // FNDDS's survey-coding defaults, not portions anyone would choose: "Quantity not specified" and amounts such as
        // "Guideline amount per fl oz of beverage" (the milk added to a cup of coffee).
        if (name is null || name.Length > MaxPortionNameLength
            || string.Equals(name, "Quantity not specified", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Guideline amount", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ImportedPortion(name, grams);
    }

    private static double? First(Dictionary<int, double> amounts, int[] ids)
    {
        foreach (var id in ids)
        {
            if (amounts.TryGetValue(id, out var amount))
            {
                return amount;
            }
        }

        return null;
    }

    private static bool SameUnit(string? fdcUnit, NutrientUnit unit) =>
        (fdcUnit?.Trim().ToLowerInvariant(), unit) switch
        {
            ("kcal", NutrientUnit.Kilocalorie) => true,
            ("g", NutrientUnit.Gram) => true,
            ("mg", NutrientUnit.Milligram) => true,
            ("µg" or "μg" or "ug" or "mcg", NutrientUnit.Microgram) => true,
            _ => false,
        };

    private static Dictionary<int, Nutrient> BuildUnits()
    {
        var units = new Dictionary<int, Nutrient>();
        foreach (var id in EnergyIds)
        {
            units.Add(id, Nutrient.Energy);
        }

        foreach (var (nutrient, ids) in NutrientIds)
        {
            foreach (var id in ids)
            {
                units.Add(id, nutrient);
            }
        }

        return units;
    }

    private static string? FdcId(JsonElement entry) =>
        entry.TryGetProperty("fdcId", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value > 0
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static double? Number(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;

    private static string? Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : Spaces().Replace(text.Trim(), " ");

    private static string? Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Normalize(NormalizationForm.FormC)
            : null;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
