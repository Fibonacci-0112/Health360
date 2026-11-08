using System.Net;
using System.Net.Http.Json;
using Health360.Contracts.Nutrition;
using Health360.Server.Foods;
using Health360.Server.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.Server.Tests;

/// <summary><c>GET /foods/barcode/{code}/off</c> against recorded Open Food Facts responses (no live calls).</summary>
public sealed class BarcodeOpenFoodFactsTests
{
    private static readonly Guid UserA = Guid.Parse("018f0000-0000-7000-8000-00000000000a");

    private const string Found = """
        {"code":"3017620422003","status":1,"product":{
          "product_name":"Nutella","brands":"Ferrero, Nutella","quantity":"400 g","serving_quantity":"15",
          "nutriments":{"energy-kcal_100g":539,"energy_100g":2252,"proteins_100g":6.3,"carbohydrates_100g":"57.5","fat_100g":30.9}}}
        """;

    private sealed class Stub(HttpStatusCode status, string body, Func<HttpRequestMessage, (HttpStatusCode, string)>? respond = null, Exception? throws = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (throws is not null)
            {
                return Task.FromException<HttpResponseMessage>(throws);
            }

            var (code, text) = respond?.Invoke(request) ?? (status, body);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(text) });
        }
    }

    private static WebApplicationFactory<Program> Create(ServerFactory factory, Stub stub, int perHour = 120, int upstreamPerMinute = 80, TimeProvider? time = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("OpenFoodFacts:RequestsPerUserPerHour", perHour.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("OpenFoodFacts:MaxUpstreamRequestsPerMinute", upstreamPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.ConfigureTestServices(services =>
            {
                if (time is not null)
                {
                    services.AddSingleton(time);
                }

                services.AddHttpClient(OpenFoodFactsClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        });

    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;

        public void Advance(TimeSpan by) => Ticks += by.Ticks;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> app, Guid? user = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestTokens.User(user ?? UserA));
        return client;
    }

    private static Stub ProductJson(string product) => new(HttpStatusCode.OK, $$"""{"code":"3017620422003","status":1,"product":{{product}}}""");

    [Fact]
    public async Task Finds_a_product_by_its_13_digit_form_with_a_user_agent_and_no_key()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Found);
        using var client = Authorized(Create(factory, stub));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(BarcodeSource.OpenFoodFacts, match!.Source);
        Assert.Equal("Nutella", match.Name);
        Assert.Equal("Ferrero", match.Brand);
        Assert.Equal("03017620422003", match.Gtin);
        Assert.Equal("Mass", match.Basis.ToString());
        Assert.Equal(539, match.Energy);
        Assert.Equal(57.5, match.Carbohydrate);
        Assert.Equal(15, match.ServingSize);
        Assert.Equal(["/api/v2/product/3017620422003.json"], stub.Paths);
        Assert.Contains("Health360", stub.Last!.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        Assert.False(stub.Last.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task A_upc_a_is_asked_for_with_one_leading_zero()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Found);
        using var client = Authorized(Create(factory, stub));

        await client.GetAsync("/foods/barcode/036000291452/off", TestContext.Current.CancellationToken);

        Assert.Equal(["/api/v2/product/0036000291452.json"], stub.Paths);
    }

    [Fact]
    public async Task An_ean_8_is_tried_in_both_its_forms()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.NotFound, "{}");
        using var client = Authorized(Create(factory, stub));

        using var response = await client.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(["/api/v2/product/0000096385074.json", "/api/v2/product/96385074.json"], stub.Paths);
    }

    [Fact]
    public async Task A_misread_code_is_a_validation_error_and_calls_nothing()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Found);
        using var client = Authorized(Create(factory, stub));

        using var response = await client.GetAsync("/foods/barcode/036000291453/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(stub.Last);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, """{"code":"3017620422003","status":0,"status_verbose":"product not found"}""")]
    public async Task An_unknown_product_is_not_found_whichever_way_Open_Food_Facts_says_so(HttpStatusCode status, string body)
    {
        await using var factory = new ServerFactory();
        using var client = Authorized(Create(factory, new Stub(status, body)));

        using var response = await client.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failure_timeout_or_unreadable_body_is_a_502()
    {
        await using var factory = new ServerFactory();
        using var down = Authorized(Create(factory, new Stub(HttpStatusCode.InternalServerError, "")));
        using var timeout = Authorized(Create(factory, new Stub(HttpStatusCode.OK, "", throws: new TaskCanceledException("timed out"))));
        using var garbage = Authorized(Create(factory, new Stub(HttpStatusCode.OK, "<html>not json")));

        foreach (var client in new[] { down, timeout, garbage })
        {
            using var response = await client.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("""{"product_name":"Cola","quantity":"1.5 L","nutriments":{"energy-kcal_100g":42}}""", "Volume")]
    [InlineData("""{"product_name":"Juice","quantity":"33 cl","nutriments":{"energy-kcal_100g":45}}""", "Volume")]
    [InlineData("""{"product_name":"Soda","quantity":"12 fl oz","nutriments":{"energy-kcal_100g":40}}""", "Volume")]
    [InlineData("""{"product_name":"Milk","quantity":"1","product_quantity_unit":"l","nutriments":{"energy-kcal_100g":46}}""", "Volume")]
    [InlineData("""{"product_name":"Flour","quantity":"1 kg","nutriments":{"energy-kcal_100g":350}}""", "Mass")]
    [InlineData("""{"product_name":"Bar","quantity":"6 x 40g","nutriments":{"energy-kcal_100g":400}}""", "Mass")]
    [InlineData("""{"product_name":"Rice","quantity":"8 oz","nutriments":{"energy-kcal_100g":360}}""", "Mass")]
    public async Task The_basis_comes_from_the_quantity_unit(string product, string basis)
    {
        await using var factory = new ServerFactory();
        using var client = Authorized(Create(factory, ProductJson(product)));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(basis, match!.Basis.ToString());
    }

    [Theory]
    [InlineData("""{"product_name":"Mystery","nutriments":{"energy-kcal_100g":100}}""")]
    [InlineData("""{"product_name":"Eggs","quantity":"12 pieces","nutriments":{"energy-kcal_100g":140}}""")]
    public async Task A_product_whose_basis_cannot_be_told_is_skipped_not_guessed(string product)
    {
        await using var factory = new ServerFactory();
        using var client = Authorized(Create(factory, ProductJson(product)));

        using var response = await client.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Energy_falls_back_to_kilojoules_and_missing_or_negative_values_stay_unknown()
    {
        await using var factory = new ServerFactory();
        var product = """{"product_name":"X","quantity":"100 g","nutriments":{"energy_100g":418.4,"proteins_100g":-1,"fat_100g":"abc"}}""";
        using var client = Authorized(Create(factory, ProductJson(product)));

        var match = await client.GetFromJsonAsync<BarcodeMatch>("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(100, match!.Energy!.Value, 6);
        Assert.Null(match.Protein);
        Assert.Null(match.Carbohydrate);
    }

    [Fact]
    public async Task Each_user_gets_a_fixed_number_of_lookups_and_the_rest_never_reach_Open_Food_Facts()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.OK, Found);
        var app = Create(factory, stub, perHour: 2);
        using var userA = Authorized(app);
        using var userB = Authorized(app, Guid.Parse("018f0000-0000-7000-8000-00000000000b"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await userA.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);
            statuses.Add(response.StatusCode);
        }

        using var other = await userB.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(3, stub.Paths.Count);
    }

    [Fact]
    public async Task All_users_share_a_rolling_per_minute_budget_of_upstream_requests()
    {
        await using var factory = new ServerFactory();
        var stub = new Stub(HttpStatusCode.NotFound, "{}");
        var clock = new Clock();
        var app = Create(factory, stub, upstreamPerMinute: 3, time: clock);
        using var userA = Authorized(app);
        using var userB = Authorized(app, Guid.Parse("018f0000-0000-7000-8000-00000000000b"));

        // An EAN-8 that Open Food Facts doesn't have is asked for in two forms: two requests.
        using var miss = await userA.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);
        // Another user's lookup gets the one request left, then the shared budget is spent.
        using var spent = await userB.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);
        var callsWhenSpent = stub.Paths.Count;
        using var refused = await userA.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);
        var callsAfterRefused = stub.Paths.Count;
        clock.Advance(TimeSpan.FromSeconds(30)); // still inside the rolling minute: nothing has renewed
        using var stillSpent = await userA.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(31));
        using var renewed = await userA.GetAsync("/foods/barcode/96385074/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, miss.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, spent.StatusCode);
        Assert.Equal(3, callsWhenSpent);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(3, callsAfterRefused); // the refused lookup made no request
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stillSpent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, renewed.StatusCode);
    }

    [Fact]
    public async Task Requires_a_token()
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, new Stub(HttpStatusCode.OK, Found)).CreateClient();

        using var response = await client.GetAsync("/foods/barcode/3017620422003/off", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
