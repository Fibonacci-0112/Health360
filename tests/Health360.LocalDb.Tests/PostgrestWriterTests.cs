using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.LocalDb.Tests;

public class PostgrestWriterTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static (PostgrestWriter Writer, FakePostgrest Server) Create(string? token = "user-token")
    {
        var server = new FakePostgrest();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:54321/") };
        return (new PostgrestWriter(http, new FixedSession(token), "anon-key"), server);
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
        var (writer, server) = Create(token: null);

        var error = await Assert.ThrowsAsync<RemoteWriteException>(() => writer.PutAsync("notes", "n1", [], Ct));

        Assert.False(Uploads.IsPermanentError(error.Code));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ConnectorGivesPowerSyncTheUsersToken()
    {
        var session = new FixedSession("user-token");
        var connector = new SupabaseConnector(
            session,
            new FakeWriter(),
            new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("https://sync.example.test")),
            new FixedClock(DateTimeOffset.UnixEpoch),
            NullLogger<SupabaseConnector>.Instance);

        var credentials = await connector.FetchCredentials();
        Assert.Equal(("https://sync.example.test/", "user-token"), (credentials!.Endpoint, credentials.Token));

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
