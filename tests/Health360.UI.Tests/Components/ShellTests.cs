using Bunit;
using Health360.UI.Auth;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>The layout's gates: sign-in, then the first sync, then onboarding, then the tabs.</summary>
public sealed class ShellTests : AppTestContext
{
    [Fact]
    public void SignedOutShowsSignIn()
    {
        var cut = Render<Routes>();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("input#email")), Wait);
        Assert.Empty(cut.FindAll("nav.tabs"));
    }

    [Fact]
    public async Task FirstSignInWaitsForTheProfileThenOnboards()
    {
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("input#email"), Wait);

        await TypeAsync(cut, "input#email", "sam@example.com");
        await SubmitAsync(cut);
        cut.WaitForAssertion(() => cut.Find("input#code"), Wait);
        Assert.Contains("sent to sam@example.com", cut.Markup, StringComparison.Ordinal);
        await TypeAsync(cut, "input#code", "123456");
        await SubmitAsync(cut);

        // Signed in, but the profile the server created at sign-up hasn't synced down yet.
        cut.WaitForAssertion(() => Assert.Contains("Setting up this device", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Equal($"bind {App.Auth.UserId}", App.Local.Calls[0]);

        await App.Database.SeedProfileAsync(ProfileId, App.Auth.UserId);
        cut.WaitForAssertion(() => Assert.Equal("Set up your profile", cut.Find("h1").TextContent), Wait);
    }

    [Fact]
    public async Task OnboardedUsersGetTheTabs()
    {
        await SignedInAndOnboardedAsync();

        var cut = Render<Routes>();

        cut.WaitForAssertion(() => Assert.Equal("Today", cut.Find("h1").TextContent), Wait);
        Assert.Equal(["Today", "Food", "Train", "Body", "Plan"], cut.FindAll("nav.tabs a").Select(a => a.TextContent));
        Assert.Equal("Main", cut.Find("nav.tabs").GetAttribute("aria-label"));
        Assert.Contains("Sync status: Offline", cut.Find("[role=status]").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("food", "Food", "1.3")]
    [InlineData("train", "Train", "1.4")]
    [InlineData("body", "Body", "1.5")]
    [InlineData("plan", "Plan", "1.1")]
    public async Task EachTabHasItsPage(string route, string title, string section)
    {
        await SignedInAndOnboardedAsync();
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);

        Services.GetRequiredService<NavigationManager>().NavigateTo(route);

        cut.WaitForAssertion(() => Assert.Equal(title, cut.Find("h1").TextContent), Wait);
        Assert.Contains($"FEATURES.md §{section}", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionAuthRefusesReturnsToSignIn()
    {
        await SignedInAndOnboardedAsync();
        App.Store.Saved = App.SavedSession() with { ExpiresAt = App.Clock.Now };
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        App.Auth.Respond = _ => Support.FakeAuth.Error(System.Net.HttpStatusCode.BadRequest, "refresh_token_not_found", "Invalid Refresh Token");

        await Services.GetRequiredService<SessionManager>().GetTokenAsync(CancellationToken.None);

        cut.WaitForAssertion(() => cut.Find("input#email"), Wait);
        Assert.DoesNotContain("clear", App.Local.Calls);
    }
}
