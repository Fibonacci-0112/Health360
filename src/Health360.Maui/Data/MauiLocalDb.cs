using Health360.LocalDb;
using PowerSync.Common.MDSQLite;
using PowerSync.Maui.SQLite;

namespace Health360.Maui.Data;

public static class MauiLocalDb
{
    /// <summary>The SQLite file, in the app's private data directory on Android and Windows.</summary>
    public const string FileName = "health360.db";

    /// <summary>
    /// Registers the offline database with the platform's SQLite and the PowerSync core extension.
    /// <see cref="SyncEndpoints"/> come from the build (<see cref="AppEndpoints"/>) and
    /// <see cref="ISupabaseSession"/> from sign-in (<c>Health360.UI</c>'s <c>SessionManager</c>).
    /// </summary>
    public static IServiceCollection AddMauiLocalDatabase(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseOwnerStore, PreferencesOwnerStore>();
        return services.AddLocalDatabase(_ => new MAUISQLiteDBOpenFactory(new MDSQLiteOpenFactoryOptions
        {
            DbFilename = Path.Combine(FileSystem.AppDataDirectory, FileName),
        }));
    }
}
