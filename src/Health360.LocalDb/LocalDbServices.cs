using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

public static class LocalDbServices
{
    /// <summary>
    /// Registers the PowerSync database, the PostgREST writer, the connector and <see cref="LocalDatabase"/>.
    /// The host registers what is platform- or session-specific: <see cref="SyncEndpoints"/>,
    /// <see cref="ISupabaseSession"/>, <see cref="IDatabaseOwnerStore"/> and logging.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="source">The SQLite file to open, through the platform's open factory.</param>
    public static IServiceCollection AddLocalDatabase(this IServiceCollection services, Func<IServiceProvider, IDatabaseSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(provider => new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = source(provider),
            Schema = AppSchema.Schema,
        }));
        services.AddSingleton<IRemoteWriter>(provider =>
        {
            var endpoints = provider.GetRequiredService<SyncEndpoints>();
            // The platform's default handler (AndroidMessageHandler on Android), so the OS trust store applies.
            var http = new HttpClient { BaseAddress = endpoints.SupabaseUrl };
            return new PostgrestWriter(
                http, provider.GetRequiredService<ISupabaseSession>(), provider.GetRequiredService<IDatabaseOwnerStore>(), endpoints.SupabaseApiKey);
        });
        services.AddSingleton<SupabaseConnector>();
        services.AddSingleton(provider => new LocalDatabase(
            provider.GetRequiredService<PowerSyncDatabase>(),
            provider.GetRequiredService<IDatabaseOwnerStore>(),
            provider.GetRequiredService<SupabaseConnector>(),
            provider.GetService<ILogger<LocalDatabase>>()));
        return services;
    }
}
