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

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static WebApplicationFactory<Program> Create(ServerFactory factory, Stub? stub, string? key = "usda-test-key") =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Usda:ApiKey", key ?? "");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient(UsdaBrandedClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => stub ?? new Stub(HttpStatusCode.OK, "{}")));
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
    public async Task Requires_a_token()
    {
        await using var factory = new ServerFactory();
        using var client = Create(factory, null).CreateClient();

        using var response = await client.GetAsync("/foods/barcode/036000291452/usda", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
