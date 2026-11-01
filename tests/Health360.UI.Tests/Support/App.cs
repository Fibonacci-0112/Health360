using Bunit;
using Health360.LocalDb;
using Health360.UI.Auth;
using Health360.UI.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.UI.Tests.Support;

/// <summary>
/// The app's services as the MAUI host wires them, with fakes at the edges: Supabase Auth over a fake
/// HTTP handler, secure storage in memory, the account binding recorded, and a real local database.
/// Uploads have nowhere to go (<see cref="NoWriter"/>).
/// </summary>
public sealed class App : IAsyncDisposable
{
    private App(TestDatabase database)
    {
        Database = database;
    }

    public TestDatabase Database { get; }

    public FakeAuth Auth { get; } = new();

    public MemorySessionStore Store { get; } = new();

    public FakeLocalAccount Local { get; } = new();

    public TestClock Clock { get; } = new();

    public FixedLocale Locale { get; set; } = new();

    public FakePhotoCapture Camera { get; } = new();

    public MemoryPhotoFiles PhotoFiles { get; } = new();

    public TestPhotoStorage PhotoStorage { get; } = new();

    /// <summary>The upload queue, with files in memory and <see cref="PhotoStorage"/> as the server (offline by default).</summary>
    public PhotoUploader Photos => photos ??= new PhotoUploader(Database.Db, PhotoFiles, PhotoStorage, Clock);

    private PhotoUploader? photos;

    public static async Task<App> StartAsync() => new(await TestDatabase.OpenAsync());

    /// <summary>A session as if this user had signed in on an earlier launch.</summary>
    public AuthSession SavedSession(string? userId = null) =>
        new(userId ?? Auth.UserId, "sam@example.com", "access-0", "refresh-0", Clock.Now.AddHours(1));

    public void Register(BunitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Services.AddLogging();
        context.Services.AddSingleton<TimeProvider>(Clock);
        context.Services.AddSingleton<IDeviceLocale>(Locale);
        context.Services.AddSingleton(Database.Db);
        context.Services.AddSingleton<IRemoteWriter>(new NoWriter());
        context.Services.AddSingleton(new SupabaseAuthClient(Auth.Client(), FakeAuth.ApiKey, Clock));
        context.Services.AddSingleton<ISessionStore>(Store);
        context.Services.AddSingleton<SessionManager>();
        context.Services.AddSingleton<ILocalAccount>(Local);
        context.Services.AddSingleton<AuthService>();
        context.Services.AddSingleton<ProfileService>();
        context.Services.AddSingleton<SyncState>();
        context.Services.AddSingleton<BodyData>();
        context.Services.AddSingleton(Photos);
        context.Services.AddSingleton(new PhotoLibrary(PhotoFiles, PhotoStorage, Photos));
        context.Services.AddSingleton<IPhotoCapture>(Camera);
    }

    public AuthService NewAuthService(out SessionManager sessions)
    {
        var client = new SupabaseAuthClient(Auth.Client(), FakeAuth.ApiKey, Clock);
        sessions = new SessionManager(client, Store, Clock, NullLogger<SessionManager>.Instance);
        return new AuthService(sessions, client, Local, NullLogger<AuthService>.Instance);
    }

    public ValueTask DisposeAsync()
    {
        photos?.Dispose();
        return Database.DisposeAsync();
    }

    /// <summary>A server that is never reached: every write fails as if offline.</summary>
    public sealed class NoWriter : IRemoteWriter
    {
        public List<string> Writes { get; } = [];

        public Task PutAsync(string table, string id, System.Text.Json.Nodes.JsonObject data, CancellationToken cancellationToken) => Fail($"PUT {table}");

        public Task PatchAsync(string table, string id, System.Text.Json.Nodes.JsonObject data, CancellationToken cancellationToken) => Fail($"PATCH {table}");

        public Task DeleteAsync(string table, string id, CancellationToken cancellationToken) => Fail($"DELETE {table}");

        private Task Fail(string write)
        {
            Writes.Add(write);
            return Task.FromException(new HttpRequestException("offline"));
        }
    }
}
