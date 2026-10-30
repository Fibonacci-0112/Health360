using Health360.LocalDb;
using PowerSync.Common.Client;

namespace Health360.UI.Tests.Support;

/// <summary>
/// A real PowerSync database (the SDK's native core and SQLite) in a temporary file, deleted afterwards.
/// Nothing connects it to a server. The same helper as in Health360.LocalDb.Tests.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string directory;

    private TestDatabase(string directory, PowerSyncDatabase db)
    {
        this.directory = directory;
        Db = db;
    }

    public PowerSyncDatabase Db { get; }

    public static async Task<TestDatabase> OpenAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "health360-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = new SQLOpenOptions { DbFilename = Path.Combine(directory, "test.db") },
            Schema = AppSchema.Schema,
        });
        await db.Init();
        return new TestDatabase(directory, db);
    }

    /// <summary>
    /// A profile row as the server's sign-up trigger creates it and sync brings it down: inserted, then
    /// taken off the upload queue, since a synced row was never a local write.
    /// </summary>
    public Task SeedProfileAsync(string profileId, string userId, string? unitPrefs = "{}", string? onboardedAt = null, string displayName = "") =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO profiles (id, user_id, display_name, unit_prefs, onboarded_at, timezone, currency) VALUES (?, ?, ?, ?, ?, 'UTC', 'USD')",
                [profileId, userId, displayName, unitPrefs, onboardedAt]);
            await tx.Execute("DELETE FROM ps_crud WHERE id = (SELECT max(id) FROM ps_crud)");
        });

    public async ValueTask DisposeAsync()
    {
        await Db.Close();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the native library still holds; the OS cleans the temp directory.
        }
    }
}
