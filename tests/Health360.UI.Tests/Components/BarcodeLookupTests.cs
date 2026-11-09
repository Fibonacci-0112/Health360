using Bunit;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>
/// Barcode lookup on the Food tab, through the UI and a real local database: a household food with the code is chosen
/// and logged as a barcode entry; an unknown code is handed to the new-food form, so the food is found by it next time.
/// </summary>
public sealed class BarcodeLookupTests : AppTestContext
{
    private const string Household = "0192f0c0-0000-7000-8000-0000000000a1";
    private const string Upc = "036000291452";
    private const string Ean13 = "0036000291452";

    private DateOnly Today => DateOnly.FromDateTime(App.Clock.Now.DateTime);

    private async Task<IRenderedComponent<Routes>> FoodAsync()
    {
        await SignedInAndOnboardedAsync("{\"energy\":\"kcal\"}");
        await App.Database.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', ?, ?)", [Household, ProfileId]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("food");
        cut.WaitForAssertion(() => cut.Find("#food-barcode"), Wait);
        cut.WaitForAssertion(() => cut.Find("#new-food-name"), Wait);
        return cut;
    }

    private async Task LookUpAsync(IRenderedComponent<Routes> cut, string code)
    {
        await TypeAsync(cut, "#food-barcode", code);
        await SubmitAsync(cut, "#look-up-barcode");
    }

    [Fact]
    public async Task An_unknown_barcode_is_saved_with_the_new_food_which_is_then_found_by_it_and_logged_as_a_barcode_entry()
    {
        var cut = await FoodAsync();

        await LookUpAsync(cut, Upc);
        cut.WaitForAssertion(() => Assert.Contains("None of your household's foods has this barcode.", cut.Find("#barcode-note").TextContent, StringComparison.Ordinal), Wait);
        // With no online source, the app says so rather than claiming the code isn't listed anywhere.
        Assert.Contains("online isn't available yet", cut.Find("#barcode-note").TextContent, StringComparison.Ordinal);
        Assert.Equal(Upc, cut.Find("#new-food-barcode").GetAttribute("value"));

        await TypeAsync(cut, "#new-food-name", "Cereal");
        await TypeAsync(cut, "#new-food-energy", "380");
        await TypeAsync(cut, "#new-food-protein", "10");
        await SubmitAsync(cut, "#save-food");
        cut.WaitForAssertion(() => cut.Find("#log-food"), Wait);
        Assert.Equal("", cut.Find("#new-food-barcode").GetAttribute("value"));
        await ClickAsync(cut, "#log-food-form button.secondary");

        // Scanned again, as the EAN-13 of the same product, the household's food is chosen straight away.
        await LookUpAsync(cut, Ean13);
        cut.WaitForAssertion(() => Assert.Contains("Cereal", cut.Find("#log-food-form").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#barcode-note"));
        await TypeAsync(cut, "#food-amount", "40");
        await ChangeAsync(cut, "#food-unit", "g");
        await SubmitAsync(cut, "#log-food");

        cut.WaitForAssertion(() => Assert.Contains("Cereal", cut.Find(".food-entry").TextContent, StringComparison.Ordinal), Wait);
        var entry = Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today));
        Assert.Equal(("barcode", 152.0), (entry.EntryMethod, entry.Energy!.Value));
        var food = Assert.Single(await FoodLibrary.FindByBarcodeAsync(App.Database.Db, Upc, 5));
        Assert.Equal(("Cereal", Upc), (food.Name, food.Barcode));
    }

    [Fact]
    public async Task Two_household_foods_with_the_code_are_both_offered()
    {
        var cut = await FoodAsync();
        var clock = App.Clock;
        await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Cola", null, FoodBasis.Volume, 100, 42, 0, 10.6, 0, []) { Barcode = Upc }, clock);
        await FoodLibrary.AddAsync(App.Database.Db, Household, new FoodInput("Cola zero", null, FoodBasis.Volume, 100, 0.3, 0, 0, 0, []) { Barcode = Ean13 }, clock);

        await LookUpAsync(cut, Upc);
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("#barcode-results li").Count), Wait);
        Assert.Empty(cut.FindAll("#log-food-form"));

        await ClickAsync(cut, "[aria-label='Choose Cola zero by barcode']");
        cut.WaitForAssertion(() => Assert.Contains("Cola zero", cut.Find("#log-food-form").TextContent, StringComparison.Ordinal), Wait);
        await TypeAsync(cut, "#food-amount", "330");
        await ChangeAsync(cut, "#food-unit", "ml");
        await SubmitAsync(cut, "#log-food");
        cut.WaitForAssertion(() => Assert.Contains("Cola zero", cut.Find(".food-entry").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("barcode", Assert.Single(await FoodLog.DayAsync(App.Database.Db, ProfileId, Today)).EntryMethod);
    }

    [Fact]
    public async Task A_misread_code_is_refused_on_lookup_and_on_saving_a_food()
    {
        var cut = await FoodAsync();

        await LookUpAsync(cut, "036000291453");
        cut.WaitForAssertion(() => Assert.Contains("doesn't read right", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("", cut.Find("#new-food-barcode").GetAttribute("value"));

        await TypeAsync(cut, "#new-food-name", "Cereal");
        await TypeAsync(cut, "#new-food-energy", "380");
        await TypeAsync(cut, "#new-food-barcode", "12345");
        await SubmitAsync(cut, "#save-food");
        cut.WaitForAssertion(() => Assert.Contains("barcode doesn't read right", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(await FoodLibrary.SearchAsync(App.Database.Db, "", 5));
    }

    [Fact]
    public async Task An_online_source_that_cannot_answer_is_named_and_a_match_is_reported_but_not_saved()
    {
        var off = new FakeSource(BarcodeSource.OpenFoodFacts) { Answer = BarcodeStepResult.Unavailable };
        var usda = new FakeSource(BarcodeSource.UsdaBranded) { Answer = BarcodeStepResult.NotFound };
        App.BarcodeSources.Add(usda);
        App.BarcodeSources.Add(off);
        var cut = await FoodAsync();

        await LookUpAsync(cut, Upc);
        cut.WaitForAssertion(() => Assert.Contains("Open Food Facts couldn't be reached", cut.Find("#barcode-note").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(["00036000291452"], off.Asked);
        Assert.Equal(["00036000291452"], usda.Asked);

        off.Answer = BarcodeStepResult.Found(new BarcodeMatch(
            BarcodeSource.OpenFoodFacts, "36000291452", "00036000291452", "Tomato soup", null, FoodBasis.Mass, 40, 1, 7, 1, null));
        await LookUpAsync(cut, Upc);
        cut.WaitForAssertion(() => Assert.Contains("Open Food Facts lists this as Tomato soup", cut.Find("#barcode-note").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(Upc, cut.Find("#new-food-barcode").GetAttribute("value"));
        Assert.Empty(await FoodLibrary.SearchAsync(App.Database.Db, "", 5));
    }

    private sealed class FakeSource(BarcodeSource source) : IBarcodeOnlineSource
    {
        public BarcodeSource Source => source;

        public BarcodeStepResult Answer { get; set; } = BarcodeStepResult.NotFound;

        public List<string> Asked { get; } = [];

        public Task<BarcodeStepResult> FindAsync(string gtin, CancellationToken cancellationToken)
        {
            Asked.Add(gtin);
            return Task.FromResult(Answer);
        }
    }
}
