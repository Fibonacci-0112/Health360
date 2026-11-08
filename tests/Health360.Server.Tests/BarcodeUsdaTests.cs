using System.Net;
using System.Net.Http.Json;
using Health360.Contracts.Nutrition;
using Health360.Server.Foods;
using Health360.Server.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.Server.Tests;

/// <summary><c>GET /foods/barcode/{code}/usda</c> against recorded FoodData Central responses (no live calls).</summary>
public sealed class BarcodeUsdaTests
{
    private static readonly Guid UserA = Guid.Parse("018f0000-0000-7000-8000-00000000000a");

    private const string Search = """
        {"foods":[
          {"fdcId":1,"description":"OTHER","gtinUpc":"036000291453","foodNutrients":[]},
          {"fdcId":2095139,"description":"CHEERIOS","brandOwner":"General Mills","gtinUpc":"36000291452","servingSize":28,"servingSizeUnit":"g",
           "foodNutrients":[{"nutrientId":1008,"value":375},{"nutrientId":1003,"value":10},{"nutrientId":1005,"value":75}]}]}
        """;

    private sealed class Stub(HttpStatusCode status, string body, Func<HttpRequestMessage, string>? respond = null, Exception? throws = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public int Calls { get; private set; }

        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Calls++;
            Queries.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["query"] ?? "");
            return throws is not null
                ? Task.FromException<HttpResponseMessage>(throws)
                : Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(respond?.Invoke(request) ?? body) });
        }
    }

    private static WebApplicationFactory<Program> Create(ServerFactory factory, Stub? stub, string? key = "usda-test-key", int perHour = 120, int upstreamPerHour = 900, TimeProvider? time = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Usda:ApiKey", key ?? "");
            builder.UseSetting("Usda:MaxUpstreamRequestsPerHour", upstreamPerHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Usda:RequestsPerUserPerHour", perHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
            {
                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                services.AddHttpClient(UsdaBrandedClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => stub ?? new Stub(HttpStatusCode.OK, "{}"));
            });
        });

    [Fact]
    public async Task Finds_the_exact_gtin_whatever_its_length_and_sends_the_key_in_a_header()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Search);
        using var client = Create(factory, stub).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/0036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal("CHEERIOS", match!.Name);
        Assert.Equal("00036000291452", match.Gtin);
        Assert.Equal(375, match.Energy);
        Assert.Null(match.Fat);
        Assert.Equal("usda-test-key", stub.Last!.Headers.GetValues("X-Api-Key").Single());
        Assert.DoesNotContain("usda-test-key", stub.Last.RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_misread_code_is_a_validation_error_and_calls_nothing()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Search);
        using var client = Create(factory, stub).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        using var response = await client.GetAsync("/foods/barcode/036000291453/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.Last);
    }

    [Fact]
    public async Task An_unknown_code_is_not_found()
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, new Stub(HttpStatusCode.OK, """{"foods":[]}""")).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        using var response = await client.GetAsync("/foods/barcode/4006381333931/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_key_the_lookup_answers_503_and_with_USDA_down_502()
    {
        await using var factory = new ServerFactory();
        using var noKey = Create(factory, null, key: null).CreateClient();
        noKey.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));
        using var down = Create(factory, new Stub(HttpStatusCode.InternalServerError, "")).CreateClient();
        down.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        using var a = await noKey.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        using var b = await down.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, a.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, b.StatusCode);
    }

    [Fact]
    public async Task Of_several_records_with_the_gtin_the_latest_published_wins()
    {
        await using var factory = new ServerFactory();
        var body = """
            {"foods":[
              {"fdcId":10,"description":"OLD","gtinUpc":"36000291452","publishedDate":"2019-04-01","foodNutrients":[{"nutrientId":1008,"value":400}]},
              {"fdcId":11,"description":"NEW","gtinUpc":"036000291452","publishedDate":"2024-10-31","foodNutrients":[{"nutrientId":1008,"value":375}]},
              {"fdcId":12,"description":"MIDDLE","gtinUpc":"36000291452","publishedDate":"2021-01-01","foodNutrients":[]}]}
            """;
        using var client = Create(factory, new Stub(HttpStatusCode.OK, body)).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal("NEW", match!.Name);
        Assert.Equal(375, match.Energy);
    }

    [Fact]
    public async Task Reads_later_pages_up_to_the_page_count()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, "", request => request.RequestUri!.Query.Contains("pageNumber=2", StringComparison.Ordinal)
            ? """{"totalPages":2,"foods":[{"fdcId":7,"description":"ON PAGE TWO","gtinUpc":"36000291452","foodNutrients":[]}]}"""
            : """{"totalPages":2,"foods":[{"fdcId":6,"description":"OTHER","gtinUpc":"1","foodNutrients":[]}]}""");
        using var client = Create(factory, stub).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal("ON PAGE TWO", match!.Name);
        Assert.Equal(2, stub.Calls);
    }

    [Fact]
    public async Task A_timeout_or_unreadable_response_from_USDA_is_a_502()
    {
        await using var factory = new ServerFactory();
        using var timeout = Create(factory, new Stub(HttpStatusCode.OK, "", throws: new TaskCanceledException("timed out"))).CreateClient();
        timeout.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));
        using var garbage = Create(factory, new Stub(HttpStatusCode.OK, "<html>not json")).CreateClient();
        garbage.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        using var a = await timeout.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        using var b = await garbage.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, a.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, b.StatusCode);
    }

    [Fact]
    public async Task Each_user_gets_a_fixed_number_of_lookups_and_the_rest_never_reach_USDA()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Search);
        var app = Create(factory, stub, perHour: 2);
        using var userA = app.CreateClient();
        userA.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));
        using var userB = app.CreateClient();
        userB.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(Guid.Parse("018f0000-0000-7000-8000-00000000000b")));

        var codes = new[] { "036000291452", "036000291452", "036000291452" };
        var statuses = new List<HttpStatusCode>();
        foreach (var code in codes)
        {
            using var response = await userA.GetAsync($"/foods/barcode/{code}/usda", TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        using var other = await userB.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(3, stub.Calls); // two for A, one for B: A's third never reached USDA
    }

    // A real response shape: USDA indexes a code exactly as stored, so a search only finds it by that form.
    private static string FoodJson(string gtinUpc, string unit, double size = 240) => $$"""
        {"foods":[{"fdcId":2625976,"description":"LATTE","gtinUpc":"{{gtinUpc}}","servingSize":{{size}},"servingSizeUnit":"{{unit}}","foodNutrients":[{"nutrientId":1008,"value":40}]}]}
        """;

    private static Stub StoredAs(string stored, string unit) =>
        new(HttpStatusCode.OK, "", request => System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["query"] == stored
            ? FoodJson(stored, unit)
            : """{"foods":[]}""");

    [Fact]
    public async Task Searches_the_code_with_its_leading_zeros_not_trimmed()
    {
        await using var factory = new ServerFactory();
        var stub = StoredAs("00016000275287", "g");
        using var client = Create(factory, stub).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/016000275287/usda", TestContext.Current.CancellationToken);

        Assert.Equal("00016000275287", match!.Gtin);
        Assert.Equal(["00016000275287"], stub.Queries);
    }

    [Fact]
    public async Task Tries_the_shorter_padded_forms_when_USDA_stores_the_code_shorter()
    {
        await using var factory = new ServerFactory();
        var stub = StoredAs("0856413007293", "MLT"); // the 13-digit form USDA holds for this latte
        using var client = Create(factory, stub).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/856413007293/usda", TestContext.Current.CancellationToken);

        Assert.Equal("00856413007293", match!.Gtin);
        Assert.Equal(["00856413007293", "0856413007293"], stub.Queries);
    }

    [Theory]
    [InlineData("MLT", "Volume")]
    [InlineData("ml", "Volume")]
    [InlineData("GRM", "Mass")]
    [InlineData("g", "Mass")]
    public async Task Maps_the_serving_unit_to_the_basis(string unit, string basis)
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, StoredAs("00856413007293", unit)).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/856413007293/usda", TestContext.Current.CancellationToken);

        Assert.Equal(basis, match!.Basis.ToString());
        Assert.Equal(240, match.ServingSize);
    }

    [Fact]
    public async Task A_record_with_an_unrecognised_unit_is_skipped_not_given_a_guessed_basis()
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, StoredAs("00856413007293", "OZA")).CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));

        using var response = await client.GetAsync("/foods/barcode/856413007293/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        // The budget reads the monotonic clock; tests move it with Advance and can step wall time back separately.
        public long Ticks { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;

        public void Advance(TimeSpan by)
        {
            Ticks += by.Ticks;
            Now += by;
        }
    }

    [Fact]
    public async Task All_users_share_a_rolling_hourly_budget_of_USDA_requests()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, """{"foods":[]}""");
        var clock = new Clock(DateTimeOffset.Parse("2026-11-08T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var app = Create(factory, stub, upstreamPerHour: 4, time: clock);
        using var userA = app.CreateClient();
        userA.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(UserA));
        using var userB = app.CreateClient();
        userB.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(Guid.Parse("018f0000-0000-7000-8000-00000000000b")));

        // A UPC-A that USDA doesn't have is searched in three forms (14, 13 and 12 digits): three requests.
        using var miss = await userA.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        // Another user's lookup gets the one request left, then the shared budget is spent.
        using var spent = await userB.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        var callsWhenSpent = stub.Calls;
        using var refused = await userA.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        var callsAfterRefused = stub.Calls;
        // Half an hour later the four requests are still inside USDA's rolling hour: nothing has renewed.
        clock.Advance(TimeSpan.FromMinutes(30));
        using var stillSpent = await userA.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);
        var callsAfterHalfHour = stub.Calls;
        clock.Now = clock.Now.AddDays(-2); // wall time stepped back: it must not matter
        clock.Advance(TimeSpan.FromMinutes(31));
        using var renewed = await userA.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, spent.StatusCode);
        Assert.Equal(4, callsWhenSpent);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(4, callsAfterRefused); // the refused lookup made no request
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stillSpent.StatusCode);
        Assert.Equal(4, callsAfterHalfHour);
        Assert.Equal(HttpStatusCode.NotFound, renewed.StatusCode);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, null).CreateClient();

        using var response = await client.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
