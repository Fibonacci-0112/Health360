using Bunit;
using Health360.Domain.Nutrition;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>
/// The USDA catalogue searched online from the Food tab and the recipe editor, through the UI and a real local
/// database: a result is copied into the household, then logged or used as an ingredient like any household food.
/// </summary>
public sealed class CatalogueFoodTests : AppTestContext
{
    private const string Household = "0192f0c0-0000-7000-8000-0000000000a1";
    private const string Chicken = "50000000-0000-0000-0000-000000000001";

    private static FoodRow ChickenBreast() => new()
    {
        Id = Chicken,
        Source = "usda_fdc",
        Name = "Chicken breast",
        Basis = "g",
        Energy100 = 120,
        Protein100 = 22.5,
        Carbohydrate100 = 0,
        Fat100 = 2.6,
        Nutrients100 = "{\"sodium\":45}",
        Portions = "[{\"name\":\"1 breast\",\"amount\":174}]",
    };

    private static string CopyId => FoodCopy.IdFor(Guid.Parse(Household), Guid.Parse(Chicken)).ToString("D");

    private async Task<IRenderedComponent<Routes>> OpenAsync(string route, string ready)
    {
        await SignedInAndOnboardedAsync("{\"energy\":\"kcal\"}");
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', ?, ?)", [Household, ProfileId]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        App.Catalogue.Foods.Add(ChickenBreast());
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo(route);
        cut.WaitForAssertion(() => cut.Find(ready), Wait);
        return cut;
    }

