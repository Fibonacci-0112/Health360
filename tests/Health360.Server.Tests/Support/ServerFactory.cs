using Health360.Server.Ai;
using Health360.Server.Auth;
using Health360.Server.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Health360.Server.Tests.Support;

/// <summary>The API host with a fake Supabase Auth and, optionally, a fake database.</summary>
public sealed class ServerFactory : WebApplicationFactory<Program>
{
    public FakeSupabaseAuth Auth { get; } = new();

    public FakeAnthropic Anthropic { get; } = new();

    /// <summary>The Anthropic API key; null leaves the API without one.</summary>
    public string? AnthropicApiKey { get; init; } = "test-anthropic-key";

    /// <summary>The legacy HS256 secret; null for a project that signs with asymmetric keys only.</summary>
    public string? JwtSecret { get; init; } = TestTokens.JwtSecret;

    public string ConnectionString { get; init; } = "Host=unused.invalid";

    /// <summary>Replaces the real database when set.</summary>
    public IUserDatabase? Database { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Supabase:Url", TestTokens.SupabaseUrl);
        builder.UseSetting("Supabase:ApiKey", TestTokens.ApiKey);
        builder.UseSetting("Supabase:JwtSecret", JwtSecret ?? "");
        builder.UseSetting("ConnectionStrings:Database", ConnectionString);
        builder.UseSetting("Anthropic:ApiKey", AnthropicApiKey ?? "");
        builder.UseSetting("Anthropic:MaxRetries", "0");

        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(SupabaseSessionVerifier.HttpClientName).ConfigurePrimaryHttpMessageHandler(Auth.CreateHandler);
            services.AddHttpClient(StructuredClaude.HttpClientName).ConfigurePrimaryHttpMessageHandler(Anthropic.CreateHandler);
            if (Database is not null)
            {
                services.Replace(ServiceDescriptor.Singleton(Database));
            }
        });
    }

    public HttpClient CreateClient(string? token)
    {
        var client = CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }
}
