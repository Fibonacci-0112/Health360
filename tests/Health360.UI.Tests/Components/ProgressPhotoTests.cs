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
}
