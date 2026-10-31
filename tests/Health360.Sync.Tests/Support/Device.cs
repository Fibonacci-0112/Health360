using Health360.Contracts.Notes;
using PowerSync.Common.Client;

namespace Health360.Sync.Tests.Support;

/// <summary>
/// One app install: its own SQLite file and the app's own sync stack (<see cref="LocalDatabase"/>,
/// <see cref="SupabaseConnector"/>, <see cref="PostgrestWriter"/>), signed in as <see cref="User"/>.
/// </summary>
public sealed class Device : IAsyncDisposable
{
    private readonly string directory;
    private readonly HttpClient http;

    private Device(string directory, SyncEndpoints endpoints, TestUser user)
    {
        this.directory = directory;
        User = user;
        Session = new SwitchableSession(user.Session);
        Owner = new MemoryOwnerStore();
        http = new HttpClient { BaseAddress = endpoints.SupabaseUrl };
        Writer = new PostgrestWriter(http, Session, Owner, endpoints.SupabaseApiKey);
        Db = new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = new SQLOpenOptions { DbFilename = Path.Combine(directory, "app.db") },
            Schema = AppSchema.Schema,
        });
        var connector = new SupabaseConnector(Session, Owner, Writer, endpoints, TimeProvider.System, Log);
        Local = new LocalDatabase(Db, Owner, connector);
    }

    public TestUser User { get; private set; }

    public PowerSyncDatabase Db { get; }

    public LocalDatabase Local { get; }

    public IRemoteWriter Writer { get; }

    public SwitchableSession Session { get; }

    public MemoryOwnerStore Owner { get; }

    /// <summary>What the connector logged, quarantined writes included.</summary>
    public ListLogger<SupabaseConnector> Log { get; } = new();

    public static Device Create(SyncBackend backend, TestUser user)
    {
        var directory = Path.Combine(Path.GetTempPath(), "health360-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new Device(directory, backend.Endpoints, user);
    }

    /// <summary>Starts syncing as the signed-in user, as the app does at sign-in, and waits for the first full sync.</summary>
    public async Task ConnectAsync()
    {
        await Local.ConnectAsync(User.UserId);
        await Db.WaitForFirstSync();
    }

    public Task DisconnectAsync() => Local.DisconnectAsync();

    /// <summary>
    /// Another user signs in on this device. The session changes first, as it does in the app; the
    /// database is rebound (and so cleared) when the app connects as them.
    /// </summary>
    public void SignInAs(TestUser user)
    {
        User = user;
        Session.Token = user.Session;
    }

    public Task<string> AddNoteAsync(string body, string? profileId = null) =>
        Notes.AddAsync(Db, profileId ?? User.ProfileId, new NoteInput(body), TimeProvider.System);

    public Task<NoteRow[]> NotesAsync() => Notes.ListAsync(Db);

    public async Task<long> PendingAsync() => await Uploads.PendingUploadCountAsync(Db);

    /// <summary>Waits until every queued write has been sent or quarantined.</summary>
    public Task DrainAsync() => Wait.ForAsync(PendingAsync, n => n == 0);

    /// <summary>
    /// Waits until the server has sent this row back. <c>ps_oplog</c> holds only what came from the
    /// server, so once it is there the device's streams have caught up past the write that made it.
    /// </summary>
    public Task SyncedAsync(string table, string id) =>
        Wait.ForAsync(
            async () => await Db.GetAll<Row>("SELECT row_id AS Id FROM ps_oplog WHERE row_type = ? AND row_id = ?", [table, id]),
            rows => rows.Length == 1);

    public async ValueTask DisposeAsync()
    {
        await Local.DisposeAsync();
        http.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the native library still holds; the OS cleans the temp directory.
        }
    }

    public sealed class Row
    {
        public string Id { get; set; } = "";
    }
}
