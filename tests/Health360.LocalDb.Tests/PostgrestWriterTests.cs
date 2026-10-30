using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.LocalDb.Tests;

public class PostgrestWriterTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly SessionToken UserA = new("user-a", "user-token");

    private static (PostgrestWriter Writer, FakePostgrest Server) Create(SessionToken? token = null, string owner = "user-a", bool signedOut = false)
    {
        var server = new FakePostgrest();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:54321/") };
        var session = new FixedSession(signedOut ? null : token ?? UserA);
        return (new PostgrestWriter(http, session, new MemoryOwnerStore { Owner = owner }, "anon-key"), server);
    }

    [Fact]
    public async Task PutUpsertsTheRowAsTheSignedInUser()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.Created, "");

        await writer.PutAsync("notes", "n1", new JsonObject { ["body"] = "hi" }, Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /rest/v1/notes?on_conflict=id", request.Line);
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
        Assert.Equal("anon-key", request.Headers["apikey"]);
        Assert.Equal("resolution=merge-duplicates,return=minimal", request.Headers["Prefer"]);
        Assert.Equal("""{"body":"hi","id":"n1"}""", request.Body);
    }

    [Fact]
    public async Task PutIgnoresDuplicatesForInsertOnlyTables()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.Created, "");

        await writer.PutAsync("consents", "c1", new JsonObject { ["granted"] = true }, Ct);

        Assert.Equal("resolution=ignore-duplicates,return=minimal", server.Requests[0].Headers["Prefer"]);
    }

    [Fact]
    public async Task PatchUpdatesByIdAndAsksForTheRowBack()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, """[{"id":"n1"}]""");

        await writer.PatchAsync("notes", "n1", new JsonObject { ["body"] = "edited" }, Ct);

        var request = server.Requests[0];
        Assert.Equal("PATCH /rest/v1/notes?id=eq.n1&select=id", request.Line);
        Assert.Equal("return=representation", request.Headers["Prefer"]);
        Assert.Equal("""{"body":"edited"}""", request.Body);
    }

    [Fact]
    public async Task PatchThatMatchesNoRowIsAPermanentError()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, "[]");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PatchAsync("notes", "n1", new JsonObject { ["body"] = "x" }, Ct));

        Assert.Equal(PostgrestWriter.NoRow, error.Code);
        Assert.True(Uploads.IsPermanentError(error.Code));
    }

    [Fact]
    public async Task DeleteOfARowThatIsGoneSucceeds()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, "[]").Reply(HttpStatusCode.OK, "[]");

        await writer.DeleteAsync("notes", "n1", Ct);

        Assert.Equal(["DELETE /rest/v1/notes?id=eq.n1&select=id", "GET /rest/v1/notes?id=eq.n1&select=id"], server.Requests.Select(r => r.Line));
    }

    [Fact]
    public async Task DeleteThatRlsBlockedIsAPermanentError()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, "[]").Reply(HttpStatusCode.OK, """[{"id":"c1"}]""");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.DeleteAsync("consents", "c1", Ct));

        Assert.Equal(PostgrestWriter.Denied, error.Code);
    }

    [Fact]
    public async Task DeleteThatRemovedTheRowStopsThere()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, """[{"id":"n1"}]""");

        await writer.DeleteAsync("notes", "n1", Ct);

        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task EscapesTheIdInTheFilter()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, """[{"id":"x"}]""");

        await writer.PatchAsync("notes", "a&b=c", new JsonObject { ["body"] = "x" }, Ct);

        Assert.Equal("PATCH /rest/v1/notes?id=eq.a%26b%3Dc&select=id", server.Requests[0].Line);
    }

    [Fact]
    public async Task CarriesThePostgresErrorCode()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.Forbidden, """{"code":"42501","message":"new row violates row-level security policy for table \"notes\"","details":null,"hint":null}""");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PutAsync("notes", "n1", [], Ct));

        Assert.Equal("42501", error.Code);
        Assert.Contains("row-level security", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorWithoutACodeIsRetried()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.BadGateway, "<html>bad gateway</html>");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PutAsync("notes", "n1", [], Ct));

        Assert.Null(error.Code);
        Assert.False(Uploads.IsPermanentError(error.Code));
    }

    [Fact]
    public async Task WithoutASessionNothingIsSentAndTheWriteIsRetried()
    {
        var (writer, server) = Create(signedOut: true);

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PutAsync("notes", "n1", [], Ct));

        Assert.False(Uploads.IsPermanentError(error.Code));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task NeverSendsTheOwnersWritesWithAnotherUsersToken()
    {
        // User B signed in, but the database still holds user A's queued writes until it is rebound.
        var (writer, server) = Create(token: new SessionToken("user-b", "b-token"), owner: "user-a");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PutAsync("notes", "n1", [], Ct));

        Assert.False(Uploads.IsPermanentError(error.Code));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ASuccessReplyThatIsNotJsonIsRetried()
    {
        var (writer, server) = Create();
        server.Reply(HttpStatusCode.OK, "<html>Sign in to the Wi-Fi</html>");

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PatchAsync("notes", "n1", new JsonObject { ["body"] = "x" }, Ct));

        Assert.False(Uploads.IsPermanentError(error.Code));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    [InlineData("https://abc.powersync.journeyapps.com/", "https://abc.powersync.journeyapps.com")]
    [InlineData("https://proxy.example.test/powersync/", "https://proxy.example.test/powersync")]
    public void TheEndpointHasNoTrailingSlash(string url, string endpoint) =>
        Assert.Equal(endpoint, SupabaseConnector.Endpoint(new Uri(url)));

    [Fact]
    public async Task ConnectorGivesPowerSyncTheUsersToken()
    {
        var session = new FixedSession(UserA);
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var connector = new SupabaseConnector(
            session,
            owner,
            new FakeWriter(),
            new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("https://sync.example.test")),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<SupabaseConnector>.Instance);

        var credentials = await connector.FetchCredentials();
        // No trailing slash: the SDK appends "/sync/stream", and "//sync/stream" is a 404.
        Assert.Equal(("https://sync.example.test", "user-token"), (credentials!.Endpoint, credentials.Token));

        session.Token = new SessionToken("user-b", "b-token");
        Assert.Null(await connector.FetchCredentials());

        session.Token = null;
        Assert.Null(await connector.FetchCredentials());
    }

    private sealed class FakePostgrest : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> responses = new();

        public List<Recorded> Requests { get; } = [];

        public FakePostgrest Reply(HttpStatusCode status, string body)
        {
            responses.Enqueue((status, body));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Recorded($"{request.Method} {request.RequestUri!.PathAndQuery}", headers, body));
            var (status, reply) = responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record Recorded(string Line, Dictionary<string, string> Headers, string Body);
}