    [Fact]
    public async Task A_catalogue_food_is_copied_into_the_household_and_logged_from_the_Food_tab()
    {
        var cut = await OpenAsync("food", "#food-search");

        await TypeAsync(cut, "#food-search", "chicken");
        cut.WaitForAssertion(() => Assert.Contains("Chicken breast", cut.Find("#catalogue-results").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("120 kcal per 100 g · USDA", cut.Find("#catalogue-results").TextContent, StringComparison.Ordinal);
        Assert.Contains("chicken", App.Catalogue.Queries);

        await ClickAsync(cut, "[aria-label='Use Chicken breast from the USDA catalogue']");
        cut.WaitForAssertion(() => cut.Find("#log-food"), Wait);
        // The copy offers its catalogue portion: 1 breast = 174 g = 208.8 kcal.
        Assert.Equal("portion:1 breast", cut.Find("#food-unit").GetAttribute("value"));
        await SubmitAsync(cut, "#log-food");

        cut.WaitForAssertion(() => Assert.Contains("Chicken breast", cut.Find(".food-entry").TextContent, StringComparison.Ordinal), Wait);
        var entry = Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, DateOnly.FromDateTime(App.Clock.Now.DateTime)));
        Assert.Equal((CopyId, 208.8), (entry.FoodId, Math.Round(entry.Energy!.Value, 6)));
        var copy = (await FoodLibrary.GetAsync(App.Database.Db, CopyId))!;
        Assert.Equal((Household, "usda_fdc"), (copy.HouseholdId, copy.Source));
        // The copy and the entry upload; the copy names its catalogue food, which the server fills it from.
        Assert.Equal(1, await App.Database.Db.Get<long>(
            "SELECT count(*) FROM ps_crud WHERE json_extract(data, '$.type') = 'foods' AND json_extract(data, '$.data.copied_from') = ?", [Chicken]));

        // Searched again, by words that match the catalogue name separately, the food is the household's: listed with
        // its foods, not offered from the catalogue again.
        await TypeAsync(cut, "#food-search", "chick brea");
        cut.WaitForAssertion(() => Assert.Contains("Chicken breast", cut.Find("#food-results").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#catalogue-results"));
    }

    [Fact]
    public async Task A_catalogue_that_cannot_be_searched_says_so_and_household_foods_still_show()
    {
        App.Catalogue.Status = CatalogueSearchStatus.Unavailable;
        var cut = await OpenAsync("food", "#food-search");

        await TypeAsync(cut, "#food-search", "chicken");
        cut.WaitForAssertion(() => Assert.Contains("couldn't be searched", cut.Find("#catalogue-note").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#catalogue-results"));
        Assert.Empty(cut.FindAll("[role=alert]"));

        App.Catalogue.Status = CatalogueSearchStatus.SignedOut;
        await TypeAsync(cut, "#food-search", "chick");
        cut.WaitForAssertion(() => Assert.Contains("Sign in again", cut.Find("#catalogue-note").TextContent, StringComparison.Ordinal), Wait);

        // A one-letter query isn't sent.
        var sent = App.Catalogue.Queries.Count;
        await TypeAsync(cut, "#food-search", "c");
        Assert.Equal(sent, App.Catalogue.Queries.Count);
    }

    [Fact]
    public async Task A_catalogue_food_becomes_a_recipe_ingredient_through_its_household_copy()
    {
        var cut = await OpenAsync("food/recipes/new", "#ingredient-search");
        await TypeAsync(cut, "#recipe-name", "Chicken bowl");

        await TypeAsync(cut, "#ingredient-search", "chicken");
        cut.WaitForAssertion(() => cut.Find("#ingredient-catalogue"), Wait);
        await ClickAsync(cut, "[aria-label='Add Chicken breast from the USDA catalogue']");

        // Added as the household's copy, with its portion: 1 breast = 174 g, 208.8 kcal for the whole recipe.
        cut.WaitForAssertion(() => Assert.Contains("209 kcal", cut.Find("#recipe-nutrition").TextContent, StringComparison.Ordinal), Wait);
        Assert.NotNull(await FoodLibrary.GetAsync(App.Database.Db, CopyId));
        await SubmitAsync(cut, "#save-recipe");
        cut.WaitForAssertion(() => cut.Find("#recipe-list"), Wait);
        var recipe = Assert.Single(await Recipes.ListAsync(App.Database.Db));
        Assert.Equal(CopyId, Assert.Single(recipe.Ingredients).FoodId);
    }

    [Fact]
    public async Task Another_households_copy_does_not_hide_the_catalogue_food_from_the_household_adding_it()
    {
        const string Cabin = "0192f0c0-0000-7000-8000-0000000000a2";
        var cut = await OpenAsync("food", "#food-search");
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Cabin')", [Cabin]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm2', ?, ?)", [Cabin, ProfileId]);
        });
        await FoodLibrary.CopyFromCatalogueAsync(App.Database.Db, Household, ChickenBreast(), App.Clock);
        cut.WaitForAssertion(() => cut.Find("#new-food-household"), Wait);

        // Home has it: not offered while Home is where new foods go.
        await ChangeAsync(cut, "#new-food-household", Household);
        await TypeAsync(cut, "#food-search", "chicken");
        cut.WaitForAssertion(() => Assert.Contains("Chicken breast", cut.Find("#food-results").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#catalogue-results"));

        // Cabin hasn't: offered, and using it makes Cabin's own copy.
        await ChangeAsync(cut, "#new-food-household", Cabin);
        await TypeAsync(cut, "#food-search", "chicke");
        cut.WaitForAssertion(() => cut.Find("#catalogue-results"), Wait);
        await ClickAsync(cut, "[aria-label='Use Chicken breast from the USDA catalogue']");
        cut.WaitForAssertion(() => cut.Find("#log-food"), Wait);
        var cabinCopy = FoodCopy.IdFor(Guid.Parse(Cabin), Guid.Parse(Chicken)).ToString("D");
        Assert.Equal(Cabin, (await FoodLibrary.GetAsync(App.Database.Db, cabinCopy))!.HouseholdId);
    }

    [Fact]
    public async Task Changing_a_recipes_household_clears_the_catalogue_results_of_the_old_search()
    {
        const string Cabin = "0192f0c0-0000-7000-8000-0000000000a2";
        var cut = await OpenAsync("food/recipes/new", "#ingredient-search");
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Cabin')", [Cabin]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm2', ?, ?)", [Cabin, ProfileId]);
        });
        cut.WaitForAssertion(() => cut.Find("#recipe-household"), Wait);

        await TypeAsync(cut, "#ingredient-search", "chicken");
        cut.WaitForAssertion(() => cut.Find("#ingredient-catalogue"), Wait);
        await ChangeAsync(cut, "#recipe-household", Cabin);

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("#ingredient-catalogue")), Wait);
        Assert.Equal("", cut.Find("#ingredient-search").GetAttribute("value"));
    }

    [Fact]
    public async Task A_copy_beyond_the_household_results_limit_stays_reachable_through_its_catalogue_row()
    {
        var cut = await OpenAsync("food", "#food-search");
        var copyId = await FoodLibrary.CopyFromCatalogueAsync(App.Database.Db, Household, ChickenBreast(), App.Clock);
        // 20 household foods that sort before the copy fill the household results, so the copy isn't listed.
        await App.Database.Db.WriteTransaction(async tx =>
        {
            for (var i = 0; i < 20; i++)
            {
                await tx.Execute(
                    "INSERT INTO foods (id, household_id, source, name, basis, energy_100, nutrients_100, portions, verified) " +
                    "VALUES (?, ?, 'custom', ?, 'g', 100, '{}', '[]', 0)",
                    [$"0192f0c0-0000-7000-8000-0000000001{i:D2}", Household, $"Aaa chicken {i:D2}"]);
            }
        });

        await TypeAsync(cut, "#food-search", "chicken");
        cut.WaitForAssertion(() => cut.Find("#catalogue-results"), Wait);
        Assert.DoesNotContain("Chicken breast", cut.Find("#food-results").TextContent, StringComparison.Ordinal);

        // Using it reuses the existing copy rather than making another.
        await ClickAsync(cut, "[aria-label='Use Chicken breast from the USDA catalogue']");
        cut.WaitForAssertion(() => cut.Find("#log-food"), Wait);
        Assert.Contains("Chicken breast", cut.Find("#log-food-form").TextContent, StringComparison.Ordinal);
        Assert.Equal(1, await App.Database.Db.Get<long>("SELECT count(*) FROM foods WHERE copied_from = ?", [Chicken]));
        Assert.NotNull(await FoodLibrary.GetAsync(App.Database.Db, copyId));
    }

    [Fact]
    public async Task Opening_another_recipe_clears_the_catalogue_results_of_the_last_search()
    {
        var cut = await OpenAsync("food/recipes/new", "#ingredient-search");
        var copyId = await FoodLibrary.CopyFromCatalogueAsync(App.Database.Db, Household, ChickenBreast(), App.Clock);
        var recipeId = await Recipes.CreateAsync(App.Database.Db, Household,
            new Health360.Contracts.Nutrition.RecipeInput("Plain chicken", 1, null, null,
                [new Health360.Contracts.Nutrition.RecipeIngredientInput(null, copyId, 100, "g", null)]), App.Clock);

        await TypeAsync(cut, "#ingredient-search", "breas");
        App.Catalogue.Foods.Add(new FoodRow
        {
            Id = "50000000-0000-0000-0000-000000000002",
            Source = "usda_fdc",
            Name = "Breaded fish",
            Basis = "g",
            Energy100 = 200,
            Nutrients100 = "{}",
            Portions = "[]",
        });
        await TypeAsync(cut, "#ingredient-search", "brea");
        cut.WaitForAssertion(() => cut.Find("#ingredient-catalogue"), Wait);

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo($"food/recipes/{recipeId}"));
        cut.WaitForAssertion(() => Assert.Equal("Plain chicken", cut.Find("#recipe-name").GetAttribute("value")), Wait);
        Assert.Empty(cut.FindAll("#ingredient-catalogue"));
        Assert.Empty(cut.FindAll("#ingredient-matches"));
    }
}
