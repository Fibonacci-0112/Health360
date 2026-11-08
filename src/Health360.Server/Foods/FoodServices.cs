using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

public static class FoodServices
{
    /// <summary>The rate-limiter policy every endpoint that calls USDA must use.</summary>
    public const string RateLimitPolicy = "usda";

    public static IServiceCollection AddFoodLookup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UsdaOptions>()
            .Bind(configuration.GetSection(UsdaOptions.Section))
            .PostConfigure(options => options.ApiKey ??= configuration["FDC_API_KEY"])
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddHttpClient(UsdaBrandedClient.HttpClientName).ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<UsdaBrandedClient>();

        // Lookups share one USDA key, so each signed-in user gets a fixed number per hour. The limiter runs after
        // authorization, so only signed-in users reach it and "sub" is their id. The rejection status and body are
        // set once, with the AI policy.
        services.AddRateLimiter(limiter => limiter.AddPolicy(RateLimitPolicy, context =>
        {
            var permits = context.RequestServices.GetRequiredService<IOptions<UsdaOptions>>().Value.RequestsPerUserPerHour;
            return RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("sub")?.Value ?? "",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
        }));
        return services;
    }
}
