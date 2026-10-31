using Bunit;
using Health360.LocalDb;
using Health360.UI.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>
/// The physiology profile screen, through the UI and a real local database: the step 1.1 exit criteria that a
/// lab RMR changes the maintenance estimate at once (and deleting it falls back), and that a measured max HR
/// changes the zones (and deleting it falls back).
/// </summary>
public sealed class PhysiologyTests : AppTestContext
{
    // Male, 30 on the test clock's 2026-10-30, 180 cm, moderately active (PAL 1.85), weighing 80 kg:
    // Mifflin-St Jeor 1781.6 kcal → maintenance 3295.96. Tanaka 208 − 0.7 × 30 = 187 bpm.
    private async Task<IRenderedComponent<Routes>> PhysiologyAsync()
    {
        await SignedInAndOnboardedAsync("{\"energy\":\"kcal\",\"pace\":\"min_per_km\"}");
        await App.Database.SeedDetailsAsync(ProfileId, "male", "1996-05-01", 180, "moderate");
        await App.Database.SeedMetricsAsync();
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000c1", ProfileId, "weight", 80, "kg", 80, "2026-10-29");
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("body/physiology");
        cut.WaitForAssertion(() => Assert.Equal("Physiology profile", cut.Find("h1").TextContent), Wait);
        return cut;
    }

    private static string Values(IRenderedComponent<Routes> cut) => cut.Find(".values").TextContent;

    [Fact]
    public async Task ALabRmrChangesTheMaintenanceEstimateAtOnceAndDeletingItFallsBack()
    {
        var cut = await PhysiologyAsync();
        cut.WaitForAssertion(() => Assert.Contains("3,296 kcal/day", Values(cut), StringComparison.Ordinal), Wait);
        Assert.Contains("1,782 kcal/day", Values(cut), StringComparison.Ordinal);
        Assert.Contains("Mifflin-St Jeor equation", Values(cut), StringComparison.Ordinal);

        // RMR is the form's first metric, and lab its default source.
        await TypeAsync(cut, "#add-physiology-value", "1900");
        await SubmitAsync(cut, "#add-physiology-value");

        cut.WaitForAssertion(() => Assert.Contains("3,515 kcal/day", Values(cut), StringComparison.Ordinal), Wait);
        Assert.Contains("RMR 1,900 kcal (Lab, 30 Oct 2026) × 1.850", Values(cut), StringComparison.Ordinal);
        var row = Assert.Single(await Measurements.ListAsync(App.Database.Db, ProfileId), r => r.MetricCode == "rmr");
        Assert.Equal(("lab", 1900.0, "kcal"), (row.Source, row.Value, row.Unit));

        await ClickAsync(() => cut.FindAll(".measurement").Single(e => e.TextContent.Contains("Resting metabolic rate", StringComparison.Ordinal))
            .QuerySelector("button")!);
        await ClickAsync(cut, "[role=alertdialog] button.danger");

        cut.WaitForAssertion(() => Assert.Contains("3,296 kcal/day", Values(cut), StringComparison.Ordinal), Wait);
        Assert.DoesNotContain(await Measurements.ListAsync(App.Database.Db, ProfileId), r => r.MetricCode == "rmr");
    }

