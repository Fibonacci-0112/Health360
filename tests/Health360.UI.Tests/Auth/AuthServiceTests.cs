using System.Net;
using Health360.UI.Auth;
using Health360.UI.Tests.Support;

namespace Health360.UI.Tests.Auth;

/// <summary>
/// The session lifecycle and account-switch isolation (CLAUDE.md, "Privacy, authentication, and
/// security"): the local database is bound to the user before any screen sees the session, and only a
/// sign-out the user asked for clears the device.
/// </summary>
public sealed class AuthServiceTests : IAsyncLifetime
{
    private App app = null!;

    public async ValueTask InitializeAsync() => app = await App.StartAsync();

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    [Fact]
    public async Task WithNoSavedSessionTheUserIsSignedOut()
    {
        using var auth = app.NewAuthService(out _);

        await auth.InitializeAsync();

        Assert.Equal(AuthStatus.SignedOut, auth.Status);
        Assert.Empty(app.Local.Calls);
    }

    [Fact]
    public async Task ASavedSessionIsBoundThenConnectedWithoutGoingOnline()
    {
        app.Store.Saved = app.SavedSession();
        using var auth = app.NewAuthService(out _);

        await auth.InitializeAsync();

        Assert.Equal((AuthStatus.SignedIn, app.Auth.UserId, "sam@example.com"), (auth.Status, auth.UserId, auth.Email));
        Assert.Equal([$"bind {app.Auth.UserId}", $"connect {app.Auth.UserId}"], app.Local.Calls);
        Assert.Empty(app.Auth.Requests);
    }

    [Fact]
    public async Task InitializingTwiceRestoresOnce()
    {
        app.Store.Saved = app.SavedSession();
        using var auth = app.NewAuthService(out _);

        await Task.WhenAll(auth.InitializeAsync(), auth.InitializeAsync());

        Assert.Equal(2, app.Local.Calls.Count);
    }

    [Fact]
    public async Task SigningInBindsTheDatabaseBeforeTheScreensSeeTheSession()
    {
        using var auth = app.NewAuthService(out var sessions);
        await auth.InitializeAsync();
        var seen = new List<(AuthStatus, int)>();
        auth.Changed += () => seen.Add((auth.Status, app.Local.Calls.Count));

        await auth.VerifyCodeAsync(" sam@example.com ", " 123456 ");

        Assert.Equal([(AuthStatus.SignedIn, 1)], seen);
        Assert.Equal([$"bind {app.Auth.UserId}", $"connect {app.Auth.UserId}"], app.Local.Calls);
        Assert.Equal("access-1", app.Store.Saved!.AccessToken);
        Assert.Equal("access-1", (await sessions.GetTokenAsync(CancellationToken.None))!.AccessToken);
        Assert.Equal(("sam@example.com", "123456"), ((string?)app.Auth.Requests[0].Body!["email"], (string?)app.Auth.Requests[0].Body!["token"]));
    }

    [Fact]
    public async Task AWrongCodeChangesNothing()
    {
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        app.Auth.Respond = _ => FakeAuth.Error(HttpStatusCode.Forbidden, "otp_expired", "Token has expired or is invalid");

        await Assert.ThrowsAsync<AuthException>(() => auth.VerifyCodeAsync("sam@example.com", "000000"));

        Assert.Equal(AuthStatus.SignedOut, auth.Status);
        Assert.Empty(app.Local.Calls);
        Assert.Null(app.Store.Saved);
    }

    [Fact]
    public async Task SignOutWithUnsyncedChangesAsksFirstAndDoesNothing()
    {
        app.Store.Saved = app.SavedSession();
        app.Local.Unsynced = new UnsyncedChanges(Pending: 2, Rejected: 1);
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();

        var unsynced = await auth.SignOutAsync();

        Assert.Equal(new UnsyncedChanges(2, 1), unsynced);
        Assert.Equal(AuthStatus.SignedIn, auth.Status);
        Assert.DoesNotContain("clear", app.Local.Calls);
        Assert.Empty(app.Auth.Requests);
        Assert.NotNull(app.Store.Saved);
    }

    [Fact]
    public async Task SignOutTheUserAgreedToEndsTheServerSessionAndClearsTheDevice()
    {
        app.Store.Saved = app.SavedSession();
        app.Local.Unsynced = new UnsyncedChanges(Pending: 2, Rejected: 0);
        using var auth = app.NewAuthService(out var sessions);
        await auth.InitializeAsync();

        Assert.Null(await auth.SignOutAsync(discardUnsynced: true));

        Assert.Equal(AuthStatus.SignedOut, auth.Status);
        Assert.Null(auth.UserId);
        Assert.Equal("clear", app.Local.Calls[^1]);
        Assert.Equal(("/auth/v1/logout?scope=local", "access-0"), (app.Auth.Requests.Single().Path, app.Auth.Requests.Single().Bearer));
        Assert.Null(app.Store.Saved);
        Assert.Null(await sessions.GetTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SignOutWhileOfflineKeepsEverything()
    {
        app.Store.Saved = app.SavedSession();
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        app.Auth.Respond = _ => throw new HttpRequestException("offline");

        await Assert.ThrowsAsync<AuthException>(() => auth.SignOutAsync());

        Assert.Equal(AuthStatus.SignedIn, auth.Status);
        Assert.DoesNotContain("clear", app.Local.Calls);
        Assert.NotNull(app.Store.Saved);
    }

    [Fact]
    public async Task ASessionAuthRefusesSignsOutButKeepsTheUsersData()
    {
        app.Store.Saved = app.SavedSession() with { ExpiresAt = app.Clock.Now };
        using var auth = app.NewAuthService(out var sessions);
        await auth.InitializeAsync();
        app.Auth.Respond = _ => FakeAuth.Error(HttpStatusCode.BadRequest, "refresh_token_not_found", "Invalid Refresh Token");
        var signedOut = new TaskCompletionSource();
        auth.Changed += () =>
        {
            if (auth.Status == AuthStatus.SignedOut)
            {
                signedOut.TrySetResult();
            }
        };

        // What the sync connector does when it needs a token.
        Assert.Null(await sessions.GetTokenAsync(CancellationToken.None));
        await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("disconnect", app.Local.Calls[^1]);
        Assert.DoesNotContain("clear", app.Local.Calls);
        Assert.Null(app.Store.Saved);
    }

    [Fact]
    public async Task SigningInAsSomeoneElseAfterwardsRebindsTheDatabase()
    {
        app.Store.Saved = app.SavedSession("22222222-2222-2222-2222-222222222222");
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        await auth.SignOutAsync(discardUnsynced: true);

        await auth.VerifyCodeAsync("sam@example.com", "123456");

        // LocalDatabase.BindToUserAsync clears another owner's rows; its tests prove that part.
        Assert.Equal($"bind {app.Auth.UserId}", app.Local.Calls[^2]);
        Assert.Equal(app.Auth.UserId, auth.UserId);
    }
}
