using FluentValidation;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

public sealed class RecipesTests
{
    private const string Profile = "10000000-0000-7000-8000-000000000001";
    private const string Household = "20000000-0000-7000-8000-000000000001";
    private const string OtherHousehold = "20000000-0000-7000-8000-000000000002";
    private static readonly DateOnly Day = new(2026, 11, 8);

    private readonly StepClock clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Pantry(string Oats, string Banana, string PeanutButter, string Milk);

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home'), (?, 'Cabin')", [Household, OtherHousehold]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('m1', ?, ?), ('m2', ?, ?)",
                [Household, Profile, OtherHousehold, Profile]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    /// <summary>The golden fixture of docs/calculations.md (Recipes): oats, banana and peanut butter, which lists no fiber.</summary>
    private async Task<Pantry> StockAsync(TestDatabase t)
    {
        var oats = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Oats", null, FoodBasis.Mass, 100, 379, 13.2, 67.7, 6.5, [new("1 cup", 81)]), clock);
        await SetFiberAsync(t, oats, 10.1);
        var banana = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Banana", null, FoodBasis.Mass, 100, 89, 1.09, 22.84, 0.33, [new("1 medium", 118)]), clock);
        await SetFiberAsync(t, banana, 2.6);
        var peanutButter = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Peanut butter", null, FoodBasis.Mass, 100, 588, 25.1, 19.6, 50.4, []), clock);
        var milk = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Milk", null, FoodBasis.Volume, 100, 64, 3.4, 4.8, 3.6, []), clock);
        return new Pantry(oats, banana, peanutButter, milk);
    }

    // The New food form enters macronutrients only; fiber arrives with other foods (a USDA copy, a label scan).
    private static async Task SetFiberAsync(TestDatabase t, string foodId, double fiber) =>
        await t.Db.Execute("UPDATE foods SET nutrients_100 = json_object('fiber', ?) WHERE id = ?", [fiber, foodId]);

    private static RecipeInput Porridge(Pantry p, double? cooked = null) => new("Porridge", 2, cooked, cooked is null ? null : "g",
        [new(null, p.Oats, 80, "g", null), new(null, p.Banana, 120, "g", null), new(null, p.PeanutButter, 32, "g", null)]);

    [Fact]
    public async Task A_saved_recipe_totals_as_the_hand_calculation_does_and_survives_reopening()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var id = await Recipes.CreateAsync(t.Db, Household, Porridge(p), clock);

        await t.ReopenAsync();
        var recipe = (await Recipes.GetAsync(t.Db, id))!;
        var nutrition = recipe.Nutrition()!;

