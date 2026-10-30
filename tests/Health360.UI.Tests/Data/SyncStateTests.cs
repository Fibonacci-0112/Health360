using Health360.LocalDb;
using Health360.UI.Data;
using Health360.UI.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.UI.Tests.Data;

public sealed class SyncStateTests : IAsyncLifetime
{
    private App app = null!;

    public async ValueTask InitializeAsync() => app = await App.StartAsync();

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task ListsTheQuarantineAsItChanges()
    {
        using var sync = new SyncState(app.Database.Db, new App.NoWriter(), TimeProvider.System, NullLogger<SyncState>.Instance);
        await Until(() => sync.Failed.Count == 0);

        await app.Database.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code, error_message, failed_at) VALUES ('crud-7', 'notes', 'n1', 'PUT', '{}', '23505', 'duplicate key', '2026-10-30T11:00:00.000Z')");
        await Until(() => sync.Failed.Count == 1);

        var failed = sync.Failed[0];
        Assert.Equal(("crud-7", "notes", "PUT", "23505", "duplicate key"), (failed.Id, failed.TableName, failed.Op, failed.ErrorCode, failed.ErrorMessage));

        await sync.DiscardAsync("crud-7");
        await Until(() => sync.Failed.Count == 0);
    }

    [Fact]
    public async Task CountsWritesWaitingToUpload()
    {
        using var sync = new SyncState(app.Database.Db, new App.NoWriter(), TimeProvider.System, NullLogger<SyncState>.Instance);

        await app.Database.Db.Execute("INSERT INTO notes (id, profile_id, body) VALUES ('n1', 'p', 'offline note')");

        await Until(() => sync.Pending == 1);
        Assert.False(sync.Connected);
    }

    [Fact]
    public async Task RetryWithoutAConnectionKeepsTheEntry()
    {
        using var sync = new SyncState(app.Database.Db, new App.NoWriter(), TimeProvider.System, NullLogger<SyncState>.Instance);
        await app.Database.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code, error_message, failed_at) VALUES ('crud-7', 'notes', 'n1', 'PUT', '{}', '23505', 'duplicate key', '2026-10-30T11:00:00.000Z')");

        Assert.Equal(RetryResult.Offline, await sync.RetryAsync("crud-7"));
        Assert.Equal(1, await Uploads.FailedUploadCountAsync(app.Database.Db));
    }
}
