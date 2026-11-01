using Bunit;
using Health360.Domain.Photos;
using Health360.LocalDb;
using Health360.UI.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

/// <summary>Progress photos on the Body tab: taking or choosing one, the upload states, retry and discard.</summary>
public sealed class ProgressPhotoTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> BodyAsync()
    {
        await SignedInAndOnboardedAsync();
        await App.Database.SeedMetricsAsync();
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("body");
        cut.WaitForAssertion(() => cut.Find("#photos-pick"), Wait);
        return cut;
    }

    [Fact]
    public async Task TakingAPhotoStoresItStrippedAndShowsItWaitingToUpload()
    {
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => Assert.Contains("No photos yet", cut.Markup, StringComparison.Ordinal), Wait);

        await ClickAsync(() => cut.FindAll("#photos-pose ~ .choices [role=radio]").Single(b => b.TextContent.Trim() == "Left side"));
        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Contains("Waiting to upload", cut.Find("[aria-label='Your progress photos']").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Left side photo saved.", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(["take"], App.Camera.Calls);
        var row = Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("side_left", "2026-10-30", PhotoUploadState.Pending), (row.Pose, row.LocalDate, row.State));
        Assert.Equal(2, App.PhotoFiles.Files.Count);
        Assert.All(App.PhotoFiles.Files.Values, f => Assert.True(JpegMetadata.IsStripped(f)));
    }

    [Fact]
    public async Task AChosenPhotoKeepsTheDayItWasTakenAndTheFutureIsRefused()
    {
        var cut = await BodyAsync();

        await ChangeAsync(cut, "#photos-date", "2026-10-31");
        await ClickAsync(cut, "#photos-pick");
        cut.WaitForAssertion(() => Assert.Contains("can't be in the future", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(App.Camera.Calls);

        await ChangeAsync(cut, "#photos-date", "2026-06-01");
        await ClickAsync(cut, "#photos-pick");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        var row = Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("2026-06-01", "2026-06-01T12:00:00.000Z"), (row.LocalDate, row.TakenAt));
    }

    [Fact]
    public async Task ATabLeftOpenPastMidnightDatesANewPhotoToday()
    {
        var cut = await BodyAsync();
        App.Clock.Now = App.Clock.Now.AddDays(1);

        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        var row = Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("2026-10-31", "2026-10-31T12:00:00.000Z"), (row.LocalDate, row.TakenAt));
    }

    [Fact]
    public async Task ACameraOpenAcrossMidnightDatesThePhotoTheNewDay()
    {
        var cut = await BodyAsync();
        App.Camera.OnOpen = () => App.Clock.Now = App.Clock.Now.AddDays(1);

        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        var row = Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(("2026-10-31", "2026-10-31T12:00:00.000Z"), (row.LocalDate, row.TakenAt));
    }

    [Fact]
    public async Task TodayPickedExplicitlyStaysFixedPastMidnight()
    {
        var cut = await BodyAsync();
        await ChangeAsync(cut, "#photos-date", "2026-10-30");
        App.Clock.Now = App.Clock.Now.AddDays(1);

        await ClickAsync(cut, "#photos-pick");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        var row = Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal("2026-10-30", row.LocalDate);
    }

    [Fact]
    public async Task CancellingTheCameraSavesNothing()
    {
        var cut = await BodyAsync();
        App.Camera.Next = null;

        await ClickAsync(cut, "#photos-pick");

        cut.WaitForAssertion(() => Assert.False(cut.Find("#photos-pick").HasAttribute("disabled")), Wait);
        Assert.Empty(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Empty(App.PhotoFiles.Files);
        Assert.DoesNotContain("saved", cut.Find("[aria-live=polite]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutACameraOnlyChoosingIsOffered()
    {
        App.Camera.CanTakePhoto = false;
        var cut = await BodyAsync();

        Assert.Empty(cut.FindAll("#photos-take"));
        await ClickAsync(cut, "#photos-pick");

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        Assert.Equal(["pick"], App.Camera.Calls);
    }

    [Fact]
    public async Task AnImageThatIsNotAJpegSaysSoAndSavesNothing()
    {
        var cut = await BodyAsync();
        App.Camera.Next = new CapturedPhoto([0x89, 0x50, 0x4E, 0x47], [0x89, 0x50, 0x4E, 0x47]);

        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Contains("couldn't be used", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Empty(App.PhotoFiles.Files);
    }

    [Fact]
    public async Task ARefusedUploadShowsWhyAndCanBeRetriedOrDiscarded()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        App.PhotoStorage.Refuse = true;

        await App.Photos.ProcessAsync(Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.Contains("Upload refused (403)", cut.Markup, StringComparison.Ordinal), Wait);
        // Retry wakes the background loop; with the server unreachable again the photo waits.
        App.PhotoStorage.Refuse = false;
        await ClickAsync(() => cut.Find("button[aria-label^='Retry uploading']"));
        cut.WaitForAssertion(() => Assert.Contains("Waiting to upload", cut.Markup, StringComparison.Ordinal), Wait);

        App.PhotoStorage.Refuse = true;
        await App.Photos.ProcessAsync(Xunit.TestContext.Current.CancellationToken);
        cut.WaitForAssertion(() => cut.Find("button[aria-label^='Discard']"), Wait);

        // Discarding deletes anything of it already in Storage, so offline it fails and keeps the photo.
        await ClickAsync(() => cut.Find("button[aria-label^='Discard']"));
        cut.WaitForAssertion(() => Assert.Contains("needs a connection", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));

        App.PhotoStorage.Reachable = true;
        await ClickAsync(() => cut.Find("button[aria-label^='Discard']"));

        cut.WaitForAssertion(() => Assert.Contains("No photos yet", cut.Markup, StringComparison.Ordinal), Wait);
        Assert.Contains("Photo discarded.", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(App.PhotoFiles.Files);
        Assert.Equal(0, await App.Photos.PendingCountAsync());
        Assert.Equal(2, App.PhotoStorage.Deleted.Count);
    }

    private const string OtherDevicePhoto = "0192f0c0-0000-7000-8000-0000000000bb";

    [Fact]
    public async Task APhotoWaitingToUploadShowsItsThumbnailAndOpensFullSizeOffline()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.StartsWith("data:image/jpeg;base64,", cut.Find(".photo-thumb img").GetAttribute("src"), StringComparison.Ordinal), Wait);
        await ClickAsync(() => cut.Find("button[aria-label^='View the Front photo']"));

        cut.WaitForAssertion(() =>
        {
            var image = cut.Find(".photo-viewer img");
            Assert.StartsWith("Front progress photo from 2026-10-30", image.GetAttribute("alt"), StringComparison.Ordinal);
            Assert.StartsWith("data:image/jpeg;base64,", image.GetAttribute("src"), StringComparison.Ordinal);
        }, Wait);

        await ClickAsync(cut, "#photo-viewer-close");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".photo-viewer")), Wait);
    }

    [Fact]
    public async Task APhotoFromAnotherDeviceThatHasntUploadedSaysItIsNotAvailableYet()
    {
        App.PhotoStorage.Reachable = true;
        await App.Database.SeedPhotoAsync(OtherDevicePhoto, ProfileId, "back", "2026-10-29");
        var cut = await BodyAsync();

        cut.WaitForAssertion(() => Assert.Contains("Not available yet: still uploading from the device it was taken on",
            cut.Find("[aria-label='Your progress photos']").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll(".photo-thumb img"));

        await ClickAsync(() => cut.Find("button[aria-label='View the Back photo from 2026-10-29']"));
        cut.WaitForAssertion(() => Assert.Contains("hasn't finished uploading from the device it was taken on",
            cut.Find(".photo-viewer").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll(".photo-viewer img"));
    }

    [Fact]
    public async Task AnUploadedPhotoIsDownloadedAndOfflineItCanBeTriedAgain()
    {
        await App.Database.SeedPhotoAsync(OtherDevicePhoto, ProfileId, "back", "2026-10-29");
        App.PhotoStorage.Stored[$"{ProfileId}/{OtherDevicePhoto}.jpg"] = Support.Jpegs.WithGps();
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => Assert.Contains("Couldn't load the preview", cut.Find("[aria-label='Your progress photos']").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("Offline", cut.Find(".photo-thumb").TextContent.Trim());

        await ClickAsync(() => cut.Find("button[aria-label='View the Back photo from 2026-10-29']"));
        cut.WaitForAssertion(() => Assert.Contains("Check your connection", cut.Find(".photo-viewer [role=alert]").TextContent, StringComparison.Ordinal), Wait);

        App.PhotoStorage.Reachable = true;
        await ClickAsync(cut, "#photo-viewer-retry");

        cut.WaitForAssertion(() => Assert.StartsWith("data:image/jpeg;base64,", cut.Find(".photo-viewer img").GetAttribute("src"), StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task ASecondPhotoInAPoseShowsTheFirstFadedOverItBeforeItIsKept()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);

        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() =>
        {
            var ghost = cut.Find(".photo-review .photo-ghost");
            Assert.StartsWith("Previous Front photo from 2026-10-30", ghost.GetAttribute("alt"), StringComparison.Ordinal);
            Assert.StartsWith("data:image/jpeg;base64,", ghost.GetAttribute("src"), StringComparison.Ordinal);
        }, Wait);
        // Nothing is saved until Keep.
        Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.True(cut.Find("#photos-take").HasAttribute("disabled"));

        await ClickAsync(cut, "#photo-review-ghost");
        Assert.Empty(cut.FindAll(".photo-ghost"));
        await ClickAsync(cut, "#photo-review-ghost");
        cut.Find(".photo-ghost");

        await ClickAsync(cut, "#photo-review-keep");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Your progress photos'] li").Count), Wait);
        Assert.Empty(cut.FindAll(".photo-review"));
        Assert.Contains("Front photo saved.", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetakeOpensTheCameraAgainAndCancelSavesNothing()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => cut.Find(".photo-review"), Wait);

        await ClickAsync(cut, "#photo-review-retake");
        cut.WaitForAssertion(() => cut.Find(".photo-review .photo-ghost"), Wait);
        Assert.Equal(["take", "take", "take"], App.Camera.Calls);

        await ClickAsync(cut, "#photo-review-cancel");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".photo-review")), Wait);
        Assert.False(cut.Find("#photos-take").HasAttribute("disabled"));
        Assert.Single(await ProgressPhotos.ListAsync(App.Database.Db, ProfileId));
        Assert.Equal(2, App.PhotoFiles.Files.Count);
    }

    [Fact]
    public async Task APreviousPhotoNotUploadedYetIsExplainedAndTheNewOneCanStillBeKept()
    {
        App.PhotoStorage.Reachable = true;
        await App.Database.SeedPhotoAsync(OtherDevicePhoto, ProfileId, "front", "2026-10-29");
        var cut = await BodyAsync();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);

        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Contains("hasn't finished uploading", cut.Find(".photo-review").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll(".photo-ghost"));
        Assert.Empty(cut.FindAll("#photo-review-ghost"));

        await ClickAsync(cut, "#photo-review-keep");
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Your progress photos'] li").Count), Wait);
    }

    [Fact]
    public async Task AFirstPhotoInAPoseIsSavedWithoutAReviewEvenWhenOtherPosesExist()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);

        await ClickAsync(() => cut.FindAll("#photos-pose ~ .choices [role=radio]").Single(b => b.TextContent.Trim() == "Back"));
        await ClickAsync(cut, "#photos-take");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Your progress photos'] li").Count), Wait);
        Assert.Empty(cut.FindAll(".photo-review"));
    }

    [Fact]
    public async Task ABackdatedPhotoIsComparedOnlyWithEarlierOnes()
    {
        var cut = await BodyAsync();
        await ClickAsync(cut, "#photos-take");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[aria-label='Your progress photos'] li")), Wait);

        // Earlier than the only front photo: nothing to line up with, so it saves directly.
        await ChangeAsync(cut, "#photos-date", "2026-06-01");
        await ClickAsync(cut, "#photos-pick");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[aria-label='Your progress photos'] li").Count), Wait);
        Assert.Empty(cut.FindAll(".photo-review"));
    }
}
