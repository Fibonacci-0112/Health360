using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Loading progress photos for viewing: local files first, then Storage, and photos not uploaded yet.</summary>
public sealed class PhotoLibraryTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";

    private const string Photo = "0190a0b0-0000-7000-8000-0000000000aa";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero));

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0x01, 0x02, 0xFF, 0xD9];

    private static async Task<(TestDatabase Db, MemoryPhotoFiles Files, FakePhotoStorage Storage, PhotoUploader Uploader, PhotoLibrary Library)> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        var files = new MemoryPhotoFiles();
        var storage = new FakePhotoStorage();
        var uploader = new PhotoUploader(t.Db, files, storage, Clock);
        return (t, files, storage, uploader, new PhotoLibrary(files, storage, uploader));
    }

    [Fact]
    public async Task APhotoWaitingToUploadIsReadFromThisDevice()
    {
        var (t, files, storage, uploader, library) = await OpenAsync();
        await using var _ = t;
        using var __ = uploader;
        files.Files[PhotoUploader.PhotoName(Photo)] = Jpeg;
        storage.Offline = true;

        var image = await library.PhotoAsync(Profile, Photo, Ct);

        Assert.Equal(PhotoImageStatus.Available, image.Status);
        Assert.Equal(Jpeg, image.Jpeg);
    }

    [Fact]
    public async Task AnUploadedPhotoIsDownloadedFromItsProfileFolder()
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;
        storage.Stored[$"{Profile}/{Photo}.jpg"] = Jpeg;

        var image = await library.PhotoAsync(Profile, Photo, Ct);

        Assert.Equal((PhotoImageStatus.Available, Jpeg), (image.Status, image.Jpeg));
        Assert.Equal([$"{Profile}/{Photo}.jpg"], storage.Downloads);
    }

    [Fact]
    public async Task APhotoWhoseFilesHaventLeftAnotherDeviceIsNotUploadedRatherThanAnError()
    {
        var (t, _, _, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;

        var image = await library.ThumbnailAsync(Profile, Photo, Ct);

        Assert.Equal(PhotoImageStatus.NotUploaded, image.Status);
        Assert.Null(image.Jpeg);
    }

    [Fact]
    public async Task OfflineAndRefusedAreReportedSeparately()
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;

        storage.Offline = true;
        Assert.Equal(PhotoImageStatus.Unreachable, (await library.PhotoAsync(Profile, Photo, Ct)).Status);

        storage.Offline = false;
        storage.Refuse.Add(Photo);
        Assert.Equal(PhotoImageStatus.Refused, (await library.PhotoAsync(Profile, Photo, Ct)).Status);
    }

    [Fact]
    public async Task SomethingThatIsNotAJpegIsNotShown()
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;
        storage.Stored[$"{Profile}/{Photo}.jpg"] = "<svg/>"u8.ToArray();

        var image = await library.PhotoAsync(Profile, Photo, Ct);

        Assert.Equal(PhotoImageStatus.Refused, image.Status);
        Assert.Null(image.Jpeg);
    }

    [Fact]
    public async Task ThumbnailsAreCachedUntilTheAccountsDataIsCleared()
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;
        storage.Stored[$"{Profile}/{Photo}_thumb.jpg"] = Jpeg;

        await library.ThumbnailAsync(Profile, Photo, Ct);
        storage.Offline = true;
        Assert.Equal(PhotoImageStatus.Available, (await library.ThumbnailAsync(Profile, Photo, Ct)).Status);
        Assert.Single(storage.Downloads);

        await uploader.ClearAsync(() => Task.CompletedTask);

        Assert.Equal(PhotoImageStatus.Unreachable, (await library.ThumbnailAsync(Profile, Photo, Ct)).Status);
    }

    [Fact]
    public async Task FullPhotosAreNotCached()
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;
        storage.Stored[$"{Profile}/{Photo}.jpg"] = Jpeg;

        await library.PhotoAsync(Profile, Photo, Ct);
        await library.PhotoAsync(Profile, Photo, Ct);

        Assert.Equal(2, storage.Downloads.Count);
    }

    [Theory]
    [InlineData("../other", Photo)]
    [InlineData(Profile, "0190A0B0-0000-7000-8000-0000000000AA")]
    [InlineData(Profile, "x/../../y")]
    public async Task IdsThatArentLowerCaseUuidsAreRefused(string profile, string photo)
    {
        var (t, _, storage, uploader, library) = await OpenAsync();
        await using var ___ = t;
        using var __ = uploader;

        await Assert.ThrowsAsync<ArgumentException>(() => library.PhotoAsync(profile, photo, Ct));

        Assert.Empty(storage.Downloads);
    }
}
