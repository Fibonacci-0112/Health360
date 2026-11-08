using Bunit;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class RecipeTests : AppTestContext
{
    private const string Household = "0192f0c0-0000-7000-8000-0000000000a1";

    private DateOnly Today => DateOnly.FromDateTime(App.Clock.Now.DateTime);

    private sealed record Pantry(string Oats, string Banana, string PeanutButter);

    /// <summary>The golden fixture of docs/calculations.md (Recipes), as household foods synced to this device.</summary>
    private async Task<Pantry> StockAsync()
    {
        await SignedInAndOnboardedAsync();
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', ?, ?)", [Household, ProfileId]);
        });
        var oats = await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Oats", null, FoodBasis.Mass, 100, 379, 13.2, 67.7, 6.5, []), App.Clock);
        var banana = await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Banana", null, FoodBasis.Mass, 100, 89, 1.09, 22.84, 0.33, []), App.Clock);
        var peanutButter = await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Peanut butter", null, FoodBasis.Mass, 100, 588, 25.1, 19.6, 50.4, []), App.Clock);
        await App.Database.Db.Execute("DELETE FROM ps_crud");
        return new Pantry(oats, banana, peanutButter);
    }

    private IRenderedComponent<Routes> Open(string route)
    {
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        return cut;
    }

    private async Task AddIngredientAsync(IRenderedComponent<Routes> cut, string search, string food, string grams)
    {
        await TypeAsync(cut, "#ingredient-search", search);
        cut.WaitForAssertion(() => cut.Find($"button[aria-label='Add {food}']"), Wait);
        await ClickAsync(cut, $"button[aria-label='Add {food}']");
        cut.WaitForAssertion(() => cut.Find($"input[aria-label='{food} amount']"), Wait);
        await ChangeAsync(cut, $"select[aria-label='{food} unit']", "g");
        await TypeAsync(cut, $"input[aria-label='{food} amount']", grams);
    }

    [Fact]
    public async Task A_new_recipe_totals_its_ingredients_as_the_hand_calculation_does_and_is_saved()
    {
        await StockAsync();
        var cut = Open("food/recipes");
        cut.WaitForAssertion(() => Assert.Contains("No recipes yet", cut.Markup, StringComparison.Ordinal), Wait);
        await ClickAsync(cut, "#new-recipe");
        cut.WaitForAssertion(() => cut.Find("#recipe-name"), Wait);

        await TypeAsync(cut, "#recipe-name", "Porridge");
        await TypeAsync(cut, "#recipe-servings", "2");
        await AddIngredientAsync(cut, "oat", "Oats", "80");
        await AddIngredientAsync(cut, "bana", "Banana", "120");
        await AddIngredientAsync(cut, "peanut", "Peanut butter", "32");

        // 598.16 kcal for the dish, 299.08 per serving; 232 g raw, so 257.8 kcal per 100 g.
        cut.WaitForAssertion(() =>
        {
            var nutrition = cut.Find("#recipe-nutrition").TextContent;
            Assert.Contains("Per serving: 299 kcal", nutrition, StringComparison.Ordinal);
            Assert.Contains("Whole recipe: 598 kcal", nutrition, StringComparison.Ordinal);
            Assert.Contains("per 100 g of the dish: 258 kcal", nutrition, StringComparison.Ordinal);
        }, Wait);
        await SubmitAsync(cut, "#save-recipe");

        cut.WaitForAssertion(() => Assert.Contains("299 kcal per serving", cut.Find("#recipe-list").TextContent, StringComparison.Ordinal), Wait);
        var saved = (await Recipes.GetAsync(App.Database.Db, Assert.Single(await Recipes.SearchAsync(App.Database.Db, "", 10)).Id))!;
        Assert.Equal(["Oats", "Banana", "Peanut butter"], saved.Ingredients.Select(i => i.FoodName));
        Assert.Equal(598.16, saved.Nutrition()!.Total.Amounts[Nutrient.Energy]!.Value, 9);
        Assert.True(await App.Database.Db.Get<long>("SELECT count(*) FROM ps_crud", []) >= 4);
    }

    [Fact]
    public async Task An_edited_recipe_keeps_the_ingredients_left_alone_and_can_be_deleted()
    {
        var p = await StockAsync();
        var id = await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput("Porridge", 2, null, null,
            [new(null, p.Oats, 80, "g", null), new(null, p.Banana, 120, "g", null), new(null, p.PeanutButter, 32, "g", null)]), App.Clock);
        var before = (await Recipes.GetAsync(App.Database.Db, id))!.Ingredients;
        var cut = Open($"food/recipes/{id}");
        cut.WaitForAssertion(() => Assert.Equal("Porridge", cut.Find("#recipe-name").GetAttribute("value")), Wait);

        await ClickAsync(cut, "button[aria-label='Remove Peanut butter']");
        await TypeAsync(cut, "#recipe-servings", "1");
        await SubmitAsync(cut, "#save-recipe");
        cut.WaitForAssertion(() => cut.Find("#recipe-list"), Wait);

        var after = (await Recipes.GetAsync(App.Database.Db, id))!;
        Assert.Equal([before[0].Id, before[1].Id], after.Ingredients.Select(i => i.Id));
        Assert.Equal(1, after.Recipe.Servings);

        await ClickAsync(cut, "button[aria-label='Edit Porridge']");
        cut.WaitForAssertion(() => cut.Find("#delete-recipe"), Wait);
        await ClickAsync(cut, "#delete-recipe");
        cut.WaitForAssertion(() => Assert.Contains("Delete Porridge for the whole household?", cut.Find("[role=alertdialog]").TextContent, StringComparison.Ordinal), Wait);
        await ClickAsync(cut, "#delete-recipe-confirm");
        cut.WaitForAssertion(() => Assert.Contains("No recipes yet", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Null(await Recipes.GetAsync(App.Database.Db, id));
    }

    [Fact]
    public async Task A_recipe_is_logged_from_the_food_tab_by_servings_and_by_weight_once_weighed()
    {
        var p = await StockAsync();
        await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput("Porridge", 2, 400, "g",
            [new(null, p.Oats, 80, "g", null), new(null, p.Banana, 120, "g", null), new(null, p.PeanutButter, 32, "g", null)]), App.Clock);
        var cut = Open("food");
        cut.WaitForAssertion(() => cut.Find("#food-search"), Wait);
        await ChangeAsync(cut, "#food-meal", "breakfast");

        await TypeAsync(cut, "#food-search", "porr");
        cut.WaitForAssertion(() => cut.Find("button[aria-label='Choose recipe Porridge']"), Wait);
        await ClickAsync(cut, "button[aria-label='Choose recipe Porridge']");
        cut.WaitForAssertion(() => cut.Find("#recipe-amount"), Wait);
        await TypeAsync(cut, "#recipe-amount", "1.5");
        // 299.08 × 1.5 = 448.62 kcal.
        cut.WaitForAssertion(() => Assert.Contains("Energy 449 kcal", cut.Find("#recipe-preview").TextContent, StringComparison.Ordinal), Wait);
        await SubmitAsync(cut, "#log-recipe");
        cut.WaitForAssertion(() => Assert.Contains("1.5 servings", cut.Find("[aria-labelledby=meal-breakfast-title]").TextContent, StringComparison.Ordinal), Wait);

        // A minute later, so the two entries sort in the order they were logged.
        App.Clock.Advance(TimeSpan.FromMinutes(1));
        await ClickAsync(cut, "button[aria-label='Choose recipe Porridge']");
        cut.WaitForAssertion(() => cut.Find("#recipe-unit"), Wait);
        await ChangeAsync(cut, "#recipe-unit", "g");
        await TypeAsync(cut, "#recipe-amount", "100");
        await SubmitAsync(cut, "#log-recipe");

        // 598.16 × 100 / 400 = 149.54 kcal in 100 g of the 400 g dish.
        cut.WaitForAssertion(() => Assert.Contains("100 g", cut.Find("[aria-labelledby=meal-breakfast-title]").TextContent, StringComparison.Ordinal), Wait);
        var entries = await FoodLog.DayAsync(App.Database.Db, ProfileId, Today);
        Assert.Equal([448.62, 149.54], entries.Select(e => Math.Round(e.Energy!.Value, 9)));
        Assert.All(entries, e => Assert.Equal("recipe", e.EntryMethod));
    }

    [Fact]
    public async Task A_recipe_whose_food_has_not_synced_is_shown_but_not_saved()
    {
        var p = await StockAsync();
        var id = await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput("Porridge", 2, null, null,
            [new(null, p.Oats, 80, "g", null), new(null, p.Banana, 120, "g", null)]), App.Clock);
        await App.Database.Db.Execute("UPDATE recipe_ingredients SET food_id = 'not-here' WHERE food_id = ?", [p.Banana]);

        var cut = Open($"food/recipes/{id}");
        cut.WaitForAssertion(() => cut.Find("#recipe-incomplete"), Wait);
        Assert.True(cut.Find("#save-recipe").HasAttribute("disabled"));
        Assert.Contains("Not on this device yet", cut.Find("#ingredient-list").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_portion_the_food_no_longer_lists_keeps_its_saved_amount_but_takes_no_new_one()
    {
        var p = await StockAsync();
        await App.Database.Db.Execute("UPDATE foods SET portions = '[{\"name\": \"1 cup\", \"amount\": 81}]' WHERE id = ?", [p.Oats]);
        var id = await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput("Oats", 1, null, null, [new(null, p.Oats, 1, null, "1 cup")]), App.Clock);
        // The cup is resized, then removed, after the recipe was saved; the recipe still holds 81 g.
        await App.Database.Db.Execute("UPDATE foods SET portions = '[]' WHERE id = ?", [p.Oats]);

        var cut = Open($"food/recipes/{id}");
        cut.WaitForAssertion(() => Assert.Contains("× 1 cup (no longer listed)", cut.Find("select[aria-label='Oats unit']").TextContent, StringComparison.Ordinal), Wait);
        // 379 × 81 / 100 = 307 kcal: the stored amount, not a recomputed one.
        Assert.Contains("Per serving: 307 kcal", cut.Find("#recipe-nutrition").TextContent, StringComparison.Ordinal);

        await TypeAsync(cut, "input[aria-label='Oats amount']", "2");
        await SubmitAsync(cut, "#save-recipe");
        cut.WaitForAssertion(() => Assert.Contains("no longer lists \"1 cup\"", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(1, (await Recipes.GetAsync(App.Database.Db, id))!.Ingredients.Single().Quantity);

        await TypeAsync(cut, "input[aria-label='Oats amount']", "1");
        await TypeAsync(cut, "#recipe-servings", "2");
        await SubmitAsync(cut, "#save-recipe");
        cut.WaitForAssertion(() => cut.Find("#recipe-list"), Wait);
        var saved = (await Recipes.GetAsync(App.Database.Db, id))!;
        Assert.Equal((2.0, 81.0), (saved.Recipe.Servings, saved.Ingredients.Single().BaseAmount));
    }

    [Fact]
    public async Task The_editor_picks_up_a_household_and_a_food_that_sync_while_it_is_open()
    {
        await SignedInAndOnboardedAsync();
        var cut = Open("food/recipes/new");
        cut.WaitForAssertion(() => Assert.Contains("hasn't synced", cut.Markup, StringComparison.Ordinal), Wait);

        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', ?, ?)", [Household, ProfileId]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        cut.WaitForAssertion(() => cut.Find("#recipe-name"), Wait);

        var oats = await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Oats", null, FoodBasis.Mass, 100, 379, 13.2, 67.7, 6.5, []), App.Clock);
        var id = await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput("Oats", 1, null, null, [new(null, oats, 50, "g", null)]), App.Clock);
        // The food row is held back, as if it hadn't synced yet.
        var food = await App.Database.Db.Get<FoodRow>("SELECT id AS Id, name AS Name, basis AS Basis, energy_100 AS Energy100 FROM foods WHERE id = ?", [oats]);
        await App.Database.Db.Execute("DELETE FROM foods WHERE id = ?", [oats]);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"food/recipes/{id}");
        cut.WaitForAssertion(() => Assert.True(cut.Find("#save-recipe").HasAttribute("disabled")), Wait);

        await App.Database.Db.Execute(
            "INSERT INTO foods (id, household_id, source, name, basis, energy_100, nutrients_100, portions, verified) VALUES (?, ?, 'custom', ?, ?, ?, '{}', '[]', 0)",
            [food.Id, Household, food.Name, food.Basis, food.Energy100]);
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("#save-recipe").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("#recipe-incomplete"));
            Assert.Contains("Per serving: 190 kcal", cut.Find("#recipe-nutrition").TextContent, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public async Task The_recipe_list_shows_every_recipe()
    {
        var p = await StockAsync();
        for (var i = 0; i < 22; i++)
        {
            await Recipes.CreateAsync(App.Database.Db, Household, new RecipeInput($"Porridge {i:00}", 1, null, null, [new(null, p.Oats, 50, "g", null)]), App.Clock);
        }

        var cut = Open("food/recipes");
        cut.WaitForAssertion(() => Assert.Equal(22, cut.FindAll("#recipe-list li").Count), Wait);
    }

    [Fact]
    public async Task A_household_that_syncs_into_an_open_editor_is_offered_and_the_chosen_one_stays()
    {
        await StockAsync();
        var cut = Open("food/recipes/new");
        cut.WaitForAssertion(() => cut.Find("#recipe-name"), Wait);
        await TypeAsync(cut, "#recipe-name", "Porridge");
        Assert.Empty(cut.FindAll("#recipe-household"));

        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES ('0192f0c0-0000-7000-8000-0000000000a2', 'Cabin')");
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm2', '0192f0c0-0000-7000-8000-0000000000a2', ?)", [ProfileId]);
            await tx.Execute("DELETE FROM ps_crud");
        });

        cut.WaitForAssertion(() =>
        {
            var select = cut.Find("#recipe-household");
            Assert.Equal(["Cabin", "Home"], select.QuerySelectorAll("option").Select(o => o.TextContent));
            Assert.Equal(Household, select.GetAttribute("value"));
        }, Wait);
        Assert.Equal("Porridge", cut.Find("#recipe-name").GetAttribute("value"));
    }

    [Fact]
    public async Task A_refresh_that_fails_is_reported_instead_of_stopping_silently()
    {
        await StockAsync();
        var cut = Open("food/recipes/new");
        cut.WaitForAssertion(() => cut.Find("#recipe-name"), Wait);

        await App.Database.Db.Execute("DROP VIEW households");
        // A food syncing in wakes the editor's watch, whose refresh now can't read the households.
        await App.Database.Db.Execute(
            "INSERT INTO foods (id, household_id, source, name, basis, energy_100, nutrients_100, portions, verified) VALUES ('rice', ?, 'custom', 'Rice', 'g', 130, '{}', '[]', 0)",
            [Household]);

        cut.WaitForAssertion(() => Assert.Contains("Couldn't refresh this recipe", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
    }
}