    [Fact]
    public async Task AMeasuredMaxHrChangesTheZonesAtOnceAndDeletingItFallsBack()
    {
        var cut = await PhysiologyAsync();
        // Friel % of max, run: zone 2 starts at 73% of 187 = 136.51.
        cut.WaitForAssertion(() => Assert.Contains("137 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("The max heart rate is an estimate (Tanaka", cut.Markup, StringComparison.Ordinal);

        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000c2", ProfileId, "max_hr", 200, "bpm", 200, "2026-10-01", "field_test");

        // 73% of 200 = 146.
        cut.WaitForAssertion(() => Assert.Contains("146 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("is an estimate", cut.Find("[aria-labelledby=zones-title]").TextContent, StringComparison.Ordinal);
        Assert.Contains("200 bpm", Values(cut), StringComparison.Ordinal);

        await Measurements.DeleteAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000c2");

        cut.WaitForAssertion(() => Assert.Contains("137 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task OtherSportsUseGarbersBands()
    {
        var cut = await PhysiologyAsync();
        cut.WaitForAssertion(() => cut.Find("table.zones"), Wait);

        await ClickAsync(() => cut.FindAll("[aria-labelledby=zones-sport] [role=radio]").Single(e => e.TextContent.Trim() == "Swim"));

        // Garber %HRmax: light starts at 57% of 187 = 106.59.
        cut.WaitForAssertion(() => Assert.Contains("Light", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("107 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMedicationSettingTurnsOffAgePredictedMaxHrAndItsZones()
    {
        var cut = await PhysiologyAsync();
        cut.WaitForAssertion(() => cut.Find("table.zones"), Wait);

        await CheckAsync(cut, "input[type=checkbox]", true);

        cut.WaitForAssertion(() => Assert.Contains("Not estimated while the medication setting", Values(cut), StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("table.zones"));
        Assert.Contains("Enter a measured max heart rate", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, await App.Database.Db.Get<long>("SELECT takes_hr_lowering_medication FROM profiles WHERE id = ?", [ProfileId]));
    }

    [Fact]
    public async Task AnsweringThePaRQuestionsGivesAJacksonVo2MaxEstimate()
    {
        var cut = await PhysiologyAsync();
        cut.WaitForAssertion(() => Assert.Contains("answer the activity questions below", Values(cut), StringComparison.Ordinal), Wait);
        Assert.True(cut.Find("[aria-labelledby=activity-title] button.primary").HasAttribute("disabled"));

        await ClickAsync(() => cut.FindAll("[aria-labelledby=activity-q1] [role=radio]").Single(e => e.TextContent.Trim() == "Heavy exercise"));
        await ClickAsync(() => cut.FindAll("[aria-labelledby=activity-q2] [role=radio]").Single(e => e.TextContent.Trim() == "1 to 3 hours per week"));
        await ClickAsync(cut, "[aria-labelledby=activity-title] button.primary");

        // Jackson BMI model, male, PA-R 6, 30 y, BMI 80 / 1.8² (docs/calculations.md).
        var expected = Health360.Domain.Physiology.Vo2Max.JacksonBmi(Health360.Domain.Physiology.Sex.Male, 6, 30, 80 / (1.8 * 1.8))!;
        cut.WaitForAssertion(() => Assert.Contains("Jackson (BMI) equation", Values(cut), StringComparison.Ordinal), Wait);
        Assert.Contains(MetricDisplay.Format(expected.Value, "ml/kg/min"), Values(cut), StringComparison.Ordinal);
        Assert.Equal(6, await App.Database.Db.Get<long>("SELECT activity_rating FROM profiles WHERE id = ?", [ProfileId]));

        // A resting HR means the Jackson model no longer applies.
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000c3", ProfileId, "resting_hr", 55, "bpm", 55, "2026-10-29");
        cut.WaitForAssertion(() => Assert.DoesNotContain("Jackson", Values(cut), StringComparison.Ordinal), Wait);

        await Measurements.DeleteAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000c3");
        cut.WaitForAssertion(() => Assert.Contains("Jackson (BMI) equation", Values(cut), StringComparison.Ordinal), Wait);

        await ClickAsync(() => cut.FindAll("[aria-labelledby=activity-title] button").Single(e => e.TextContent.Trim() == "Clear my rating"));
        cut.WaitForAssertion(() => Assert.Contains("answer the activity questions below", Values(cut), StringComparison.Ordinal), Wait);
        Assert.Null(await App.Database.Db.Get<long?>("SELECT activity_rating FROM profiles WHERE id = ?", [ProfileId]));
    }

    [Fact]
    public async Task APaceIsEnteredAsMinutesAndSeconds()
    {
        var cut = await PhysiologyAsync();
        cut.WaitForAssertion(() => cut.Find("#add-physiology-metric"), Wait);

        await ChangeAsync(cut, "#add-physiology-metric", "threshold_pace");
        await TypeAsync(cut, "#add-physiology-value", "4:30");
        await SubmitAsync(cut, "#add-physiology-value");

        cut.WaitForAssertion(() => Assert.Contains("4:30 min/km", cut.Find(".measurement-list").TextContent, StringComparison.Ordinal), Wait);
        var row = Assert.Single(await Measurements.ListAsync(App.Database.Db, ProfileId), r => r.MetricCode == "threshold_pace");
        Assert.Equal((270.0, "s_per_km"), (row.Value, row.Unit));
    }

    private static AngleSharp.Dom.IElement ValueRow(IRenderedComponent<Routes> cut, string name) =>
        cut.FindAll(".value-row").Single(e => e.QuerySelector("dt")!.TextContent == name);

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Routes> cut, string label) =>
        cut.FindAll("button").Single(e => e.GetAttribute("aria-label") == label);

    [Fact]
    public async Task PinningAnOlderLabValueKeepsItInUseUntilGoingBackToTheBestValue()
    {
        var cut = await PhysiologyAsync();
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d1", ProfileId, "max_hr", 192, "bpm", 192, "2025-03-01", "lab");
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d2", ProfileId, "max_hr", 200, "bpm", 200, "2026-10-01", "lab");
        // The newest lab value wins: zone 2 starts at 73% of 200 = 146.
        cut.WaitForAssertion(() => Assert.Contains("146 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);

        await ClickAsync(() => Button(cut, "Use 192 bpm (Lab, 1 Mar 2025) for Max heart rate"));

        // 73% of 192 = 140.16.
        cut.WaitForAssertion(() => Assert.Contains("140 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Lab, 1 Mar 2025, your choice", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal);
        var pin = Assert.Single(await MetricPins.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("max_hr", "measurement", "0192f0c0-0000-7000-8000-0000000000d1"), (pin.MetricCode, pin.Target, pin.MeasurementId));

        // A newer value doesn't replace a pinned one.
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d3", ProfileId, "max_hr", 205, "bpm", 205, "2026-10-29", "lab");
        cut.WaitForAssertion(() => Assert.Contains("205 bpm", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("140 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal);

        await ClickAsync(() => Button(cut, "Go back to the best value for Max heart rate"));

        // 73% of 205 = 149.65.
        cut.WaitForAssertion(() => Assert.Contains("150 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("your choice", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal);
        Assert.Empty(await MetricPins.ListAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task TheEstimateCanBePinnedOverAMeasuredValue()
    {
        var cut = await PhysiologyAsync();
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d4", ProfileId, "max_hr", 200, "bpm", 200, "2026-10-01", "field_test");
        cut.WaitForAssertion(() => Assert.Contains("146 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);

        await ClickAsync(() => Button(cut, "Use the estimate, 187 bpm, for Max heart rate"));

        cut.WaitForAssertion(() => Assert.Contains("137 bpm", cut.Find("table.zones").TextContent, StringComparison.Ordinal), Wait);
        var row = ValueRow(cut, "Max heart rate").TextContent;
        Assert.Contains("Estimate", row, StringComparison.Ordinal);
        Assert.Contains("equation, your choice", row, StringComparison.Ordinal);
        Assert.Equal("estimate", Assert.Single(await MetricPins.ListAsync(App.Database.Db, ProfileId)).Target);
    }

    [Fact]
    public async Task APinWhoseValueWasDeletedIsReportedAndCanBeCleared()
    {
        var cut = await PhysiologyAsync();
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d5", ProfileId, "max_hr", 200, "bpm", 200, "2026-10-01", "lab");
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d6", ProfileId, "max_hr", 195, "bpm", 195, "2026-09-01", "lab");
        await MetricPins.PinMeasurementAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000d6", TimeProvider.System);
        cut.WaitForAssertion(() => Assert.Contains("your choice", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal), Wait);

        // Deleted (on this device or, once synced, another): the next best is used and the user is told.
        await Measurements.DeleteAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000d6");

        cut.WaitForAssertion(() => Assert.Contains("no longer available, so the next best is used", ValueRow(cut, "Max heart rate").TextContent,
            StringComparison.Ordinal), Wait);
        Assert.Contains("200 bpm", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal);
        Assert.Single(await MetricPins.ListAsync(App.Database.Db, ProfileId));

        await ClickAsync(() => Button(cut, "Clear your choice for Max heart rate"));

        cut.WaitForAssertion(() => Assert.DoesNotContain("no longer available", ValueRow(cut, "Max heart rate").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(await MetricPins.ListAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task APinWithNothingLeftToFallBackOnIsStillReported()
    {
        // Resting HR has no estimate, so once its only value is deleted there is no value at all.
        var cut = await PhysiologyAsync();
        await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000d7", ProfileId, "resting_hr", 52, "bpm", 52, "2026-10-29");
        await MetricPins.PinMeasurementAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000d7", TimeProvider.System);
        cut.WaitForAssertion(() => Assert.Contains("your choice", ValueRow(cut, "Resting heart rate").TextContent, StringComparison.Ordinal), Wait);

        await Measurements.DeleteAsync(App.Database.Db, ProfileId, "0192f0c0-0000-7000-8000-0000000000d7");

        cut.WaitForAssertion(() => Assert.Contains("The value you chose is no longer available.", ValueRow(cut, "Resting heart rate").TextContent,
            StringComparison.Ordinal), Wait);
        Assert.Contains("Not estimated", ValueRow(cut, "Resting heart rate").TextContent, StringComparison.Ordinal);

        await ClickAsync(() => Button(cut, "Clear your choice for Resting heart rate"));

        cut.WaitForAssertion(() => Assert.DoesNotContain("no longer available", ValueRow(cut, "Resting heart rate").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(await MetricPins.ListAsync(App.Database.Db, ProfileId));
    }
}
