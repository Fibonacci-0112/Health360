namespace Health360.Server.Ai;

public static class AiServices
{
    public static IServiceCollection AddClaude(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AnthropicOptions>()
            .Bind(configuration.GetSection(AnthropicOptions.Section))
            .PostConfigure(options => options.ApiKey ??= configuration["ANTHROPIC_API_KEY"]);

        // The SDK retries and times out on its own; the client's own timeout stays out of its way.
        services.AddHttpClient(StructuredClaude.HttpClientName)
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<StructuredClaude>();
        return services;
    }
}
