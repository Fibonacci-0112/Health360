using Bunit;
using Health360.Contracts.Profiles;
using Health360.Domain.Units;
using Health360.UI.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class SettingsTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> SettingsAsync()
    {
        await SignedInAndOnboardedAsync();
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

    public sealed class Saved
    {
        public string? UnitPrefs { get; set; }
    }
}
