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
        uploader.AddAsync(Profile, pose, Clock.GetUtcNow(), Day, photo ?? Jpegs.WithGps(), Jpegs.WithGps(), cancellationToken: Ct);

    [Fact]
    public async Task AddingStoresStrippedFilesTheRowAndAQueueEntry()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
    public async Task DiscardRemovesARefusedPhotoEverywhereButNothingElse()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var refused = await AddAsync(uploader);
        var accepted = await AddAsync(uploader, "back");
        storage.Refuse.Add(refused);
        await uploader.ProcessAsync(Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => uploader.DiscardFailedAsync(accepted, Ct));

        await uploader.DiscardFailedAsync(refused, Ct);

        // Both of its paths are deleted from Storage, in case the thumbnail went up before the photo was refused.
        Assert.Equal([$"{Profile}/{refused}_thumb.jpg", $"{Profile}/{refused}.jpg"], storage.Deleted);
        Assert.Empty(await uploader.FailedAsync());
        Assert.Empty(files.List());
        var left = Assert.Single(await ProgressPhotos.ListAsync(t.Db, Profile));
        Assert.Equal(accepted, left.Id);
    }

    [Fact]
    public async Task DiscardWhileOfflineChangesNothing()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);
        storage.Refuse.Add(id);
        await uploader.ProcessAsync(Ct);
        storage.Offline = true;

        await Assert.ThrowsAsync<PhotoUploadException>(() => uploader.DiscardFailedAsync(id, Ct));

        Assert.Single(await uploader.FailedAsync());
        Assert.Equal(2, files.List().Count);
        Assert.Single(await ProgressPhotos.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task ARefusedRowIsReportedAndDiscardRemovesItsQuarantinedWrite()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        var writer = new FakeWriter { Fail = w => w.Table == "progress_photos" ? new RemoteWriteException("denied", "42501") : null };

        // Its files are up but its row isn't, so it isn't reported as uploaded yet.
        var waiting = Single(await ProgressPhotos.ListAsync(t.Db, Profile));
        Assert.Equal((PhotoUploadState.Pending, 0L, 1L), (waiting.State, waiting.Queued, waiting.RowPending));
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);

        var row = Single(await ProgressPhotos.ListAsync(t.Db, Profile));
        Assert.Equal((PhotoUploadState.Failed, false, "42501"), (row.State, row.FilesFailed, row.RowErrorCode));
        await uploader.DiscardFailedAsync(id, Ct);

        Assert.Equal(0, await Uploads.FailedUploadCountAsync(t.Db));
        Assert.Empty(await ProgressPhotos.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task APhotoTakenAcrossAnAccountClearIsRefused()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        var before = uploader.ClearGeneration;
        await uploader.ClearAsync(() => Task.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            uploader.AddAsync(Profile, "front", Clock.GetUtcNow(), Day, Jpegs.WithGps(), Jpegs.WithGps(), before, Ct));

        Assert.Empty(files.List());
        Assert.Empty(await ProgressPhotos.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task APhotoForAProfileNotOnThisDeviceIsRefused()
    {
        await using var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);

        // As after a clear: the generation the screen read is current, but the profile it read is gone.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            uploader.AddAsync(Profile, "front", Clock.GetUtcNow(), Day, Jpegs.WithGps(), Jpegs.WithGps(), uploader.ClearGeneration, Ct));

        Assert.Empty(files.List());
        Assert.Empty(await ProgressPhotos.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task ARejectedWriteOfAPhotoDiscardedMeanwhileIsNotQuarantined()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        storage.Refuse.Add(id);
        await uploader.ProcessAsync(Ct);
        // The row's PUT is still queued when the photo is discarded; the server then rejects it.
        await uploader.DiscardFailedAsync(id, Ct);
        var writer = new FakeWriter { Fail = w => w.Table == "progress_photos" && w.Op == "PUT" ? new RemoteWriteException("denied", "42501") : null };

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);

        Assert.Equal(0, await Uploads.FailedUploadCountAsync(t.Db));
        Assert.Equal(["PUT", "DELETE"], writer.Writes.Where(w => w.Table == "progress_photos").Select(w => w.Op));
    }

    [Fact]
    public async Task DiscardStillWorksAfterPowerSyncRolledTheRejectedRowBack()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        var files = new MemoryPhotoFiles();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);
        var writer = new FakeWriter { Fail = w => w.Table == "progress_photos" ? new RemoteWriteException("denied", "42501") : null };
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);
        // The rollback: the row goes as sync brings the server's state, with no upload queued.
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("DELETE FROM progress_photos WHERE id = ?", [id]);
            await tx.Execute("DELETE FROM ps_crud WHERE id = (SELECT max(id) FROM ps_crud)");
        });

        await uploader.DiscardFailedAsync(id, Ct);

        Assert.Equal([$"{Profile}/{id}_thumb.jpg", $"{Profile}/{id}.jpg"], storage.Deleted);
        Assert.Equal(0, await Uploads.FailedUploadCountAsync(t.Db));
        Assert.Equal(0, await uploader.PendingCountAsync());
        Assert.Empty(files.List());
    }

    [Fact]
    public async Task ARejectedDeleteIsNotAPhotoToDiscard()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: Ct);
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) VALUES (?, 'progress_photos', ?, 'DELETE', '{}', '42501')",
            ["crud-1", id]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => uploader.DiscardFailedAsync(id, Ct));

        Assert.Empty(storage.Deleted);
        Assert.Single(await ProgressPhotos.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task AnEntryAnEarlierAttemptQuarantinedGoesWhenThePhotoIsDiscardedBeforeTheRetry()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        storage.Refuse.Add(id);
        await uploader.ProcessAsync(Ct);
        // An earlier attempt quarantined the row's PUT, then a later op in its transaction failed transiently.
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) " +
            "VALUES ((SELECT 'crud-' || min(id) FROM ps_crud), 'progress_photos', ?, 'PUT', '{}', '42501')",
            [id]);
        await uploader.DiscardFailedAsync(id, Ct);
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) " +
            "VALUES ((SELECT 'crud-' || min(id) FROM ps_crud), 'progress_photos', ?, 'PUT', '{}', '42501')",
            [id]);
        var writer = new FakeWriter { Fail = w => w.Table == "progress_photos" && w.Op == "PUT" ? new RemoteWriteException("denied", "42501") : null };

        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: Ct);

        Assert.Equal(0, await Uploads.FailedUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task ARejectedDeleteIsNotShownAsARefusedPhoto()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), new FakePhotoStorage(), Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: Ct);
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) VALUES ('crud-9', 'progress_photos', ?, 'DELETE', '{}', '42501')",
            [id]);

        var row = Single(await ProgressPhotos.ListAsync(t.Db, Profile));

        Assert.Equal((0L, PhotoUploadState.Uploaded), (row.RowRejected, row.State));
    }

    private static ProgressPhotoRow Single(ProgressPhotoRow[] rows) => Assert.Single(rows);

    [Fact]
    public async Task ListsEachPhotoWithTheStateOfItsFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage { Offline = true };
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);

        Assert.Equal(PhotoUploadState.Pending, Assert.Single(await ProgressPhotos.ListAsync(t.Db, Profile)).State);
        storage.Offline = false;
        storage.Refuse.Add(id);
        await uploader.ProcessAsync(Ct);
        var failed = Assert.Single(await ProgressPhotos.ListAsync(t.Db, Profile));
        Assert.Equal((PhotoUploadState.Failed, "403"), (failed.State, failed.ErrorCode));
        Assert.Empty(await ProgressPhotos.ListAsync(t.Db, "0190a0b0-0000-7000-8000-000000000009"));
    }

    [Fact]
    public async Task NeverUploadsAFileThatStillCarriesMetadata()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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

    private static async Task<long> DeletionsAsync(TestDatabase t) =>
        (await t.Db.Get<CountRow>($"SELECT count(*) AS N FROM {PhotoUploader.DeletionsTable}")).N;

    [Fact]
    public async Task DeletingAPhotoRemovesItsRowAndLocalFilesThenItsStoredFilesOnceTheRowDeleteIsUp()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await t.Db.Execute("DELETE FROM ps_crud");

        await uploader.DeleteAsync(Profile, id, Ct);

        Assert.Equal(0, (await t.Db.Get<CountRow>("SELECT count(*) AS N FROM progress_photos")).N);
        Assert.Empty(files.List());
        Assert.Equal(1, await DeletionsAsync(t));
        Assert.Equal(1, await uploader.PendingCountAsync());
        // The row delete is still in the upload queue, so the files stay until it reaches the server.
        await uploader.ProcessAsync(Ct);
        Assert.Empty(storage.Deleted);

        await t.Db.Execute("DELETE FROM ps_crud");
        var report = await uploader.ProcessAsync(Ct);

        Assert.Equal(1, report.Deleted);
        Assert.Equal([$"{Profile}/{id}_thumb.jpg", $"{Profile}/{id}.jpg"], storage.Deleted);
        Assert.Equal(0, await DeletionsAsync(t));
        Assert.Equal(0, await uploader.PendingCountAsync());
    }

    [Fact]
    public async Task DeletingAPhotoStillWaitingToUploadCancelsItsUpload()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage { Offline = true };
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);

        await uploader.DeleteAsync(Profile, id, Ct);

        Assert.Equal(0, (await t.Db.Get<CountRow>($"SELECT count(*) AS N FROM {PhotoUploader.Table}")).N);
        Assert.Empty(files.List());
        // Offline the file deletes wait; the insert and the delete cancel out in the upload queue or go up in order.
        await t.Db.Execute("DELETE FROM ps_crud");
        var offline = await uploader.ProcessAsync(Ct);
        Assert.True(offline.Stopped);
        Assert.Equal(1, await DeletionsAsync(t));

        storage.Offline = false;
        await uploader.ProcessAsync(Ct);
        Assert.Empty(storage.Uploads);
        Assert.Equal(2, storage.Deleted.Count);
        Assert.Equal(0, await DeletionsAsync(t));
    }

    [Fact]
    public async Task ARefusedRowDeleteKeepsTheStoredFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await uploader.DeleteAsync(Profile, id, Ct);
        await t.Db.Execute("DELETE FROM ps_crud");
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) VALUES (uuid(), 'progress_photos', ?, 'DELETE', '{}', '42501')", [id]);

        await uploader.ProcessAsync(Ct);

        Assert.Empty(storage.Deleted);
        Assert.Equal(1, await DeletionsAsync(t));
    }

    [Fact]
    public async Task AnUploadThatKeepsFailingDoesNotHoldBackAnotherPhotosFileDeletes()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FailingUploadStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var deletedId = await AddAsync(uploader);
        await uploader.DeleteAsync(Profile, deletedId, Ct);
        await AddAsync(uploader, "back");
        await t.Db.Execute("DELETE FROM ps_crud");

        var report = await uploader.ProcessAsync(Ct);

        Assert.True(report.Stopped);
        Assert.Equal(1, report.Deleted);
        Assert.Equal(2, storage.Deleted.Count);
        Assert.Equal(0, await DeletionsAsync(t));
    }

    [Fact]
    public async Task AnEarlierRefusedWriteOfADeletedPhotoIsDroppedAndDoesNotBlockItsFileDeletes()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await t.Db.Execute("DELETE FROM ps_crud");
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) VALUES (uuid(), 'progress_photos', ?, 'PATCH', '{}', '23514')", [id]);

        await uploader.DeleteAsync(Profile, id, Ct);
        await t.Db.Execute("DELETE FROM ps_crud");
        await uploader.ProcessAsync(Ct);

        Assert.Equal(0, (await t.Db.Get<CountRow>("SELECT count(*) AS N FROM failed_uploads")).N);
        Assert.Equal(2, storage.Deleted.Count);
    }

    [Fact]
    public async Task APhotoDeletedOnAnotherDeviceStopsUploadingAndAnythingOfItAlreadyUpIsDeleted()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        var id = await AddAsync(uploader);
        // Its row went up, then another device deleted it and sync removed it here.
        await t.Db.Execute("DELETE FROM ps_crud");
        await t.Db.Execute("DELETE FROM progress_photos WHERE id = ?", [id]);
        await t.Db.Execute("DELETE FROM ps_crud");

        await uploader.ProcessAsync(Ct);

        Assert.Empty(storage.Uploads);
        Assert.Empty(files.List());
        Assert.Equal(0, (await t.Db.Get<CountRow>($"SELECT count(*) AS N FROM {PhotoUploader.Table}")).N);
        Assert.Equal([$"{Profile}/{id}_thumb.jpg", $"{Profile}/{id}.jpg"], storage.Deleted);
        Assert.Equal(0, await DeletionsAsync(t));
    }

    [Fact]
    public async Task DiscardingARefusedDeleteKeepsTheStoredFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader);
        await uploader.ProcessAsync(Ct);
        await uploader.DeleteAsync(Profile, id, Ct);
        await t.Db.Execute("DELETE FROM ps_crud");
        await t.Db.Execute(
            "INSERT INTO failed_uploads (id, table_name, row_id, op, data, error_code) VALUES ('f1', 'progress_photos', ?, 'DELETE', '{}', '42501')", [id]);

        await uploader.DiscardRefusedDeleteAsync("f1", id);
        await uploader.ProcessAsync(Ct);

        Assert.Empty(storage.Deleted);
        Assert.Equal(0, await DeletionsAsync(t));
        Assert.Equal(0, (await t.Db.Get<CountRow>("SELECT count(*) AS N FROM failed_uploads")).N);
    }

    [Fact]
    public async Task ARowSyncedBackAfterADeleteKeepsItsFiles()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var storage = new FakePhotoStorage();
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), storage, Clock);
        var id = await AddAsync(uploader, "back");
        await uploader.ProcessAsync(Ct);
        await uploader.DeleteAsync(Profile, id, Ct);
        await t.Db.Execute("DELETE FROM ps_crud");
        // The server kept the row and sync brought it back.
        await t.Db.Execute(
            "INSERT INTO progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path, created_at, updated_at) " +
            "VALUES (?, ?, 'back', '2026-10-31T09:30:00.000Z', '2026-10-31', ?, ?, '2026-10-31T09:30:00.000Z', '2026-10-31T09:30:00.000Z')",
            [id, Profile, $"{Profile}/{id}.jpg", $"{Profile}/{id}_thumb.jpg"]);
        await t.Db.Execute("DELETE FROM ps_crud");

        await uploader.ProcessAsync(Ct);

        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task DeletingAPhotoThatIsNotHereOrNotThisProfilesIsRefused()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        using var uploader = new PhotoUploader(t.Db, new MemoryPhotoFiles(), new FakePhotoStorage(), Clock);
        var id = await AddAsync(uploader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => uploader.DeleteAsync(Profile, "0190a0b0-0000-7000-8000-0000000000ff", Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => uploader.DeleteAsync("0190a0b0-0000-7000-8000-000000000002", id, Ct));

        Assert.Equal(1, (await t.Db.Get<CountRow>("SELECT count(*) AS N FROM progress_photos")).N);
        Assert.Equal(0, await DeletionsAsync(t));
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

    [Fact]
    public async Task StorageDownloadsAsTheOwnerThroughTheAuthenticatedRoute()
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(HttpStatusCode.OK);
        server.Body = "jpeg";

        var bytes = await storage.DownloadAsync($"{Profile}/x_thumb.jpg", Ct);

        Assert.Equal("jpeg"u8.ToArray(), bytes);
        var request = Assert.Single(server.Requests);
        Assert.Equal($"GET /storage/v1/object/authenticated/progress-photos/{Profile}/x_thumb.jpg", request.Line);
        Assert.Equal("Bearer user-token", request.Headers["Authorization"]);
        Assert.Equal("anon-key", request.Headers["apikey"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.BadRequest, """{"statusCode":"404","error":"not_found","message":"Object not found"}""")]
    public async Task StorageReportsAFileNotUploadedYetAsMissing(HttpStatusCode status, string body)
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(status);
        server.Body = body;

        Assert.Null(await storage.DownloadAsync($"{Profile}/x.jpg", Ct));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public async Task StorageDownloadFailuresAreNotMissingFiles(HttpStatusCode status, bool permanent)
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(status);

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() => storage.DownloadAsync($"{Profile}/x.jpg", Ct));

        Assert.Equal(permanent, error.Permanent);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"statusCode":"404","error":"Bucket not found","message":"Bucket not found"}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"statusCode":"404","error":"not_found","message":"Bucket not found"}""")]
    public async Task StorageReportsAMissingBucketAsBrokenNotPending(HttpStatusCode status, string body)
    {
        var (storage, server) = Storage();
        server.Replies.Enqueue(status);
        server.Body = body;

        var error = await Assert.ThrowsAsync<PhotoUploadException>(() => storage.DownloadAsync($"{Profile}/x.jpg", Ct));

        Assert.True(error.Permanent);
    }

    [Fact]
    public async Task StorageDownloadsNothingWithoutTheOwnersSession()
    {
        var (storage, server) = Storage(owner: "user-b");

        await Assert.ThrowsAsync<PhotoUploadException>(() => storage.DownloadAsync($"{Profile}/x.jpg", Ct));

        Assert.Empty(server.Requests);
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
        await t.SeedProfileAsync(Profile);
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
        await t.SeedProfileAsync(Profile);
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
    public async Task AnAddWaitingAcrossAnAccountSwitchIsRefused()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var owner = new MemoryOwnerStore { Owner = "user-a" };
        var files = new MemoryPhotoFiles { Gate = new TaskCompletionSource() };
        using var uploader = new PhotoUploader(t.Db, files, new FakePhotoStorage(), Clock);
        var database = Database(t, owner, uploader);

        // The first add holds the lock; the second queues behind it, then the switch begins.
        var first = AddAsync(uploader);
        await files.Writing.Task.WaitAsync(Ct);
        var second = AddAsync(uploader, "back");
        await Task.Delay(50, Ct);
        var switching = database.BindToUserAsync("user-b");
        await Task.Delay(50, Ct);
        files.Gate.SetResult();

        await first;
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        await switching;

        Assert.Empty(files.List());
        Assert.Equal(0, await uploader.PendingCountAsync());
        Assert.Equal(0, (await t.Db.Get<CountRow>("SELECT count(*) AS N FROM progress_photos")).N);
    }

    [Fact]
    public async Task SweepAlsoRemovesHalfWrittenFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "health360-photos-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var t = await TestDatabase.OpenAsync();
            await t.SeedProfileAsync(Profile);
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

    private sealed class CountRow
    {
        public long N { get; set; }
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

    public List<string> Deleted { get; } = [];

    /// <summary>Files Storage holds, by path, for downloads.</summary>
    public Dictionary<string, byte[]> Stored { get; } = new(StringComparer.Ordinal);

    public List<string> Downloads { get; } = [];

    /// <summary>Runs inside each download before it answers.</summary>
    public Action? DuringDownload { get; set; }

    public Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        DuringDownload?.Invoke();
        if (Offline)
        {
            throw new PhotoUploadException("offline", permanent: false, code: null);
        }

        if (Refuse.Any(id => path.Contains(id, StringComparison.Ordinal)))
        {
            throw new PhotoUploadException("denied", permanent: true, code: "403");
        }

        Downloads.Add(path);
        return Task.FromResult(Stored.TryGetValue(path, out var jpeg) ? jpeg : null);
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new PhotoUploadException("offline", permanent: false, code: null);
        }

        Deleted.Add(path);
        return Task.CompletedTask;
    }

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

/// <summary>Storage that deletes but can't take uploads, as during a server error on uploads only.</summary>
public sealed class FailingUploadStorage : IPhotoStorage
{
    public List<string> Deleted { get; } = [];

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        Deleted.Add(path);
        return Task.CompletedTask;
    }

    public Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

    public Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken) =>
        throw new PhotoUploadException("server error", permanent: false, code: "503");
}

/// <summary>Storage whose upload never answers until it is cancelled.</summary>
public sealed class BlockingPhotoStorage : IPhotoStorage
{
    public Task DeleteAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<byte[]?> DownloadAsync(string path, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);

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
