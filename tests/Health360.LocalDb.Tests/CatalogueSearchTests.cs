using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Health360.Domain.Nutrition;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Catalogue search over PostgREST, and copying a result into a household in a real PowerSync database.</summary>
public class CatalogueSearchTests
{
    private const string Household = "0192f0c0-0000-7000-8000-0000000000a1";
    private const string Chicken = "50000000-0000-0000-0000-000000000001";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    // JSON written with single quotes, for readability.
    private static readonly string ChickenJson = (
        "[{'id':'50000000-0000-0000-0000-000000000001','source':'usda_fdc','name':'Chicken breast','brand':null,'basis':'g'," +
        "'energy_100':120,'protein_100':22.5,'carbohydrate_100':0,'fat_100':2.6,'nutrients_100':{'sodium':45}," +
        "'portions':[{'name':'1 breast','amount':174}]}]").Replace('\'', '"');

    private static (PostgrestCatalogueSearch Search, Server Server) Create(bool signedOut = false)
    {
        var server = new Server();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:54321/") };
        var session = new FixedSession(signedOut ? null : new SessionToken("user-a", "user-token"));
        return (new PostgrestCatalogueSearch(http, session, new MemoryOwnerStore { Owner = "user-a" }, "anon-key"), server);
    }

    [Fact]
    public async Task ASearchCallsTheRpcAsTheSignedInUserAndReadsTheFoods()
    {
        var (search, server) = Create();
        server.Reply(HttpStatusCode.OK, ChickenJson);

        var result = await search.SearchAsync("  chicken breast ", 80, Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /rest/v1/rpc/search_catalogue_foods", request.Line);
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
        Assert.Equal("anon-key", request.Headers["apikey"]);
        var body = JsonNode.Parse(request.Body)!;
        Assert.Equal(("chicken breast", 50), (body["query"]!.GetValue<string>(), body["max_results"]!.GetValue<int>()));

        Assert.Equal(CatalogueSearchStatus.Found, result.Status);
        var food = Assert.Single(result.Foods);
        Assert.Equal((Chicken, (string?)null, "usda_fdc", "Chicken breast", 120.0), (food.Id, food.HouseholdId, food.Source, food.Name, food.Energy100!.Value));
        var loggable = food.ToFood()!;
        Assert.Equal(45, loggable.Per100[Nutrient.Sodium]);
        Assert.Equal(174, Assert.Single(loggable.Portions).BaseAmount);
    }

    [Fact]
    public async Task AShortQueryIsNotSentAndFailuresAreReportedNotThrown()
    {
        var (search, server) = Create();
        Assert.Equal((CatalogueSearchStatus.Found, 0), (await search.SearchAsync(" c ", 20, Ct) is var r ? (r.Status, r.Foods.Count) : default));
        Assert.Empty(server.Requests);

        server.Reply(HttpStatusCode.ServiceUnavailable, "{}");
        Assert.Equal(CatalogueSearchStatus.Unavailable, (await search.SearchAsync("chicken", 20, Ct)).Status);
        server.Reply(HttpStatusCode.OK, "<html>captive portal</html>");
        Assert.Equal(CatalogueSearchStatus.Unavailable, (await search.SearchAsync("chicken", 20, Ct)).Status);
        server.Fail();
        Assert.Equal(CatalogueSearchStatus.Unavailable, (await search.SearchAsync("chicken", 20, Ct)).Status);

        var (signedOut, offline) = Create(signedOut: true);
        Assert.Equal(CatalogueSearchStatus.SignedOut, (await signedOut.SearchAsync("chicken", 20, Ct)).Status);
        Assert.Empty(offline.Requests);
    }

    [Fact]
    public void ARowMissingWhatALoggableFoodNeedsIsSkipped()
    {
        var foods = PostgrestCatalogueSearch.Parse((
            "[{'id':'not-a-guid','name':'A','basis':'g'},{'id':'50000000-0000-0000-0000-000000000002','basis':'g'}," +
            "{'id':'50000000-0000-0000-0000-000000000003','name':'Milk','basis':'ml','source':'usda_fdc','energy_100':'oops'}]")
            .Replace('\'', '"'));

        var milk = Assert.Single(foods!);
        Assert.Equal(("Milk", (double?)null, "{}", "[]"), (milk.Name, milk.Energy100, milk.Nutrients100, milk.Portions));
    }

    [Fact]
    public async Task CopyingAResultAddsOneHouseholdFoodThatUploadsWithItsCatalogueId()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
        await t.Db.Execute("DELETE FROM ps_crud");
        var food = PostgrestCatalogueSearch.Parse(ChickenJson)![0];

        var id = await FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, food, Clock);
        var again = await FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, food, Clock);

        Assert.Equal(FoodCopy.IdFor(Guid.Parse(Household), Guid.Parse(Chicken)).ToString("D"), id);
        Assert.Equal(id, again);
        var copy = (await FoodLibrary.GetAsync(t.Db, id))!;
        Assert.Equal((Household, "usda_fdc", "Chicken breast", 120.0), (copy.HouseholdId, copy.Source, copy.Name, copy.Energy100!.Value));
        Assert.Equal(174, Assert.Single(copy.ToFood()!.Portions).BaseAmount);
        Assert.Contains(await FoodLibrary.SearchAsync(t.Db, "chicken", 20, Household), f => f.Id == id);

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);
        var put = Assert.Single(writer.Writes);
        Assert.Equal(("PUT", "foods", id, Chicken, Household), (put.Op, put.Table, put.Id,
            put.Data!["copied_from"]!.GetValue<string>(), put.Data["household_id"]!.GetValue<string>()));
        Assert.IsType<JsonObject>(put.Data["nutrients_100"]);
        Assert.IsType<JsonArray>(put.Data["portions"]);
    }

    [Fact]
    public async Task OnlyCatalogueFoodsAreCopiedAndOnlyIntoAHouseholdOnThisDevice()
    {
        await using var t = await TestDatabase.OpenAsync();
        var food = PostgrestCatalogueSearch.Parse(ChickenJson)![0];

        await Assert.ThrowsAsync<InvalidOperationException>(() => FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, food, Clock));
        food.HouseholdId = Household;
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, food, Clock));
        food.HouseholdId = null;
        food.Source = "custom";
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, food, Clock));
    }

    [Fact]
    public async Task HouseholdSearchMatchesEachWordAsTheCatalogueDoesAndCopiesAreFoundById()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
        var id = await FoodLibrary.CopyFromCatalogueAsync(t.Db, Household, PostgrestCatalogueSearch.Parse(ChickenJson)![0], Clock);

        Assert.Contains(await FoodLibrary.SearchAsync(t.Db, "chick brea", 20, Household), f => f.Id == id);
        Assert.Contains(await FoodLibrary.SearchAsync(t.Db, "BREAST chicken", 20, Household), f => f.Id == id);
        Assert.Empty(await FoodLibrary.SearchAsync(t.Db, "chicken thigh", 20, Household));
        // Every word counts, however many: 20 that match and a 21st that doesn't find nothing.
        var matching = string.Join(' ', Enumerable.Repeat("chi", 20));
        Assert.Contains(await FoodLibrary.SearchAsync(t.Db, matching, 20, Household), f => f.Id == id);
        Assert.Empty(await FoodLibrary.SearchAsync(t.Db, matching + " thigh", 20, Household));
        Assert.Empty(await FoodLibrary.SearchAsync(t.Db, new string('c', FoodLibrary.MaxQueryLength + 1), 20, Household));

        Assert.Equal([Chicken], await FoodLibrary.CopiedAsync(t.Db, Household, [Chicken, "50000000-0000-0000-0000-000000000009"]));
        Assert.Empty(await FoodLibrary.CopiedAsync(t.Db, "0192f0c0-0000-7000-8000-0000000000a2", [Chicken]));
        Assert.Empty(await FoodLibrary.CopiedAsync(t.Db, Household, []));
    }

    private sealed class Server : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)?> responses = new();

        public List<(string Line, Dictionary<string, string> Headers, string Body)> Requests { get; } = [];

        public void Reply(HttpStatusCode status, string body) => responses.Enqueue((status, body));

        public void Fail() => responses.Enqueue(null);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(($"{request.Method} {request.RequestUri!.PathAndQuery}", headers, body));
            return responses.Dequeue() is { } reply
                ? new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") }
                : throw new HttpRequestException("connection refused");
        }
    }
}
