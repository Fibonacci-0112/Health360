using System.Text.Json.Nodes;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>The upload queue and the failed_uploads quarantine, against a real PowerSync database.</summary>
public class UploadTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static Task<PowerSync.Common.DB.NonQueryResult> AddNote(TestDatabase t, string id, string body) =>
        t.Db.Execute("INSERT INTO notes (id, profile_id, body, local_date) VALUES (?, ?, ?, ?)", [id, Profile, body, "2026-10-30"]);

    private static async Task<long> FailedCount(TestDatabase t) => await Uploads.FailedUploadCountAsync(t.Db);

    [Fact]
    public async Task UploadsAnInsertAsAPutOfTheWholeRow()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PUT", "notes", "n1"), (write.Op, write.Table, write.Id));
        Assert.Equal("hello", write.Data!["body"]!.GetValue<string>());
        Assert.Equal(Profile, write.Data!["profile_id"]!.GetValue<string>());
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task UploadsUpdatesAndDeletes()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        await t.Db.Execute("UPDATE notes SET body = ? WHERE id = ?", ["edited", "n1"]);
        await t.Db.Execute("DELETE FROM notes WHERE id = ?", ["n1"]);
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["PUT", "PATCH", "DELETE"], writer.Writes.Select(w => w.Op));
        Assert.Equal("""{"body":"edited"}""", writer.Writes[1].Data!.ToJsonString());
    }

    [Fact]
    public async Task ConvertsJsonAndBooleanColumnsOnTheWayOut()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO profiles (id, user_id, unit_prefs) VALUES (?, ?, ?)", [Profile, "u", """{"load":"kg"}"""]);
        await t.Db.Execute("INSERT INTO consents (id, profile_id, kind, granted) VALUES (?, ?, ?, ?)", ["c1", Profile, "photo_ai", 1]);
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("kg", writer.Writes[0].Data!["unit_prefs"]!["load"]!.GetValue<string>());
        Assert.True(writer.Writes[1].Data!["granted"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("2026-10-30T12:34:56.789Z")]
    [InlineData("2026-10-30T12:34:56+02:00")]
    [InlineData("2026-10-30 12:34:56.789+00")]
    [InlineData("2026-10-30")]
    public async Task SendsTimestampsExactlyAsStored(string timestamp)
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO notes (id, profile_id, body, created_at) VALUES (?, ?, ?, ?)", ["n1", Profile, "x", timestamp]);
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(timestamp, writer.Writes[0].Data!["created_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task SendsNothingForAnUpdateThatChangesNothing()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        await t.Db.Execute("UPDATE notes SET body = ? WHERE id = ?", ["hello", "n1"]);
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(writer.Writes, w => w.Op == "PATCH" && w.Data!.Count == 0);
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task QuarantinesARejectedWriteAndCarriesOn()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "forged", "a");
        await AddNote(t, "fine", "b");
        var writer = new FakeWriter { Fail = w => w.Id == "forged" ? new RemoteWriteException("rls", "42501") : null };

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["forged", "fine"], writer.Writes.Select(w => w.Id));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
        var row = await t.Db.Get<FailedRow>(
            "SELECT table_name AS TableName, row_id AS RowId, op AS Op, data AS Data, error_code AS ErrorCode, failed_at AS FailedAt FROM failed_uploads");
        Assert.Equal(("notes", "forged", "PUT", "42501", "2026-10-30T12:00:00.000Z"), (row.TableName, row.RowId, row.Op, row.ErrorCode, row.FailedAt));
        Assert.Equal("a", JsonNode.Parse(row.Data)!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task QuarantinesARejectedOpOnceEvenWhenItsTransactionIsRetried()
    {
        await using var t = await TestDatabase.OpenAsync();
        // One transaction: op 1 is rejected for good, op 2 hits a network error the first time.
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO notes (id, profile_id, body) VALUES (?, ?, ?)", ["a", Profile, "forged"]);
            await tx.Execute("INSERT INTO notes (id, profile_id, body) VALUES (?, ?, ?)", ["b", Profile, "fine"]);
        });
        var networkDown = true;
        var writer = new FakeWriter
        {
            Fail = w => w.Id == "a" ? new RemoteWriteException("rls", "42501") : networkDown ? new HttpRequestException("offline") : null,
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await FailedCount(t));
        Assert.Equal(2, await Uploads.PendingUploadCountAsync(t.Db));

        networkDown = false;
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, await FailedCount(t));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task ClearsARejectionOnceARetriedTransactionGetsTheOpAccepted()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO notes (id, profile_id, body) VALUES (?, ?, ?)", ["a", Profile, "x"]);
            await tx.Execute("INSERT INTO notes (id, profile_id, body) VALUES (?, ?, ?)", ["b", Profile, "y"]);
        });
        var attempt = 0;
        var writer = new FakeWriter
        {
            // Attempt 1: "a" is rejected, then "b" fails transiently, so the transaction comes round again.
            Fail = w => (attempt, w.Id) switch
            {
                (0, "a") => new RemoteWriteException("conflict", "23505"),
                (0, "b") => new HttpRequestException("offline"),
                _ => null,
            },
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await FailedCount(t));
        attempt = 1;
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await FailedCount(t));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("PGRST301")]
    [InlineData("53300")]
    public async Task LeavesTransientFailuresQueued(string? code)
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        var writer = new FakeWriter { Fail = _ => new RemoteWriteException("try later", code) };

        await Assert.ThrowsAsync<RemoteWriteException>(() => Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, await Uploads.PendingUploadCountAsync(t.Db));
        Assert.Equal(0, await FailedCount(t));
    }

    [Fact]
    public async Task QuarantinesMalformedJsonWithoutSendingIt()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO profiles (id, user_id, unit_prefs) VALUES (?, ?, ?)", [Profile, "u", "{oops"]);
        var writer = new FakeWriter();

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(writer.Writes);
        Assert.Equal(1, await FailedCount(t));
    }

    [Fact]
    public async Task RetrySendsAFixedWriteAndRemovesTheEntry()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        var reject = true;
        var writer = new FakeWriter { Fail = _ => reject ? new RemoteWriteException("check", "23514") : null };
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var id = await QuarantinedId(t);

        reject = false;
        var result = await Uploads.RetryFailedUploadAsync(t.Db, id, writer, Clock, TestContext.Current.CancellationToken);

        Assert.Equal(RetryResult.Sent, result);
        Assert.Equal(0, await FailedCount(t));
        var resent = writer.Writes[^1];
        Assert.Equal(("PUT", "notes", "n1", "hello"), (resent.Op, resent.Table, resent.Id, resent.Data!["body"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RetryKeepsTheEntryWithTheNewErrorWhenRejectedAgain()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        var code = "23514";
        var writer = new FakeWriter { Fail = _ => new RemoteWriteException("no", code) };
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var id = await QuarantinedId(t);

        code = "42501";
        var result = await Uploads.RetryFailedUploadAsync(t.Db, id, writer, Clock, TestContext.Current.CancellationToken);

        Assert.Equal(RetryResult.Rejected, result);
        Assert.Equal("42501", (await t.Db.Get<FailedRow>("SELECT error_code AS ErrorCode FROM failed_uploads")).ErrorCode);
    }

    [Fact]
    public async Task RetryKeepsTheEntryUnchangedWhenOffline()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        var offline = false;
        var writer = new FakeWriter { Fail = _ => offline ? new HttpRequestException("offline") : new RemoteWriteException("no", "23514") };
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var id = await QuarantinedId(t);

        offline = true;
        var result = await Uploads.RetryFailedUploadAsync(t.Db, id, writer, Clock, TestContext.Current.CancellationToken);

        Assert.Equal(RetryResult.Offline, result);
        Assert.Equal("23514", (await t.Db.Get<FailedRow>("SELECT error_code AS ErrorCode FROM failed_uploads")).ErrorCode);
    }

    [Fact]
    public async Task RetryReportsOfflineWhenTheReplyIsNotJson()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter { Fail = _ => new RemoteWriteException("no", "23514") }, Clock, cancellationToken: TestContext.Current.CancellationToken);
        await t.Db.Execute("UPDATE notes SET body = ? WHERE id = ?", ["fixed", "n1"]);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter { Fail = w => w.Op == "PATCH" ? new RemoteWriteException("no", "23514") : null }, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var patchId = (await t.Db.Get<FailedRow>("SELECT id AS Id FROM failed_uploads WHERE op = 'PATCH'")).Id;
        using var http = new HttpClient(new HtmlPage()) { BaseAddress = new Uri("http://127.0.0.1:54321/") };
        var writer = new PostgrestWriter(http, new FixedSession(new SessionToken("u", "t")), new MemoryOwnerStore { Owner = "u" }, "anon");

        var result = await Uploads.RetryFailedUploadAsync(t.Db, patchId, writer, Clock, TestContext.Current.CancellationToken);

        Assert.Equal(RetryResult.Offline, result);
        Assert.Equal(2, await FailedCount(t));
    }

    [Fact]
    public async Task DiscardDropsTheEntry()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter { Fail = _ => new RemoteWriteException("no", "23514") }, Clock, cancellationToken: TestContext.Current.CancellationToken);

        await Uploads.DiscardFailedUploadAsync(t.Db, await QuarantinedId(t));

        Assert.Equal(0, await FailedCount(t));
    }

    [Fact]
    public async Task NeverQueuesWritesToFailedUploads()
    {
        await using var t = await TestDatabase.OpenAsync();
        await AddNote(t, "n1", "hello");
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter { Fail = _ => new RemoteWriteException("no", "23514") }, Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    private static async Task<string> QuarantinedId(TestDatabase t) =>
        (await t.Db.Get<FailedRow>("SELECT id AS Id FROM failed_uploads")).Id;

    /// <summary>Answers every request with 200 and an HTML page, as a captive portal does.</summary>
    private sealed class HtmlPage : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>") });
    }

    private sealed class FailedRow
    {
        public string Id { get; set; } = "";

        public string TableName { get; set; } = "";

        public string RowId { get; set; } = "";

        public string Op { get; set; } = "";

        public string Data { get; set; } = "";

        public string? ErrorCode { get; set; }

        public string FailedAt { get; set; } = "";
    }
}
