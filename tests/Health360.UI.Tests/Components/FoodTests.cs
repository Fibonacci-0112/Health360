using Bunit;
using Health360.Domain.Nutrition;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class FoodTests : AppTestContext
{
    private const string Household = "0192f0c0-0000-7000-8000-0000000000a1";

    private DateOnly Today => DateOnly.FromDateTime(App.Clock.Now.DateTime);

    private async Task<IRenderedComponent<Routes>> FoodAsync(string unitPrefs = "{}", bool household = true)
    {
        await SignedInAndOnboardedAsync(unitPrefs);
        if (household)
        {
            // As the household stream brings them down: synced rows, not local writes.
            await App.Database.Db.WriteTransaction(async tx =>
            {
                await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
                await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', ?, ?)", [Household, ProfileId]);
                await tx.Execute("DELETE FROM ps_crud");
            });
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("food");
        cut.WaitForAssertion(() => cut.Find("#food-search"), Wait);
        return cut;
    }

    private async Task SaveGranolaAsync(IRenderedComponent<Routes> cut)
    {
        cut.WaitForAssertion(() => cut.Find("#new-food-name"), Wait);
        await TypeAsync(cut, "#new-food-name", "Granola");
        await TypeAsync(cut, "#new-food-per", "30");
        await TypeAsync(cut, "#new-food-portion", "1 serving");
        await TypeAsync(cut, "#new-food-energy", "130");
        await TypeAsync(cut, "#new-food-protein", "3");
        await TypeAsync(cut, "#new-food-carbohydrate", "20");
        await SubmitAsync(cut, "#save-food");
        cut.WaitForAssertion(() => cut.Find("#log-food"), Wait);
    }

    [Fact]
    public async Task A_day_is_logged_offline_from_a_new_household_food_and_a_quick_add_with_its_totals()
    {
        var cut = await FoodAsync();
        cut.WaitForAssertion(() => Assert.Contains("Nothing logged", cut.Find("[aria-labelledby=food-total-title]").TextContent, StringComparison.Ordinal), Wait);
        await ChangeAsync(cut, "#food-meal", "breakfast");
        await SaveGranolaAsync(cut);

        // The new food is chosen, by its portion; 2 servings = 60 g.
        Assert.Equal("portion:1 serving", cut.Find("#food-unit").GetAttribute("value"));
        await TypeAsync(cut, "#food-amount", "2");
        cut.WaitForAssertion(() => Assert.Contains("Energy 260 kcal", cut.Find("#food-preview").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Fat not listed", cut.Find("#food-preview").TextContent, StringComparison.Ordinal);
        await SubmitAsync(cut, "#log-food");
        cut.WaitForAssertion(() => cut.Find("[aria-labelledby=meal-breakfast-title] .food-entry"), Wait);

        await ChangeAsync(cut, "#food-meal", "dinner");
        await TypeAsync(cut, "#quick-name", "Pizza");
        await TypeAsync(cut, "#quick-energy", "800");
        await TypeAsync(cut, "#quick-fat", "30");
        await SubmitAsync(cut, "#quick-add");

        cut.WaitForAssertion(() =>
        {
            var dinner = cut.Find("[aria-labelledby=meal-dinner-title]").TextContent;
            Assert.Contains("Pizza", dinner, StringComparison.Ordinal);
            var totals = cut.Find("#food-totals").TextContent;
            Assert.Contains("1,060 kcal", totals, StringComparison.Ordinal);
            // Granola listed no fat and the quick add no protein: both totals are lower bounds.
            Assert.Contains("at least 30 g", totals, StringComparison.Ordinal);
            Assert.Contains("at least 6 g", totals, StringComparison.Ordinal);
        }, Wait);
        Assert.Contains("2 × 1 serving", cut.Find("[aria-labelledby=meal-breakfast-title]").TextContent, StringComparison.Ordinal);

        var entries = await FoodLog.DayAsync(App.Database.Db, ProfileId, Today);
        Assert.Equal([("breakfast", "search", (double?)2), ("dinner", "quick_add", null)], entries.OrderBy(e => e.Meal, StringComparer.Ordinal).Select(e => (e.Meal, e.EntryMethod, e.Quantity)));
        Assert.Equal(260, entries.Single(e => e.Meal == "breakfast").Energy!.Value, 9);
        // Everything is queued to upload: the food, both entries.
        Assert.Equal(3, await App.Database.Db.Get<long>("SELECT count(*) FROM ps_crud", []));

        // Recently logged foods are offered when the search is empty.
        cut.WaitForAssertion(() => Assert.Contains("Granola", cut.Find("#food-results").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task An_entry_is_deleted_after_confirming()
    {
        var cut = await FoodAsync();
        await TypeAsync(cut, "#quick-energy", "250");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => cut.Find(".food-entry"), Wait);

        await ClickAsync(cut, "button[aria-label='Delete Quick add']");
        cut.WaitForAssertion(() => cut.Find(".food-entry .confirm"), Wait);
        await ClickAsync(cut, ".food-entry .confirm button.danger");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".food-entry")), Wait);
        Assert.Empty(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today));
    }

    [Fact]
    public async Task Each_day_shows_its_own_entries()
    {
        var cut = await FoodAsync();
        await ClickAsync(cut, "#food-previous");
        cut.WaitForAssertion(() => cut.Find("#food-today"), Wait);
        await TypeAsync(cut, "#quick-name", "Yesterday's lunch");
        await TypeAsync(cut, "#quick-energy", "500");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => Assert.Contains("Yesterday's lunch", cut.Find(".food-entry").TextContent, StringComparison.Ordinal), Wait);

        await ClickAsync(cut, "#food-today");
        cut.WaitForAssertion(() => Assert.Contains("Nothing logged", cut.Find("[aria-labelledby=food-total-title]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today.AddDays(-1)));
        Assert.Empty(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today));
    }

    [Fact]
    public async Task Energy_is_entered_and_shown_in_kilojoules_when_preferred_and_stored_in_kcal()
    {
        var cut = await FoodAsync("{\"energy\":\"kJ\"}");
        Assert.Contains("Energy (kJ)", cut.Find("label[for=quick-energy]").TextContent, StringComparison.Ordinal);
        await TypeAsync(cut, "#quick-energy", "418.4");
        await SubmitAsync(cut, "#quick-add");

        cut.WaitForAssertion(() => Assert.Contains("418 kJ", cut.Find("#food-totals").TextContent, StringComparison.Ordinal), Wait);
        var entry = Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today));
        Assert.Equal(100, entry.Energy!.Value, 9);
    }

    [Fact]
    public async Task A_food_the_database_would_refuse_shows_why_and_a_bad_amount_is_not_logged()
    {
        var cut = await FoodAsync();
        cut.WaitForAssertion(() => cut.Find("#new-food-name"), Wait);
        await TypeAsync(cut, "#new-food-name", "Mystery");
        await SubmitAsync(cut, "#save-food");
        cut.WaitForAssertion(() => Assert.Contains("at least calories", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);

        await SaveGranolaAsync(cut);
        await ChangeAsync(cut, "#food-unit", "g");
        await TypeAsync(cut, "#food-amount", "lots");
        await SubmitAsync(cut, "#log-food");
        cut.WaitForAssertion(() => Assert.Contains("as a number", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today));
        Assert.Single(await FoodLibrary.SearchAsync(App.Database.Db, "", 10));
    }

    [Fact]
    public async Task Without_a_synced_household_foods_cannot_be_added_but_quick_add_works()
    {
        var cut = await FoodAsync(household: false);
        cut.WaitForAssertion(() => Assert.Contains("hasn't synced", cut.Find("[aria-labelledby=new-food-title]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#save-food"));

        await TypeAsync(cut, "#quick-protein", "25");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => Assert.Contains("energy not listed", cut.Find(".food-entry").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(25, Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today)).Amounts()[Nutrient.Protein]);
    }
}
