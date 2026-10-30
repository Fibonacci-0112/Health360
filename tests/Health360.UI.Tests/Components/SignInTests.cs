using System.Net;
using Bunit;
using Health360.UI.Pages;
using Health360.UI.Tests.Support;

namespace Health360.UI.Tests.Components;

public sealed class SignInTests : AppTestContext
{
    private async Task<IRenderedComponent<SignIn>> AtCodeStepAsync()
    {
        var cut = Render<SignIn>();
        await TypeAsync(cut, "input#email", "sam@example.com");
        await SubmitAsync(cut);
        cut.WaitForAssertion(() => cut.Find("input#code"), Wait);
        return cut;
    }

    [Fact]
    public async Task AnInvalidEmailIsCaughtBeforeAnyRequest()
    {
        var cut = Render<SignIn>();

        await TypeAsync(cut, "input#email", "sam@example");
        await SubmitAsync(cut);

        Assert.Equal("Enter a valid email address.", cut.Find("[role=alert]").TextContent);
        Assert.Empty(App.Auth.Requests);
    }

    [Fact]
    public async Task ACodeThatIsntSixDigitsIsCaughtBeforeAnyRequest()
    {
        var cut = await AtCodeStepAsync();

        await TypeAsync(cut, "input#code", "12345");
        await SubmitAsync(cut);

        Assert.Equal("The code is 6 digits.", cut.Find("[role=alert]").TextContent);
        Assert.Single(App.Auth.Requests);
    }

    [Fact]
    public async Task AWrongCodeSaysSo()
    {
        var cut = await AtCodeStepAsync();
        App.Auth.Respond = _ => FakeAuth.Error(HttpStatusCode.Forbidden, "otp_expired", "Token has expired or is invalid");

        await TypeAsync(cut, "input#code", "000000");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.StartsWith("That code is wrong or has expired.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Null(App.Store.Saved);
    }

    [Fact]
    public async Task OfflineSaysSo()
    {
        App.Auth.Respond = _ => throw new HttpRequestException("offline");
        var cut = Render<SignIn>();

        await TypeAsync(cut, "input#email", "sam@example.com");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal("Couldn't reach the server. Check your connection and try again.", cut.Find("[role=alert]").TextContent), Wait);
        Assert.NotNull(cut.Find("input#email"));
    }

    [Fact]
    public async Task TooManyCodesSaysToWait()
    {
        App.Auth.Respond = _ => FakeAuth.Error(HttpStatusCode.TooManyRequests, "over_email_send_rate_limit", "Email rate limit exceeded");
        var cut = Render<SignIn>();

        await TypeAsync(cut, "input#email", "sam@example.com");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal("Too many attempts. Wait a minute, then try again.", cut.Find("[role=alert]").TextContent), Wait);
    }

    [Fact]
    public async Task ADifferentEmailGoesBack()
    {
        var cut = await AtCodeStepAsync();

        await ClickAsync(cut, "button.secondary");

        Assert.Equal("sam@example.com", cut.Find("input#email").GetAttribute("value"));
    }
}
