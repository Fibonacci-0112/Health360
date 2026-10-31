using Health360.LocalDb;
using Health360.UI.Auth;
using Health360.UI.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Health360.UI;

public static class UiServices
{
    /// <summary>How long an Auth request may take before the app reports the server as unreachable.</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Registers sign-in, the session (as the <see cref="ISupabaseSession"/> sync and uploads use), the
    /// profile, sync state and the body data. The host registers <see cref="SyncEndpoints"/>, <see cref="ISessionStore"/>
    /// and the local database (<see cref="LocalDbServices.AddLocalDatabase"/>).
    /// </summary>
    public static IServiceCollection AddHealth360Ui(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDeviceLocale, DeviceLocale>();
        services.AddSingleton(provider =>
        {
            var endpoints = provider.GetRequiredService<SyncEndpoints>();
            // The platform's default handler (AndroidMessageHandler on Android), so the OS trust store applies.
            var http = new HttpClient { BaseAddress = endpoints.SupabaseUrl, Timeout = AuthTimeout };
            return new SupabaseAuthClient(http, endpoints.SupabaseApiKey, provider.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton<SessionManager>();
        services.AddSingleton<ISupabaseSession>(provider => provider.GetRequiredService<SessionManager>());
        services.AddSingleton<ILocalAccount, LocalAccount>();
        services.AddSingleton<AuthService>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<SyncState>();
        services.AddSingleton<BodyData>();
        return services;
    }
}
