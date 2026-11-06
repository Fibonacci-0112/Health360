using System.Globalization;
using System.Text.Json;
using Health360.Contracts.Training;
using Health360.Domain.Physiology;
using Health360.Domain.Training;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

public sealed class CardioSessionRow
{
    public string Id { get; set; } = "";

    public string LocalDate { get; set; } = "";

    public string? EnergyEstimate { get; set; }

    public CardioEstimate? Estimate => CardioJson.Parse(EnergyEstimate);
}

/// <summary>Manual cardio sessions and their owner-only full estimate snapshots.</summary>
public static class CardioLog
{
    /// <summary>HR zone sets for this profile, now and after local or synced changes.</summary>
    public static IAsyncEnumerable<StoredZones[]> WatchZones(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<StoredZones>(
            "SELECT id AS Id, basis AS Basis, boundaries_canonical AS Boundaries, sport AS Sport, effective_from AS EffectiveFrom " +
            "FROM zone_sets WHERE profile_id = ? AND metric = 'hr' ORDER BY id",
            [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    /// <summary>Latest effective stored HR zones for the sport, then all-sports, then derived zones.</summary>
    public static async Task<CardioZones?> ZonesAsync(
        IPowerSyncDatabase db, string profileId, ZoneSport sport, DateOnly date, PhysiologyProfile profile)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(profile);
        var rows = await db.GetAll<StoredZones>(
            "SELECT id AS Id, basis AS Basis, boundaries_canonical AS Boundaries FROM zone_sets " +
            "WHERE profile_id = ? AND metric = 'hr' AND (sport = ? OR sport = 'all') AND effective_from <= ? " +
            "ORDER BY CASE WHEN sport = ? THEN 0 ELSE 1 END, effective_from DESC, id DESC",
            [profileId, SportCode(sport), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SportCode(sport)]).ConfigureAwait(false);
        foreach (var row in rows)
        {
            // Old derived sets don't retain their input provenance. Under medication, only explicitly custom or
            // lab zones are safe to reuse; otherwise derive afresh from the measured profile values.
            if (profile.Inputs.TakesHeartRateLoweringMedication && row.Basis is not ("custom" or "lab"))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(row.Boundaries);
                var boundaries = json.RootElement.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                if (boundaries.Length is < 2 or > 10 || boundaries.Any(b => !double.IsFinite(b) || b < 0)
                    || boundaries.Skip(1).Where((b, i) => b <= boundaries[i]).Any())
                {
                    continue;
                }

                return new CardioZones($"Stored HR zones ({row.Basis})",
                    [.. boundaries.Select((b, i) => new HeartRateZone((i + 1).ToString(CultureInfo.InvariantCulture), b,
                        i + 1 < boundaries.Length ? boundaries[i + 1] : null))], [], row.Id);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                // A malformed synced zone set must not break manual cardio entry.
            }
        }

        return CardioZones.From(profile.ZonesFor(sport));
    }

    public static async Task<string> SaveAsync(
        IPowerSyncDatabase db, string profileId, CardioEntry entry, CardioPhysiology physiology, CardioZones? zones, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        var estimate = CardioEnergy.Estimate(entry, physiology, zones);
        var id = ExerciseLibrary.NewId(clock);
        var end = entry.EndedAt?.ToUniversalTime() ?? clock.GetUtcNow();
        if (end > clock.GetUtcNow().AddSeconds(1)) throw new ArgumentException("A completed session cannot end in the future.", nameof(entry));
        var start = end.AddSeconds(-entry.DurationSeconds);
        estimate = estimate with { Entry = estimate.Entry with { EndedAt = end } };
        var startDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start, clock.LocalTimeZone).DateTime);
        await db.WriteTransaction(async tx =>
        {
            await tx.Execute(
            "INSERT INTO workout_sessions (id, profile_id, kind, sport, started_at, ended_at, local_date, avg_hr, max_hr, " +
            "zone_seconds, energy_estimate, rpe, created_at, updated_at) VALUES (?, ?, 'cardio', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            [id, profileId, SportCode(estimate.Activity.Sport),
                start.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                end.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), entry.AverageHr, entry.MaxHr,
                entry.ZoneSeconds is { } times ? CardioJson.SerializeTimes(times) : null,
                CardioJson.SerializeWorkoutSummary(estimate), entry.Rpe, Timestamps.UtcNow(clock), Timestamps.UtcNow(clock)]).ConfigureAwait(false);
            await tx.Execute(
                "INSERT INTO cardio_estimates (id, profile_id, estimate, created_at, updated_at) VALUES (?, ?, ?, ?, ?)",
                [id, profileId, CardioJson.Serialize(estimate), Timestamps.UtcNow(clock), Timestamps.UtcNow(clock)]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>All snapshots in a date range, for weekly totals without recomputing from today's profile.</summary>
    public static Task<CardioSessionRow[]> SessionsAsync(IPowerSyncDatabase db, string profileId, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.GetAll<CardioSessionRow>(
            "SELECT ws.id AS Id, ws.local_date AS LocalDate, coalesce(ce.estimate, ws.energy_estimate) AS EnergyEstimate " +
            "FROM workout_sessions ws LEFT JOIN cardio_estimates ce ON ce.id = ws.id AND ce.profile_id = ws.profile_id " +
            "WHERE ws.profile_id = ? AND ws.kind = 'cardio' AND ws.ended_at IS NOT NULL AND ws.local_date BETWEEN ? AND ? " +
            "ORDER BY ws.started_at DESC, ws.id DESC",
            [profileId, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)]);
    }

    private static string SportCode(ZoneSport sport) => sport.ToString().ToLowerInvariant();

    public sealed class StoredZones
    {
        public string Id { get; set; } = "";

        public string Basis { get; set; } = "";

        public string Boundaries { get; set; } = "";

        public string Sport { get; set; } = "";

        public string EffectiveFrom { get; set; } = "";
    }
}
