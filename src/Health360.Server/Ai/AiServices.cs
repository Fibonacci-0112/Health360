using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Health360.Server.Ai;

public static class AiServices
{
    /// <summary>The rate-limiter policy every endpoint that calls Claude must use.</summary>
    public const string RateLimitPolicy = "ai";

    public static IServiceCollection AddClaude(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.Section))
            .PostConfigure(options => options.ApiKey ??= configuration["ANTHROPIC_API_KEY"]);

        // StructuredClaude is a singleton and keeps one HttpClient, so the factory never rotates its
        // handler. The handler recycles its own connections instead, so DNS changes are picked up.
        // The SDK retries and times out on its own; the client's own timeout stays out of its way.
        services.AddHttpClient(StructuredClaude.HttpClientName)
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
        services.AddSingleton<StructuredClaude>();

        // Every AI request costs money, so each signed-in user gets a fixed number per hour. The
        // limiter runs after authorization, so only signed-in users reach it and "sub" is their id.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, cancellationToken) =>
                new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited" }, cancellationToken));
            limiter.AddPolicy(RateLimitPolicy, context =>
            {
                var permits = context.RequestServices.GetRequiredService<IOptions<AnthropicOptions>>().Value.RequestsPerUserPerHour;
                return RateLimitPartition.GetFixedWindowLimiter(
                    context.User.FindFirst("sub")?.Value ?? "",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
            });
        });

        return services;
    }
}
