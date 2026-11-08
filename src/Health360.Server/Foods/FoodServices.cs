namespace Health360.Server.Foods;

public static class FoodServices
{
    public static IServiceCollection AddFoodLookup(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UsdaOptions>()
            .Bind(configuration.GetSection(UsdaOptions.Section))
            .PostConfigure(options => options.ApiKey ??= configuration["FDC_API_KEY"])
            .ValidateOnStart();
        services.AddHttpClient(UsdaBrandedClient.HttpClientName).ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<UsdaBrandedClient>();
        return services;
    }
}
