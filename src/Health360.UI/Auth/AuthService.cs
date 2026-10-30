using Microsoft.Extensions.Logging;

namespace Health360.UI.Auth;

public enum AuthStatus
{
    /// <summary>The saved session hasn't been read yet.</summary>
    Loading,
    SignedOut,
    SignedIn,
}

/// <summary>Changes on this device that haven't reached the server.</summary>
/// <param name="Pending">Writes still waiting to upload.</param>
/// <param name="Rejected">Writes the server rejected, kept in Settings for retry.</param>
public sealed record UnsyncedChanges(long Pending, long Rejected)
{
    public bool Any => Pending > 0 || Rejected > 0;
}

/// <summary>What sign-in and sign-out need from the local database: its account binding.</summary>
public interface ILocalAccount
{
    /// <summary>Makes the database the user's, clearing it if someone else's rows are there.</summary>
    Task BindAsync(string userId);

    /// <summary>Binds the database to the user, then starts syncing as them.</summary>
    Task ConnectAsync(string userId);

    /// <summary>Stops syncing but keeps every row and unsent write for the same user's return.</summary>
    Task DisconnectAsync();

    /// <summary>Removes this account's data from the device, unsent writes included.</summary>
    Task ClearAsync();

    Task<UnsyncedChanges> UnsyncedAsync();
}

/// <summary>
/// Sign-in by emailed code and the session's lifecycle, ported from the prototype's <c>auth.tsx</c>:
/// <list type="bullet">
/// <item>Every session binds the local database to its user before any screen sees it, so the next person
/// to sign in never inherits the previous user's rows or unsent writes.</item>
/// <item>Only a sign-out the user asks for clears the device. A session that ends on its own (a refresh
/// token Auth refuses) keeps everything for the same user's return.</item>
/// </list>
/// </summary>
public sealed partial class AuthService : IDisposable
{
    private readonly SessionManager sessions;
    private readonly SupabaseAuthClient client;
    private readonly ILocalAccount local;
    private readonly ILogger<AuthService> logger;

    // Transitions run one at a time, so a database clear always finishes before a new session is
    // published to the screens or connected.
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lock initLock = new();
    private Task? initialized;

    public AuthService(SessionManager sessions, SupabaseAuthClient client, ILocalAccount local, ILogger<AuthService> logger)
    {
        this.sessions = sessions;
        this.client = client;
        this.local = local;
        this.logger = logger;
        sessions.Expired += OnExpired;
    }

    /// <summary>Raised after <see cref="Status"/> or the user changes, possibly off the UI thread.</summary>
    public event Action? Changed;

    public AuthStatus Status { get; private set; } = AuthStatus.Loading;

    public string? UserId { get; private set; }

    public string? Email { get; private set; }

    /// <summary>Reads the saved session once; later calls wait for the first.</summary>
    public Task InitializeAsync()
    {
        lock (initLock)
        {
            return initialized ??= RestoreAsync();
        }
    }

    /// <summary>Emails a sign-in code. Throws <see cref="AuthException"/>.</summary>
    public Task SendCodeAsync(string email) => client.SendCodeAsync(email.Trim());

    /// <summary>Signs in with the emailed code, then binds the local database and starts syncing.</summary>
    public async Task VerifyCodeAsync(string email, string code)
    {
        var session = await client.VerifyCodeAsync(email.Trim(), code.Trim()).ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await local.BindAsync(session.UserId).ConfigureAwait(false);
            await sessions.StartAsync(session).ConfigureAwait(false);
            Publish(AuthStatus.SignedIn, session);
        }
        finally
        {
            gate.Release();
        }

        await ConnectAsync(session.UserId).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs out and removes this account's data from the device. If that would delete changes that never
    /// reached the server, nothing happens and the counts come back so the caller can ask first; pass
    /// <paramref name="discardUnsynced"/> once the user has agreed. Throws <see cref="AuthException"/> if
    /// Auth couldn't be reached; nothing is cleared then.
    /// </summary>
    public async Task<UnsyncedChanges?> SignOutAsync(bool discardUnsynced = false)
    {
        if (!discardUnsynced)
        {
            var unsynced = await local.UnsyncedAsync().ConfigureAwait(false);
            if (unsynced.Any)
            {
                return unsynced;
            }
        }

        // A fresh token, so the server session is really ended (a refused refresh means it already was).
        var token = await sessions.GetTokenAsync(CancellationToken.None).ConfigureAwait(false);
        if (token is not null)
        {
            await client.SignOutAsync(token.AccessToken).ConfigureAwait(false);
        }

        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await local.ClearAsync().ConfigureAwait(false);
            await sessions.EndAsync().ConfigureAwait(false);
            Publish(AuthStatus.SignedOut, null);
        }
        finally
        {
            gate.Release();
        }

        return null;
    }

    public void Dispose()
    {
        sessions.Expired -= OnExpired;
        gate.Dispose();
    }

    private async Task RestoreAsync()
    {
        string? userId = null;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = await sessions.RestoreAsync().ConfigureAwait(false);
            if (session is not null)
            {
                await local.BindAsync(session.UserId).ConfigureAwait(false);
                userId = session.UserId;
            }

            Publish(session is null ? AuthStatus.SignedOut : AuthStatus.SignedIn, session);
        }
        finally
        {
            gate.Release();
        }

        // Offline at launch is fine: the saved session stands and sync connects when it can.
        if (userId is not null)
        {
            await ConnectAsync(userId).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(string userId)
    {
        try
        {
            await local.ConnectAsync(userId).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The app keeps working offline; PowerSync retries once connected.
            LogConnectFailed(logger, error.GetType().Name);
        }
    }

    // Raised inside SessionManager's refresh, which a sync or upload may be waiting on: finish elsewhere.
    private void OnExpired(string userId) => _ = Task.Run(() => EndExpiredAsync(userId));

    private async Task EndExpiredAsync(string userId)
    {
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (UserId != userId)
                {
                    return;
                }

                await local.DisconnectAsync().ConfigureAwait(false);
                Publish(AuthStatus.SignedOut, null);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            LogExpiryFailed(logger, error.GetType().Name);
        }
    }

    private void Publish(AuthStatus status, AuthSession? session)
    {
        Status = status;
        UserId = session?.UserId;
        Email = session?.Email;
        Changed?.Invoke();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting sync failed ({Error}); it will be retried")]
    private static partial void LogConnectFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ending an expired session failed ({Error})")]
    private static partial void LogExpiryFailed(ILogger logger, string error);
}
