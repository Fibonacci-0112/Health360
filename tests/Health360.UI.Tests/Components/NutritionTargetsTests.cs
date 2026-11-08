using Bunit;
using Health360.Domain.Nutrition;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>
/// Nutrition targets through the UI and a real local database, including step 1.3's exit criterion: formula targets
/// follow a lab RMR being entered and deleted, stored targets included, and a manual target stays an override.
/// </summary>
public sealed class NutritionTargetsTests : AppTestContext
{
    // As in PhysiologyTests: male, 30, 180 cm, moderately active (PAL 1.85), 80 kg. Mifflin-St Jeor 1781.6 kcal,
    // maintenance 3295.96 kcal. A lab RMR of 1900 makes it 3515.
    private const string LabRmr = "0192f0c0-0000-7000-8000-0000000000d1";

    private async Task<IRenderedComponent<Routes>> FoodAsync(bool weighed = true)
    {
        await SignedInAndOnboardedAsync("{\"energy\":\"kcal\",\"proteinRatio\":\"g_per_kg\"}");
        await App.Database.SeedDetailsAsync(ProfileId, "male", "1996-05-01", 180, "moderate");
        await App.Database.SeedMetricsAsync();
        if (weighed)
        {
            await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d0", ProfileId, "weight", 80, "kg", 80, "2026-10-29");
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("food");
        cut.WaitForAssertion(() => cut.Find("#target-values"), Wait);
        return cut;
    }

    private static string Target(IRenderedComponent<Routes> cut, string code) =>
        cut.Find($"#target-values [data-target={code}]").TextContent;

    /// <summary>Follows the card's Change link (bUnit doesn't navigate on a link click).</summary>
    private Task EditorAsync(IRenderedComponent<Routes> cut)
    {
        Assert.Equal("food/targets", cut.Find("#change-targets").GetAttribute("href"));
        Navigate(cut, "food/targets");
        cut.WaitForAssertion(() => cut.Find("#targets-form"), Wait);
        return Task.CompletedTask;
    }

    private void BackToFood(IRenderedComponent<Routes> cut)
    {
        Navigate(cut, "food");
        cut.WaitForAssertion(() => cut.Find("#target-values"), Wait);
    }

    private void Navigate(IRenderedComponent<Routes> cut, string route) =>
        cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo(route)).Wait(Xunit.TestContext.Current.CancellationToken);

