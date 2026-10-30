using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.LocalDb.Tests;

/// <summary>Account-switch isolation: the database never hands one user's rows or writes to another.</summary>
public class LocalDatabaseTests
{
    private static LocalDatabase Wrap(TestDatabase t, MemoryOwnerStore owner) =>
        new(t.Db, owner, new SupabaseConnector(
            new FixedSession(null),
            new FakeWriter(),
            new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<SupabaseConnector>.Instance));

    private static async Task SeedAsync(TestDatabase t)
    {
        await t.Db.Execute("INSERT INTO notes (id, profile_id, body) VALUES (?, ?, ?)", ["n1", "p", "mine"]);
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data) VALUES (?, ?, ?, ?, ?)",
            ["crud-9", "notes", "n0", "PUT", "{}"]);
    }

    private static async Task<(long Notes, long Pending, long Failed)> CountsAsync(TestDatabase t) =>
        ((await t.Db.Get<Count>("SELECT count(*) AS N FROM notes")).N,
         await Uploads.PendingUploadCountAsync(t.Db),
         await Uploads.FailedUploadCountAsync(t.Db));

    [Fact]
    public async Task KeepsEverythingForTheSameUser()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        await SeedAsync(t);

        await Wrap(t, owner).BindToUserAsync("user-a");

        Assert.Equal((1, 1, 1), await CountsAsync(t));
    }

    [Fact]
    public async Task ClearsRowsPendingWritesAndQuarantineForAnotherUser()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        await SeedAsync(t);

        await Wrap(t, owner).BindToUserAsync("user-b");

        Assert.Equal((0, 0, 0), await CountsAsync(t));
        Assert.Equal("user-b", owner.Owner);
    }

    [Fact]
    public async Task ClearsADatabaseWhoseOwnerIsUnknown()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore();
        await SeedAsync(t);

        await Wrap(t, owner).BindToUserAsync("user-a");

        Assert.Equal((0, 0, 0), await CountsAsync(t));
    }

    [Fact]
    public async Task ClearRemovesTheDataAndTheOwner()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        await SeedAsync(t);

        await Wrap(t, owner).ClearAsync();

        Assert.Equal((0, 0, 0), await CountsAsync(t));
        Assert.Null(owner.Owner);
    }

    [Fact]
    public async Task DisconnectKeepsUnsentWrites()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        await SeedAsync(t);

        await Wrap(t, owner).DisconnectAsync();

        Assert.Equal((1, 1, 1), await CountsAsync(t));
        Assert.Equal("user-a", owner.Owner);
    }

    private sealed class Count
    {
        public long N { get; set; }
    }
}
