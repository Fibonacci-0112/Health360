using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// the database to the signed-in user first, and clears it if someone else owned it. Progress photos waiting to
/// upload live as files beside the database (<paramref name="photos"/>); uploads stop and the files are deleted whenever
/// it is cleared.
/// </summary>
public sealed partial class LocalDatabase(
    PowerSyncDatabase db,
    IDatabaseOwnerStore owner,
    IPowerSyncBackendConnector connector,
    ILogger<LocalDatabase>? logger = null,
    PhotoUploader? photos = null) : IAsyncDisposable
{
    /// <summary>
    /// A 2 s retry (the SDK's default is 5 s) keeps "reconnect, then visible on another device" near
    /// 5 s (PLAN.md §3).
    /// </summary>
    public const int RetryDelayMs = 2000;

    // Owner changes, connects and clears run one at a time, so a clear always finishes before the
    // next user's session connects.
    private readonly SemaphoreSlim gate = new(1, 1);

    // The Connect calls not yet finished (guarded by gate), so DisposeAsync can stop them before closing.
    private Task? connecting;

    private bool disposed;

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
        Task started;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BindAsync(userId).ConfigureAwait(false);
            started = db.Connect(connector, new PowerSyncConnectionOptions(retryDelayMs: RetryDelayMs));
            connecting = connecting is { IsCompleted: false } earlier ? Task.WhenAll(earlier, started) : started;
        }
        finally
        {
            gate.Release();
        }

        // Nobody awaits it, so log a failure to start (the type only: no tokens or row data).
        _ = started.ContinueWith(
            t => LogConnectFailed(logger ?? (ILogger)NullLogger.Instance, t.Exception?.InnerException?.GetType().Name ?? "unknown"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Starting sync failed ({Error}); PowerSync retries on the next connect")]
    private static partial void LogConnectFailed(ILogger logger, string error);

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
            await StopSyncAsync().ConfigureAwait(false);
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
            await StopSyncAsync().ConfigureAwait(false);
            await ClearLocalAsync().ConfigureAwait(false);
            owner.Forget();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Stops syncing and closes the database. Safe to call more than once. The gate is deliberately not
    /// disposed: a call still queued on it would then never complete, whereas once it gets the gate it
    /// meets the closed database's own error. A SemaphoreSlim that never handed out its wait handle holds
    /// nothing that needs disposing.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            // Closing while a Connect is still starting overflows the stack in PowerSync.Common 1.0.0.
            await StopSyncAsync().ConfigureAwait(false);
            await db.Close().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Disconnects, and waits until no Connect from <see cref="ConnectAsync"/> is still starting. The
    /// SDK's Connect first awaits a disconnect of its own, then checks the client isn't closed, and only
    /// then registers with its connection manager. Close disposes the client's resources before it marks
    /// it closed, so a Close that lands in that gap lets the connect start on a disposed client: it fails
    /// synchronously and retries itself recursively until the stack overflows, killing the process
    /// (PowerSync.Common 1.0.0, <c>ConnectionManager.CheckedConnectInternal</c>). A Disconnect landing in
    /// the same gap is overtaken, but once the connect is registered a Disconnect ends it, whether it is
    /// still starting or retrying offline, so disconnecting until it has finished stops it without
    /// waiting for the network. Call with the gate held.
    /// </summary>
    private async Task StopSyncAsync()
    {
        await db.Disconnect().ConfigureAwait(false);
        while (connecting is { IsCompleted: false } pending)
        {
            await Task.WhenAny(pending, Task.Delay(50)).ConfigureAwait(false);
            await db.Disconnect().ConfigureAwait(false);
        }

        connecting = null;
    }

    private async Task BindAsync(string userId)
    {
        if (owner.Load() == userId)
        {
            return;
        }

        // clearLocal also empties failed_uploads and photo_uploads, which hold the previous user's rejected writes
        // and unsent photos. Their photo files go too.
        await StopSyncAsync().ConfigureAwait(false);
        await ClearLocalAsync().ConfigureAwait(false);
        owner.Save(userId);
    }

    private Task ClearLocalAsync() =>
        photos is null
            ? db.DisconnectAndClear(clearLocal: true)
            : photos.ClearAsync(() => db.DisconnectAndClear(clearLocal: true));
}
