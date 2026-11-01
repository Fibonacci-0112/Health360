using PowerSync.Common.Client;

namespace Health360.LocalDb.Tests.Support;

/// <summary>
/// A real PowerSync database (the SDK's native core and SQLite) in a temporary file, deleted afterwards.
/// Nothing connects it to a server: tests drive the upload queue directly.
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
        var directory = Path.Combine(Path.GetTempPath(), "health360-localdb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = new SQLOpenOptions { DbFilename = Path.Combine(directory, "test.db") },
            Schema = AppSchema.Schema,
        });
        await db.Init();
        return new TestDatabase(directory, db);
    }

    /// <summary>A profile row as sync brings it down: in the table, with no upload queued for it.</summary>
    public Task SeedProfileAsync(string profileId, string userId = "user-a") =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO profiles (id, user_id) VALUES (?, ?)", [profileId, userId]);
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

/// <summary>An owner store in memory, standing in for the app's preferences.</summary>
public sealed class MemoryOwnerStore : IDatabaseOwnerStore
{
    public string? Owner { get; set; }

    public string? Load() => Owner;

    public void Save(string userId) => Owner = userId;

    public void Forget() => Owner = null;
}
