using Health360.LocalDb;
using Microsoft.Extensions.Logging;

namespace Health360.UI.Auth;

/// <summary>
/// Keeps the session across launches. The MAUI app uses platform secure storage (Android Keystore,
/// Windows DPAPI). Only the session is kept here; health data lives in the PowerSync database.
/// </summary>
public interface ISessionStore
{
    /// <summary>The saved session, or null when there is none (or it can't be read).</summary>
    Task<AuthSession?> LoadAsync();

    Task SaveAsync(AuthSession session);

    Task ClearAsync();
}

/// <summary>
/// Holds the signed-in session and hands out access tokens, refreshing them shortly before they expire.
/// It is what the sync connector and uploads ask for a token (<see cref="ISupabaseSession"/>).
/// Refreshes run one at a time, because Auth rotates refresh tokens and a second refresh with the old
/// token would be refused.
/// </summary>
public sealed partial class SessionManager(SupabaseAuthClient client, ISessionStore store, TimeProvider clock, ILogger<SessionManager> logger)
    : ISupabaseSession, IDisposable
{
    /// <summary>A token closer than this to expiry is refreshed before it is handed out.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim gate = new(1, 1);
    private AuthSession? current;

    /// <summary>
    /// Raised with the user's id when Auth refuses the refresh token (revoked, signed out elsewhere, or
    /// expired). The session is already gone; local data is kept, since nobody chose to give it up.
    /// Raised inside the refresh: handlers must not wait for the session or the local database.
    /// </summary>
    public event Action<string>? Expired;

    public AuthSession? Current => Volatile.Read(ref current);

    /// <summary>Restores the session saved on this device, if any.</summary>
    public async Task<AuthSession?> RestoreAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var saved = await store.LoadAsync().ConfigureAwait(false);
            Volatile.Write(ref current, saved);
            return saved;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task StartAsync(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await store.SaveAsync(session).ConfigureAwait(false);
            Volatile.Write(ref current, session);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task EndAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref current, null);
            await store.ClearAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The access token and its user, refreshed first if it expires within <see cref="RefreshMargin"/>.
    /// Null when signed out, when Auth refused the refresh, or when it couldn't be reached and the token
    /// has already expired (sync and uploads then retry later).
    /// </summary>
    public Task<SessionToken?> GetTokenAsync(CancellationToken cancellationToken) => TokenAsync(forSignOut: false, cancellationToken);

    /// <summary>
    /// Like <see cref="GetTokenAsync"/>, for ending the session on the server: null only when there is no
    /// session or Auth refused the refresh token (the server session is already gone). If Auth can't be
    /// reached and the token has expired, it throws <see cref="AuthException"/> instead, so sign-out keeps
    /// everything rather than clearing the device while the server session lives on.
    /// </summary>
    public Task<SessionToken?> GetTokenForSignOutAsync() => TokenAsync(forSignOut: true, CancellationToken.None);

    private async Task<SessionToken?> TokenAsync(bool forSignOut, CancellationToken cancellationToken)
    {
        var session = Current;
        if (session is null)
        {
            return null;
        }

        if (session.ExpiresAt - clock.GetUtcNow() > RefreshMargin)
        {
            return Token(session);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed (or ended) it while this one waited.
            session = Current;
            if (session is null)
            {
                return null;
            }

            if (session.ExpiresAt - clock.GetUtcNow() > RefreshMargin)
            {
                return Token(session);
            }

            AuthSession next;
            try
            {
                // Never cancelled by the caller: Auth rotates the refresh token as soon as it answers, so an
                // abandoned request would lose the new one and the old one would then be refused.
                next = await client.RefreshAsync(session.RefreshToken, CancellationToken.None).ConfigureAwait(false);
            }
            catch (AuthException error) when (error.Kind == AuthErrorKind.Rejected)
            {
                LogRefreshRefused(logger, error.Code ?? "no code");
                Volatile.Write(ref current, null);
                await store.ClearAsync().ConfigureAwait(false);
                Expired?.Invoke(session.UserId);
                return null;
            }
            catch (AuthException error)
            {
                LogRefreshFailed(logger, error.Kind);
                if (session.ExpiresAt > clock.GetUtcNow())
                {
                    return Token(session);
                }

                if (forSignOut)
                {
                    throw;
                }

                return null;
            }

            if (next.UserId != session.UserId)
            {
                // Never happens with a real Auth server; refuse rather than hand out another user's token.
                throw new InvalidOperationException("The refreshed session belongs to another user.");
            }

            await store.SaveAsync(next).ConfigureAwait(false);
            Volatile.Write(ref current, next);
            return Token(next);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();

    private static SessionToken Token(AuthSession session) => new(session.UserId, session.AccessToken);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Auth refused the refresh token ({Code}); the session has ended")]
    private static partial void LogRefreshRefused(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Information, Message = "Token refresh failed ({Kind}); will retry")]
    private static partial void LogRefreshFailed(ILogger logger, AuthErrorKind kind);
}
