using Health360.Contracts.Body;
using Health360.Domain.Physiology;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Pins written, replaced and removed in a real PowerSync database, and what they upload.</summary>
public class MetricPinsTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Other = "0190a0b0-0000-7000-8000-000000000002";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private static readonly MetricDefinition MaxHr = new("max_hr", "physiology", ["bpm"], "bpm", 0, null);

    /// <summary>A database with max_hr defined and two max HR values for <see cref="Profile"/>, the queue empty.</summary>
    private static async Task<(TestDatabase Db, string Lab, string Field)> SeededAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.Db.Execute(
            "INSERT INTO metric_definitions (id, code, category, units, canonical_unit, valid_min) VALUES (?, 'max_hr', 'physiology', '[\"bpm\"]', 'bpm', 0)",
            [Guid.NewGuid().ToString()]);
        var lab = Assert.Single(await Measurements.AddAsync(t.Db, Profile,
            [(MaxHr, new MeasurementInput("max_hr", 192, "bpm", "lab", new DateOnly(2025, 3, 1)))], Clock));
        var field = Assert.Single(await Measurements.AddAsync(t.Db, Profile,
            [(MaxHr, new MeasurementInput("max_hr", 188, "bpm", "field_test", new DateOnly(2026, 10, 1)))], Clock));
        await t.Db.Execute("DELETE FROM ps_crud");
        return (t, lab, field);
    }

    private static async Task<List<FakeWriter.Write>> UploadAsync(TestDatabase t)
    {
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        return writer.Writes;
    }

    [Fact]
    public void TheIdIsDerivedFromTheProfileAndMetricAsTheServerDerivesIt()
    {
        // The golden value supabase/tests/database/05_metric_pins.test.sql checks private.metric_pin_id against.
        Assert.Equal("6a8b1fc1-d36f-8c03-ba62-7ced888bd0da", MetricPins.IdFor(Profile, "max_hr"));
        Assert.Equal(MetricPins.IdFor(Profile, "max_hr"), MetricPins.IdFor(Profile.ToUpperInvariant(), "max_hr"));
        Assert.NotEqual(MetricPins.IdFor(Profile, "max_hr"), MetricPins.IdFor(Other, "max_hr"));
        Assert.NotEqual(MetricPins.IdFor(Profile, "max_hr"), MetricPins.IdFor(Profile, "rmr"));
    }

    [Fact]
    public async Task PinningAMeasurementStoresOneRowAndUploadsItAsAnUpsert()
    {
        var (t, lab, _) = await SeededAsync();
        await using var _db = t;

        await MetricPins.PinMeasurementAsync(t.Db, Profile, lab, Clock);

        var pin = Assert.Single(await MetricPins.ListAsync(t.Db, Profile));
        Assert.Equal((MetricPins.IdFor(Profile, "max_hr"), "max_hr", "measurement", lab), (pin.Id, pin.MetricCode, pin.Target, pin.MeasurementId));
        Assert.Equal(Pin.ToMeasurement(Guid.Parse(lab)), pin.ToPin());

        var writes = await UploadAsync(t);
        var put = Assert.Single(writes, w => w.Op == "PUT");
        Assert.Equal(("metric_pins", pin.Id), (put.Table, put.Id));
        Assert.Equal(
            (Profile, "max_hr", "physiology", "measurement", lab),
            (put.Data!["profile_id"]!.GetValue<string>(), put.Data["metric_code"]!.GetValue<string>(),
             put.Data["category"]!.GetValue<string>(), put.Data["target"]!.GetValue<string>(), put.Data["measurement_id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task RepinningReplacesThePinAndNeverUploadsAPatch()
    {
        var (t, lab, field) = await SeededAsync();
        await using var _db = t;
        await MetricPins.PinMeasurementAsync(t.Db, Profile, lab, Clock);
        await UploadAsync(t);

        await MetricPins.PinMeasurementAsync(t.Db, Profile, field, Clock);
        await MetricPins.PinEstimateAsync(t.Db, Profile, "max_hr", Clock);

        var pin = Assert.Single(await MetricPins.ListAsync(t.Db, Profile));
        Assert.Equal(("estimate", null), (pin.Target, pin.MeasurementId));
        Assert.Same(Pin.ToEstimate, pin.ToPin());

        // A PATCH would be quarantined if another device had unpinned meanwhile; a PUT always lands.
        var writes = await UploadAsync(t);
        Assert.DoesNotContain(writes, w => w.Op == "PATCH");
        var last = writes[^1];
        Assert.Equal(("PUT", "estimate"), (last.Op, last.Data!["target"]!.GetValue<string>()));
        Assert.True(last.Data.ContainsKey("measurement_id"), "measurement_id must be sent: " + last.Data.ToJsonString());
        Assert.Null(last.Data["measurement_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnpinningDeletesTheRowAndUnpinningNothingIsAnError()
    {
        var (t, lab, _) = await SeededAsync();
        await using var _db = t;
        await MetricPins.PinMeasurementAsync(t.Db, Profile, lab, Clock);
        await UploadAsync(t);

        await MetricPins.UnpinAsync(t.Db, Profile, "max_hr");

        Assert.Empty(await MetricPins.ListAsync(t.Db, Profile));
        var delete = Assert.Single(await UploadAsync(t));
        Assert.Equal(("DELETE", "metric_pins", MetricPins.IdFor(Profile, "max_hr")), (delete.Op, delete.Table, delete.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MetricPins.UnpinAsync(t.Db, Profile, "max_hr"));
    }

    [Fact]
    public async Task APinNeedsTheMeasurementOnThisDeviceAndInThisProfile()
    {
        var (t, lab, _) = await SeededAsync();
        await using var _db = t;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MetricPins.PinMeasurementAsync(t.Db, Profile, "0190a0b0-0000-7000-8000-0000000000ff", Clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MetricPins.PinMeasurementAsync(t.Db, Other, lab, Clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => MetricPins.PinEstimateAsync(t.Db, Profile, "rmr", Clock));

        Assert.Empty(await MetricPins.ListAsync(t.Db, Profile));
        Assert.Empty(await MetricPins.ListAsync(t.Db, Other));
        Assert.Empty(await UploadAsync(t));
    }

    [Fact]
    public async Task DeletingThePinnedMeasurementKeepsThePinSoItCanBeReportedAsUnavailable()
    {
        var (t, lab, field) = await SeededAsync();
        await using var _db = t;
        await MetricPins.PinMeasurementAsync(t.Db, Profile, lab, Clock);

        await Measurements.DeleteAsync(t.Db, Profile, lab);

        var pin = Assert.Single(await MetricPins.ListAsync(t.Db, Profile));
        var remaining = (await Measurements.ListAsync(t.Db, Profile)).Select(r => r.ToMetricValue()!.Measured);
        var resolution = Resolver.Resolve(remaining, null, pin.ToPin())!;
        Assert.True(resolution.PinUnavailable);
        Assert.Equal(Guid.Parse(field), resolution.Measurement!.Id);
    }

    [Theory]
    [InlineData("estimate", "0190a0b0-0000-7000-8000-000000000003")]
    [InlineData("measurement", null)]
    [InlineData("measurement", "not-a-uuid")]
    [InlineData("lab", null)]
    public void AMalformedSyncedRowIsNoPin(string target, string? measurementId) =>
        Assert.Null(new MetricPinRow { Target = target, MeasurementId = measurementId }.ToPin());

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
