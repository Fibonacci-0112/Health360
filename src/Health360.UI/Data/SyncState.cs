using Health360.LocalDb;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;

namespace Health360.UI.Data;

/// <summary>
/// Whether this device is syncing, how many writes haven't reached the server (PLAN.md §2.7: the
/// "unsynced entries" indicator), and the writes the server rejected, with their retry and discard actions.
/// </summary>
public sealed partial class SyncState : IDisposable
{
    /// <summary>How often the upload queue is counted; PowerSync has no event for it.</summary>
    public static readonly TimeSpan PendingPollInterval = TimeSpan.FromSeconds(1);

    private readonly PowerSyncDatabase db;
    private readonly IRemoteWriter writer;
    private readonly TimeProvider clock;
    private readonly ILogger<SyncState> logger;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopping;
    private int disposed;

    public SyncState(PowerSyncDatabase db, IRemoteWriter writer, TimeProvider clock, ILogger<SyncState> logger)
    {
        this.db = db;
        this.writer = writer;
        this.clock = clock;
        this.logger = logger;
        stopping = stop.Token;
        Update();
        _ = Task.Run(WatchStatusAsync, CancellationToken.None);
        _ = Task.Run(PollPendingAsync, CancellationToken.None);
        _ = Task.Run(WatchFailedAsync, CancellationToken.None);
    }

    /// <summary>Raised when anything here changes, possibly off the UI thread.</summary>
    public event Action? Changed;

    public bool Connected { get; private set; }

    /// <summary>True once this device has completed a sync (so a missing profile row is really missing).</summary>
    public bool HasSynced { get; private set; }

    public long Pending { get; private set; }

    public IReadOnlyList<FailedUpload> Failed { get; private set; } = [];

    /// <summary>Sends a quarantined write again, after the user fixed what was wrong.</summary>
    public Task<RetryResult> RetryAsync(string id) => Uploads.RetryFailedUploadAsync(db, id, writer, clock);

    /// <summary>Drops a quarantined write for good.</summary>
    public Task DiscardAsync(string id) => Uploads.DiscardFailedUploadAsync(db, id);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        stop.Cancel();
        stop.Dispose();
    }

    private void Update()
    {
        var status = db.CurrentStatus;
        var connected = status.Connected;
        var hasSynced = status.HasSynced == true;
        if (connected == Connected && hasSynced == HasSynced)
        {
            return;
        }

        Connected = connected;
        HasSynced = hasSynced;
        Changed?.Invoke();
    }

    private async Task WatchStatusAsync()
    {
        try
        {
            await foreach (var _ in db.Events.OnStatusChanged.ListenAsync(stopping).ConfigureAwait(false))
            {
                Update();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PollPendingAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(PendingPollInterval, clock);
            do
            {
                // The status event isn't raised when only the first sync's completion is restored at launch.
                Update();
                try
                {
                    var pending = await Uploads.PendingUploadCountAsync(db).ConfigureAwait(false);
                    if (pending != Pending)
                    {
                        Pending = pending;
                        Changed?.Invoke();
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException and not ObjectDisposedException)
                {
                    // Counted again on the next tick (a clear or close can briefly interrupt it).
                    LogFailed(logger, "counting pending uploads", error.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false));
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task WatchFailedAsync()
    {
        try
        {
            await foreach (var rows in FailedUploads.Watch(db, stopping).ConfigureAwait(false))
            {
                Failed = rows;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            LogFailed(logger, "watching failed uploads", error.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sync state: {What} failed ({Error})")]
    private static partial void LogFailed(ILogger logger, string what, string error);
}
