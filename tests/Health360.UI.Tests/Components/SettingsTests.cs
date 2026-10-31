using Bunit;
using Health360.Contracts.Profiles;
using Health360.Domain.Units;
using Health360.UI.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class SettingsTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> SettingsAsync(string unitPrefs = "{}", string? details = null)
    {
        await SignedInAndOnboardedAsync(unitPrefs);
        if (details is not null)
        {
            await App.Database.Db.Execute($"UPDATE profiles SET {details} WHERE id = ?", [ProfileId]);
        }

        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("settings");
        cut.WaitForAssertion(() => Assert.Equal("Settings", cut.Find("h1").TextContent), Wait);
        return cut;
    }

    private async Task QuarantineAsync(string id, string code) =>
        await App.Database.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code, error_message, failed_at) VALUES (?, 'notes', 'n1', 'PUT', '{}', ?, 'duplicate key', '2026-10-30T11:00:00.000Z')",
            [id, code]);

    [Fact]
    public async Task ChangingAUnitSavesItLocally()
    {
        var cut = await SettingsAsync();

        await ClickAsync(() => cut.FindAll("[aria-labelledby='settings-unit-temperature'] [role=radio]").Single(b => b.TextContent.Trim() == "°F"));

        cut.WaitForAssertion(async () =>
        {
            var row = await App.Database.Db.Get<Saved>("SELECT unit_prefs AS UnitPrefs FROM profiles WHERE id = ?", [ProfileId]);
            Assert.Equal(TemperatureUnit.Fahrenheit, UnitPrefsJson.Parse(row.UnitPrefs, UnitPrefs.Metric).Temperature);
        }, Wait);
    }

    private Task<Details> DetailsAsync() => App.Database.Db.Get<Details>(
        "SELECT birth_date AS BirthDate, sex_for_calcs AS SexForCalcs, height AS Height, height_unit AS HeightUnit, height_cm AS HeightCm, activity_level AS ActivityLevel FROM profiles WHERE id = ?",
        [ProfileId]);

    private static readonly string Metric = UnitPrefsJson.Serialize(UnitPrefs.Metric);

    [Fact]
    public async Task EditedDetailsSaveLocally()
    {
        var cut = await SettingsAsync(Metric);

        await ChangeAsync(cut, "#details-birth", "1985-12-01");
        await ChangeAsync(cut, "#details-height-cm", "172,5");
        await ClickAsync(() => cut.FindAll("[aria-labelledby='details-sex-label'] [role=radio]").Single(b => b.TextContent.Trim() == "Male"));
        await ClickAsync(() => cut.FindAll("[aria-labelledby='details-activity-label'] [role=radio]").Single(b => b.TextContent.Trim() == "Active"));
        await SubmitAsync(cut, "#details-birth");

        cut.WaitForAssertion(() => Assert.Contains("Saved.", cut.Markup, StringComparison.Ordinal), Wait);
        var saved = await DetailsAsync();
        Assert.Equal(("1985-12-01", "male", 172.5, "cm", 172.5, "active"),
            (saved.BirthDate, saved.SexForCalcs, saved.Height, saved.HeightUnit, saved.HeightCm, saved.ActivityLevel));
    }

    [Fact]
    public async Task SavedDisappearsOnceAFieldIsEditedAgain()
    {
        var cut = await SettingsAsync(Metric);
        cut.WaitForAssertion(() => cut.Find("#details-birth"), Wait);
        await ChangeAsync(cut, "#details-birth", "1985-12-01");
        await SubmitAsync(cut, "#details-birth");
        cut.WaitForAssertion(() => Assert.Equal("Saved.", cut.Find("#details-birth").Closest("form")!.QuerySelector("[role=status]")!.TextContent), Wait);

        await ChangeAsync(cut, "#details-birth", "1985-12-02");

        cut.WaitForAssertion(() => Assert.Equal("", cut.Find("#details-birth").Closest("form")!.QuerySelector("[role=status]")!.TextContent), Wait);
    }

    [Fact]
    public async Task ChoosingTheSelectedOptionAgainClearsIt()
    {
        var cut = await SettingsAsync(Metric, "sex_for_calcs = 'female', activity_level = 'light'");
        cut.WaitForAssertion(() => cut.Find("#details-birth"), Wait);

        await ClickAsync(() => cut.FindAll("[aria-labelledby='details-activity-label'] [role=radio]").Single(b => b.TextContent.Trim() == "Light"));
        await SubmitAsync(cut, "#details-birth");

        cut.WaitForAssertion(() => Assert.Contains("Saved.", cut.Markup, StringComparison.Ordinal), Wait);
        var saved = await DetailsAsync();
        Assert.Equal(("female", null), (saved.SexForCalcs, saved.ActivityLevel));
    }

    [Fact]
    public async Task AHeightLeftAsShownIsNotRewrittenThroughItsRoundedDisplay()
    {
        // Entered as 5 ft 5 in; shown in cm, rounded.
        var cut = await SettingsAsync(Metric, "height = 65, height_unit = 'in', height_cm = 165.1");
        cut.WaitForAssertion(() => Assert.Equal("165.1", cut.Find("#details-height-cm").GetAttribute("value")), Wait);

        await ChangeAsync(cut, "#details-birth", "1990-04-02");
        await SubmitAsync(cut, "#details-birth");

        cut.WaitForAssertion(() => Assert.Contains("Saved.", cut.Markup, StringComparison.Ordinal), Wait);
        var saved = await DetailsAsync();
        Assert.Equal((65.0, "in", "1990-04-02"), (saved.Height, saved.HeightUnit, saved.BirthDate));
    }

    [Fact]
    public async Task FeetAndInchesShowAsEntered()
    {
        var cut = await SettingsAsync(UnitPrefsJson.Serialize(UnitPrefs.DefaultFor("en-US")), "height = 70.5, height_unit = 'in', height_cm = 179.07");

        cut.WaitForAssertion(() => Assert.Equal("5", cut.Find("[aria-label=Feet]").GetAttribute("value")), Wait);
        Assert.Equal("10.5", cut.Find("[aria-label=Inches]").GetAttribute("value"));
    }

    [Fact]
    public async Task AnInvalidBirthDateIsShownAndNothingIsSaved()
    {
        var cut = await SettingsAsync(Metric, "birth_date = '1990-04-02'");
        cut.WaitForAssertion(() => cut.Find("#details-birth"), Wait);

        await ChangeAsync(cut, "#details-birth", "02/04/1990");
        await SubmitAsync(cut, "#details-birth");

        cut.WaitForAssertion(() => Assert.Contains("Birth date must look like 1990-04-02.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("1990-04-02", (await DetailsAsync()).BirthDate);
    }

    [Fact]
    public async Task AnImplausibleHeightIsShownAndNothingIsSaved()
    {
        var cut = await SettingsAsync(Metric);
        cut.WaitForAssertion(() => cut.Find("#details-height-cm"), Wait);

        await ChangeAsync(cut, "#details-height-cm", "17");
        await SubmitAsync(cut, "#details-birth");

        cut.WaitForAssertion(() => Assert.Contains("That height looks wrong.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Null((await DetailsAsync()).Height);
    }

    [Fact]
    public async Task RejectedWritesAreListedWithTheirReasonAndCanBeDiscarded()
    {
        var cut = await SettingsAsync();
        Assert.Empty(cut.FindAll(".failed"));

        await QuarantineAsync("crud-7", "23505");
        cut.WaitForAssertion(() => Assert.Contains("PUT notes · 23505", cut.Find(".failed").TextContent, StringComparison.Ordinal), Wait);
        cut.WaitForAssertion(() => Assert.Contains("1 rejected", cut.Find(".badge").TextContent, StringComparison.Ordinal), Wait);

        await ClickAsync(() => cut.FindAll(".failed button").Single(b => b.TextContent == "Discard"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".failed")), Wait);
    }

    [Fact]
    public async Task RetryWhileOfflineSaysSoAndKeepsTheEntry()
    {
        var cut = await SettingsAsync();
        await QuarantineAsync("crud-7", "23505");
        cut.WaitForAssertion(() => cut.Find(".failed"), Wait);

        await ClickAsync(() => cut.FindAll(".failed button").Single(b => b.TextContent == "Retry"));

        cut.WaitForAssertion(() => Assert.Contains("Retry needs a connection to the server.", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Single(cut.FindAll(".failed"));
    }

    [Fact]
    public async Task SignOutWithUnsyncedChangesAsksFirst()
    {
        var cut = await SettingsAsync();
        App.Local.Unsynced = new UnsyncedChanges(Pending: 1, Rejected: 2);

        await ClickAsync(() => cut.FindAll("button").Single(b => b.TextContent == "Sign out"));

        cut.WaitForAssertion(() => Assert.Contains("1 change not synced yet and 2 changes the server rejected", cut.Find("[role=alertdialog]").TextContent, StringComparison.Ordinal), Wait);
        Assert.DoesNotContain("clear", App.Local.Calls);

        await ClickAsync(() => cut.FindAll("button").Single(b => b.TextContent == "Stay signed in"));
        Assert.Empty(cut.FindAll("[role=alertdialog]"));
        Assert.Equal(AuthStatus.SignedIn, Services.GetRequiredService<AuthService>().Status);
    }

    [Fact]
    public async Task ConfirmedSignOutClearsTheDeviceAndShowsSignIn()
    {
        var cut = await SettingsAsync();
        App.Local.Unsynced = new UnsyncedChanges(Pending: 1, Rejected: 0);
        await ClickAsync(() => cut.FindAll("button").Single(b => b.TextContent == "Sign out"));
        cut.WaitForAssertion(() => cut.Find("[role=alertdialog]"), Wait);

        await ClickAsync(cut, "[role=alertdialog] button.danger");

        cut.WaitForAssertion(() => cut.Find("input#email"), Wait);
        Assert.Equal("clear", App.Local.Calls[^1]);
        Assert.Null(App.Store.Saved);
    }

    [Fact]
    public async Task SignOutOfflineSaysSoAndStaysSignedIn()
    {
        var cut = await SettingsAsync();
        App.Auth.Respond = _ => throw new HttpRequestException("offline");

        await ClickAsync(() => cut.FindAll("button").Single(b => b.TextContent == "Sign out"));

        cut.WaitForAssertion(() => Assert.Equal("Couldn't sign out. Check your connection and try again.", cut.Find("[role=alert]").TextContent), Wait);
        Assert.Equal("Settings", cut.Find("h1").TextContent);
    }

    public sealed class Details
    {
        public string? BirthDate { get; set; }

        public string? SexForCalcs { get; set; }

        public double? Height { get; set; }

        public string? HeightUnit { get; set; }

        public double? HeightCm { get; set; }

        public string? ActivityLevel { get; set; }
    }

    public sealed class Saved
    {
        public string? UnitPrefs { get; set; }
    }
}
