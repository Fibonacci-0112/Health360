using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Health360.Contracts.Body;
using Health360.Domain.Ids;
using Health360.Domain.Physiology;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary><c>metric_definitions</c> as stored locally (G, synced on the reference stream).</summary>
public sealed class MetricDefinitionRow
{
    public string Code { get; set; } = "";

    public string Category { get; set; } = "";

    /// <summary>JSON text: an array of unit codes.</summary>
    public string? Units { get; set; }

    public string CanonicalUnit { get; set; } = "";

    public double? ValidMin { get; set; }

    public double? ValidMax { get; set; }

    /// <summary>The definition, or null when the stored units aren't a JSON array of codes.</summary>
    public MetricDefinition? ToDefinition()
    {
        JsonArray? array;
        try
        {
            array = string.IsNullOrEmpty(Units) ? null : JsonNode.Parse(Units) as JsonArray;
        }
        catch (JsonException)
        {
            array = null;
        }

        var units = array?.Select(node => node is JsonValue value && value.TryGetValue<string>(out var code) ? code : null).ToList();
        return units is { Count: > 0 } && units.TrueForAll(u => u is not null)
            ? new MetricDefinition(Code, Category, units!, CanonicalUnit, ValidMin, ValidMax)
            : null;
    }
}

public static class MetricDefinitions
{
    private const string ListSql =
        "SELECT code AS Code, category AS Category, units AS Units, canonical_unit AS CanonicalUnit, " +
        "valid_min AS ValidMin, valid_max AS ValidMax FROM metric_definitions ORDER BY code";

    /// <summary>Every metric, now and after every change (empty until the reference stream has synced).</summary>
    public static IAsyncEnumerable<MetricDefinitionRow[]> Watch(PowerSyncDatabase db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<MetricDefinitionRow>(ListSql, [], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<MetricDefinitionRow[]> ListAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<MetricDefinitionRow>(ListSql, []).ConfigureAwait(false);
    }
}

/// <summary>One <c>measurements</c> row as the body and physiology screens read it.</summary>
public sealed class MeasurementRow
{
    public string Id { get; set; } = "";

    public string MetricCode { get; set; } = "";

    /// <summary>As entered, in <see cref="Unit"/>.</summary>
    public double Value { get; set; }

    public string Unit { get; set; } = "";

    /// <summary>In the metric's canonical unit; the server recomputes it, so a synced row carries the server's value.</summary>
    public double ValueCanonical { get; set; }

    public string MeasuredAt { get; set; } = "";

    public string LocalDate { get; set; } = "";

    public string? GroupId { get; set; }

    public string Source { get; set; } = "";

    public string? Method { get; set; }

    /// <summary>
    /// The row as the resolver takes it, or null when a synced row can't be read (an unknown source, a malformed
    /// id or date), so one bad row can't break the screen.
    /// </summary>
    public MetricValue? ToMetricValue() =>
        Guid.TryParse(Id, out var id)
        && DateOnly.TryParseExact(LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && MeasurementSources.TryParse(Source, out var source)
        && double.IsFinite(ValueCanonical)
            ? new MetricValue(MetricCode, new Measured(id, ValueCanonical, source, date, Method))
            : null;
}

/// <summary>
/// Body, vitals and physiology values (<c>measurements</c>, U) in the local database. Writes upload when online,
/// like every other write; the server recomputes the canonical value and the category whatever is sent.
/// </summary>
public static class Measurements
{
    private const string MineSql =
        "SELECT id AS Id, metric_code AS MetricCode, value AS Value, unit AS Unit, value_canonical AS ValueCanonical, " +
        "measured_at AS MeasuredAt, local_date AS LocalDate, group_id AS GroupId, source AS Source, method AS Method " +
        "FROM measurements WHERE profile_id = ? ORDER BY measured_at DESC, id DESC";

