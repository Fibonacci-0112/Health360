using Bunit;
using Health360.LocalDb;

namespace Health360.UI.Tests.Components;

/// <summary>The throwaway notes sync test on Today (Phase 0b).</summary>
public sealed class NotesTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> TodayAsync()
    {
        await SignedInAndOnboardedAsync();
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => Assert.Contains("No notes yet.", cut.Find(".notes").TextContent, StringComparison.Ordinal), Wait);
        return cut;
    }

    [Fact]
    public async Task AddingANoteSavesItLocallyAndQueuesTheUpload()
    {
        App.Clock.Now = DateTimeOffset.Parse("2026-10-30T03:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        App.Clock.Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var cut = await TodayAsync();

        await TypeAsync(cut, "#note-body", "  first note  ");
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.Contains("first note", cut.Find(".note").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("2026-10-29", cut.Find(".note").TextContent, StringComparison.Ordinal);
        Assert.Equal("", cut.Find("#note-body").GetAttribute("value"));
        var note = Assert.Single(await Notes.ListAsync(App.Database.Db));
        Assert.Equal("first note", note.Body);
        var owner = await App.Database.Db.Get<Owner>("SELECT profile_id AS ProfileId FROM notes WHERE id = ?", [note.Id]);
        Assert.Equal(ProfileId, owner.ProfileId);
        var tx = await App.Database.Db.GetNextCrudTransaction();
        Assert.Equal(("notes", note.Id), (tx!.Crud.Single().Table, tx.Crud.Single().Id));
    }

    [Fact]
    public async Task ABlankNoteCannotBeAdded()
    {
        var cut = await TodayAsync();

        await TypeAsync(cut, "#note-body", "   ");

        Assert.True(cut.Find(".note-form button").HasAttribute("disabled"));
        await SubmitAsync(cut);
        Assert.Empty(await Notes.ListAsync(App.Database.Db));
    }

    [Fact]
    public async Task ATooLongNoteSaysSoAndCannotBeAdded()
    {
        var cut = await TodayAsync();

        await TypeAsync(cut, "#note-body", new string('a', 2001));

        Assert.Contains("at most 2000 characters", cut.Find("#note-too-long").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find(".note-form button").HasAttribute("disabled"));
        await SubmitAsync(cut);
        Assert.Empty(await Notes.ListAsync(App.Database.Db));
    }

    [Fact]
    public async Task NotesFromAnotherDeviceAppearAsTheySync()
    {
        var cut = await TodayAsync();

        await App.Database.Db.Execute(
            "INSERT INTO notes (id, profile_id, body, local_date, created_at) VALUES (?, ?, 'from my phone', '2026-10-30', '2026-10-30T11:00:00.000Z')",
            ["0192f0c0-0000-7000-8000-0000000000aa", ProfileId]);

        cut.WaitForAssertion(() => Assert.Contains("from my phone", cut.Find(".note-list").TextContent, StringComparison.Ordinal), Wait);
    }

    public sealed class Owner
    {
        public string ProfileId { get; set; } = "";
    }
}
