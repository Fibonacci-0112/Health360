using PowerSync.Common.Client;
using PowerSync.Common.Client.Connection;
using PowerSync.Common.Client.Sync.Stream;

namespace Health360.LocalDb;

/// <summary>Remembers which user's rows (and pending writes) are in the local database.</summary>
public interface IDatabaseOwnerStore
{
    string? Load();

    void Save(string userId);

    void Forget();
}

/// <summary>
/// The app's one PowerSync database, bound to the user who owns its rows. A session can vanish without
/// a sign-out (revoked or expired while the app was closed), so the next person to sign in must never
/// inherit the previous user's rows, their unsent writes or their quarantined ones: every connect binds
/// the database to the signed-in user first, and clears it if someone else owned it.
/// </summary>
public sealed class LocalDatabase(PowerSyncDatabase db, IDatabaseOwnerStore owner, IPowerSyncBackendConnector connector) : IAsyncDisposable
{
    /// <summary>
    /// A 2 s retry (the SDK's default is 5 s) keeps "reconnect, then visible on another device" near
    /// 5 s (PLAN.md §3).
    /// </summary>
    public const int RetryDelayMs = 2000;

    // Owner changes, connects and clears run one at a time, so a clear always finishes before the
    // next user's session connects.
    private readonly SemaphoreSlim gate = new(1, 1);

    public PowerSyncDatabase Db => db;

    /// <summary>
    /// Binds the database to <paramref name="userId"/>, then starts syncing as them. Returns once sync has
    /// started, not once it has connected: PowerSync's <c>Connect</c> waits for the first successful
    /// connection, which offline may be never (it keeps retrying on its own). Waiting for it here would
    /// hold the gate, so a sign-out or account switch would wait for the network.
    /// </summary>
    public async Task ConnectAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        Task connecting;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BindAsync(userId).ConfigureAwait(false);
            connecting = db.Connect(connector, new PowerSyncConnectionOptions(retryDelayMs: RetryDelayMs));
        }
        finally
        {
            gate.Release();
        }

        // Observe a failure to start so it isn't an unobserved task exception; sync status reports it.
        _ = connecting.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Makes sure the database belongs to <paramref name="userId"/> before anything reads or syncs it,
    /// clearing it if another user's rows are there.
    /// </summary>
    public async Task BindToUserAsync(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BindAsync(userId).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops syncing but keeps every row, unsent writes included, for the same user's return. Use this
    /// when the session ends without the user asking (a refresh token that stopped working).
    /// </summary>
    public async Task DisconnectAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await db.Disconnect().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Removes this account's data from the device: synced rows, pending uploads and quarantined writes.
    /// Only for a sign-out the user asked for, after they have agreed to lose anything unsynced.
    /// </summary>
    public async Task ClearAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await db.DisconnectAndClear(clearLocal: true).ConfigureAwait(false);
            owner.Forget();
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await db.Close().ConfigureAwait(false);
        gate.Dispose();
    }

    private async Task BindAsync(string userId)
    {
        if (owner.Load() == userId)
        {
            return;
        }

        // clearLocal also empties failed_uploads, which holds the previous user's rejected writes.
        await db.DisconnectAndClear(clearLocal: true).ConfigureAwait(false);
        owner.Save(userId);
    }
}
