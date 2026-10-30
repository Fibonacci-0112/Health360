using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using PowerSync.Common.Client;

namespace Health360.LocalDb.Tests;

public class ServicesTests
{
    [Fact]
    public async Task ResolvesOnceTheHostRegistersItsParts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "health360-services-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(new FixedClock(DateTimeOffset.UnixEpoch))
            .AddSingleton(new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")))
            .AddSingleton<ISupabaseSession>(new FixedSession(null))
            .AddSingleton<IDatabaseOwnerStore>(new MemoryOwnerStore())
            .AddLocalDatabase(_ => new SQLOpenOptions { DbFilename = Path.Combine(directory, "test.db") });

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using (provider)
        {
            var local = provider.GetRequiredService<LocalDatabase>();
            Assert.Same(provider.GetRequiredService<PowerSyncDatabase>(), local.Db);
            Assert.IsType<PostgrestWriter>(provider.GetRequiredService<IRemoteWriter>());
            // The host's clock is kept, not replaced by the system clock.
            Assert.IsType<FixedClock>(provider.GetRequiredService<TimeProvider>());
            await local.Db.Init();
            await local.Db.Close();
        }

        Directory.Delete(directory, recursive: true);
    }
}
