using Bunit;
using Health360.Domain.Body;
using Health360.LocalDb;
using Health360.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>The Body tab: weigh-ins and the trend, measurements, the Navy estimate and deleting.</summary>
public sealed class BodyTests : AppTestContext
{
    private const string Metric = "{\"bodyWeight\":\"kg\",\"circumference\":\"cm\"}";

    private async Task<IRenderedComponent<Routes>> BodyAsync(bool metrics = true, string unitPrefs = Metric, bool seedProfile = true)
    {
        if (seedProfile)
        {
            await SignedInAndOnboardedAsync(unitPrefs);
        }

        if (metrics)
        {
            await App.Database.SeedMetricsAsync();
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("body");
        cut.WaitForAssertion(() => Assert.Equal("Body", cut.Find("h1").TextContent), Wait);
        return cut;
    }

    [Fact]
    public async Task AWeighInIsSavedAndShowsInTheTrendAndTheList()
    {
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => cut.Find("#add-weight-value"), Wait);
        Assert.Contains("No weigh-ins yet", cut.Markup, StringComparison.Ordinal);

        await TypeAsync(cut, "#add-weight-value", "80,4");
        await SubmitAsync(cut, "#add-weight-value");

        cut.WaitForAssertion(() => Assert.Contains("80.4 kg", cut.Find(".headline").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Weight saved.", cut.Markup, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(cut.Find("#add-weight-value").GetAttribute("value")));
        Assert.NotNull(cut.Find("svg[role=img]"));
        Assert.Contains("One weigh-in on 30 Oct 2026", cut.Find("desc").TextContent, StringComparison.Ordinal);
        Assert.Contains("80.4 kg", cut.Find(".measurement-list").TextContent, StringComparison.Ordinal);
        var row = Assert.Single(await Measurements.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("weight", 80.4, "kg", "2026-10-30", "manual"), (row.MetricCode, row.Value, row.Unit, row.LocalDate, row.Source));
        var tx = await App.Database.Db.GetNextCrudTransaction();
        Assert.Equal(("measurements", row.Id), (tx!.Crud.Single().Table, tx.Crud.Single().Id));
    }

    [Fact]
    public async Task TheWeighInUnitFollowsThePreferenceAndTheTrendShowsInIt()
    {
        var cut = await BodyAsync(unitPrefs: "{\"bodyWeight\":\"lb\"}");
        cut.WaitForAssertion(() => cut.Find("#add-weight-value"), Wait);

        Assert.Equal("true", cut.Find("[role=radio][aria-checked=true]").GetAttribute("aria-checked"));
        Assert.Equal("lb", cut.Find("[role=radio][aria-checked=true]").TextContent.Trim());
        await TypeAsync(cut, "#add-weight-value", "176.5");
        await SubmitAsync(cut, "#add-weight-value");

        cut.WaitForAssertion(() => Assert.Contains("176.5 lb", cut.Find(".headline").TextContent, StringComparison.Ordinal), Wait);
        var row = Assert.Single(await Measurements.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal((176.5, "lb"), (row.Value, row.Unit));
    }

    [Theory]
    [InlineData("-3", "The value can't be negative.")]
    [InlineData("heavy", "Enter a number.")]
    [InlineData("", "Enter a number.")]
    public async Task AnInvalidValueSaysWhyAndSavesNothing(string typed, string message)
    {
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => cut.Find("#add-weight-value"), Wait);

        await TypeAsync(cut, "#add-weight-value", typed);
        await SubmitAsync(cut, "#add-weight-value");

        cut.WaitForAssertion(() => Assert.Equal(message, cut.Find("[role=alert]").TextContent), Wait);
        Assert.Empty(await Measurements.ListAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task BloodPressureSavesBothNumbersTogether()
    {
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => cut.Find("#add-body-metric"), Wait);

        await ChangeAsync(cut, "#add-body-metric", "bp");
        cut.WaitForAssertion(() => cut.Find("#add-body-diastolic"), Wait);
        await TypeAsync(cut, "#add-body-value", "118");
        await TypeAsync(cut, "#add-body-diastolic", "76");
        await SubmitAsync(cut, "#add-body-value");

        cut.WaitForAssertion(() => Assert.Contains("Blood pressure saved.", cut.Markup, StringComparison.Ordinal), Wait);
        var rows = await Measurements.ListAsync(App.Database.Db, ProfileId);
        Assert.Equal(["bp_diastolic", "bp_systolic"], rows.Select(r => r.MetricCode).Order());
        Assert.Single(rows.Select(r => r.GroupId).Distinct());
    }

    [Fact]
    public async Task TheNavyEstimateShowsAsAnEstimateWithItsRange()
    {
        await SignedInAndOnboardedAsync(Metric);
        await App.Database.SeedDetailsAsync(ProfileId, "male", "1996-05-01", 180, "moderate");
        var cut = await BodyAsync(seedProfile: false);
        cut.WaitForAssertion(() => Assert.Contains("Add your neck and your waist at the navel", cut.Markup, StringComparison.Ordinal), Wait);

        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000a1", ProfileId, "circ_neck", 38, "cm", 38, "2026-10-29");
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000a2", ProfileId, "circ_waist_navel", 90, "cm", 90, "2026-10-29");

        var expected = NavyMethod.Male(180, 38, 90)!;
        cut.WaitForAssertion(() =>
        {
            var card = cut.Find("[aria-labelledby=navy-title]").TextContent;
            Assert.Contains($"{MetricDisplay.Number(expected.PercentFat, 1)}%", card, StringComparison.Ordinal);
            Assert.Contains("Estimate", card, StringComparison.Ordinal);
            Assert.Contains("± 3.52", card, StringComparison.Ordinal);
        }, Wait);
    }

    [Fact]
    public async Task DeletingAsksFirstThenRemovesTheValue()
    {
        await SignedInAndOnboardedAsync(Metric);
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000b1", ProfileId, "weight", 80, "kg", 80, "2026-10-29");
        var cut = await BodyAsync(seedProfile: false);
        cut.WaitForAssertion(() => cut.Find(".measurement-list"), Wait);

        await ClickAsync(cut, ".measurement > button");
        cut.WaitForAssertion(() => Assert.Contains("Delete this value?", cut.Find("[role=alertdialog]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Single(await Measurements.ListAsync(App.Database.Db, ProfileId));
        await ClickAsync(cut, "[role=alertdialog] button.danger");

        cut.WaitForAssertion(() => Assert.Contains("Nothing entered yet.", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Empty(await Measurements.ListAsync(App.Database.Db, ProfileId));
        var tx = await App.Database.Db.GetNextCrudTransaction();
        Assert.Equal("DELETE", tx!.Crud.Single().Op.ToString().ToUpperInvariant());
    }

    [Fact]
    public async Task BeforeTheMetricListHasSyncedTheFormsSaySo()
    {
        var cut = await BodyAsync(metrics: false);

        cut.WaitForAssertion(() => Assert.Contains("hasn't reached this device yet", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("#add-weight-value"));
    }
}
