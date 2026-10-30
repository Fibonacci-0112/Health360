using FluentValidation;
using Health360.Contracts.Notes;
using Health360.Domain.Ids;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>The throwaway notes table's local writes and listing, against a real PowerSync database.</summary>
public class NotesTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";

    // 03:30 UTC on the 30th is still the 29th at UTC-5 (Chicago on that date). A custom zone, because this
    // project runs with invariant globalization, and Windows can't look up an IANA id without ICU.
    private static readonly ZonedClock Clock = new(
        new DateTimeOffset(2026, 10, 30, 3, 30, 0, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone("UTC-05", TimeSpan.FromHours(-5), "UTC-05", "UTC-05"));

    [Fact]
    public async Task AddsATrimmedNoteForTheLocalDayAndQueuesItForUpload()
    {
        await using var t = await TestDatabase.OpenAsync();

        var id = await Notes.AddAsync(t.Db, Profile, new NoteInput("  hello  "), Clock);

        Assert.Equal(Clock.GetUtcNow().ToUnixTimeMilliseconds(), UuidV7.TimestampOf(Guid.Parse(id)));
        var note = Assert.Single(await Notes.ListAsync(t.Db));
        Assert.Equal((id, "hello", "2026-10-29", "2026-10-30T03:30:00.000Z"), (note.Id, note.Body, note.LocalDate, note.CreatedAt));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PUT", "notes", id), (write.Op, write.Table, write.Id));
        Assert.Equal(Profile, write.Data!["profile_id"]!.GetValue<string>());
        Assert.Equal("2026-10-30T03:30:00.000Z", write.Data!["updated_at"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsAnEmptyNoteAndWritesNothing(string body)
    {
        await using var t = await TestDatabase.OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() => Notes.AddAsync(t.Db, Profile, new NoteInput(body), Clock));

        Assert.Empty(await Notes.ListAsync(t.Db));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task ListsNewestFirst()
    {
        await using var t = await TestDatabase.OpenAsync();
        var first = await Notes.AddAsync(t.Db, Profile, new NoteInput("first"), Clock);
        var second = await Notes.AddAsync(t.Db, Profile, new NoteInput("second"), new ZonedClock(Clock.GetUtcNow().AddSeconds(1), TimeZoneInfo.Utc));

        Assert.Equal([second, first], (await Notes.ListAsync(t.Db)).Select(n => n.Id));
    }

    private sealed class ZonedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
