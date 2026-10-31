using Health360.Contracts.Body;
using Health360.Contracts.Profiles;
using Health360.Domain.Physiology;
using Health360.LocalDb;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;
using MeasurementStore = Health360.LocalDb.Measurements;

namespace Health360.UI.Data;

/// <summary>
/// The signed-in user's measurements and the metric definitions, from the local database and kept current with
/// watched queries, plus the physiology profile <c>Health360.Domain</c> computes from them. Entering or deleting
/// a value changes the watched rows, so everything built from them follows at once.
/// </summary>
public sealed partial class BodyData : IDisposable
{
    private readonly PowerSyncDatabase db;
    private readonly ProfileService profiles;
    private readonly TimeProvider clock;
    private readonly ILogger<BodyData> logger;
    private readonly Lock watchLock = new();
    private readonly CancellationTokenSource definitionsWatch = new();
    private CancellationTokenSource? measurementsWatch;
    private string? watchedProfile;

    public BodyData(PowerSyncDatabase db, ProfileService profiles, TimeProvider clock, ILogger<BodyData> logger)
    {
        this.db = db;
        this.profiles = profiles;
        this.clock = clock;
        this.logger = logger;
        profiles.Changed += Follow;
        var token = definitionsWatch.Token;
        _ = Task.Run(() => WatchDefinitionsAsync(token), CancellationToken.None);
        Follow();
    }

    /// <summary>Raised when anything below changes, possibly off the UI thread.</summary>
    public event Action? Changed;

    /// <summary>The profile's measurements, newest first; null until the local query has answered.</summary>
    public IReadOnlyList<MeasurementRow>? Measurements { get; private set; }

    /// <summary>Metrics by code. Empty until the reference stream has synced to this device.</summary>
    public IReadOnlyDictionary<string, MetricDefinition> Definitions { get; private set; } =
        new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);

    /// <summary>True when a watched query failed; what's shown may then be out of date.</summary>
    public bool Failed { get; private set; }

    /// <summary>Today on this device, the day new entries default to and the profile is computed for.</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>The profile as of today; null until the profile and its measurements have loaded.</summary>
    public PhysiologyProfile? Profile()
    {
        var profile = profiles.Profile;
        var rows = Measurements;
        if (profile is null || rows is null)
        {
            return null;
        }

        return PhysiologyProfile.Build(new PhysiologyInputs(
            Today,
            [.. rows.Select(r => r.ToMetricValue()).OfType<MetricValue>()],
            profile.SexForCalcs switch
            {
                SexForCalcs.Female => Sex.Female,
                SexForCalcs.Male => Sex.Male,
                _ => null,
            },
            DateOnly.TryParseExact(profile.BirthDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var birth) ? birth : null,
            profile.HeightCm,
            Domain.Physiology.ActivityLevels.TryParse(profile.ActivityLevel, out var level) ? level : null,
            PhysiologyPrefsJson.Parse(profile.PhysiologyPrefs).TakesHeartRateLoweringMedication));
    }

    /// <summary>Saves values measured together to the local database; they upload when online.</summary>
    public Task<IReadOnlyList<string>> AddAsync(IReadOnlyList<(MetricDefinition Metric, MeasurementInput Input)> entries) =>
        MeasurementStore.AddAsync(db, RequireProfile().Id, entries, clock);

    public Task DeleteAsync(string id) => MeasurementStore.DeleteAsync(db, RequireProfile().Id, id);

    public Task SetHeartRateLoweringMedicationAsync(bool value) =>
        Profiles.SetHeartRateLoweringMedicationAsync(db, RequireProfile().Id, value);

    public void Dispose()
    {
        profiles.Changed -= Follow;
        definitionsWatch.Cancel();
        definitionsWatch.Dispose();
        lock (watchLock)
        {
            measurementsWatch?.Cancel();
            measurementsWatch?.Dispose();
            measurementsWatch = null;
        }
    }

    private ProfileRow RequireProfile() =>
        profiles.Profile ?? throw new InvalidOperationException("The profile hasn't synced to this device yet.");

    /// <summary>Watches the measurements of the current profile, and stops watching the previous one's.</summary>
    private void Follow()
    {
        var profileId = profiles.Profile?.Id;
        CancellationTokenSource? started = null;
        lock (watchLock)
        {
            if (profileId == watchedProfile)
            {
                // The profile's own fields (sex, birth date, prefs) may have changed: the computed profile follows.
                Changed?.Invoke();
                return;
            }

            measurementsWatch?.Cancel();
            measurementsWatch?.Dispose();
            measurementsWatch = null;
            watchedProfile = profileId;
            Measurements = null;
            if (profileId is not null)
            {
                started = measurementsWatch = new CancellationTokenSource();
            }
        }

        Changed?.Invoke();
        if (started is not null)
        {
            var token = started.Token;
            _ = Task.Run(() => WatchMeasurementsAsync(profileId!, token), CancellationToken.None);
        }
    }

    private async Task WatchMeasurementsAsync(string profileId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var rows in MeasurementStore.Watch(db, profileId, cancellationToken).ConfigureAwait(false))
            {
                lock (watchLock)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    Measurements = rows;
                }

                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            // The type only: a message can quote SQL parameters.
            LogWatchFailed(logger, "measurements", error.GetType().Name);
            Failed = true;
            Changed?.Invoke();
        }
    }

    private async Task WatchDefinitionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var rows in MetricDefinitions.Watch(db, cancellationToken).ConfigureAwait(false))
            {
                Definitions = rows
                    .Select(r => r.ToDefinition())
                    .OfType<MetricDefinition>()
                    .ToDictionary(d => d.Code, StringComparer.Ordinal);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            LogWatchFailed(logger, "metric definitions", error.GetType().Name);
            Failed = true;
            Changed?.Invoke();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Watching {What} failed ({Error})")]
    private static partial void LogWatchFailed(ILogger logger, string what, string error);
}
