using Health360.Contracts.Training;
using Health360.Domain.Physiology;
using Health360.Domain.Training;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

public sealed class CardioLogTests
{
    private const string Profile = "10000000-0000-7000-8000-000000000001";
    private const string Other = "10000000-0000-7000-8000-000000000002";
    private static readonly DateOnly Day = new(2026, 11, 4);
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
    private static readonly CardioPhysiology Physiology = new(80, Sex.Male, 30);

    private static PhysiologyProfile ProfileWithMax(bool medication = false) => PhysiologyProfile.Build(
        new(Day, [], BirthDate: new(1996, 11, 4), TakesHeartRateLoweringMedication: medication));

    [Fact]
    public async Task A_saved_session_survives_reopening_and_uploads_the_exact_snapshot()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        var zones = CardioZones.From(ProfileWithMax().ZonesFor(ZoneSport.Run));
        var id = await CardioLog.SaveAsync(t.Db, Profile, new("12020", 1800, 150, 180, Rpe: 6, Distance: 3, DistanceUnit: "mi"),
            Physiology, zones, Clock);
        var saved = Assert.Single(await CardioLog.SessionsAsync(t.Db, Profile, Day, Day));
        var json = saved.EnergyEstimate!;

        await t.ReopenAsync();
        var reopened = Assert.Single(await CardioLog.SessionsAsync(t.Db, Profile, Day, Day));
        Assert.Equal(json, reopened.EnergyEstimate);
        Assert.Equal(id, reopened.Id);
        Assert.Equal(4828.032, reopened.Estimate!.DistanceMetres);
        Assert.Equal(80, reopened.Estimate.Physiology.WeightKg);
        var workoutSummary = await t.Db.Get<EnergyEstimateRow>("SELECT energy_estimate AS Value FROM workout_sessions WHERE id = ?", [id]);
        using (var summary = System.Text.Json.JsonDocument.Parse(workoutSummary.Value))
        {
            var root = summary.RootElement;
            Assert.False(root.TryGetProperty("physiology", out _));
            Assert.False(root.TryGetProperty("zones", out _));
            Assert.False(root.TryGetProperty("notes", out _));
            Assert.DoesNotContain("VO₂max", root.GetProperty("method").GetString());
            var parsedSummary = CardioJson.Parse(workoutSummary.Value)!;
            Assert.Null(parsedSummary.Physiology.WeightKg);
            Assert.Null(parsedSummary.Zones);
            Assert.Equal(150, parsedSummary.Entry.AverageHr);
        }
        Assert.Null(await WorkoutLog.ActiveSessionAsync(t.Db, Profile));
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        var sessionUpload = Assert.Single(writer.Writes, write => write.Table == "workout_sessions");
        Assert.Equal(("PUT", "workout_sessions", id), (sessionUpload.Op, sessionUpload.Table, sessionUpload.Id));
        Assert.Equal("cardio", sessionUpload.Data!["kind"]!.GetValue<string>());
        Assert.Equal("run", sessionUpload.Data["sport"]!.GetValue<string>());
        Assert.Equal(6, sessionUpload.Data["rpe"]!.GetValue<double>());
        Assert.Equal("2026-11-04T11:30:00.000Z", sessionUpload.Data["started_at"]!.GetValue<string>());
        Assert.Equal(CardioEnergy.MethodVersion, sessionUpload.Data["energy_estimate"]!["version"]!.GetValue<int>());
        var privateUpload = Assert.Single(writer.Writes, write => write.Table == "cardio_estimates");
        Assert.Equal(("PUT", "cardio_estimates", id), (privateUpload.Op, privateUpload.Table, privateUpload.Id));
        Assert.Equal(80, privateUpload.Data!["estimate"]!["physiology"]!["weight_kg"]!.GetValue<double>());
    }

    [Fact]
    public async Task Zone_selection_is_scoped_effective_and_sport_first_and_edits_leave_snapshots_unchanged()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await t.SeedProfileAsync(Other);
        await AddZonesAsync(t, "a", Profile, "all", "2026-11-01", "[0,100,140]");
        await AddZonesAsync(t, "b", Profile, "run", "2026-10-01", "[0,120,160]");
        await AddZonesAsync(t, "c", Profile, "run", "2026-11-05", "[0,130,170]");
        await AddZonesAsync(t, "d", Other, "run", "2026-11-04", "[0,150,190]");
        var zones = await CardioLog.ZonesAsync(t.Db, Profile, ZoneSport.Run, Day, ProfileWithMax());
        Assert.Equal("b", zones!.StoredId);
        Assert.Equal([0.0, 120, 160], zones.Zones.Select(z => z.LowerBpm));
        var id = await CardioLog.SaveAsync(t.Db, Profile, new("12020", 1800, 150, 185, [0, 1200, 600]), Physiology, zones, Clock);
        var before = Assert.Single(await CardioLog.SessionsAsync(t.Db, Profile, Day, Day)).EnergyEstimate;

        await t.Db.Execute("UPDATE zone_sets SET boundaries_canonical = '[0,130,180]' WHERE id = 'b'");
        var changed = await CardioLog.ZonesAsync(t.Db, Profile, ZoneSport.Run, Day, ProfileWithMax());
        Assert.Equal(130, changed!.Zones[1].LowerBpm);
        var after = Assert.Single(await CardioLog.SessionsAsync(t.Db, Profile, Day, Day));
        Assert.Equal(id, after.Id);
        Assert.Equal(before, after.EnergyEstimate);
        Assert.Equal(120, after.Estimate!.Zones!.Zones[1].LowerBpm);
        Assert.Empty(await CardioLog.SessionsAsync(t.Db, Other, Day, Day));
    }

    [Fact]
    public async Task All_sports_zones_precede_derived_zones_and_bad_rows_are_skipped()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await AddZonesAsync(t, "a", Profile, "all", "2026-11-01", "[0,100,140]");
        await AddZonesAsync(t, "b", Profile, "run", "2026-11-01", "[0,160,120]");

        Assert.Equal("a", (await CardioLog.ZonesAsync(t.Db, Profile, ZoneSport.Run, Day, ProfileWithMax()))!.StoredId);
        await t.Db.Execute("DELETE FROM zone_sets");
        var derived = await CardioLog.ZonesAsync(t.Db, Profile, ZoneSport.Run, Day, ProfileWithMax());
        Assert.Null(derived!.StoredId);
        Assert.Contains("Friel", derived.Method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Medication_does_not_reuse_old_max_hr_zones_without_provenance()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await AddZonesAsync(t, "a", Profile, "run", "2026-11-01", "[0,100,140]", "max_hr");

        Assert.Null(await CardioLog.ZonesAsync(t.Db, Profile, ZoneSport.Run, Day, ProfileWithMax(medication: true)));
    }

    [Fact]
    public async Task Invalid_entry_creates_no_row_or_upload()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);

        await Assert.ThrowsAsync<ArgumentException>(() => CardioLog.SaveAsync(t.Db, Profile, new("12020", 0), Physiology, null, Clock));

        Assert.Empty(await CardioLog.SessionsAsync(t.Db, Profile, Day, Day));
        Assert.Equal(0, await t.Db.Get<long>("SELECT count(*) FROM ps_crud", []));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"version\":999}")]
    public void Unreadable_or_future_snapshots_are_not_displayed(string? json) => Assert.Null(CardioJson.Parse(json));

    private sealed class EnergyEstimateRow
    {
        public string Value { get; set; } = "";
    }

    private static Task<PowerSync.Common.DB.NonQueryResult> AddZonesAsync(TestDatabase t, string id, string profile, string sport, string date, string boundaries, string basis = "custom") =>
        t.Db.Execute("INSERT INTO zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from) " +
            "VALUES (?, ?, ?, 'hr', ?, ?, 'bpm', ?, ?)", [id, profile, sport, basis, boundaries, boundaries, date]);
}
