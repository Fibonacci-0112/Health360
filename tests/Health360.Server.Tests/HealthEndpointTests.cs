using System.Net;
using Health360.Server.Tests.Support;

namespace Health360.Server.Tests;

public class HealthEndpointTests(ServerFactory factory) : IClassFixture<ServerFactory>
{
    [Fact]
    public async Task Health_returns_ok_without_a_token()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"status":"ok"}""", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_routes_still_require_a_token()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/notes", UriKind.Relative), TestContext.Current.CancellationToken);

        // The fallback policy runs before routing gives up, so probing reveals nothing without a token.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
