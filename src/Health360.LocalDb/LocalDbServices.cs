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
    /// <param name="photoDirectory">
    /// A directory in the app's private storage for progress photos waiting to upload. When given, the
    /// <see cref="PhotoUploader"/> is registered too.
    /// </param>
    public static IServiceCollection AddLocalDatabase(
        this IServiceCollection services,
        Func<IServiceProvider, IDatabaseSource> source,
        Func<IServiceProvider, string>? photoDirectory = null)
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
        if (photoDirectory is not null)
        {
            services.AddSingleton<IPhotoFileStore>(provider => new DirectoryPhotoFileStore(photoDirectory(provider)));
            services.AddSingleton<IPhotoStorage>(provider =>
            {
                var endpoints = provider.GetRequiredService<SyncEndpoints>();
                // No overall timeout: a full-size photo on a slow connection can take a while. Offline fails fast.
                var http = new HttpClient { BaseAddress = endpoints.SupabaseUrl, Timeout = Timeout.InfiniteTimeSpan };
                return new SupabasePhotoStorage(
                    http, provider.GetRequiredService<ISupabaseSession>(), provider.GetRequiredService<IDatabaseOwnerStore>(), endpoints.SupabaseApiKey);
            });
            services.AddSingleton<PhotoUploader>();
        }

        services.AddSingleton(provider => new LocalDatabase(
            provider.GetRequiredService<PowerSyncDatabase>(),
            provider.GetRequiredService<IDatabaseOwnerStore>(),
            provider.GetRequiredService<SupabaseConnector>(),
            provider.GetService<ILogger<LocalDatabase>>(),
            provider.GetService<IPhotoFileStore>()));
        return services;
    }
}
