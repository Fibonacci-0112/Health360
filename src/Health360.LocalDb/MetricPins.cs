using Health360.Domain.Ids;
using Health360.Domain.Physiology;
using PowerSync.Common.Client;
using PowerSync.Common.DB;

namespace Health360.LocalDb;

/// <summary>One <c>metric_pins</c> row: the value the user chose for a metric.</summary>
public sealed class MetricPinRow
{
    public const string MeasurementTarget = "measurement";
    public const string EstimateTarget = "estimate";

    public string Id { get; set; } = "";

    public string MetricCode { get; set; } = "";

    /// <summary><see cref="MeasurementTarget"/> or <see cref="EstimateTarget"/>.</summary>
    public string Target { get; set; } = "";

    public string? MeasurementId { get; set; }

    /// <summary>
    /// The pin as the resolver takes it, or null when a synced row can't be read (an unknown target, a malformed
    /// id), so one bad row can't break the screen. A pin whose measurement is gone is still a pin: the resolver
    /// reports it as unavailable.
    /// </summary>
    public Pin? ToPin() => Target switch
    {
        EstimateTarget when MeasurementId is null => Pin.ToEstimate,
        MeasurementTarget when Guid.TryParse(MeasurementId, out var id) => Pin.ToMeasurement(id),
        _ => null,
    };
}

/// <summary>
/// The user's pins (<c>metric_pins</c>, U) in the local database: at most one per metric, pointing at a measurement
/// or at the estimate (PLAN.md §2.8). The row id is derived from the profile and metric (<see cref="IdFor"/>), the
/// same on every device and on the server, so pins set offline on two devices end as one row: the last upload wins.
/// </summary>
public static class MetricPins
{
    private const string MineSql =
        "SELECT id AS Id, metric_code AS MetricCode, target AS Target, measurement_id AS MeasurementId " +
        "FROM metric_pins WHERE profile_id = ? ORDER BY metric_code";

    /// <summary>The pin row id for a profile and metric; <c>private.metric_pin_id</c> on the server checks it.</summary>
    public static string IdFor(string profileId, string metricCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(metricCode);
        // Postgres' uuid::text: lowercase, hyphenated. Parsing first makes an upper-case id give the same name.
        var profile = Guid.Parse(profileId).ToString("D");
        return UuidV8.FromName($"health360/metric_pins/{profile}/{metricCode}").ToString("D");
    }

    /// <summary>The profile's pins, now and after every change.</summary>
    public static IAsyncEnumerable<MetricPinRow[]> Watch(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<MetricPinRow>(MineSql, [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<MetricPinRow[]> ListAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<MetricPinRow>(MineSql, [profileId]).ConfigureAwait(false);
    }

    /// <summary>
    /// Pins one of the profile's measurements as the value to use for its metric, replacing any pin the metric had.
    /// Throws if the measurement isn't in the local database, so a pin that points nowhere is never shown as saved.
    /// </summary>
    public static async Task PinMeasurementAsync(IPowerSyncDatabase db, string profileId, string measurementId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(measurementId);
        ArgumentNullException.ThrowIfNull(clock);
        await db.WriteTransaction(async tx =>
        {
            var measurement = await tx.GetOptional<MetricOf>(
                "SELECT metric_code AS MetricCode, category AS Category FROM measurements WHERE id = ? AND profile_id = ?",
                [measurementId, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The measurement is not in the local database.");
            await ReplaceAsync(tx, profileId, measurement.MetricCode, measurement.Category, MetricPinRow.MeasurementTarget,
                measurementId, clock).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Pins the estimate for <paramref name="metricCode"/>, so it is used even when measured values exist. Needs the
    /// metric's definition on this device (the reference stream), for its sharing category.
    /// </summary>
    public static async Task PinEstimateAsync(IPowerSyncDatabase db, string profileId, string metricCode, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(metricCode);
        ArgumentNullException.ThrowIfNull(clock);
        await db.WriteTransaction(async tx =>
        {
            var metric = await tx.GetOptional<MetricOf>(
                "SELECT code AS MetricCode, category AS Category FROM metric_definitions WHERE code = ?", [metricCode]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The metric's definition hasn't synced to this device yet.");
            await ReplaceAsync(tx, profileId, metric.MetricCode, metric.Category, MetricPinRow.EstimateTarget, null, clock)
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the pin for <paramref name="metricCode"/>, so the resolver's own order applies again. Throws if there
    /// was no pin, so an unpin that changed nothing is never shown as done.
    /// </summary>
    public static async Task UnpinAsync(IPowerSyncDatabase db, string profileId, string metricCode)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        var id = IdFor(profileId, metricCode);
        await db.WriteTransaction(async tx =>
        {
            // PowerSync tables are views whose row counts SQLite reports as 0, so the row is looked up first.
            _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM metric_pins WHERE id = ? AND profile_id = ?", [id, profileId])
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("There is no pin for this metric on this device.");
            await tx.Execute("DELETE FROM metric_pins WHERE id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the pin as a delete and an insert, never an update. An insert uploads as an upsert (PUT), which
    /// succeeds whatever another device did to the row meanwhile. An update would upload as a PATCH, and if
    /// another device had unpinned the metric, that PATCH would match no row and be quarantined.
    /// </summary>
    private static async Task ReplaceAsync(ITransaction tx, string profileId, string metricCode, string category, string target,
        string? measurementId, TimeProvider clock)
    {
        var id = IdFor(profileId, metricCode);
        var now = Timestamps.UtcNow(clock);
        await tx.Execute("DELETE FROM metric_pins WHERE id = ?", [id]).ConfigureAwait(false);
        await tx.Execute(
            "INSERT INTO metric_pins (id, profile_id, metric_code, category, target, measurement_id, created_at, updated_at) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            [id, profileId, metricCode, category, target, measurementId, now, now]).ConfigureAwait(false);
    }

    internal sealed class MetricOf
    {
        public string MetricCode { get; set; } = "";

        public string Category { get; set; } = "";
    }

    internal sealed class IdOnly
    {
        public string Id { get; set; } = "";
    }
}
