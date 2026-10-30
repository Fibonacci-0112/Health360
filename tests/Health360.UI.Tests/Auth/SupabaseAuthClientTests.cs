using System.Net;
using Health360.UI.Auth;
using Health360.UI.Tests.Support;

namespace Health360.UI.Tests.Auth;

public sealed class SupabaseAuthClientTests : IDisposable
{
    private readonly FakeAuth auth = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => auth.Dispose();
    private readonly TestClock clock = new();

    private SupabaseAuthClient Client(string apiKey = FakeAuth.ApiKey) => new(auth.Client(), apiKey, clock);

    [Fact]
    public async Task SendsTheCodeRequestWithThePublishableKeyAndCreatesTheAccount()
    {
        await Client().SendCodeAsync("sam@example.com", Ct);

        var request = Assert.Single(auth.Requests);
        Assert.Equal(("POST", "/auth/v1/otp", FakeAuth.ApiKey, (string?)null), (request.Method, request.Path, request.ApiKey, request.Bearer));
        Assert.Equal("sam@example.com", (string?)request.Body!["email"]);
        Assert.True((bool)request.Body["create_user"]!);
    }

    [Fact]
    public async Task VerifiesTheEmailedCodeIntoASession()
    {
        var session = await Client().VerifyCodeAsync("sam@example.com", "123456", Ct);

        var request = Assert.Single(auth.Requests);
        Assert.Equal("/auth/v1/verify", request.Path);
        Assert.Equal(("email", "sam@example.com", "123456"), ((string?)request.Body!["type"], (string?)request.Body["email"], (string?)request.Body["token"]));
        Assert.Equal(auth.UserId, session.UserId);
        Assert.Equal("sam@example.com", session.Email);
        Assert.Equal(("access-1", "refresh-1"), (session.AccessToken, session.RefreshToken));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(auth.ExpiresAt), session.ExpiresAt);
    }

    [Fact]
    public async Task UsesExpiresInWhenAuthSendsNoExpiryTime()
    {
        auth.Respond = r => FakeAuth.Json(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"r","expires_in":3600,"user":{"id":"u"}}""");

        var session = await Client().VerifyCodeAsync("sam@example.com", "123456", Ct);

        Assert.Equal(clock.Now.AddHours(1), session.ExpiresAt);
        Assert.Null(session.Email);
    }

    [Fact]
    public async Task AWrongOrExpiredCodeIsRejectedWithAuthsCode()
    {
        auth.Respond = _ => FakeAuth.Error(HttpStatusCode.Forbidden, "otp_expired", "Token has expired or is invalid");

        var error = await Assert.ThrowsAsync<AuthException>(() => Client().VerifyCodeAsync("sam@example.com", "000000", Ct));

        Assert.Equal((AuthErrorKind.Rejected, "otp_expired", 403), (error.Kind, error.Code, error.Status));
        Assert.Equal("Token has expired or is invalid", error.Message);
    }

    [Fact]
    public async Task ReadsTheOlderErrorFormatToo()
    {
        auth.Respond = _ => FakeAuth.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Invalid Refresh Token"}""");

        var error = await Assert.ThrowsAsync<AuthException>(() => Client().RefreshAsync("refresh-0", Ct));

        Assert.Equal((AuthErrorKind.Rejected, "invalid_grant", "Invalid Refresh Token"), (error.Kind, error.Code, error.Message));
    }

    [Fact]
    public async Task TooManyCodesIsRateLimited()
    {
        auth.Respond = _ => FakeAuth.Error(HttpStatusCode.TooManyRequests, "over_email_send_rate_limit", "Email rate limit exceeded");

        var error = await Assert.ThrowsAsync<AuthException>(() => Client().SendCodeAsync("sam@example.com", Ct));

        Assert.Equal(AuthErrorKind.RateLimited, error.Kind);
    }

    public static TheoryData<HttpResponseMessage> Unavailable() =>
    [
        FakeAuth.Json(HttpStatusCode.BadGateway, "<html>Bad gateway</html>"),
        FakeAuth.Error(HttpStatusCode.InternalServerError, "unexpected_failure", "Database error"),
        // A captive portal answering 200 with its own page.
        FakeAuth.Json(HttpStatusCode.OK, "<html>Sign in to the Wi-Fi</html>"),
        // A gateway's 4xx without Auth's JSON body.
        FakeAuth.Json(HttpStatusCode.Forbidden, "<html>Forbidden</html>"),
    ];

    [Theory]
    [MemberData(nameof(Unavailable))]
    public async Task AnythingButAuthsAnswerIsUnavailableNotRejected(HttpResponseMessage response)
    {
        auth.Respond = _ => response;

        var error = await Assert.ThrowsAsync<AuthException>(() => Client().VerifyCodeAsync("sam@example.com", "123456", Ct));

        Assert.Equal(AuthErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task NoNetworkIsUnavailable()
    {
        auth.Respond = _ => throw new HttpRequestException("No route to host");

        var error = await Assert.ThrowsAsync<AuthException>(() => Client().SendCodeAsync("sam@example.com", Ct));

        Assert.Equal(AuthErrorKind.Unavailable, error.Kind);
    }

    [Fact]
    public async Task ABuildWithoutAKeyMakesNoRequest()
    {
        var error = await Assert.ThrowsAsync<AuthException>(() => Client(apiKey: "").SendCodeAsync("sam@example.com", Ct));

        Assert.Equal(AuthErrorKind.NotConfigured, error.Kind);
        Assert.Empty(auth.Requests);
    }

    [Fact]
    public async Task RefreshSendsTheRefreshToken()
    {
        var session = await Client().RefreshAsync("refresh-0", Ct);

        var request = Assert.Single(auth.Requests);
        Assert.Equal("/auth/v1/token?grant_type=refresh_token", request.Path);
        Assert.Equal("refresh-0", (string?)request.Body!["refresh_token"]);
        Assert.Equal("refresh-1", session.RefreshToken);
    }

    [Fact]
    public async Task SignOutEndsOnlyThisDevicesSession()
    {
        await Client().SignOutAsync("access-0", Ct);

        var request = Assert.Single(auth.Requests);
        Assert.Equal(("/auth/v1/logout?scope=local", "access-0"), (request.Path, request.Bearer));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task SignOutOfASessionAuthNoLongerHasSucceeds(HttpStatusCode status)
    {
        auth.Respond = _ => FakeAuth.Json(status, "{}");

        await Client().SignOutAsync("access-0", Ct);
    }

    [Fact]
    public async Task SignOutWhileOfflineFails()
    {
        auth.Respond = _ => throw new HttpRequestException("offline");

        await Assert.ThrowsAsync<AuthException>(() => Client().SignOutAsync("access-0", Ct));
    }

    [Fact]
    public void ASessionNeverPrintsItsTokens()
    {
        var text = new AuthSession("user", "sam@example.com", "secret-access", "secret-refresh", clock.Now).ToString();

        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sam@example.com", text, StringComparison.Ordinal);
    }
}
