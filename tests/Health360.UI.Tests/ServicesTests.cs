using Health360.LocalDb;
using Health360.UI.Auth;
using Health360.UI.Data;
using Health360.UI.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using PowerSync.Common.Client;

namespace Health360.UI.Tests;

public class ServicesTests
{
    /// <summary>The registrations Health360.Maui makes, minus the platform SQLite and secure storage.</summary>
    [Fact]
    public async Task TheHostsGraphResolvesWithoutCycles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "health360-ui-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")))
            .AddSingleton<ISessionStore>(new MemorySessionStore())
            .AddSingleton<IDatabaseOwnerStore>(new MemoryOwnerStore())
            .AddLocalDatabase(_ => new SQLOpenOptions { DbFilename = Path.Combine(directory, "test.db") }, _ => Path.Combine(directory, "photos"))
            .AddSingleton<IPhotoCapture>(new FakePhotoCapture())
            .AddHealth360Ui();

        var completed = false;
        try
        {
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            await using (provider)
            {
                // Sync and uploads take their token from sign-in's session.
                Assert.Same(provider.GetRequiredService<SessionManager>(), provider.GetRequiredService<ISupabaseSession>());
                Assert.NotNull(provider.GetRequiredService<AuthService>());
                Assert.NotNull(provider.GetRequiredService<ProfileService>());
                Assert.IsType<DeviceLocale>(provider.GetRequiredService<IDeviceLocale>());
                Assert.IsType<LocalAccount>(provider.GetRequiredService<ILocalAccount>());
                Assert.NotNull(provider.GetRequiredService<PhotoUploader>());

                // Stop and await the background workers before closing, so nothing still touches the files.
                await provider.GetRequiredService<SyncState>().DisposeAsync();
                await provider.GetRequiredService<PhotoUploader>().DisposeAsync();
                await provider.GetRequiredService<PowerSyncDatabase>().Close();
            }

            completed = true;
        }
        finally
        {
            // Only report a leftover directory when the test itself passed, so it never hides the real failure.
            DeleteDirectory(directory, throwOnFailure: completed);
        }
    }

    // Native SQLite handles can take a moment to be released after Close().
    private static void DeleteDirectory(string directory, bool throwOnFailure)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 20)
                {
                    if (throwOnFailure)
                    {
                        throw new IOException($"Could not delete the test directory '{directory}'.", error);
                    }

                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
