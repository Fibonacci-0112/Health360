using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.LocalDb.Tests;

/// <summary>Account-switch isolation: the database never hands one user's rows or writes to another.</summary>
public class LocalDatabaseTests
{
    private static LocalDatabase Wrap(TestDatabase t, MemoryOwnerStore owner) =>
        new(t.Db, owner, new SupabaseConnector(
            new FixedSession(null),
            owner,
            new FakeWriter(),
            new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<SupabaseConnector>.Instance));

    private static async Task SeedAsync(TestDatabase t)
    {
        await t.Db.Execute("INSERT INTO measurements (id, profile_id, method) VALUES (?, ?, ?)", ["n1", "p", "mine"]);
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data) VALUES (?, ?, ?, ?, ?)",
            ["crud-9", "measurements", "n0", "PUT", "{}"]);
    }

    private static async Task<(long Rows, long Pending, long Failed)> CountsAsync(TestDatabase t) =>
        ((await t.Db.Get<Count>("SELECT count(*) AS N FROM measurements")).N,
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

    [Fact]
    public async Task ConnectingWhileTheServerIsUnreachableReturnsAndLeavesSignOutUnblocked()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore();
        // A session and a PowerSync URL nothing listens on: the first connection never succeeds.
        var local = new LocalDatabase(t.Db, owner, new SupabaseConnector(
            new FixedSession(new SessionToken("user-a", "a-token")),
            owner,
            new FakeWriter(),
            new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:9")),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<SupabaseConnector>.Instance));
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;

        await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
        Assert.Equal("user-a", owner.Owner);

        // Neither waits for a connection that may never come (offline sign-out, account switch).
        await local.DisconnectAsync().WaitAsync(timeout, ct);
        await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
        await local.ClearAsync().WaitAsync(timeout, ct);
        Assert.Null(owner.Owner);
    }

    [Fact]
    public async Task DisconnectingWhileAReconnectIsStartingKeepsSyncStoppedAndWritesPending()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        // Each attempt syncs against its own endpoint that holds every connection open, so "stopped" can be checked on
        // the wire: after the disconnect the client must close the connections the reconnect made and dial no more.
        // Two PowerSync.Common 1.0.0 behaviours are allowed for, neither of which the app relies on:
        // - The first connection's request may stay open: when a reconnect supersedes a stream whose request has
        //   reached the server, the SDK doesn't cancel that request, and it lingers (about 75 s here) after the stream
        //   is gone. The stream never reports Connected again and the writes stay pending, which is checked.
        // - The Connecting flag isn't checked: a reconnect the disconnect overtook can set it again afterwards with
        //   nothing running, and the app never reads it.
        var running = new List<(HoldingSyncServer Server, TestDatabase Test, LocalDatabase Local, int Dialled)>();
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var server = new HoldingSyncServer();
                var t = await TestDatabase.OpenAsync();
                var owner = new MemoryOwnerStore();
                var local = new LocalDatabase(t.Db, owner, new SupabaseConnector(
                    new FixedSession(new SessionToken("user-a", "a-token")),
                    owner,
                    new FakeWriter(),
                    new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", server.Url),
                    new FixedClock(DateTimeOffset.UnixEpoch),
                    NullLogger<SupabaseConnector>.Instance));
                running.Add((server, t, local, 0));

                await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
                // The first attempt has reached the server, so the reconnect has a live stream to tear down.
                Assert.True(await server.AcceptedAtLeastAsync(1, timeout, ct), $"the first connection never reached the server (iteration {i})");
                // Reconnecting tears down the old stream asynchronously. Disconnect must also stop the
                // new connection even if it hasn't registered with the SDK's connection manager yet.
                await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
                await local.DisconnectAsync().WaitAsync(timeout, ct);
                await SeedAsync(t);

                Assert.True(await server.AllClosedAsync(timeout, ct, first: 1), $"a reconnect's sync connection outlived the disconnect (iteration {i})");
                Assert.False(t.Db.CurrentStatus.Connected);
                Assert.Equal((1, 1, 1), await CountsAsync(t));
                running[i] = (server, t, local, server.Accepted);
            }

            // Still disconnected well past the retry delay: no attempt dials again, and the writes stay pending.
            await Task.Delay(TimeSpan.FromMilliseconds(LocalDatabase.RetryDelayMs * 2), ct);
            for (var i = 0; i < running.Count; i++)
            {
                var (server, t, _, dialled) = running[i];
                Assert.True(dialled == server.Accepted, $"attempt {i} dialled again after its disconnect");
                Assert.True(server.OpenFrom(1) == 0, $"a reconnect's sync connection reopened after the disconnect (attempt {i})");
                Assert.False(t.Db.CurrentStatus.Connected);
                Assert.Equal((1, 1, 1), await CountsAsync(t));
            }
        }
        finally
        {
            foreach (var (server, t, local, _) in running)
            {
                await local.DisposeAsync();
                await t.DisposeAsync();
                await server.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DisposingWhileAReconnectIsStartingNeitherCrashesNorHangs()
    {
        // PowerSync.Common 1.0.0 overflows the stack, killing the process, if Close lands while a Connect
        // is still tearing down the previous connection. A reconnect followed straight away by shutdown is
        // what the sync tests' cleanup does, and so does an app closed mid-reconnect.
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 10; i++)
        {
            await using var t = await TestDatabase.OpenAsync();
            var owner = new MemoryOwnerStore();
            // A session and a PowerSync URL nothing listens on: the first connection never succeeds.
            var local = new LocalDatabase(t.Db, owner, new SupabaseConnector(
                new FixedSession(new SessionToken("user-a", "a-token")),
                owner,
                new FakeWriter(),
                new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:9")),
                new FixedClock(DateTimeOffset.UnixEpoch),
                NullLogger<SupabaseConnector>.Instance));

            await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
            await Task.Delay(300, ct); // the first attempt is under way, so the reconnect has a stream to tear down
            await local.ConnectAsync("user-a").WaitAsync(timeout, ct);
            await local.DisposeAsync().AsTask().WaitAsync(timeout, ct);

            Assert.True(t.Db.Closed);
        }
    }

    [Fact]
    public async Task OverlappingDisposalsAndQueuedCallsAllComplete()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var local = Wrap(t, owner);
        var timeout = TimeSpan.FromSeconds(10);
        var ct = TestContext.Current.CancellationToken;

        // Three disposals and a call queued behind them: none may be left waiting on the gate. The queued
        // call meets the closed database, so it may fail, but it must finish.
        var disposals = Enumerable.Range(0, 3).Select(_ => local.DisposeAsync().AsTask()).ToArray();
        var queued = local.DisconnectAsync();

        await Task.WhenAll(disposals).WaitAsync(timeout, ct);
        await Task.WhenAny(queued).WaitAsync(timeout, ct);
        Assert.True(t.Db.Closed);
    }

    private sealed class Count
    {
        public long N { get; set; }
    }
}
