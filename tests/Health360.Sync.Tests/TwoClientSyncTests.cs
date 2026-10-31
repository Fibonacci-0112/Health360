using System.Diagnostics;
using Health360.Domain.Units;
using Health360.Sync.Tests.Support;

namespace Health360.Sync.Tests;

/// <summary>
/// Phase 0b exit criteria (PLAN.md §3), through the app's own .NET sync stack: a note written offline
/// reaches a second device within about 5 seconds of reconnecting, and user B never receives it. pgTAP
/// proves the RLS half; this proves the Sync Streams half. Each test
/// makes its own rows, so they pass in any order.
/// </summary>
[Trait("Category", "Sync")]
public sealed class TwoClientSyncTests(SyncFixture fx) : IClassFixture<SyncFixture>, IAsyncLifetime
{
    private Device A1 => fx.A1;

    private Device A2 => fx.A2;

    private Device B => fx.B;

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

        // A test that failed while a device was offline mustn't leave it offline for the next one.
        foreach (var device in new[] { A1, A2, B })
        {
            await device.ConnectAsync();
        }
    }

    [Fact]
    public async Task EachDeviceStartsWithOnlyItsOwnProfileAndHousehold()
    {
        Assert.Equal([fx.Alex.ProfileId], await IdsAsync(A1, "SELECT id AS Id FROM profiles"));
        Assert.Equal([fx.Alex.ProfileId], await IdsAsync(A2, "SELECT id AS Id FROM profiles"));
        Assert.Equal([fx.Blake.ProfileId], await IdsAsync(B, "SELECT id AS Id FROM profiles"));
        Assert.Equal([fx.Blake.ProfileId], await IdsAsync(B, "SELECT profile_id AS Id FROM household_members"));
    }

    [Fact]
    public async Task AnOfflineNoteReachesTheSecondDeviceWithinAboutFiveSeconds()
    {
        await A1.DrainAsync();
        await A1.DisconnectAsync();
        var id = await A1.AddNoteAsync("written offline");
        Assert.Equal(1, await A1.PendingAsync());

        var elapsed = Stopwatch.StartNew();
        await A1.Local.ConnectAsync(fx.Alex.UserId);
        await Wait.ForAsync(A2.NotesAsync, notes => notes.Any(n => n.Id == id), TimeSpan.FromSeconds(15));
        elapsed.Stop();

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"Took {elapsed.Elapsed.TotalSeconds:0.0} s.");
        Assert.Equal("written offline", (await A2.NotesAsync()).Single(n => n.Id == id).Body);
        await A1.DrainAsync();
    }

    [Fact]
    public async Task UserBNeverReceivesAnotherUsersRows()
    {
        var alexs = await A1.AddNoteAsync("alex's");
        await A2.SyncedAsync("notes", alexs);

        // B writes a note of its own and waits for the server's copy. Once it has it, B's stream has
        // caught up past A's write, so anything of A's that leaked would be here too.
        var blakes = await B.AddNoteAsync("blake's");
        await B.SyncedAsync("notes", blakes);

        Assert.All(await IdsAsync(B, "SELECT profile_id AS Id FROM notes"), p => Assert.Equal(fx.Blake.ProfileId, p));
        Assert.DoesNotContain(await B.NotesAsync(), n => n.Id == alexs);
        Assert.Empty(await IdsAsync(B, "SELECT id AS Id FROM profiles WHERE id = ?", fx.Alex.ProfileId));
        Assert.Empty(await IdsAsync(B, "SELECT row_id AS Id FROM ps_oplog WHERE row_id = ?", alexs));
        Assert.DoesNotContain(await A2.NotesAsync(), n => n.Id == blakes);
    }

    [Fact]
    public async Task ProfileEditsUploadWithServerTypes()
    {
        // jsonb goes up as an object, not a string of JSON, and the server recomputes the canonical
        // height from what was entered (the 1 cm sent here is overwritten).
        await A1.Db.Execute(
            "UPDATE profiles SET unit_prefs = ?, height = 70, height_unit = 'in', height_cm = 1 WHERE id = ?",
            ["""{"load":"lb"}""", fx.Alex.ProfileId]);
        await A1.DrainAsync();

        var row = await Wait.ForAsync(
            () => A2.Db.Get<ProfileRow>("SELECT unit_prefs AS UnitPrefs, height_cm AS HeightCm FROM profiles"),
            r => r.HeightCm > 100);
        Assert.Equal("""{"load":"lb"}""", System.Text.Json.Nodes.JsonNode.Parse(row.UnitPrefs!)!.ToJsonString());
        Assert.Equal(177.8, row.HeightCm!.Value, 10);
        Assert.Empty(await FailedAsync(A1, fx.Alex.ProfileId));
    }

    [Fact]
    public async Task SettingsChangedOnTwoDevicesBothSurvive()
    {
        // A2 changes its units while offline; meanwhile A1 turns the medication flag on and it reaches the
        // server. When A2 reconnects, its upload carries only unit_prefs, so the flag isn't undone.
        await A2.DrainAsync();
        await A2.DisconnectAsync();
        await A2.Db.Execute("UPDATE profiles SET unit_prefs = ? WHERE id = ?", ["""{"distance":"mi"}""", fx.Alex.ProfileId]);
        await Profiles.SetHeartRateLoweringMedicationAsync(A1.Db, fx.Alex.ProfileId, true);
        await A1.DrainAsync();

        await A2.ConnectAsync();
        await A2.DrainAsync();

        foreach (var device in new[] { A1, A2 })
        {
            var row = await Wait.ForAsync(
                () => device.Db.Get<SettingsRow>(
                    "SELECT unit_prefs AS UnitPrefs, takes_hr_lowering_medication AS Flag FROM profiles WHERE id = ?", [fx.Alex.ProfileId]),
                r => r.Flag == 1 && r.UnitPrefs is not null && r.UnitPrefs.Contains("\"mi\"", StringComparison.Ordinal));
            Assert.Equal(1, row.Flag);
        }

        Assert.Empty(await FailedAsync(A1, fx.Alex.ProfileId));
        Assert.Empty(await FailedAsync(A2, fx.Alex.ProfileId));

        // Leave Alex's profile as other tests expect it.
        await Profiles.SetHeartRateLoweringMedicationAsync(A1.Db, fx.Alex.ProfileId, false);
        await A1.DrainAsync();
    }

    [Fact]
    public async Task MeasurementsSyncToTheOwnersDevicesWithServerCanonicalValuesAndNeverToB()
    {
        // A DEXA scan, one value in it entered in lb, and pace zones in seconds per mile. The client sends a
        // wrong canonical value and category; the server recomputes both and sync brings its values back.
        var group = Guid.CreateVersion7().ToString();
        var weight = Guid.CreateVersion7().ToString();
        var zones = Guid.CreateVersion7().ToString();
        await A1.Db.Execute(
            "INSERT INTO measurement_groups (id, profile_id, kind, measured_at, local_date, device_make) VALUES (?, ?, 'dexa', ?, '2026-10-31', 'Hologic')",
            [group, fx.Alex.ProfileId, "2026-10-31T07:00:00Z"]);
        await A1.Db.Execute(
            """
            INSERT INTO measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, group_id, source, method)
            VALUES (?, ?, 'lean_mass', 'physiology', 181.3, 'lb', -1, ?, '2026-10-31', ?, 'lab', 'dexa')
            """,
            [weight, fx.Alex.ProfileId, "2026-10-31T07:00:00Z", group]);
        await A1.Db.Execute(
            """
            INSERT INTO zone_sets (id, profile_id, sport, metric, basis, boundaries, unit, boundaries_canonical, effective_from)
            VALUES (?, ?, 'run', 'pace', 'custom', '[600, 540]', 's_per_mi', '[]', '2026-10-31')
            """,
            [zones, fx.Alex.ProfileId]);
        await A1.DrainAsync();
        Assert.Empty(await FailedAsync(A1, weight));
        Assert.Empty(await FailedAsync(A1, zones));

        var row = await Wait.ForAsync(
            () => A2.Db.GetOptional<MeasurementRow>(
                "SELECT value AS Value, unit AS Unit, value_canonical AS ValueCanonical, category AS Category, group_id AS GroupId FROM measurements WHERE id = ?",
                [weight]),
            r => r is { ValueCanonical: > 0 });
        Assert.Equal((181.3, "lb", "body", group), (row!.Value, row.Unit, row.Category, row.GroupId));
        // The server stores Domain's exact double (pgTAP and CanonicalUnitParityTests check that). PowerSync
        // delivers double precision columns rounded to 15 significant digits, so the device copy of
        // 82.23629668100001 kg is 82.236296681: equal well within Quantity.CanonicalEquals' tolerance.
        var expected = Conversion.Convert(181.3, Unit.Pound, Unit.Kilogram);
        Assert.InRange(row.ValueCanonical, expected * (1 - 1e-14), expected * (1 + 1e-14));
        await A2.SyncedAsync("measurement_groups", group);

        var canonical = await Wait.ForAsync(
            () => A2.Db.GetOptional<ZoneRow>("SELECT boundaries_canonical AS BoundariesCanonical FROM zone_sets WHERE id = ?", [zones]),
            r => r is { BoundariesCanonical: not null and not "[]" });
        Assert.Equal(
            [Pace.ToSecondsPerKm(600, PaceUnit.MinutesPerMile), Pace.ToSecondsPerKm(540, PaceUnit.MinutesPerMile)],
            System.Text.Json.JsonSerializer.Deserialize<double[]>(canonical!.BoundariesCanonical!)!);

        // B's own write coming back means B's stream has caught up past A's; nothing of A's arrived.
        var marker = await B.AddNoteAsync("marker");
        await B.SyncedAsync("notes", marker);
        foreach (var (table, id) in new[] { ("measurement_groups", group), ("measurements", weight), ("zone_sets", zones) })
        {
            Assert.Empty(await IdsAsync(B, $"SELECT id AS Id FROM {table} WHERE id = ?", id));
            Assert.Empty(await IdsAsync(B, "SELECT row_id AS Id FROM ps_oplog WHERE row_id = ?", id));
        }

        // Metric definitions are reference data: every signed-in device has them.
        Assert.NotEmpty(await IdsAsync(B, "SELECT id AS Id FROM metric_definitions WHERE code = 'weight'"));
    }

    [Fact]
    public async Task PinsSetOfflineOnTwoDevicesConvergeOnOneRowOutliveTheirValueAndNeverReachB()
    {
        var profile = fx.Alex.ProfileId;
        var pinId = MetricPins.IdFor(profile, "max_hr");
        var measured = Guid.CreateVersion7().ToString();
        await A1.Db.Execute(
            """
            INSERT INTO measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, source)
            VALUES (?, ?, 'max_hr', 'physiology', 190, 'bpm', 190, '2026-10-01T07:00:00Z', '2026-10-01', 'lab')
            """,
            [measured, profile]);
        await A1.DrainAsync();
        await A2.SyncedAsync("measurements", measured);
        await Wait.ForAsync(() => IdsAsync(A2, "SELECT id AS Id FROM metric_definitions WHERE code = 'max_hr'"), ids => ids.Length == 1);

        // Both of Alex's devices pin max HR while A2 is offline: A1 the measurement, A2 the estimate. The id is
        // derived from the profile and metric, so A2's upload upserts the row A1 made rather than being rejected
        // as a second pin for the metric. The later upload wins, on both devices.
        await A2.DrainAsync();
        await A2.DisconnectAsync();
        await MetricPins.PinEstimateAsync(A2.Db, profile, "max_hr", TimeProvider.System);
        await MetricPins.PinMeasurementAsync(A1.Db, profile, measured, TimeProvider.System);
        await A1.DrainAsync();
        await A1.SyncedAsync("metric_pins", pinId);

        await A2.ConnectAsync();
        await A2.DrainAsync();
        foreach (var device in new[] { A1, A2 })
        {
            await Wait.ForAsync(() => MetricPins.ListAsync(device.Db, profile), pins => pins is [{ Target: "estimate", MeasurementId: null }]);
        }

        Assert.Empty(await FailedAsync(A1, pinId));
        Assert.Empty(await FailedAsync(A2, pinId));

        // Deleting the pinned value keeps the pin on every device, so the app can say the choice is gone.
        await MetricPins.PinMeasurementAsync(A1.Db, profile, measured, TimeProvider.System);
        await A1.DrainAsync();
        await Wait.ForAsync(() => MetricPins.ListAsync(A2.Db, profile), pins => pins is [{ Target: "measurement" }]);
        await Measurements.DeleteAsync(A1.Db, profile, measured);
        await A1.DrainAsync();
        await Wait.ForAsync(() => IdsAsync(A2, "SELECT id AS Id FROM measurements WHERE id = ?", measured), ids => ids.Length == 0);
        var kept = Assert.Single(await MetricPins.ListAsync(A2.Db, profile));
        Assert.Equal(("measurement", measured), (kept.Target, kept.MeasurementId));

        // B's own write coming back means B's stream has caught up past A's; Alex's pin never arrived.
        var marker = await B.AddNoteAsync("marker");
        await B.SyncedAsync("notes", marker);
        Assert.Empty(await IdsAsync(B, "SELECT id AS Id FROM metric_pins"));
        Assert.Empty(await IdsAsync(B, "SELECT row_id AS Id FROM ps_oplog WHERE row_id = ?", pinId));

        // Unpinning reaches the other device too, and leaves Alex's profile as other tests expect it.
        await MetricPins.UnpinAsync(A1.Db, profile, "max_hr");
        await A1.DrainAsync();
        await Wait.ForAsync(() => MetricPins.ListAsync(A2.Db, profile), pins => pins.Length == 0);
        Assert.Empty(await FailedAsync(A1, pinId));
    }

    [Fact]
    public async Task AnOfflineEditToANoteAnotherDeviceDeletedIsQuarantinedNotLost()
    {
        var id = await A1.AddNoteAsync("to be deleted");
        await A2.SyncedAsync("notes", id);

        await A1.DisconnectAsync();
        await A1.Db.Execute("UPDATE notes SET body = 'edited offline' WHERE id = ?", [id]);
        await A2.Db.Execute("DELETE FROM notes WHERE id = ?", [id]);
        await A2.DrainAsync();

        await A1.ConnectAsync();
        await A1.DrainAsync();
        var failed = Assert.Single(await FailedAsync(A1, id));
        Assert.Equal(("PATCH", PostgrestWriter.NoRow), (failed.Op, failed.ErrorCode));
        // The server's state wins: the note is gone here too.
        await Wait.ForAsync(async () => await IdsAsync(A1, "SELECT id AS Id FROM notes WHERE id = ?", id), ids => ids.Length == 0);

        // Retrying sends the saved edit again; the note is still gone, so it stays quarantined.
        Assert.Equal(RetryResult.Rejected, await Uploads.RetryFailedUploadAsync(A1.Db, failed.Id, A1.Writer, TimeProvider.System, TestContext.Current.CancellationToken));
        Assert.Single(await FailedAsync(A1, id));
    }

    [Fact]
    public async Task ADeleteRlsSilentlyBlocksIsQuarantinedNotReportedAsDone()
    {
        // Consents keep their history: users may insert them, never delete them.
        var id = Guid.CreateVersion7().ToString();
        await B.Db.Execute(
            "INSERT INTO consents (id, profile_id, kind, version, granted, decided_at) VALUES (?, ?, 'tos', '1', 1, ?)",
            [id, fx.Blake.ProfileId, DateTimeOffset.UtcNow.ToString("O")]);
        await B.DrainAsync();
        await B.SyncedAsync("consents", id);

        await B.Db.Execute("DELETE FROM consents WHERE id = ?", [id]);
        await B.DrainAsync();

        var failed = Assert.Single(await FailedAsync(B, id));
        Assert.Equal(("DELETE", PostgrestWriter.Denied), (failed.Op, failed.ErrorCode));
        // The server still has it, so sync puts it back.
        await Wait.ForAsync(async () => await IdsAsync(B, "SELECT id AS Id FROM consents WHERE id = ?", id), ids => ids.Length == 1);
    }

    [Fact]
    public async Task AForgedWriteIsQuarantinedWithoutBlockingTheQueue()
    {
        // B writes into A's profile: RLS rejects it (42501), so it is parked in failed_uploads, and B's
        // next, valid write still goes through.
        var forged = await B.AddNoteAsync("forged", fx.Alex.ProfileId);
        var after = await B.AddNoteAsync("after");
        await B.DrainAsync();

        var failed = Assert.Single(await FailedAsync(B, forged));
        Assert.Equal(("PUT", "42501"), (failed.Op, failed.ErrorCode));
        // The connector logs the table and code only: no ids, no row data.
        var logged = Assert.Single(B.Log.Messages, m => m.Contains("42501", StringComparison.Ordinal) && m.Contains("notes", StringComparison.Ordinal));
        Assert.DoesNotContain(forged, logged, StringComparison.Ordinal);
        Assert.DoesNotContain("forged", logged, StringComparison.Ordinal);

        // The server's state wins: the forged row disappears locally, the valid one stays.
        await B.SyncedAsync("notes", after);
        await Wait.ForAsync(B.NotesAsync, notes => notes.All(n => n.Id != forged));
        Assert.Contains(await B.NotesAsync(), n => n.Id == after);
        // Nor did it reach A: by the time A's next write is back on A's devices, it would be there too.
        var marker = await A1.AddNoteAsync("marker");
        await A2.SyncedAsync("notes", marker);
        Assert.DoesNotContain(await A2.NotesAsync(), n => n.Id == forged);
    }

    [Fact]
    public async Task AnotherUserSigningInNeverInheritsTheFirstUsersRowsOrUnsentWrites()
    {
        // A third install, used by Alex, then by Blake without a sign-out in between (Alex's session
        // expired while the app was closed, say).
        await using var shared = Device.Create(fx.Backend, fx.Alex);
        await shared.ConnectAsync();
        await shared.DisconnectAsync();
        var unsent = await shared.AddNoteAsync("alex's, never sent");
        Assert.Equal(1, await shared.PendingAsync());

        shared.SignInAs(fx.Blake);
        await shared.ConnectAsync();

        Assert.Equal(0, await shared.PendingAsync());
        Assert.Equal(0, await Uploads.FailedUploadCountAsync(shared.Db));
        Assert.Equal([fx.Blake.ProfileId], await IdsAsync(shared, "SELECT id AS Id FROM profiles"));
        Assert.All(await IdsAsync(shared, "SELECT profile_id AS Id FROM notes"), p => Assert.Equal(fx.Blake.ProfileId, p));

        // Alex's note never reached the server: by the time Alex's next write reaches Alex's other
        // device, the unsent one would have too.
        var marker = await A1.AddNoteAsync("marker");
        await A2.SyncedAsync("notes", marker);
        Assert.DoesNotContain(await A2.NotesAsync(), n => n.Id == unsent);
    }

    private static async Task<string[]> IdsAsync(Device device, string sql, params object[] args) =>
        [.. (await device.Db.GetAll<Device.Row>(sql, args)).Select(r => r.Id)];

    private static async Task<FailedRow[]> FailedAsync(Device device, string rowId) =>
        await device.Db.GetAll<FailedRow>(
            "SELECT id AS Id, op AS Op, error_code AS ErrorCode FROM failed_uploads WHERE row_id = ?", [rowId]);

    public sealed class FailedRow
    {
        public string Id { get; set; } = "";

        public string Op { get; set; } = "";

        public string? ErrorCode { get; set; }
    }

    public sealed class MeasurementRow
    {
        public double Value { get; set; }

        public string Unit { get; set; } = "";

        public double ValueCanonical { get; set; }

        public string Category { get; set; } = "";

        public string? GroupId { get; set; }
    }

    public sealed class ZoneRow
    {
        public string? BoundariesCanonical { get; set; }
    }

    public sealed class SettingsRow
    {
        public string? UnitPrefs { get; set; }

        public long Flag { get; set; }
    }

    public sealed class ProfileRow
    {
        public string? UnitPrefs { get; set; }

        public double? HeightCm { get; set; }
    }
}
