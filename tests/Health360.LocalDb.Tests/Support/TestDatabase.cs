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
