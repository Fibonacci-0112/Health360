using System.Net;
using System.Text;
using Health360.Domain.Photos;
using Health360.LocalDb.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.LocalDb.Tests;

/// <summary>The progress-photo upload queue (PLAN.md §3, step 1.1) against a real PowerSync database.</summary>
public sealed class PhotoUploaderTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero));

    private static readonly DateOnly Day = new(2026, 10, 31);

    private static Task<string> AddAsync(PhotoUploader uploader, string pose = "front", byte[]? photo = null) =>
        uploader.AddAsync(Profile, pose, Clock.GetUtcNow(), Day, photo ?? Jpegs.WithGps(), Jpegs.WithGps(), Ct);

    [Fact]
    public async Task AddingStoresStrippedFilesTheRowAndAQueueEntry()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);

        var id = await AddAsync(uploader);

        Assert.True(Guid.TryParse(id, out var guid));
        Assert.Equal(7, guid.Version);
        Assert.Equal([PhotoUploader.PhotoName(id), PhotoUploader.ThumbName(id)], files.List().Order(StringComparer.Ordinal));
        Assert.All(files.Files.Values, f => Assert.True(JpegMetadata.IsStripped(f)));
        Assert.All(files.Files.Values, f => Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(f), StringComparison.Ordinal));

        var row = await t.Db.Get<PhotoRow>(
            "SELECT profile_id AS ProfileId, pose AS Pose, taken_at AS TakenAt, local_date AS LocalDate, storage_path AS StoragePath, " +
            "thumb_path AS ThumbPath FROM progress_photos WHERE id = ?", [id]);
        Assert.Equal(Profile, row.ProfileId);
        Assert.Equal("front", row.Pose);
        Assert.Equal("2026-10-31T09:30:00.000Z", row.TakenAt);
        Assert.Equal("2026-10-31", row.LocalDate);
        Assert.Equal($"{Profile}/{id}.jpg", row.StoragePath);
        Assert.Equal($"{Profile}/{id}_thumb.jpg", row.ThumbPath);
        // The row goes up through the normal upload queue.
        Assert.Equal(1, await Uploads.PendingUploadCountAsync(t.Db));
        Assert.Equal(1, await uploader.PendingCountAsync());
    }

    public static TheoryData<string, string, byte[]> Rejected => new()
    {
        { "not a JPEG", "front", [0x89, 0x50, 0x4E, 0x47] },
        { "truncated", "front", Jpegs.WithGps()[..^3] },
        { "sideways", "front", Jpegs.Rotated() },
        { "unknown pose", "selfie", Jpegs.WithGps() },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public async Task RejectsBadInputBeforeStoringAnything(string name, string pose, byte[] photo)
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);

        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(uploader, pose, photo));

        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
        Assert.True(await Uploads.PendingUploadCountAsync(t.Db) == 0, name);
    }

    [Fact]
    public async Task UploadsTheThumbnailThenThePhotoAndCleansUp()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);

        var report = await uploader.ProcessAsync(Ct);

        Assert.Equal(new PhotoUploadReport(1, 0, false), report);
        Assert.Equal([$"{Profile}/{id}_thumb.jpg", $"{Profile}/{id}.jpg"], storage.Uploads.Select(u => u.Path));
        Assert.All(storage.Uploads, u => Assert.True(JpegMetadata.IsStripped(u.Jpeg)));
        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
    }

    [Fact]
    public async Task KeepsEverythingWhileOfflineAndUploadsLater()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage { Offline = true };
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var first = await AddAsync(uploader);
        var second = await AddAsync(uploader, "back");

        var offline = await uploader.ProcessAsync(Ct);

        Assert.Equal(new PhotoUploadReport(0, 0, true), offline);
        Assert.Equal(4, files.List().Count);
        Assert.Equal(2, await uploader.PendingCountAsync());
        Assert.Empty(await uploader.FailedAsync());
        // The pass stopped at the first photo rather than trying every one.
        Assert.Equal(1, storage.Attempts);

        storage.Offline = false;
        var online = await uploader.ProcessAsync(Ct);

        Assert.Equal(new PhotoUploadReport(2, 0, false), online);
        // The fixed clock gives both the same timestamp, so which goes first is a tie; both must arrive.
        Assert.Equal(4, storage.Uploads.Count);
        Assert.All(new[] { first, second }, id => Assert.Contains(storage.Uploads, u => u.Path == $"{Profile}/{id}.jpg"));
        Assert.Empty(files.List());
    }

    [Fact]
    public async Task MarksAPermanentRefusalFailedWithoutBlockingLaterPhotos()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var refused = await AddAsync(uploader);
        var accepted = await AddAsync(uploader, "back");
        storage.Refuse.Add(refused);

        var report = await uploader.ProcessAsync(Ct);

        Assert.Equal(new PhotoUploadReport(1, 1, false), report);
        var failed = Assert.Single(await uploader.FailedAsync());
        Assert.Equal((refused, "403"), (failed.Id, failed.ErrorCode));
        Assert.Contains(storage.Uploads, u => u.Path.Contains(accepted, StringComparison.Ordinal));
        Assert.Equal(2, files.List().Count);

        // A failed photo isn't retried on its own, only when the user asks.
        Assert.Equal(new PhotoUploadReport(0, 0, false), await uploader.ProcessAsync(Ct));
        storage.Refuse.Clear();
        await uploader.RetryAsync(refused);
        Assert.Equal(new PhotoUploadReport(1, 0, false), await uploader.ProcessAsync(Ct));
        Assert.Empty(files.List());
    }

    [Fact]
    public async Task NeverUploadsAFileThatStillCarriesMetadata()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);
        files.Files[PhotoUploader.PhotoName(id)] = Jpegs.WithGps();

        await uploader.ProcessAsync(Ct);

        Assert.DoesNotContain(storage.Uploads, u => u.Path.EndsWith($"{id}.jpg", StringComparison.Ordinal));
        Assert.Equal("not_stripped", Assert.Single(await uploader.FailedAsync()).ErrorCode);
    }

    [Fact]
    public async Task MarksAPhotoWhoseFileIsGoneFailed()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        var id = await AddAsync(uploader);
        files.Delete(PhotoUploader.ThumbName(id));

        await uploader.ProcessAsync(Ct);

        Assert.Equal("missing_file", Assert.Single(await uploader.FailedAsync()).ErrorCode);
    }

    [Fact]
    public async Task CancelStopsTheUploadAndDeletesTheFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);

        await uploader.CancelAsync(id);
        await uploader.ProcessAsync(Ct);

        Assert.Empty(files.List());
        Assert.Empty(storage.Uploads);
        Assert.Equal(0, await uploader.PendingCountAsync());
    }

    [Fact]
    public async Task SweepDeletesFilesNoEntryRefersTo()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        var kept = await AddAsync(uploader);
        await files.WriteAsync("0190a0b0-0000-7000-8000-00000000dead.jpg", Jpegs.Stripped(), Ct);

        Assert.Equal(1, await uploader.SweepAsync());

        Assert.Equal([PhotoUploader.PhotoName(kept), PhotoUploader.ThumbName(kept)], files.List().Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnotherUsersSignInClearsTheQueueAndItsFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        await AddAsync(uploader);
        var database = new LocalDatabase(
            t.Db,
            owner,
            new SupabaseConnector(
                new FixedSession(null),
                owner,
                new FakeWriter(),
                new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")),
                Clock,
                NullLogger<SupabaseConnector>.Instance),
            photos: uploader);

        await database.BindToUserAsync("user-a");
        Assert.Equal(2, files.List().Count);

        await database.BindToUserAsync("user-b");

        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
    }

    [Fact]
    public async Task DirectoryStoreWritesReadsListsAndDeletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "health360-photos-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DirectoryPhotoFileStore(root);
            const string Name = "0190a0b0-0000-7000-8000-000000000002_thumb.jpg";

            Assert.Null(await store.ReadAsync(Name, Ct));
            await store.WriteAsync(Name, [1, 2, 3], Ct);
            await File.WriteAllTextAsync(Path.Combine(root, "other.txt"), "x", Ct);

            Assert.Equal([1, 2, 3], await store.ReadAsync(Name, Ct));
            Assert.Equal([Name], store.List());
            Assert.Throws<ArgumentException>(() => store.Delete("../escape.jpg"));
            store.Delete(Name);
            store.Delete(Name);
            Assert.Empty(store.List());
            store.DeleteAll();
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StorageUploadsAsTheOwnerWithUpsert()
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(HttpStatusCode.OK);

        await storage.UploadAsync($"{Profile}/x_thumb.jpg", [0xFF, 0xD8], Ct);

        var request = Assert.Single(server.Requests);
        Assert.Equal($"POST /storage/v1/object/progress-photos/{Profile}/x_thumb.jpg", request.Line);
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
        Assert.Equal("anon-key", request.Headers["apikey"]);
        Assert.Equal("true", request.Headers["x-upsert"]);
        Assert.Equal("image/jpeg", request.ContentType);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public async Task StorageSortsRefusalsIntoPermanentAndRetried(HttpStatusCode status, bool permanent)
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(status);

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() => storage.UploadAsync($"{Profile}/x.jpg", [1], Ct));

        Assert.Equal(permanent, error.Permanent);
        Assert.Equal(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Code);
    }

    [Theory]
    [InlineData("""{"statusCode":"400","error":"InvalidJWT","message":"jwt expired"}""", false)]
    [InlineData("""{"statusCode":"401","error":"Unauthorized","message":"no"}""", false)]
    [InlineData("""{"statusCode":"403","error":"Unauthorized","message":"new row violates row-level security policy"}""", true)]
    [InlineData("not json", true)]
    public async Task StorageRetriesA400ThatIsReallyAnExpiredToken(string body, bool permanent)
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(HttpStatusCode.BadRequest);
        server.Body = body;

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() => storage.UploadAsync($"{Profile}/x.jpg", [1], Ct));

        Assert.Equal(permanent, error.Permanent);
    }

    [Fact]
    public async Task ClearingCancelsAnUploadInFlightAndWaitsForIt()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var files = new MemoryPhotoFiles();
        var storage = new BlockingPhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        await AddAsync(uploader);
        var database = Database(t, owner, uploader);

        var pass = uploader.ProcessAsync(Ct);
        await storage.Started.Task.WaitAsync(Ct);
        await database.BindToUserAsync("user-b");

        Assert.True(storage.Cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass);
        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
        Assert.Equal("user-b", owner.Owner);
    }

    [Fact]
    public async Task ClearingWaitsForAPhotoBeingAddedSoNoEntryLosesItsFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var files = new MemoryPhotoFiles { Gate = new TaskCompletionSource() };
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        var database = Database(t, owner, uploader);

        var adding = AddAsync(uploader);
        await files.Writing.Task.WaitAsync(Ct);
        var clearing = database.BindToUserAsync("user-b");
        await Task.Delay(100, Ct);
        Assert.False(clearing.IsCompleted);
        files.Gate.SetResult();
        await adding;
        await clearing;

        // The add finished first, then the clear removed both its entry and its files.
        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
    }

    [Fact]
    public async Task SweepAlsoRemovesHalfWrittenFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "health360-photos-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var t = await TestDatabase.OpenAsync();
            var store = new DirectoryPhotoFileStore(root);
            using var uploader = new PhotoUploader(t.Db, store, new FakePhotoStorage(), Clock);
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, "0190a0b0-0000-7000-8000-000000000003.jpg.tmp"), [1], Ct);

            Assert.Equal(1, await uploader.SweepAsync());
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LocalDatabase Database(TestDatabase t, MemoryOwnerStore owner, PhotoUploader uploader) =>
        new(
            t.Db,
            owner,
            new SupabaseConnector(
                new FixedSession(null),
                owner,
                new FakeWriter(),
                new SyncEndpoints(new Uri("http://127.0.0.1:54321"), "anon", new Uri("http://127.0.0.1:8080")),
                Clock,
                NullLogger<SupabaseConnector>.Instance),
            photos: uploader);

    [Fact]
    public async Task StorageSendsNothingWithoutTheOwnersSession()
    {
        var (storage, server) = Storage(owner: "user-b");

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() => storage.UploadAsync($"{Profile}/x.jpg", [1], Ct));

        Assert.False(error.Permanent);
        Assert.Empty(server.Requests);
    }

    private static (SupabasePhotoStorage Storage, FakeStorageServer Server) Storage(string owner = "user-a")
    {
        var server = new FakeStorageServer();
        var http = new HttpClient(server) { BaseAddress = new Uri("http://127.0.0.1:54321/") };
        var session = new FixedSession(new SessionToken("user-a", "user-token"));
        return (new SupabasePhotoStorage(http, session, new MemoryOwnerStore { Owner = owner }, "anon-key"), server);
    }

    private sealed class PhotoRow
    {
        public string ProfileId { get; set; } = "";

        public string Pose { get; set; } = "";

        public string TakenAt { get; set; } = "";

        public string LocalDate { get; set; } = "";

        public string StoragePath { get; set; } = "";

        public string ThumbPath { get; set; } = "";
    }

    private sealed class FakeStorageServer : HttpMessageHandler
    {
        public Queue<HttpStatusCode> Replies { get; } = new();

        public string Body { get; set; } = "{}";

        public List<(string Line, Dictionary<string, string> Headers, string? ContentType)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                $"{request.Method} {request.RequestUri!.PathAndQuery}",
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                request.Content?.Headers.ContentType?.MediaType));
            return Task.FromResult(new HttpResponseMessage(Replies.Dequeue()) { Content = new StringContent(Body) });
        }
    }
}

