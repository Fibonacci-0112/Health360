using Health360.Server.Auth;
using Health360.Server.Data;
using Health360.Server.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Health360.Server.Tests;

/// <summary>
/// The settings the host checks before it starts (<c>ValidateOnStart</c>), so a misconfigured
/// deployment fails at once instead of passing /health and failing every request. Runs the same
/// registrations as Program.cs and the startup validator directly: through WebApplicationFactory, a
/// host that fails to start sometimes surfaces as ObjectDisposedException instead of the real error.
/// </summary>
public class StartupValidationTests
{
    private static readonly Dictionary<string, string?> ValidSettings = new()
    {
        ["Supabase:Url"] = TestTokens.SupabaseUrl,
        ["Supabase:ApiKey"] = TestTokens.ApiKey,
        ["ConnectionStrings:Database"] = "Host=unused.invalid",
    };

    [Fact]
    public void Starts_with_the_required_settings() => Validate(ValidSettings);

    [Theory]
    [InlineData("ConnectionStrings:Database", "Database")]
    [InlineData("Supabase:Url", "Url")]
    [InlineData("Supabase:ApiKey", "ApiKey")]
    public void Refuses_to_start_without_a_required_setting(string setting, string member)
    {
        var settings = new Dictionary<string, string?>(ValidSettings) { [setting] = "" };

        var error = Assert.Throws<OptionsValidationException>(() => Validate(settings));

        Assert.Contains($"'{member}'", error.Message, StringComparison.Ordinal);
    }

    private static void Validate(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSupabaseAuthentication(configuration);
        services.AddUserDatabase(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }
}
