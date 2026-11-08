using System.Globalization;
using System.Text.Json.Nodes;
using FluentValidation;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.Domain.Units;
using PowerSync.Common.Client;
using PowerSync.Common.DB;

namespace Health360.LocalDb;

/// <summary>One <c>recipes</c> row (H, synced on the household stream).</summary>
public sealed class RecipeRow
{
    public string Id { get; set; } = "";

    public string HouseholdId { get; set; } = "";

    public string Name { get; set; } = "";

    public double Servings { get; set; }

    /// <summary>As entered, in <see cref="CookedWeightUnit"/>; null when the dish wasn't weighed.</summary>
    public double? CookedWeight { get; set; }

    public string? CookedWeightUnit { get; set; }

    /// <summary>In grams; the server recomputes it, so a synced row carries the server's value.</summary>
    public double? CookedWeightG { get; set; }
}

/// <summary>One <c>recipe_ingredients</c> row with its food, which is null when the food isn't on this device.</summary>
public sealed class RecipeIngredientRow
{
    public string Id { get; set; } = "";

    public string RecipeId { get; set; } = "";

    public long Position { get; set; }

    public string FoodId { get; set; } = "";

    public double Quantity { get; set; }

    public string QuantityUnit { get; set; } = "";

    public string? PortionName { get; set; }

    public string Basis { get; set; } = "";

    /// <summary>In g or ml of <see cref="Basis"/>; the server recomputes it for a unit, as the app does.</summary>
    public double BaseAmount { get; set; }

    public string? FoodName { get; set; }

    public string? FoodBrand { get; set; }

    public string? FoodBasis { get; set; }

    public double? Energy100 { get; set; }

    public double? Protein100 { get; set; }

    public double? Carbohydrate100 { get; set; }

    public double? Fat100 { get; set; }

    public string? Nutrients100 { get; set; }

    public string? Portions { get; set; }

    /// <summary>The ingredient's food, or null when it hasn't synced to this device (or can't be read).</summary>
    public LoggableFood? Food() => FoodName is null
        ? null
        : new FoodRow
        {
            Id = FoodId,
            Name = FoodName,
            Brand = FoodBrand,
            Basis = FoodBasis ?? "",
            Energy100 = Energy100,
            Protein100 = Protein100,
            Carbohydrate100 = Carbohydrate100,
            Fat100 = Fat100,
            Nutrients100 = Nutrients100,
            Portions = Portions,
        }.ToFood();
}

/// <summary>A recipe with its ingredients in order.</summary>
public sealed record RecipeDetail(RecipeRow Recipe, IReadOnlyList<RecipeIngredientRow> Ingredients)
{
    /// <summary>Whether every ingredient's food is on this device, so the recipe can be totalled and edited here.</summary>
    public bool Complete => Ingredients.All(i => i.Food() is { } food && FoodBases.Code(food.Basis) == i.Basis);

    /// <summary>
    /// The dish's nutrition from the stored ingredient amounts (<see cref="RecipeNutrition.Compute"/>), or null when an
    /// ingredient's food isn't on this device.
    /// </summary>
    public RecipeNutritionResult? Nutrition()
    {
        if (!Complete || !double.IsFinite(Recipe.Servings) || Recipe.Servings <= 0)
        {
            return null;
        }

        var ingredients = Ingredients.Select(i =>
        {
            var food = i.Food()!;
            return new RecipeIngredient(food.Per100, food.Basis, i.BaseAmount);
        }).ToList();
        var cooked = Recipe.CookedWeightG is { } g && double.IsFinite(g) && g > 0 ? g : (double?)null;
        return RecipeNutrition.Compute(ingredients, Recipe.Servings, cooked);
    }
}

/// <summary>
/// The household's recipes (<c>recipes</c> and <c>recipe_ingredients</c>, H) in the local database. An ingredient is
/// always a food of the recipe's own household, so every member can total the recipe offline. Writes upload when
/// online and sync to the other members.
/// </summary>
public static class Recipes
{
    private const string RecipeColumns =
        "r.id AS Id, r.household_id AS HouseholdId, r.name AS Name, r.servings AS Servings, r.cooked_weight AS CookedWeight, " +
        "r.cooked_weight_unit AS CookedWeightUnit, r.cooked_weight_g AS CookedWeightG";

    private const string IngredientsSelect =
        "SELECT i.id AS Id, i.recipe_id AS RecipeId, i.position AS Position, i.food_id AS FoodId, i.quantity AS Quantity, i.quantity_unit AS QuantityUnit, " +
        "i.portion_name AS PortionName, i.basis AS Basis, i.base_amount AS BaseAmount, f.name AS FoodName, f.brand AS FoodBrand, " +
        "f.basis AS FoodBasis, f.energy_100 AS Energy100, f.protein_100 AS Protein100, f.carbohydrate_100 AS Carbohydrate100, " +
        "f.fat_100 AS Fat100, f.nutrients_100 AS Nutrients100, f.portions AS Portions " +
        "FROM recipe_ingredients i LEFT JOIN foods f ON f.id = i.food_id AND f.household_id = i.household_id ";