/// <summary>Photo files in memory. With a <see cref="Gate"/>, the first write waits for it.</summary>
public sealed class MemoryPhotoFiles : IPhotoFileStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task WriteAsync(string name, byte[] jpeg, CancellationToken cancellationToken)
    {
        Files[name] = jpeg;
        Writing.TrySetResult();
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }
    }

    public Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Files.TryGetValue(name, out var bytes) ? bytes : null);

    public void Delete(string name) => Files.Remove(name);

    public IReadOnlyList<string> List() => [.. Files.Keys];

    public void DeleteAll() => Files.Clear();
}

/// <summary>Storage that records uploads, can be offline, and refuses photos by id.</summary>
public sealed class FakePhotoStorage : IPhotoStorage
{
    public bool Offline { get; set; }

    public HashSet<string> Refuse { get; } = [];

    public int Attempts { get; private set; }

    public List<(string Path, byte[] Jpeg)> Uploads { get; } = [];

    public Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken)
    {
        Attempts++;
        if (Offline)
        {
            throw new PhotoUploadException("offline", permanent: false, code: null);
        }

        if (Refuse.Any(id => path.Contains(id, StringComparison.Ordinal)))
        {
            throw new PhotoUploadException("denied", permanent: true, code: "403");
        }

        Uploads.Add((path, jpeg));
        return Task.CompletedTask;
    }
}

/// <summary>Storage whose upload never answers until it is cancelled.</summary>
public sealed class BlockingPhotoStorage : IPhotoStorage
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Cancelled { get; private set; }

    public async Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            throw;
        }
    }
}

/// <summary>Small JPEG byte streams: a minimal image, with and without EXIF.</summary>
internal static class Jpegs
{
    public static byte[] Stripped() => Build([]);

    public static byte[] WithGps() => Build(Exif(1));

    public static byte[] Rotated() => Build(Exif(6));

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    // Little-endian TIFF, one IFD0 entry (orientation), then some GPS text.
    private static byte[] Exif(int orientation) =>
        Segment(0xE1, [
            .. "Exif\0\0II"u8, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0, 0, 0, 0, 0,
            .. "GPS 51.5007N 0.1246W"u8,
        ]);

    private static byte[] Build(byte[] metadata) =>
    [
        0xFF, 0xD8, .. metadata,
        .. Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]),
        .. Segment(0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]),
        .. Segment(0xC4, [0, 1, .. new byte[15], 0]),
        .. Segment(0xDA, [1, 1, 0, 0, 63, 0]),
        0x12, 0x34, 0xFF, 0xD9,
    ];
}
