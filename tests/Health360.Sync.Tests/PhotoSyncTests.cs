using Health360.Sync.Tests.Support;

namespace Health360.Sync.Tests;

/// <summary>
/// Progress photos against a real Supabase Storage and PowerSync, through the app's own photo stack (PLAN.md §3,
/// step 1.1 exit): a photo taken in airplane mode uploads later, stripped of its metadata; only its owner can see it,
/// and another household member is refused (403) unless the owner shares photos with them; deleting it removes the row
/// everywhere and both files from Storage. pgTAP checks the bucket's policies against a stand-in Storage schema; this
/// checks them through the Storage API itself.
/// </summary>
[Trait("Category", "Sync")]
[Collection(SerialSync.Name)]
public sealed class PhotoSyncTests(SyncFixture fx) : IClassFixture<SyncFixture>, IAsyncLifetime
{
    // Storage hides an object its select policy denies, so a refused read may look like a missing file.
    private static readonly int[] RefusedRead = [403, 404];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Device A1 => fx.A1;

    private Device A2 => fx.A2;

    private Device B => fx.B;

    private string Alex => fx.Alex.ProfileId;

    public ValueTask InitializeAsync()
    {
        Assert.SkipUnless(SyncBackend.Enabled, $"Set {SyncBackend.EnableVariable}=1 and the stack's settings to run the sync tests.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!SyncBackend.Enabled)
        {
            return;
        }

        // A test that failed in airplane mode mustn't leave a device offline for the next one.
        foreach (var device in new[] { A1, A2, B })
        {
            await device.GoOnlineAsync();
        }
    }

    [Fact]
    public async Task APhotoTakenInAirplaneModeUploadsLaterStrippedAndOnlyItsOwnerCanSeeIt()
    {
        // Casey belongs to Alex's household; Blake doesn't.
        var casey = await fx.Backend.CreateUserAsync("casey", Ct);
        await fx.Backend.AddToHouseholdAsync(fx.Alex, casey, Ct);

        // Airplane mode: the photo is kept on the device, row and files both waiting.
        await A1.DrainAsync();
        await A1.GoOfflineAsync();
        var takenAt = DateTimeOffset.UtcNow;
        var id = await A1.Photos.AddAsync(Alex, "front", takenAt, DateOnly.FromDateTime(takenAt.UtcDateTime), Jpegs.WithGps(), Jpegs.WithGps(), cancellationToken: Ct);
        var offline = await A1.Photos.ProcessAsync(Ct);
        Assert.Equal((0, 0, true), (offline.Uploaded, offline.Failed, offline.Stopped));
        Assert.Equal(1, await A1.Photos.PendingCountAsync());
        Assert.True(await A1.PendingAsync() > 0, "The row should be waiting in the upload queue.");

        // It can be viewed here from its local file, already stripped.
        var local = await A1.Library.PhotoAsync(Alex, id, Ct);
        Assert.Equal(PhotoImageStatus.Available, local.Status);
        var stripped = local.Jpeg!;
        Assert.False(Jpegs.HasGps(stripped));

        // Back online: the files go up and the row with them.
        await A1.GoOnlineAsync();
        var online = await A1.Photos.ProcessAsync(Ct);
        Assert.Equal((1, 0, false), (online.Uploaded, online.Failed, online.Stopped));
        Assert.Equal(0, await A1.Photos.PendingCountAsync());
        await A1.DrainAsync();
        Assert.Equal(0, await Uploads.FailedUploadCountAsync(A1.Db));

        // The owner's other device gets the row, and both files from Storage, exactly as stripped.
        await A2.SyncedAsync("progress_photos", id);
        var photo = await A2.Library.PhotoAsync(Alex, id, Ct);
        Assert.Equal(PhotoImageStatus.Available, photo.Status);
        Assert.Equal(stripped, photo.Jpeg);
        var thumb = await A2.Library.ThumbnailAsync(Alex, id, Ct);
        Assert.Equal(PhotoImageStatus.Available, thumb.Status);
        Assert.False(Jpegs.HasGps(thumb.Jpeg!));

        var photoPath = $"{Alex}/{PhotoUploader.PhotoName(id)}";
        var thumbPath = $"{Alex}/{PhotoUploader.ThumbName(id)}";
        var stored = await fx.Backend.StorageAsync(fx.Alex, HttpMethod.Get, photoPath, null, Ct, "authenticated/");
        Assert.True(stored.Succeeded, $"The owner's download answered {stored.Status}.");
        Assert.Equal(stripped, stored.Body);

        // Blake, in another household, gets neither the row nor the files. B's own write coming back means B's stream
        // has caught up past Alex's.
        var marker = await B.AddWeightAsync(70);
        await B.SyncedAsync("measurements", marker);
        Assert.Empty(await B.Db.GetAll<Device.Row>("SELECT id AS Id FROM progress_photos"));
        Assert.Empty(await B.Db.GetAll<Device.Row>("SELECT row_id AS Id FROM ps_oplog WHERE row_id = ?", [id]));
        await AssertCannotReadAsync(fx.Blake, photoPath, stripped);
        await AssertCannotReadAsync(fx.Blake, thumbPath, null);

        // Casey, in Alex's household, can't read them either without a grant, and can't write into Alex's folder: 403.
        await AssertCannotReadAsync(casey, photoPath, stripped);
        await AssertCannotReadAsync(casey, thumbPath, null);
        await AssertCannotWriteAsync(casey);
        // Nor delete them: whatever Storage answers, the owner's file is still there, unchanged.
        await fx.Backend.StorageAsync(casey, HttpMethod.Delete, photoPath, null, Ct);
        Assert.Equal(stripped, (await fx.Backend.StorageAsync(fx.Alex, HttpMethod.Get, photoPath, null, Ct, "authenticated/")).Body);

        // Sharing body values doesn't share photos.
        await GrantAsync(casey, "body");
        await AssertCannotReadAsync(casey, photoPath, stripped);

        // Sharing photos lets Casey read them, still not write or delete: grants are read-only.
        await GrantAsync(casey, "photos");
        var shared = await fx.Backend.StorageAsync(casey, HttpMethod.Get, photoPath, null, Ct, "authenticated/");
        Assert.True(shared.Succeeded, $"With a photos grant, the member's download answered {shared.Status}.");
        Assert.Equal(stripped, shared.Body);
        await AssertCannotWriteAsync(casey);
        await fx.Backend.StorageAsync(casey, HttpMethod.Delete, photoPath, null, Ct);
        Assert.Equal(stripped, (await fx.Backend.StorageAsync(fx.Alex, HttpMethod.Get, photoPath, null, Ct, "authenticated/")).Body);

        await DeleteAsync(id);
    }

    [Fact]
    public async Task DeletingAPhotoRemovesItsRowEverywhereAndBothFilesFromStorage()
    {
        var takenAt = DateTimeOffset.UtcNow;
        var id = await A1.Photos.AddAsync(Alex, "side_left", takenAt, DateOnly.FromDateTime(takenAt.UtcDateTime), Jpegs.WithGps(), Jpegs.Plain(), cancellationToken: Ct);
        Assert.Equal(1, (await A1.Photos.ProcessAsync(Ct)).Uploaded);
        await A1.DrainAsync();
        await A2.SyncedAsync("progress_photos", id);
        Assert.Equal(PhotoImageStatus.Available, (await A2.Library.ThumbnailAsync(Alex, id, Ct)).Status);

        // The row goes first, through the upload queue; the files wait until that delete has reached the server.
        await A1.Photos.DeleteAsync(Alex, id, Ct);
        Assert.Equal(1, await A1.Photos.PendingCountAsync());
        await A1.DrainAsync();
        Assert.Equal(0, await Uploads.FailedUploadCountAsync(A1.Db));
        var pass = await A1.Photos.ProcessAsync(Ct);
        Assert.Equal((1, false), (pass.Deleted, pass.Stopped));
        Assert.Equal(0, await A1.Photos.PendingCountAsync());

        // Both files are gone from Storage, for the owner too.
        foreach (var name in new[] { PhotoUploader.PhotoName(id), PhotoUploader.ThumbName(id) })
        {
            var path = $"{Alex}/{name}";
            Assert.Null(await A1.Storage.DownloadAsync(path, Ct));
            var answer = await fx.Backend.StorageAsync(fx.Alex, HttpMethod.Get, path, null, Ct, "authenticated/");
            Assert.False(answer.Succeeded, $"{name} is still in Storage ({answer.Status}).");

            // Deleting a file that is already gone counts as done, so a retried pass can't get stuck.
            await A1.Storage.DeleteAsync(path, Ct);
        }

        // The other device drops the row, and the photo can't be loaded there any more.
        await Wait.ForAsync(
            () => A2.Db.GetAll<Device.Row>("SELECT id AS Id FROM progress_photos WHERE id = ?", [id]), rows => rows.Length == 0);
        A2.Library.Forget(id);
        Assert.Equal(PhotoImageStatus.NotUploaded, (await A2.Library.PhotoAsync(Alex, id, Ct)).Status);
        Assert.Equal(PhotoImageStatus.NotUploaded, (await A2.Library.ThumbnailAsync(Alex, id, Ct)).Status);
    }

    /// <summary>The user's download is refused and returns none of the photo.</summary>
    private async Task AssertCannotReadAsync(TestUser user, string path, byte[]? photo)
    {
        var answer = await fx.Backend.StorageAsync(user, HttpMethod.Get, path, null, Ct, "authenticated/");
        Assert.False(answer.Succeeded, $"{user}'s download of another profile's photo answered {answer.Status}.");
        Assert.Contains(answer.Effective, RefusedRead);
        if (photo is not null)
        {
            Assert.NotEqual(photo, answer.Body);
        }

        Assert.False(Jpegs.HasGps(answer.Body));
    }

    /// <summary>The user can't put a file into Alex's folder: Storage answers 403.</summary>
    private async Task AssertCannotWriteAsync(TestUser user)
    {
        var path = $"{Alex}/{Guid.CreateVersion7()}.jpg";
        var answer = await fx.Backend.StorageAsync(user, HttpMethod.Post, path, Jpegs.Plain(), Ct);
        Assert.Equal(403, answer.Effective);
        Assert.Equal(404, (await fx.Backend.StorageAsync(fx.Alex, HttpMethod.Get, path, null, Ct, "authenticated/")).Effective);
    }

    /// <summary>Alex shares one category with <paramref name="grantee"/>, from A1, through the upload queue.</summary>
    private async Task GrantAsync(TestUser grantee, string category)
    {
        var household = await A1.Db.Get<Device.Row>(
            "SELECT household_id AS Id FROM household_members WHERE profile_id = ?", [Alex]);
        var id = Guid.CreateVersion7().ToString();
        await A1.Db.Execute(
            "INSERT INTO sharing_grants (id, profile_id, household_id, grantee_profile_id, category, access) VALUES (?, ?, ?, ?, ?, 'read')",
            [id, Alex, household.Id, grantee.ProfileId, category]);
        await A1.DrainAsync();
        Assert.Equal(0, await Uploads.FailedUploadCountAsync(A1.Db));
    }

    /// <summary>Deletes a photo through the app's delete flow, so the run leaves no files behind.</summary>
    private async Task DeleteAsync(string id)
    {
        await A1.Photos.DeleteAsync(Alex, id, Ct);
        await A1.DrainAsync();
        Assert.Equal(1, (await A1.Photos.ProcessAsync(Ct)).Deleted);
    }
}
