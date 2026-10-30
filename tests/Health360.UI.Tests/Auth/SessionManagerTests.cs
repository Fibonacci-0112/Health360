using System.Net;
using Health360.UI.Auth;
using Health360.UI.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.UI.Tests.Auth;

public sealed class SessionManagerTests : IDisposable
{
    private readonly FakeAuth auth = new();

    public void Dispose() => auth.Dispose();
    private readonly TestClock clock = new();
    private readonly MemorySessionStore store = new();

    private SessionManager Sessions() =>
        new(new SupabaseAuthClient(auth.Client(), FakeAuth.ApiKey, clock), store, clock, NullLogger<SessionManager>.Instance);

    private AuthSession Session(TimeSpan expiresIn) => new(auth.UserId, null, "access-0", "refresh-0", clock.Now + expiresIn);

    [Fact]
    public async Task NoSessionMeansNoToken()
    {
        using var sessions = Sessions();

        Assert.Null(await sessions.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFreshTokenIsHandedOutWithoutAsking()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.FromMinutes(30)));

        var token = await sessions.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal((auth.UserId, "access-0"), (token!.UserId, token.AccessToken));
        Assert.Empty(auth.Requests);
    }

    [Fact]
    public async Task ATokenAboutToExpireIsRefreshedAndTheRotatedTokenSaved()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.FromSeconds(30)));

        var token = await sessions.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-1", token!.AccessToken);
        Assert.Equal("refresh-1", store.Saved!.RefreshToken);
        Assert.Equal("refresh-1", sessions.Current!.RefreshToken);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRefresh()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.Zero));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => sessions.GetTokenAsync(TestContext.Current.CancellationToken)));

        // Auth rotates refresh tokens: a second refresh with refresh-0 would be refused.
        Assert.Single(auth.Requests);
        Assert.All(tokens, t => Assert.Equal("access-1", t!.AccessToken));
    }

    [Fact]
    public async Task ARefusedRefreshEndsTheSessionAndSaysWhose()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.Zero));
        auth.Respond = _ => FakeAuth.Error(HttpStatusCode.BadRequest, "refresh_token_not_found", "Invalid Refresh Token");
        string? expired = null;
        sessions.Expired += user => expired = user;

        Assert.Null(await sessions.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.Equal(auth.UserId, expired);
        Assert.Null(sessions.Current);
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task OfflineKeepsTheSessionAndItsStillValidToken()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.FromSeconds(30)));
        auth.Respond = _ => throw new HttpRequestException("offline");

        var token = await sessions.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("access-0", token!.AccessToken);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task OfflineWithAnExpiredTokenGivesNoTokenButKeepsTheSession()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.FromMinutes(-5)));
        auth.Respond = _ => throw new HttpRequestException("offline");
        var expired = false;
        sessions.Expired += _ => expired = true;

        Assert.Null(await sessions.GetTokenAsync(TestContext.Current.CancellationToken));

        // Sync and uploads retry later; the user is still signed in once back online.
        Assert.False(expired);
        Assert.NotNull(sessions.Current);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task RestoresTheSavedSession()
    {
        store.Saved = Session(TimeSpan.FromHours(1));
        using var sessions = Sessions();

        Assert.Equal(store.Saved, await sessions.RestoreAsync());
        Assert.Equal(store.Saved, sessions.Current);
    }

    [Fact]
    public async Task EndForgetsTheSessionOnTheDevice()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.FromHours(1)));

        await sessions.EndAsync();

        Assert.Null(sessions.Current);
        Assert.Null(store.Saved);
        Assert.Null(await sessions.GetTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGatewayErrorDuringRefreshKeepsTheSession()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.Zero));
        auth.Respond = _ => FakeAuth.Json(HttpStatusCode.Forbidden, """{"message":"You cannot consume this service"}""");
        var expired = false;
        sessions.Expired += _ => expired = true;

        Assert.Null(await sessions.GetTokenAsync(TestContext.Current.CancellationToken));

        Assert.False(expired);
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task ARefreshTheCallerAbandonsStillSavesTheRotatedToken()
    {
        using var sessions = Sessions();
        await sessions.StartAsync(Session(TimeSpan.Zero));
        using var caller = new CancellationTokenSource();
        var sent = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        auth.Respond = r =>
        {
            sent.TrySetResult();
            release.Task.Wait(TestContext.Current.CancellationToken);
            return null;
        };

        var getting = Task.Run(() => sessions.GetTokenAsync(caller.Token), TestContext.Current.CancellationToken);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await caller.CancelAsync();
        release.SetResult();
        await getting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Auth has rotated the refresh token; losing the new one would end the session at the next refresh.
        Assert.Equal("refresh-1", store.Saved!.RefreshToken);
    }
}