    /// <summary>The profile's measurements, newest first, now and after every change.</summary>
    public static IAsyncEnumerable<MeasurementRow[]> Watch(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<MeasurementRow>(MineSql, [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<MeasurementRow[]> ListAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<MeasurementRow>(MineSql, [profileId]).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds values measured together, in one transaction: one value is one row, and several (blood pressure's two
    /// numbers) also get a <c>measurement_groups</c> row of kind <c>manual</c> that ties them together. Ids are
    /// UUIDv7s made on the device, so a retried upload can't create a second row.
    /// </summary>
    /// <param name="db">The local database.</param>
    /// <param name="profileId">The signed-in user's profile.</param>
    /// <param name="entries">Each value with its metric; all must share one date and source.</param>
    /// <param name="clock">The device clock.</param>
    /// <returns>The new measurement ids, in the order given.</returns>
    /// <exception cref="ValidationException">An entry fails <see cref="MeasurementInputValidator"/>; nothing is written.</exception>
    public static async Task<IReadOnlyList<string>> AddAsync(
        IPowerSyncDatabase db,
        string profileId,
        IReadOnlyList<(MetricDefinition Metric, MeasurementInput Input)> entries,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(clock);
        if (entries.Count == 0)
        {
            throw new ArgumentException("Nothing to add.", nameof(entries));
        }

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        foreach (var (metric, input) in entries)
        {
            new MeasurementInputValidator(metric, today).ValidateAndThrow(input);
        }

        var first = entries[0].Input;
        if (entries.Any(e => e.Input.LocalDate != first.LocalDate || e.Input.Source != first.Source))
        {
            throw new ArgumentException("Values measured together share one date and source.", nameof(entries));
        }

        var now = Timestamps.UtcNow(clock);
        var measuredAt = MeasuredAt(first.LocalDate, clock);
        var localDate = first.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var groupId = entries.Count > 1 ? NewId(clock) : null;
        var ids = entries.Select(_ => NewId(clock)).ToList();

        await db.WriteTransaction(async tx =>
        {
            if (groupId is not null)
            {
                await tx.Execute(
                    "INSERT INTO measurement_groups (id, profile_id, kind, measured_at, local_date, created_at, updated_at) " +
                    "VALUES (?, ?, 'manual', ?, ?, ?, ?)",
                    [groupId, profileId, measuredAt, localDate, now, now]).ConfigureAwait(false);
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var (metric, input) = entries[i];
                await tx.Execute(
                    "INSERT INTO measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, " +
                    "local_date, group_id, source, method, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, ?)",
                    [
                        ids[i], profileId, metric.Code, metric.Category, input.Value!.Value, input.Unit,
                        metric.ToCanonical(input.Value.Value, input.Unit)!.Value, measuredAt, localDate, groupId, input.Source, now, now,
                    ]).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return ids;
    }

    /// <summary>
    /// Deletes one of the profile's measurements. A value that was entered with others (blood pressure) goes with
    /// them: the whole group is deleted, which on the server deletes its values too. Throws if the row isn't in the
    /// local database, so a delete that changed nothing is never shown as done.
    /// </summary>
    public static async Task DeleteAsync(IPowerSyncDatabase db, string profileId, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentException.ThrowIfNullOrEmpty(id);
        await db.WriteTransaction(async tx =>
        {
            // PowerSync tables are views whose row counts SQLite reports as 0, so the row is looked up first.
            var found = await tx.GetOptional<GroupOf>(
                "SELECT group_id AS GroupId FROM measurements WHERE id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The measurement is not in the local database.");
            if (found.GroupId is { } group)
            {
                await tx.Execute("DELETE FROM measurements WHERE group_id = ? AND profile_id = ?", [group, profileId]).ConfigureAwait(false);
                await tx.Execute("DELETE FROM measurement_groups WHERE id = ? AND profile_id = ?", [group, profileId]).ConfigureAwait(false);
            }
            else
            {
                await tx.Execute("DELETE FROM measurements WHERE id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// When the value was measured: now for today, otherwise noon local time on that day (the form asks for a day,
    /// not a time). UTC, as <see cref="Timestamps.UtcNow"/> writes it.
    /// </summary>
    internal static string MeasuredAt(DateOnly day, TimeProvider clock)
    {
        var local = clock.GetLocalNow();
        if (DateOnly.FromDateTime(local.DateTime) == day)
        {
            return Timestamps.UtcNow(clock);
        }

        var noon = day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        var utc = new DateTimeOffset(noon, clock.LocalTimeZone.GetUtcOffset(noon)).ToUniversalTime();
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string NewId(TimeProvider clock) => UuidV7.Create(clock, RandomNumberGenerator.Fill).ToString();

    internal sealed class GroupOf
    {
        public string? GroupId { get; set; }
    }
}