        Assert.Equal(["Oats", "Banana", "Peanut butter"], recipe.Ingredients.Select(i => i.FoodName));
        // 379 × 80/100 + 89 × 120/100 + 588 × 32/100 = 598.16 kcal; 299.08 per serving; 232 g raw; 598.16 × 100 / 232.
        Assert.Equal(598.16, nutrition.Total.Amounts[Nutrient.Energy]!.Value, 9);
        Assert.Equal(299.08, nutrition.PerServing.Amounts[Nutrient.Energy]!.Value, 9);
        Assert.Equal(9.95, nutrition.PerServing.Amounts[Nutrient.Protein]!.Value, 9);
        Assert.Equal(11.2, nutrition.Total.Amounts[Nutrient.Fiber]!.Value, 9);
        Assert.Equal(["fiber"], nutrition.Total.Incomplete);
        Assert.Equal(232, nutrition.DishWeightG);
        Assert.Equal(598.16 * 100 / 232, nutrition.Per100g!.Amounts[Nutrient.Energy]!.Value, 9);

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        var upload = Assert.Single(writer.Writes, w => w.Table == "recipes");
        Assert.Equal(("PUT", id, Household, 2.0), (upload.Op, upload.Id, upload.Data!["household_id"]!.GetValue<string>(), upload.Data["servings"]!.GetValue<double>()));
        var ingredients = writer.Writes.Where(w => w.Table == "recipe_ingredients").ToList();
        Assert.Equal([80.0, 120, 32], ingredients.Select(w => w.Data!["base_amount"]!.GetValue<double>()));
        Assert.All(ingredients, w => Assert.Equal((id, Household, "g"),
            (w.Data!["recipe_id"]!.GetValue<string>(), w.Data["household_id"]!.GetValue<string>(), w.Data["basis"]!.GetValue<string>())));
    }

    [Fact]
    public async Task Ingredients_take_their_food_units_or_portions_and_a_weighed_dish_gives_its_own_per_100_g()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var id = await Recipes.CreateAsync(t.Db, Household, new RecipeInput("Banana oats", 1, 1, "lb",
            [new(null, p.Oats, 0.5, null, "1 cup"), new(null, p.Banana, 1, "oz", null), new(null, p.Milk, 1, "cup", null)]), clock);

        var recipe = (await Recipes.GetAsync(t.Db, id))!;
        // Half of an 81 g cup; an ounce is exactly 28.349523125 g; a US cup is 8 × 29.5735295625 ml.
        Assert.Equal([40.5, 28.349523125, 8 * 29.5735295625], recipe.Ingredients.Select(i => i.BaseAmount));
        Assert.Equal(["portion", "oz", "cup"], recipe.Ingredients.Select(i => i.QuantityUnit));
        Assert.Equal(453.59237, recipe.Recipe.CookedWeightG);
        // Milk is measured by volume, but the dish was weighed: per 100 g uses the cooked weight.
        Assert.Equal(453.59237, recipe.Nutrition()!.DishWeightG);
    }

    [Fact]
    public async Task A_recipe_the_database_would_refuse_is_not_written()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var elsewhere = await FoodLibrary.AddAsync(t.Db, OtherHousehold, new FoodInput("Cabin bread", null, FoodBasis.Mass, 100, 250, 9, 49, 3, []), clock);

        await Assert.ThrowsAsync<ValidationException>(() => Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Ingredients = [] }, clock));
        await Assert.ThrowsAsync<ValidationException>(() => Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Servings = 0 }, clock));
        await Assert.ThrowsAsync<ValidationException>(() => Recipes.CreateAsync(t.Db, Household, Porridge(p) with { CookedWeight = 500 }, clock));
        // Another household's food, a unit of the wrong dimension, a portion the food doesn't have.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Ingredients = [new(null, elsewhere, 100, "g", null)] }, clock));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Ingredients = [new(null, p.Oats, 1, "cup", null)] }, clock));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Ingredients = [new(null, p.Oats, 1, null, "1 slice")] }, clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recipes.CreateAsync(t.Db, "gone", Porridge(p), clock));

        Assert.Empty(await Recipes.SearchAsync(t.Db, "", 10));
    }

    [Fact]
    public async Task Editing_keeps_unchanged_ingredient_rows_updates_changed_ones_and_deletes_removed_ones()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var id = await Recipes.CreateAsync(t.Db, Household, Porridge(p), clock);
        var before = (await Recipes.GetAsync(t.Db, id))!.Ingredients;
        await t.Db.Execute("DELETE FROM ps_crud");

        // Banana moves to the front unchanged, oats get more, peanut butter goes, milk comes in.
        await Recipes.UpdateAsync(t.Db, id, new RecipeInput("Porridge", 3, null, null,
        [
            new(before[1].Id, p.Banana, 120, "g", null),
            new(before[0].Id, p.Oats, 100, "g", null),
            new(null, p.Milk, 200, "ml", null),
        ]), clock);

        var after = (await Recipes.GetAsync(t.Db, id))!;
        Assert.Equal(["Banana", "Oats", "Milk"], after.Ingredients.Select(i => i.FoodName));
        Assert.Equal((before[1].Id, before[0].Id), (after.Ingredients[0].Id, after.Ingredients[1].Id));
        Assert.Equal([120.0, 100, 200], after.Ingredients.Select(i => i.BaseAmount));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        var ingredientWrites = writer.Writes.Where(w => w.Table == "recipe_ingredients").ToList();
        // The banana only moved: its quantity is not sent again, so the server keeps the amount it stored.
        var banana = Assert.Single(ingredientWrites, w => w.Id == before[1].Id);
        Assert.Equal("PATCH", banana.Op);
        Assert.False(banana.Data!.ContainsKey("quantity"));
        Assert.Equal(("PATCH", 100.0), (ingredientWrites.Single(w => w.Id == before[0].Id).Op, ingredientWrites.Single(w => w.Id == before[0].Id).Data!["quantity"]!.GetValue<double>()));
        Assert.Equal("DELETE", ingredientWrites.Single(w => w.Id == before[2].Id).Op);
        Assert.Equal("PUT", ingredientWrites.Single(w => w.Id == after.Ingredients[2].Id).Op);
        Assert.Equal(3, after.Recipe.Servings);
    }

    [Fact]
    public async Task A_recipe_whose_food_has_not_synced_can_be_seen_but_not_edited_or_logged()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var id = await Recipes.CreateAsync(t.Db, Household, Porridge(p), clock);
        await t.Db.Execute("UPDATE recipe_ingredients SET food_id = 'not-here' WHERE food_id = ?", [p.PeanutButter]);

        var recipe = (await Recipes.GetAsync(t.Db, id))!;
        Assert.False(recipe.Complete);
        Assert.Null(recipe.Nutrition());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recipes.UpdateAsync(t.Db, id, Porridge(p), clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FoodLog.LogRecipeAsync(t.Db, Profile, id, "lunch", Day, 1, "serving", clock));
    }

    [Fact]
    public async Task Logging_a_recipe_keeps_a_snapshot_that_later_edits_and_deletion_leave_alone()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        var id = await Recipes.CreateAsync(t.Db, Household, Porridge(p, cooked: 400), clock);

        var servings = await FoodLog.LogRecipeAsync(t.Db, Profile, id, "breakfast", Day, 1.5, "serving", clock);
        var weighed = await FoodLog.LogRecipeAsync(t.Db, Profile, id, "snack", Day, 100, "g", clock);
        await Recipes.UpdateAsync(t.Db, id, Porridge(p) with { Servings = 4 }, clock);
        await Recipes.DeleteAsync(t.Db, id);

        var entries = await FoodLog.DayAsync(t.Db, Profile, Day);
        Assert.Equal([servings, weighed], entries.Select(e => e.Id));
        // 299.08 × 1.5; 598.16 × 100 / 400 × 100 / 100. Fiber stays a lower bound: peanut butter didn't list it.
        Assert.Equal(("recipe", "Porridge", "serving", 448.62), (entries[0].EntryMethod, entries[0].Name, entries[0].QuantityUnit, Math.Round(entries[0].Energy!.Value, 9)));
        Assert.Equal(["fiber"], entries[0].Incomplete());
        Assert.Equal(("g", 149.54), (entries[1].QuantityUnit, Math.Round(entries[1].Energy!.Value, 9)));
        Assert.Equal(["fiber"], FoodLog.Total(entries).Incomplete);
        Assert.Null(await Recipes.GetAsync(t.Db, id));
        Assert.Equal(0, await t.Db.Get<long>("SELECT count(*) FROM recipe_ingredients", []));

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        var logged = writer.Writes.First(w => w.Table == "food_log_entries" && w.Id == servings);
        Assert.Equal((id, "serving"), (logged.Data!["recipe_id"]!.GetValue<string>(), logged.Data["quantity_unit"]!.GetValue<string>()));
        Assert.False(logged.Data.ContainsKey("basis"));
        Assert.Equal("fiber", logged.Data["incomplete_nutrients"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task A_dish_that_was_not_weighed_is_logged_in_servings_only()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        // Milk is measured by volume, so the raw weight isn't known either.
        var id = await Recipes.CreateAsync(t.Db, Household, new RecipeInput("Latte", 1, null, null, [new(null, p.Milk, 250, "ml", null)]), clock);

        Assert.Null((await Recipes.GetAsync(t.Db, id))!.Nutrition()!.Per100g);
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogRecipeAsync(t.Db, Profile, id, "snack", Day, 200, "g", clock));
        await FoodLog.LogRecipeAsync(t.Db, Profile, id, "snack", Day, 1, "serving", clock);
        Assert.Equal(160, Assert.Single(await FoodLog.DayAsync(t.Db, Profile, Day)).Energy!.Value, 9);
        Assert.Equal(["serving"], FoodLog.RecipeUnits(false));
    }

    [Fact]
    public async Task Search_finds_recipes_by_name()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        await Recipes.CreateAsync(t.Db, Household, Porridge(p), clock);
        await Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Name = "Overnight oats" }, clock);

        Assert.Equal(["Overnight oats", "Porridge"], (await Recipes.SearchAsync(t.Db, "", 10)).Select(r => r.Name));
        Assert.Equal(["Overnight oats"], (await Recipes.SearchAsync(t.Db, "oats", 10)).Select(r => r.Name));
    }

    [Fact]
    public async Task A_household_search_filters_before_the_limit()
    {
        await using var t = await OpenAsync();
        for (var i = 0; i < 3; i++)
        {
            await FoodLibrary.AddAsync(t.Db, OtherHousehold, new FoodInput($"Oat bar {i}", null, FoodBasis.Mass, 100, 400, 8, 60, 15, []), clock);
        }

        var ours = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Oat milk", null, FoodBasis.Volume, 100, 46, 1, 6.5, 1.5, []), clock);

        Assert.DoesNotContain(await FoodLibrary.SearchAsync(t.Db, "oat", 2), f => f.Id == ours);
        Assert.Equal([ours], (await FoodLibrary.SearchAsync(t.Db, "oat", 2, Household)).Select(f => f.Id));
    }

    [Fact]
    public async Task Every_recipe_is_listed_when_no_limit_is_given()
    {
        await using var t = await OpenAsync();
        var p = await StockAsync(t);
        for (var i = 0; i < 22; i++)
        {
            await Recipes.CreateAsync(t.Db, Household, Porridge(p) with { Name = $"Porridge {i:00}" }, clock);
        }

        Assert.Equal(22, (await Recipes.SearchAsync(t.Db, null, -1)).Length);
        var listed = await Recipes.ListAsync(t.Db);
        Assert.Equal(22, listed.Count);
        Assert.Equal("Porridge 00", listed[0].Recipe.Name);
        Assert.All(listed, r => Assert.Equal(["Oats", "Banana", "Peanut butter"], r.Ingredients.Select(i => i.FoodName)));
        Assert.All(listed, r => Assert.Equal(598.16, r.Nutrition()!.Total.Amounts[Nutrient.Energy]!.Value, 9));
    }

    /// <summary>Moves a second on at each read, so rows written one after another sort in that order.</summary>
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = now;
            now += TimeSpan.FromSeconds(1);
            return current;
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
