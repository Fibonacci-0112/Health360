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
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-average", "150");
        await TypeAsync(cut, "#cardio-max", "185");
        await TypeAsync(cut, "#cardio-distance", "3");
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
        await TypeAsync(cut, "#cardio-duration", "30");
        for (var i = 1; i <= 5; i++)
        {
            await TypeAsync(cut, $"input[aria-label='Zone {i} minutes']", i == 2 ? "20" : "0");
        }

        await SubmitAsync(cut);
        Assert.Contains("add up to the session duration", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Empty(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));

        await TypeAsync(cut, "input[aria-label='Zone 3 minutes']", "10");
        await ClickAsync(cut, "#preview-cardio");
        Assert.Contains("Keytel, time in zones", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synced_zone_insert_edit_and_delete_refresh_the_form_and_clear_stale_watch_times()
    {
        var cut = await CardioAsync();
        await App.Database.Db.Execute(
            "INSERT INTO zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from) " +
            "VALUES ('cardio-zones', ?, 'run', 'hr', 'custom', '[0,100,150]', 'bpm', '[0,100,150]', '2026-01-01')", [ProfileId]);
        cut.WaitForAssertion(() =>
            Assert.Contains("Stored HR zones", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal), Wait);
        await TypeAsync(cut, "#cardio-duration", "30");
        for (var i = 1; i <= 3; i++)
        {
            await TypeAsync(cut, $"input[aria-label='Zone {i} minutes']", i == 2 ? "30" : "0");
        }

        await ClickAsync(cut, "#preview-cardio");
        Assert.Single(cut.FindAll(".cardio-estimate"));
        await App.Database.Db.Execute(
            "UPDATE zone_sets SET boundaries = '[0,130,170]', boundaries_canonical = '[0,130,170]' WHERE id = 'cardio-zones'");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("130 to <170", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Zones changed", cut.Markup, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll(".cardio-estimate"));
            Assert.All(cut.FindAll("input[aria-label$=' minutes']"), input => Assert.True(string.IsNullOrEmpty(input.GetAttribute("value"))));
            Assert.Equal("30", cut.Find("#cardio-duration").GetAttribute("value"));
        }, Wait);

        await App.Database.Db.Execute("DELETE FROM zone_sets WHERE id = 'cardio-zones'");
        cut.WaitForAssertion(() =>
            Assert.Contains("Friel", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task Save_stops_when_the_duration_moves_watch_times_onto_another_zone_set()
    {
        var cut = await CardioAsync();
        await App.Database.Db.Execute(
            "INSERT INTO zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from) " +
            "VALUES ('cardio-zones', ?, 'run', 'hr', 'custom', '[0,100,150]', 'bpm', '[0,100,150]', ?)",
            [ProfileId, Today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)]);
        cut.WaitForAssertion(() =>
            Assert.Contains("Stored HR zones", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal), Wait);
        await CheckAsync(cut, "input[type=checkbox]", true);
        cut.WaitForAssertion(() => cut.Find("#cardio-time"), Wait);
        await ChangeAsync(cut, "#cardio-time", "00:10");
        for (var i = 1; i <= 3; i++)
        {
            await TypeAsync(cut, $"input[aria-label='Zone {i} minutes']", i == 2 ? "30" : "0");
        }

        // Thirty minutes before 00:10 starts yesterday, before the stored zones took effect.
        await TypeAsync(cut, "#cardio-duration", "30");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Enter your watch times again", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Friel", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal);
        }, Wait);
        Assert.Empty(cut.FindAll(".cardio-estimate"));
        Assert.Empty(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today.AddDays(-1), Today));
    }

    [Fact]
    public async Task A_saved_session_whose_draft_cannot_be_cleared_is_not_saved_again_on_retry()
    {
        var cut = await CardioAsync();
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-rpe", "5");
        await App.Database.Db.Execute("CREATE TEMP TRIGGER fail_draft_delete INSTEAD OF DELETE ON workout_state BEGIN SELECT RAISE(ABORT, 'disk busy'); END");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("was saved", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
        Assert.True(cut.Find("#cardio-duration").HasAttribute("disabled"));

        await App.Database.Db.Execute("DROP TRIGGER temp.fail_draft_delete");
        await SubmitAsync(cut);
        cut.WaitForAssertion(() => cut.Find("[aria-labelledby=cardio-week-title] details"), Wait);
        Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
    }

    [Fact]
    public async Task An_estimate_requested_while_the_zones_refresh_waits_for_them()
    {
        var cut = await CardioAsync();
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-average", "150");
        var activity = cut.Find("#cardio-activity").GetAttribute("value")!;

        // Start a refresh (as a synced zone change does) and tap Estimate while it may still be reading the zones. Whether
        // the read yields first varies, so the overlap is tried several times; every tap must produce the estimate.
        for (var i = 0; i < 10; i++)
        {
            var refreshing = ChangeAsync(cut, "#cardio-activity", activity);
            await ClickAsync(cut, "#preview-cardio");
            await refreshing;

            Assert.Single(cut.FindAll(".cardio-estimate"));
            Assert.DoesNotContain("Wait for your physiology", cut.Markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Two_save_taps_while_the_zones_refresh_save_one_session()
    {
        var cut = await CardioAsync();
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-rpe", "5");
        var activity = cut.Find("#cardio-activity").GetAttribute("value")!;

        // Both taps arrive while a refresh is reading the zones, so both wait in the preview.
        var refreshing = ChangeAsync(cut, "#cardio-activity", activity);
        var first = SubmitAsync(cut);
        var second = SubmitAsync(cut);
        await Task.WhenAll(refreshing, first, second);

        Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
    }

    [Fact]
    public async Task Watch_times_a_refresh_clears_while_saving_stop_the_save()
    {
        var cut = await CardioAsync();
        await App.Database.Db.Execute(
            "INSERT INTO zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from) " +
            "VALUES ('cardio-zones', ?, 'run', 'hr', 'custom', '[0,100,150]', 'bpm', '[0,100,150]', '2026-01-01')", [ProfileId]);
        cut.WaitForAssertion(() =>
            Assert.Contains("Stored HR zones", cut.Find("[aria-labelledby=cardio-zones-title]").TextContent, StringComparison.Ordinal), Wait);
        var bike = Domain.Training.CardioEnergy.Activities.First(a => a.Sport == Domain.Physiology.ZoneSport.Bike).Code;
        await TypeAsync(cut, "#cardio-duration", "30");
        for (var i = 1; i <= 3; i++)
        {
            await TypeAsync(cut, $"input[aria-label='Zone {i} minutes']", i == 2 ? "30" : "0");
        }

        // Switching sport starts a refresh that swaps the zone set and clears the entered watch times. Starting it and the
        // save tap in one dispatcher turn makes the tap land before the refresh can resume, as a tap during a slow zone
        // read does.
        await Renderer.Dispatcher.InvokeAsync(async () =>
        {
            var refreshing = cut.Find("#cardio-activity").ChangeAsync(new ChangeEventArgs { Value = bike });
            var save = cut.Find("form").SubmitAsync(EventArgs.Empty);
            await Task.WhenAll(refreshing, save);
        });

        Assert.Empty(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
        cut.WaitForAssertion(() =>
            Assert.Contains("Enter your watch times again", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task Medication_suppresses_age_based_zones_and_hr_energy()
    {
        var cut = await CardioAsync(medication: true);
        Assert.Contains("No HR zones available", cut.Markup, StringComparison.Ordinal);
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-average", "150");
        await ClickAsync(cut, "#preview-cardio");

        Assert.Contains("2024 Compendium METs", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        Assert.Contains("210–390 kcal", cut.Find(".cardio-estimate").TextContent, StringComparison.Ordinal);
        Assert.Contains("never the HR equation", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_weight_is_explained_without_blocking_a_session_log()
    {
        var cut = await CardioAsync(weight: false);
        await TypeAsync(cut, "#cardio-duration", "30");
        await ClickAsync(cut, "#preview-cardio");
        Assert.Contains("Energy estimate unavailable", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Enter a body weight", cut.Markup, StringComparison.Ordinal);
        await SubmitAsync(cut);
        cut.WaitForAssertion(() => cut.Find("[aria-labelledby=cardio-week-title] details"), Wait);

        Assert.Null(Assert.Single(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today)).Estimate!.Kcal);
    }

    [Fact]
    public async Task Unfinished_text_and_elevation_resume_when_the_page_is_recreated()
    {
        var cut = await CardioAsync();
        await TypeAsync(cut, "#cardio-duration", "45");
        await TypeAsync(cut, "#cardio-elevation", "125.5");
        await TypeAsync(cut, "#cardio-average", "unfinished");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("#start-empty"), Wait);
        navigation.NavigateTo("train/cardio");
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("45", cut.Find("#cardio-duration").GetAttribute("value"));
            Assert.Equal("125.5", cut.Find("#cardio-elevation").GetAttribute("value"));
            Assert.Equal("unfinished", cut.Find("#cardio-average").GetAttribute("value"));
        }, Wait);
        Assert.Empty(await CardioLog.SessionsAsync(App.Database.Db, ProfileId, Today, Today));
    }

    [Fact]
    public async Task Invalid_optional_numbers_are_rejected_instead_of_becoming_missing_inputs()
    {
        var cut = await CardioAsync();
        await TypeAsync(cut, "#cardio-duration", "30");
        await TypeAsync(cut, "#cardio-average", "oops");
        await SubmitAsync(cut);

        Assert.Contains("Heart rate must be", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".cardio-estimate"));
    }
}