    [Fact]
    public async Task Formula_targets_start_from_maintenance_and_follow_a_lab_RMR_entered_and_deleted()
    {
        var cut = await FoodAsync();

        // Starting settings: maintenance, 1.6 g/kg of 80 kg = 128 g, 30% fat = 109.9 g, carbohydrate the rest = 448.8 g.
        cut.WaitForAssertion(() => Assert.Contains("0 kcal of 3,296 kcal · 3,296 kcal left", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        Assert.Contains("of 128 g", Target(cut, "protein"), StringComparison.Ordinal);
        Assert.Contains("of 110 g", Target(cut, "fat"), StringComparison.Ordinal);
        Assert.Contains("of 449 g", Target(cut, "carbohydrate"), StringComparison.Ordinal);
        var basis = cut.Find("#target-basis").TextContent;
        Assert.Contains("RMR 1,782 kcal, Mifflin-St Jeor estimate, × activity 1.85", basis, StringComparison.Ordinal);
        Assert.Contains("starting settings", basis, StringComparison.Ordinal);

        await App.Database.SeedMeasurementAsync(LabRmr, ProfileId, "rmr", 1900, "kcal", 1900, "2026-10-01", "lab");
        cut.WaitForAssertion(() => Assert.Contains("of 3,515 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        Assert.Contains("RMR 1,900 kcal, Lab, 1 Oct 2026", cut.Find("#target-basis").TextContent, StringComparison.Ordinal);

        await Measurements.DeleteAsync(App.Database.Db, ProfileId, LabRmr);
        cut.WaitForAssertion(() => Assert.Contains("of 3,296 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task Saved_formula_targets_keep_their_adjustment_and_still_follow_the_RMR_and_the_log()
    {
        var cut = await FoodAsync();
        await EditorAsync(cut);
        await TypeAsync(cut, "#target-adjustment", "-500");
        await TypeAsync(cut, "#target-fat-percent", "25");
        cut.WaitForAssertion(() => Assert.Contains("2,796 kcal", cut.Find("#targets-preview").TextContent, StringComparison.Ordinal), Wait);
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Targets saved", cut.Markup, StringComparison.Ordinal), Wait);

        var row = await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId);
        Assert.Equal(TargetSettings.Default with { EnergyAdjustmentKcal = -500, FatPercent = 25 }, row!.ToSettings());
        Assert.Equal(1, await App.Database.Db.Get<long>("SELECT count(*) FROM ps_crud WHERE data LIKE '%nutrition_targets%'", []));

        BackToFood(cut);
        cut.WaitForAssertion(() => Assert.Contains("of 2,796 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("starting settings", cut.Find("#target-basis").TextContent, StringComparison.Ordinal);
        Assert.Contains("− 500 kcal", cut.Find("#target-basis").TextContent, StringComparison.Ordinal);

        // The stored row holds the settings, so the target moves with a lab RMR: 1900 × 1.85 − 500 = 3015.
        await App.Database.SeedMeasurementAsync(LabRmr, ProfileId, "rmr", 1900, "kcal", 1900, "2026-10-01", "lab");
        cut.WaitForAssertion(() => Assert.Contains("of 3,015 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        await Measurements.DeleteAsync(App.Database.Db, ProfileId, LabRmr);
        cut.WaitForAssertion(() => Assert.Contains("of 2,796 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);

        // What's logged counts against them.
        await TypeAsync(cut, "#quick-energy", "800");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => Assert.Contains("800 kcal of 2,796 kcal · 1,996 kcal left", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        // The quick add lists no protein: the eaten amount is unknown, so only the target is shown.
        Assert.Contains("target 128 g", Target(cut, "protein"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manual_target_is_an_override_a_lab_RMR_does_not_change()
    {
        var cut = await FoodAsync();
        await EditorAsync(cut);
        await ClickAsync(() => cut.FindAll("[role=radio]").Single(e => e.TextContent.Contains("My own numbers", StringComparison.Ordinal)));
        await TypeAsync(cut, "#target-manual-energy", "2400");
        await TypeAsync(cut, "#target-manual-protein", "150");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Targets saved", cut.Markup, StringComparison.Ordinal), Wait);

        BackToFood(cut);
        cut.WaitForAssertion(() => Assert.Contains("of 2,400 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        Assert.Contains("of 150 g", Target(cut, "protein"), StringComparison.Ordinal);
        Assert.Contains("no target", Target(cut, "fat"), StringComparison.Ordinal);
        Assert.Contains("Set by you", cut.Find("#target-basis").TextContent, StringComparison.Ordinal);

        await App.Database.SeedMeasurementAsync(LabRmr, ProfileId, "rmr", 1900, "kcal", 1900, "2026-10-01", "lab");
        // Wait until the profile has the lab RMR, so the check below runs after the card has seen it.
        var body = Services.GetRequiredService<Health360.UI.Data.BodyData>();
        cut.WaitForAssertion(() => Assert.Equal(1900, body.Profile()!.Rmr!.Resolution.Value), Wait);
        cut.WaitForAssertion(() => Assert.Contains("of 2,400 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);

        // A macro with no target still shows a lower-bound total as one: 6 g of fat, and an entry that lists none.
        await TypeAsync(cut, "#quick-energy", "300");
        await TypeAsync(cut, "#quick-fat", "6");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => Assert.Contains("300 kcal of 2,400 kcal", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        await TypeAsync(cut, "#quick-energy", "100");
        await SubmitAsync(cut, "#quick-add");
        cut.WaitForAssertion(() => Assert.Equal("at least 6 g · no target", Target(cut, "fat").Replace("Fat", "", StringComparison.Ordinal).Trim()), Wait);
        var row = await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId);
        Assert.Equal((TargetMethod.Manual, 2400.0, 150.0), (row!.ToSettings()!.Method, row.ManualEnergyKcal!.Value, row.ManualProteinG!.Value));
    }

    [Fact]
    public async Task Switching_the_ratio_unit_converts_the_number_and_missing_inputs_say_what_is_needed()
    {
        var cut = await FoodAsync(weighed: false);
        cut.WaitForAssertion(() => Assert.Contains("no target", Target(cut, "energy"), StringComparison.Ordinal), Wait);
        var problems = string.Join(" ", cut.FindAll(".target-problem").Select(p => p.TextContent));
        Assert.Contains("no maintenance estimate yet", problems, StringComparison.Ordinal);
        Assert.Contains("Log a weigh-in", problems, StringComparison.Ordinal);

        await EditorAsync(cut);
        Assert.Equal("1.6", cut.Find("#target-protein-ratio").GetAttribute("value"));
        await ClickAsync(() => cut.FindAll("[aria-labelledby=target-ratio-unit] [role=radio]").Single(e => e.TextContent.Trim() == "lb"));
        // 1.6 g/kg × 0.45359237 = 0.7257 g/lb, shown to 2 decimals.
        cut.WaitForAssertion(() => Assert.Equal("0.73", cut.Find("#target-protein-ratio").GetAttribute("value")), Wait);

        // Converting at the 10 g/kg limit shows 4.54 g/lb, which is over it once rounded: the exact value is kept.
        await ClickAsync(() => cut.FindAll("[aria-labelledby=target-ratio-unit] [role=radio]").Single(e => e.TextContent.Trim() == "kg"));
        await TypeAsync(cut, "#target-protein-ratio", "10");
        await ClickAsync(() => cut.FindAll("[aria-labelledby=target-ratio-unit] [role=radio]").Single(e => e.TextContent.Trim() == "lb"));
        cut.WaitForAssertion(() => Assert.Equal("4.54", cut.Find("#target-protein-ratio").GetAttribute("value")), Wait);
        Assert.Empty(cut.FindAll("#targets-preview .muted:not(.small)"));
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Targets saved", cut.Markup, StringComparison.Ordinal), Wait);
        var saved = (await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId))!.ToSettings()!;
        Assert.Equal(Health360.Domain.Units.ProteinRatioUnit.GramsPerPound, saved.ProteinRatioUnit);
        Assert.Equal(10, Health360.Domain.Units.ProteinRatio.ToGPerKg(saved.ProteinRatio, saved.ProteinRatioUnit), 12);

        // The stated maximum is one the check accepts: 4.53 g/lb, not 4.54 (10.009 g/kg).
        await TypeAsync(cut, "#target-protein-ratio", "5");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("at most 4.53 g/lb", cut.Find(".error").TextContent, StringComparison.Ordinal), Wait);
        await TypeAsync(cut, "#target-protein-ratio", "4.53");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".error")), Wait);

        await TypeAsync(cut, "#target-fat-percent", "100");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Enter fat as a percentage", cut.Find(".error").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(TargetSettings.DefaultFatPercent, (await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId))!.FatPercent);
    }

    [Fact]
    public async Task Saving_without_changes_keeps_stored_values_the_display_rounds()
    {
        var cut = await FoodAsync();
        // Finer than the editor shows, and a g/lb ratio exactly at the 10 g/kg limit that rounds to above it.
        var manual = TargetSettings.Default with
        {
            Method = TargetMethod.Manual,
            ProteinRatio = 4.5359237,
            ProteinRatioUnit = Health360.Domain.Units.ProteinRatioUnit.GramsPerPound,
            FatPercent = 27.55,
            ManualEnergyKcal = 2400.4,
            ManualProteinG = 123.44,
            ManualFatG = 70.06,
        };
        await NutritionTargetStore.SaveAsync(App.Database.Db, ProfileId, manual, App.Clock);

        await EditorAsync(cut);
        Assert.Equal("123.4", cut.Find("#target-manual-protein").GetAttribute("value"));
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Targets saved", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Equal(manual, (await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId))!.ToSettings());

        // The formula view of the same row saves unchanged too, the boundary ratio included.
        await ClickAsync(() => cut.FindAll("[role=radio]").Single(e => e.TextContent.Contains("From my profile", StringComparison.Ordinal)));
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".error")), Wait);
        var formula = (await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId))!.ToSettings()!;
        Assert.Equal((TargetMethod.Formula, 4.5359237, 27.55), (formula.Method, formula.ProteinRatio, formula.FatPercent));
    }

    [Fact]
    public async Task An_invalid_hidden_formula_field_does_not_block_a_manual_target()
    {
        var cut = await FoodAsync();
        await EditorAsync(cut);
        await TypeAsync(cut, "#target-fat-percent", "abc");
        await ClickAsync(() => cut.FindAll("[role=radio]").Single(e => e.TextContent.Contains("My own numbers", StringComparison.Ordinal)));
        await TypeAsync(cut, "#target-manual-energy", "21000");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("at most 20,000 kcal", cut.Find(".error").TextContent, StringComparison.Ordinal), Wait);
        await TypeAsync(cut, "#target-manual-energy", "2200");
        await SubmitAsync(cut, "#targets-form");
        cut.WaitForAssertion(() => Assert.Contains("Targets saved", cut.Markup, StringComparison.Ordinal), Wait);

        // The formula settings the form was filled with are kept for switching back.
        var saved = (await NutritionTargetStore.GetAsync(App.Database.Db, ProfileId))!.ToSettings()!;
        Assert.Equal((TargetMethod.Manual, 2200.0, TargetSettings.DefaultFatPercent), (saved.Method, saved.ManualEnergyKcal!.Value, saved.FatPercent));
    }
}
