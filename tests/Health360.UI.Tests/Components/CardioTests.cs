using Bunit;
using Health360.LocalDb;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class CardioTests : AppTestContext
{
    private DateOnly Today => DateOnly.FromDateTime(App.Clock.Now.DateTime);

    private async Task<IRenderedComponent<Routes>> CardioAsync(bool medication = false, bool weight = true)
    {
        await SignedInAndOnboardedAsync("{\"distance\":\"mi\",\"pace\":\"min_per_mi\"}");
        await App.Database.SeedDetailsAsync(ProfileId, "male", "1996-05-01", 180, "moderate");
        await App.Database.SeedMetricsAsync();
        if (weight)
        {
            await App.Database.SeedMeasurementAsync("0192f0c0-0000-7000-8000-0000000000c1", ProfileId, "weight", 80, "kg", 80, "2026-10-29");
        }

        if (medication)
        {
            await Profiles.SetHeartRateLoweringMedicationAsync(App.Database.Db, ProfileId, true);
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("a[href='train/cardio']"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo(cut.Find("a[href='train/cardio']").GetAttribute("href")!);
        cut.WaitForAssertion(() => cut.Find("#save-cardio"), Wait);
        return cut;
    }

    [Fact]
    public async Task Manual_cardio_previews_hr_energy_and_saves_offline_with_weekly_totals()
    {
        var cut = await CardioAsync();
        Assert.Equal("mi", cut.Find("select[aria-label='Distance unit']").GetAttribute("value"));
        Assert.Contains("Friel", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal);
        await ChangeAsync(cut, "#cardio-duration", "30");
        await ChangeAsync(cut, "#cardio-average", "150");
        await ChangeAsync(cut, "#cardio-max", "185");
        await ChangeAsync(cut, "#cardio-distance", "3");
        await ClickAsync(cut, "#preview-cardio");

        Assert.Contains("Keytel, average HR", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        Assert.Contains("Not added to your calorie target", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("min/mi", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => cut.Find("[aria-labelledby=cardio-week-title] details"), Wait);
        var saved = Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
        Assert.Equal((150.0, 185.0, 3.0, "mi"),
            (saved.Estimate!.Entry.AverageHr, saved.Estimate.Entry.MaxHr, saved.Estimate.Entry.Distance, saved.Estimate.Entry.DistanceUnit));
        Assert.True(await App.Database.Db.Get<long>("SELECT count(*) FROM ps_crud", []) > 0);
        Assert.Null(await WorkoutLog.ActiveSessionAsync(App.Database.Db, ProfileId));
    }

    [Fact]
    public async Task Watch_totals_must_cover_the_whole_session_before_saving()
    {
        var cut = await CardioAsync();
        await ChangeAsync(cut, "#cardio-duration", "30");
        for (var i = 1; i <= 5; i++)
        {
            await ChangeAsync(cut, $"input[aria-label='Zone {i} minutes']", i == 2 ? "20" : "0");
        }

        await SubmitAsync(cut);
        Assert.Contains("add up to the session duration", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Empty(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));

        await ChangeAsync(cut, "input[aria-label='Zone 3 minutes']", "10");
        await ClickAsync(cut, "#preview-cardio");
        Assert.Contains("Keytel, time in zones", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Medication_suppresses_age_based_zones_and_hr_energy()
    {
        var cut = await CardioAsync(medication: true);
        Assert.Contains("No HR zones available", cut.Markup, StringComparison.Ordinal);
        await ChangeAsync(cut, "#cardio-duration", "30");
        await ChangeAsync(cut, "#cardio-average", "150");
        await ClickAsync(cut, "#preview-cardio");

        Assert.Contains("2024 Compendium METs", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        Assert.Contains("210–390 kcal", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        Assert.Contains("never the HR equation", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_weight_is_explained_without_blocking_a_session_log()
    {
        var cut = await CardioAsync(weight: false);
        await ChangeAsync(cut, "#cardio-duration", "30");
        await ClickAsync(cut, "#preview-cardio");
        Assert.Contains("Energy estimate unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Enter a body weight", cut.Markup, StringComparison.Ordinal);
        await SubmitAsync(cut);
        cut.WaitForAssertion(() => cut.Find("[aria-labelledby=cardio-week-title] details"), Wait);

        Assert.Null(Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today)).Estimate!.Kcal);
    }

    [Fact]
    public async Task Invalid_optional_numbers_are_rejected_instead_of_becoming_missing_inputs()
    {
        var cut = await CardioAsync();
        await ChangeAsync(cut, "#cardio-duration", "30");
        await ChangeAsync(cut, "#cardio-average", "oops");
        await SubmitAsync(cut);

        Assert.Contains("Heart rate must be", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".cardio-estimate"));
    }
}
