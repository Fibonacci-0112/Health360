using FluentValidation;
using Health360.Contracts.Body;
using Health360.Contracts.Profiles;
using Health360.Domain.Ids;
using Health360.Domain.Physiology;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Measurements written, listed and deleted in a real PowerSync database, and what they upload.</summary>
public class MeasurementsTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Other = "0190a0b0-0000-7000-8000-000000000002";

    // 03:30 UTC on the 30th is still the 29th at UTC-5. A custom zone: this project runs with invariant globalization.
    private static readonly ZonedClock Clock = new(
        new DateTimeOffset(2026, 10, 30, 3, 30, 0, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone("UTC-05", TimeSpan.FromHours(-5), "UTC-05", "UTC-05"));

    private static readonly DateOnly Today = new(2026, 10, 29);

    private static readonly MetricDefinition Weight = new("weight", "body", ["kg", "lb"], "kg", 0, null);
    private static readonly MetricDefinition Systolic = new("bp_systolic", "body", ["mmHg"], "mmHg", 0, null);
    private static readonly MetricDefinition Diastolic = new("bp_diastolic", "body", ["mmHg"], "mmHg", 0, null);

    private static (MetricDefinition, MeasurementInput) Entry(MetricDefinition metric, double? value, string unit, DateOnly? date = null,
        string source = "manual") =>
        (metric, new MeasurementInput(metric.Code, value, unit, source, date ?? Today));

    [Fact]
    public async Task AddsAValueAsEnteredWithItsCanonicalValueAndQueuesTheUpload()
    {
        await using var t = await TestDatabase.OpenAsync();

        var id = Assert.Single(await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 176.5, "lb")], Clock));

        Assert.Equal(Clock.GetUtcNow().ToUnixTimeMilliseconds(), UuidV7.TimestampOf(Guid.Parse(id)));
        var row = Assert.Single(await Measurements.ListAsync(t.Db, Profile));
        Assert.Equal((id, "weight", 176.5, "lb"), (row.Id, row.MetricCode, row.Value, row.Unit));
        Assert.Equal(176.5 * 0.45359237, row.ValueCanonical);
        Assert.Equal(("2026-10-29", "2026-10-30T03:30:00.000Z", "manual", null), (row.LocalDate, row.MeasuredAt, row.Source, row.GroupId));

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PUT", "measurements", id), (write.Op, write.Table, write.Id));
        Assert.Equal(Profile, write.Data!["profile_id"]!.GetValue<string>());
        Assert.Equal("body", write.Data!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnEarlierDayIsRecordedAtNoonLocalTime()
    {
        await using var t = await TestDatabase.OpenAsync();

        await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 80, "kg", new DateOnly(2026, 10, 1))], Clock);

        var row = Assert.Single(await Measurements.ListAsync(t.Db, Profile));
        Assert.Equal(("2026-10-01", "2026-10-01T17:00:00.000Z"), (row.LocalDate, row.MeasuredAt));
    }

    [Fact]
    public async Task ValuesEnteredTogetherShareAGroupThatUploadsFirst()
    {
        await using var t = await TestDatabase.OpenAsync();

        var ids = await Measurements.AddAsync(t.Db, Profile, [Entry(Systolic, 118, "mmHg"), Entry(Diastolic, 76, "mmHg")], Clock);

        var rows = await Measurements.ListAsync(t.Db, Profile);
        Assert.Equal(2, rows.Length);
        var group = Assert.Single(rows.Select(r => r.GroupId).Distinct());
        Assert.NotNull(group);
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            [("measurement_groups", group), ("measurements", ids[0]), ("measurements", ids[1])],
            writer.Writes.Select(w => (w.Table, w.Id)));
        Assert.Equal("manual", writer.Writes[0].Data!["kind"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null, "kg")]
    [InlineData(-1.0, "kg")]
    [InlineData(80.0, "st")]
    public async Task AnInvalidValueWritesNothing(double? value, string unit)
    {
        await using var t = await TestDatabase.OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() => Measurements.AddAsync(t.Db, Profile, [Entry(Weight, value, unit)], Clock));

        Assert.Empty(await Measurements.ListAsync(t.Db, Profile));
        Assert.Equal(0, await Uploads.PendingUploadCountAsync(t.Db));
    }

    [Fact]
    public async Task OneInvalidValueOfSeveralWritesNone()
    {
        await using var t = await TestDatabase.OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            Measurements.AddAsync(t.Db, Profile, [Entry(Systolic, 118, "mmHg"), Entry(Diastolic, null, "mmHg")], Clock));

        Assert.Empty(await Measurements.ListAsync(t.Db, Profile));
    }

    [Fact]
    public async Task AFutureDateIsRejected()
    {
        await using var t = await TestDatabase.OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 80, "kg", Today.AddDays(1))], Clock));
    }

    [Fact]
    public async Task DeletingAValueQueuesTheDelete()
    {
        await using var t = await TestDatabase.OpenAsync();
        var id = Assert.Single(await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 80, "kg")], Clock));
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: TestContext.Current.CancellationToken);

        await Measurements.DeleteAsync(t.Db, Profile, id);

        Assert.Empty(await Measurements.ListAsync(t.Db, Profile));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(("DELETE", "measurements", id), (writer.Writes.Single().Op, writer.Writes.Single().Table, writer.Writes.Single().Id));
    }

    [Fact]
    public async Task DeletingAGroupedValueDeletesTheWholeGroup()
    {
        await using var t = await TestDatabase.OpenAsync();
        var ids = await Measurements.AddAsync(t.Db, Profile, [Entry(Systolic, 118, "mmHg"), Entry(Diastolic, 76, "mmHg")], Clock);
        await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 80, "kg")], Clock);

        await Measurements.DeleteAsync(t.Db, Profile, ids[1]);

        Assert.Equal(["weight"], (await Measurements.ListAsync(t.Db, Profile)).Select(r => r.MetricCode));
        Assert.Equal(0, await t.Db.Get<long>("SELECT count(*) FROM measurement_groups", []));
    }

    [Fact]
    public async Task DeletingSomethingNotOnThisDeviceOrAnotherProfilesValueFails()
    {
        await using var t = await TestDatabase.OpenAsync();
        var id = Assert.Single(await Measurements.AddAsync(t.Db, Other, [Entry(Weight, 80, "kg")], Clock));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Measurements.DeleteAsync(t.Db, Profile, id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Measurements.DeleteAsync(t.Db, Profile, Guid.NewGuid().ToString()));

        Assert.Single(await Measurements.ListAsync(t.Db, Other));
    }

    [Fact]
    public async Task ListsOnlyTheProfilesOwnValuesNewestFirst()
    {
        await using var t = await TestDatabase.OpenAsync();
        var older = Assert.Single(await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 81, "kg", Today.AddDays(-1))], Clock));
        var newer = Assert.Single(await Measurements.AddAsync(t.Db, Profile, [Entry(Weight, 80, "kg")], Clock));
        await Measurements.AddAsync(t.Db, Other, [Entry(Weight, 60, "kg")], Clock);

        Assert.Equal([newer, older], (await Measurements.ListAsync(t.Db, Profile)).Select(r => r.Id));
    }

    [Fact]
    public void ARowBecomesTheResolversInputAndAnUnreadableOneIsSkipped()
    {
        var row = new MeasurementRow
        {
            Id = "0192f0c0-0000-7000-8000-0000000000aa",
            MetricCode = "rmr",
            ValueCanonical = 1800,
            LocalDate = "2026-03-01",
            Source = "lab",
            Method = "cart",
        };

        Assert.Equal(
            new MetricValue("rmr", new Measured(Guid.Parse(row.Id), 1800, MeasurementSource.Lab, new DateOnly(2026, 3, 1), "cart")),
            row.ToMetricValue());
        row.Source = "guess";
        Assert.Null(row.ToMetricValue());
    }

    [Theory]
    [InlineData("[\"kg\",\"lb\"]", true)]
    [InlineData("[]", false)]
    [InlineData("[1]", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void DefinitionsNeedAnArrayOfUnitCodes(string? units, bool readable)
    {
        var row = new MetricDefinitionRow { Code = "weight", Category = "body", Units = units, CanonicalUnit = "kg", ValidMin = 0 };

        var definition = row.ToDefinition();

        Assert.Equal(readable, definition is not null);
        if (definition is not null)
        {
            Assert.Equal(["kg", "lb"], definition.Units);
        }
    }

    [Fact]
    public async Task TheMedicationFlagUploadsOnlyItsOwnColumnAsABoolean()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO profiles (id, user_id, unit_prefs) VALUES (?, 'user-1', '{}')", [Profile]);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: TestContext.Current.CancellationToken);

        await Profiles.SetHeartRateLoweringMedicationAsync(t.Db, Profile, true);

        Assert.Equal(1, (await Profiles.GetMineAsync(t.Db, "user-1"))!.TakesHrLoweringMedication);
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PATCH", "profiles", Profile), (write.Op, write.Table, write.Id));
        // Only the changed column, so it can't overwrite another device's change to a different setting.
        Assert.Equal(["takes_hr_lowering_medication"], write.Data!.Select(p => p.Key));
        Assert.True(write.Data!["takes_hr_lowering_medication"]!.GetValue<bool>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Profiles.SetHeartRateLoweringMedicationAsync(t.Db, Other, true));
    }

    [Fact]
    public async Task TheActivityRatingUploadsOnlyItsOwnColumnAndCanBeCleared()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute("INSERT INTO profiles (id, user_id, unit_prefs) VALUES (?, 'user-1', '{}')", [Profile]);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null((await Profiles.GetMineAsync(t.Db, "user-1"))!.ActivityRating);

        await Profiles.SetActivityRatingAsync(t.Db, Profile, 6);

        Assert.Equal(6, (await Profiles.GetMineAsync(t.Db, "user-1"))!.ActivityRating);
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PATCH", "profiles", Profile), (write.Op, write.Table, write.Id));
        Assert.Equal(["activity_rating"], write.Data!.Select(p => p.Key));
        Assert.Equal(6, write.Data!["activity_rating"]!.GetValue<long>());

        await Profiles.SetActivityRatingAsync(t.Db, Profile, null);
        Assert.Null((await Profiles.GetMineAsync(t.Db, "user-1"))!.ActivityRating);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Profiles.SetActivityRatingAsync(t.Db, Profile, 8));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Profiles.SetActivityRatingAsync(t.Db, Profile, -1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Profiles.SetActivityRatingAsync(t.Db, Other, 3));
    }

    [Fact]
    public async Task EditedDetailsWriteAndUploadOnlyTheFieldsGiven()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute(
            "INSERT INTO profiles (id, user_id, unit_prefs, birth_date, sex_for_calcs, height, height_unit, height_cm, activity_level) VALUES (?, 'user-1', '{}', '1990-04-02', 'female', 65, 'in', 165.1, 'light')",
            [Profile]);
        await Uploads.UploadPendingAsync(t.Db, new FakeWriter(), Clock, cancellationToken: TestContext.Current.CancellationToken);

        // The form's birth date and height are an older copy; only sex and activity were edited.
        await Profiles.SetDetailsAsync(
            t.Db, Profile, new ProfileDetailsForm("1980-01-01", null, 70, "in", 177.8, "active"),
            ProfileDetailFields.SexForCalcs | ProfileDetailFields.ActivityLevel);

        var saved = (await Profiles.GetMineAsync(t.Db, "user-1"))!;
        Assert.Equal((null, "active", 65.0, "in", "1990-04-02"), (saved.SexForCalcs, saved.ActivityLevel, saved.Height, saved.HeightUnit, saved.BirthDate));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var write = Assert.Single(writer.Writes);
        Assert.Equal(("PATCH", "profiles", Profile), (write.Op, write.Table, write.Id));
        Assert.Equal(["activity_level", "sex_for_calcs"], write.Data!.Select(p => p.Key).Order());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Profiles.SetDetailsAsync(t.Db, Other, new ProfileDetailsForm(null, null, null, null, null, null), ProfileDetailFields.All));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Profiles.SetDetailsAsync(t.Db, Other, new ProfileDetailsForm(null, null, null, null, null, null), ProfileDetailFields.None));
    }

    private sealed class ZonedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => zone;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