    private const string IngredientsSql = IngredientsSelect + "WHERE i.recipe_id = ? ORDER BY i.position, i.id";

    /// <summary>The household recipes whose name contains <paramref name="query"/>, by name; all of them when it is empty.</summary>
    public static Task<RecipeRow[]> SearchAsync(IPowerSyncDatabase db, string? query, int limit)
    {
        ArgumentNullException.ThrowIfNull(db);
        var pattern = "%" + EscapeLike(query?.Trim() ?? "") + "%";
        return db.GetAll<RecipeRow>(
            $"SELECT {RecipeColumns} FROM recipes r WHERE r.name LIKE ? ESCAPE '\\' ORDER BY r.name COLLATE NOCASE, r.id LIMIT ?",
            [pattern, limit]);
    }

    /// <summary>
    /// Every household recipe with its ingredients, by name, in two reads however many recipes there are.
    /// </summary>
    public static async Task<IReadOnlyList<RecipeDetail>> ListAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        var recipes = await db.GetAll<RecipeRow>($"SELECT {RecipeColumns} FROM recipes r ORDER BY r.name COLLATE NOCASE, r.id", []).ConfigureAwait(false);
        var ingredients = await db.GetAll<RecipeIngredientRow>(IngredientsSelect + "ORDER BY i.recipe_id, i.position, i.id", []).ConfigureAwait(false);
        var byRecipe = ingredients.ToLookup(i => i.RecipeId, StringComparer.Ordinal);
        return [.. recipes.Select(r => new RecipeDetail(r, [.. byRecipe[r.Id]]))];
    }

    public static async Task<RecipeDetail?> GetAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        var recipe = await db.GetOptional<RecipeRow>($"SELECT {RecipeColumns} FROM recipes r WHERE r.id = ?", [id]).ConfigureAwait(false);
        if (recipe is null)
        {
            return null;
        }

        var ingredients = await db.GetAll<RecipeIngredientRow>(IngredientsSql, [id]).ConfigureAwait(false);
        return new RecipeDetail(recipe, ingredients);
    }

    /// <summary>Adds a recipe to the household, with its ingredients in the order given.</summary>
    public static async Task<string> CreateAsync(IPowerSyncDatabase db, string householdId, RecipeInput input, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(householdId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(clock);
        new RecipeInputValidator().ValidateAndThrow(input);
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM households WHERE id = ?", [householdId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("That household is no longer on this device.");
            await tx.Execute(
                "INSERT INTO recipes (id, household_id, name, servings, cooked_weight, cooked_weight_unit, cooked_weight_g, created_at, updated_at) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                [id, householdId, input.Name.Trim(), input.Servings, input.CookedWeight, input.CookedWeightUnit, CookedGrams(input), now, now])
                .ConfigureAwait(false);
            for (var position = 0; position < input.Ingredients.Count; position++)
            {
                await InsertIngredientAsync(tx, householdId, id, position, input.Ingredients[position], clock).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Saves an edited recipe. An ingredient given with its existing id and unchanged amount keeps its row (only its
    /// position is written if it moved), so a later change to its food's portions can't make the upload fail; a
    /// changed one is updated, a new one inserted, and one left out deleted. Refused while an ingredient's food isn't
    /// on this device, since the editor couldn't have shown it.
    /// </summary>
    public static async Task UpdateAsync(IPowerSyncDatabase db, string id, RecipeInput input, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(clock);
        new RecipeInputValidator().ValidateAndThrow(input);
        await db.WriteTransaction(async tx =>
        {
            var recipe = await tx.GetOptional<RecipeRow>($"SELECT {RecipeColumns} FROM recipes r WHERE r.id = ?", [id]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The recipe is no longer on this device.");
            var existing = (await tx.GetAll<RecipeIngredientRow>(IngredientsSql, [id]).ConfigureAwait(false)).ToDictionary(i => i.Id, StringComparer.Ordinal);
            if (!new RecipeDetail(recipe, [.. existing.Values]).Complete)
            {
                throw new InvalidOperationException("Some of this recipe's ingredients haven't synced to this device yet.");
            }

            var now = Timestamps.UtcNow(clock);
            await tx.Execute(
                "UPDATE recipes SET name = ?, servings = ?, cooked_weight = ?, cooked_weight_unit = ?, cooked_weight_g = ?, updated_at = ? WHERE id = ?",
                [input.Name.Trim(), input.Servings, input.CookedWeight, input.CookedWeightUnit, CookedGrams(input), now, id]).ConfigureAwait(false);
            var kept = new HashSet<string>(StringComparer.Ordinal);
            for (var position = 0; position < input.Ingredients.Count; position++)
            {
                var ingredient = input.Ingredients[position];
                if (ingredient.Id is { } ingredientId && existing.TryGetValue(ingredientId, out var row) && kept.Add(ingredientId))
                {
                    await UpdateIngredientAsync(tx, recipe.HouseholdId, row, position, ingredient, now).ConfigureAwait(false);
                }
                else
                {
                    await InsertIngredientAsync(tx, recipe.HouseholdId, id, position, ingredient, clock).ConfigureAwait(false);
                }
            }

            foreach (var removed in existing.Keys.Where(k => !kept.Contains(k)))
            {
                await tx.Execute("DELETE FROM recipe_ingredients WHERE id = ?", [removed]).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>Deletes a recipe and its ingredients. Entries logged from it keep their own snapshot.</summary>
    public static Task DeleteAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.WriteTransaction(async tx =>
        {
            await tx.Execute("DELETE FROM recipe_ingredients WHERE recipe_id = ?", [id]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM recipes WHERE id = ?", [id]).ConfigureAwait(false);
        });
    }

    internal static Task<RecipeIngredientRow[]> IngredientsAsync(ITransaction tx, string recipeId) =>
        tx.GetAll<RecipeIngredientRow>(IngredientsSql, [recipeId]);

    internal static Task<RecipeRow?> RecipeAsync(ITransaction tx, string recipeId) =>
        tx.GetOptional<RecipeRow>($"SELECT {RecipeColumns} FROM recipes r WHERE r.id = ?", [recipeId]);

    private static async Task InsertIngredientAsync(
        ITransaction tx, string householdId, string recipeId, int position, RecipeIngredientInput ingredient, TimeProvider clock)
    {
        var (basis, baseAmount) = await ResolveAsync(tx, householdId, ingredient).ConfigureAwait(false);
        var now = Timestamps.UtcNow(clock);
        await tx.Execute(
            "INSERT INTO recipe_ingredients (id, household_id, recipe_id, position, food_id, quantity, quantity_unit, portion_name, basis, " +
            "base_amount, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            [ExerciseLibrary.NewId(clock), householdId, recipeId, position, ingredient.FoodId, ingredient.Quantity, ingredient.Unit ?? "portion",
                ingredient.Unit is null ? ingredient.PortionName!.Trim() : null, basis, baseAmount, now, now]).ConfigureAwait(false);
    }

    private static async Task UpdateIngredientAsync(
        ITransaction tx, string householdId, RecipeIngredientRow row, int position, RecipeIngredientInput ingredient, string now)
    {
        var unit = ingredient.Unit ?? "portion";
        var portion = ingredient.Unit is null ? ingredient.PortionName!.Trim() : null;
        if (row.FoodId == ingredient.FoodId && row.Quantity.Equals(ingredient.Quantity) && row.QuantityUnit == unit && row.PortionName == portion)
        {
            if (row.Position != position)
            {
                await tx.Execute("UPDATE recipe_ingredients SET position = ?, updated_at = ? WHERE id = ?", [position, now, row.Id]).ConfigureAwait(false);
            }

            return;
        }

        var (basis, baseAmount) = await ResolveAsync(tx, householdId, ingredient).ConfigureAwait(false);
        await tx.Execute(
            "UPDATE recipe_ingredients SET position = ?, food_id = ?, quantity = ?, quantity_unit = ?, portion_name = ?, basis = ?, base_amount = ?, " +
            "updated_at = ? WHERE id = ?",
            [position, ingredient.FoodId, ingredient.Quantity, unit, portion, basis, baseAmount, now, row.Id]).ConfigureAwait(false);
    }

    /// <summary>The ingredient's basis and amount in it, from its food, which must be one of the recipe's household.</summary>
    private static async Task<(string Basis, double BaseAmount)> ResolveAsync(ITransaction tx, string householdId, RecipeIngredientInput ingredient)
    {
        var row = await tx.GetOptional<FoodRow>(
            "SELECT id AS Id, household_id AS HouseholdId, source AS Source, name AS Name, brand AS Brand, basis AS Basis, " +
            "energy_100 AS Energy100, protein_100 AS Protein100, carbohydrate_100 AS Carbohydrate100, fat_100 AS Fat100, " +
            "nutrients_100 AS Nutrients100, portions AS Portions FROM foods WHERE id = ? AND household_id = ?",
            [ingredient.FoodId, householdId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("An ingredient's food isn't one of this household's foods on this device.");
        var food = row.ToFood() ?? throw new InvalidOperationException($"{row.Name} can't be read on this device.");
        var (baseAmount, _) = FoodLog.Compute(food, ingredient.Quantity, ingredient.Unit, ingredient.PortionName?.Trim());
        return (FoodBases.Code(food.Basis), baseAmount);
    }

    private static double? CookedGrams(RecipeInput input) =>
        input.CookedWeight is { } w && input.CookedWeightUnit is { } unit ? UnitCodes.Convert(w, unit, "g") : null;

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Nutrient codes in <paramref name="amounts"/> that are only lower bounds, as stored: a JSON array.</summary>
    internal static string IncompleteJson(NutrientAmounts amounts, IReadOnlySet<string> incomplete) =>
        new JsonArray([.. incomplete.Where(amounts.Values.ContainsKey).Order(StringComparer.Ordinal).Select(code => (JsonNode)code)]).ToJsonString();

    internal static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
