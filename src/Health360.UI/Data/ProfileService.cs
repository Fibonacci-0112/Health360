using Health360.Contracts.Profiles;
using Health360.Domain.Units;
using Health360.LocalDb;
using Health360.UI.Auth;
using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;

namespace Health360.UI.Data;

/// <summary>
/// The signed-in user's profile from the local database, kept current with a watched query. The app
/// shell uses it to choose between onboarding and the tabs.
/// </summary>
public sealed partial class ProfileService : IDisposable
{
    private readonly PowerSyncDatabase db;
    private readonly AuthService auth;
    private readonly IDeviceLocale locale;
    private readonly TimeProvider clock;
    private readonly ILogger<ProfileService> logger;
    private readonly Lock watchLock = new();
    private CancellationTokenSource? watch;
    private string? watchedUser;

    public ProfileService(PowerSyncDatabase db, AuthService auth, IDeviceLocale locale, TimeProvider clock, ILogger<ProfileService> logger)
    {
        this.db = db;
        this.auth = auth;
        this.locale = locale;
        this.clock = clock;
        this.logger = logger;
        auth.Changed += Follow;
        Follow();
    }

    /// <summary>Raised when <see cref="Profile"/> or <see cref="Loaded"/> changes, possibly off the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Null until the row has synced to this device (first sign-in) or when signed out.</summary>
    public ProfileRow? Profile { get; private set; }

    /// <summary>True once the local query has answered for the current user.</summary>
    public bool Loaded { get; private set; }

    /// <summary>Saved preferences, or the device locale's defaults for anything not saved.</summary>
    public UnitPrefs UnitPrefs => UnitPrefsJson.Parse(Profile?.UnitPrefs, DefaultUnitPrefs);

    public UnitPrefs DefaultUnitPrefs => UnitPrefs.DefaultFor(locale.CultureName);

    /// <summary>Saves onboarding to the local database; it uploads when online.</summary>
    public Task CompleteOnboardingAsync(OnboardingForm form, double? heightCm) =>
        Profiles.CompleteOnboardingAsync(db, RequireProfile().Id, form, heightCm, clock);

    /// <summary>Saves edited profile details to the local database; they upload when online.</summary>
    public Task SetDetailsAsync(ProfileDetailsForm form, ProfileDetailFields fields) =>
        Profiles.SetDetailsAsync(db, RequireProfile().Id, form, fields);

    public Task SetUnitPrefsAsync(UnitPrefs prefs) => Profiles.SetUnitPrefsAsync(db, RequireProfile().Id, prefs);

    public void Dispose()
    {
        auth.Changed -= Follow;
        lock (watchLock)
        {
            watch?.Cancel();
            watch?.Dispose();
            watch = null;
        }
    }

    private ProfileRow RequireProfile() =>
        Profile ?? throw new InvalidOperationException("The profile hasn't synced to this device yet.");

    /// <summary>Watches the profile of whoever is signed in now, and stops watching the previous one.</summary>
    private void Follow()
    {
        var userId = auth.Status == AuthStatus.SignedIn ? auth.UserId : null;
        CancellationTokenSource? started = null;
        lock (watchLock)
        {
            if (userId == watchedUser)
            {
                return;
            }

            watch?.Cancel();
            watch?.Dispose();
            watch = null;
            watchedUser = userId;
            Profile = null;
            Loaded = false;
            if (userId is not null)
            {
                started = watch = new CancellationTokenSource();
            }
        }

        Changed?.Invoke();
        if (started is not null)
        {
            var token = started.Token;
            _ = Task.Run(() => WatchAsync(userId!, token), CancellationToken.None);
        }
    }

    private async Task WatchAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var rows in Profiles.WatchMine(db, userId, cancellationToken).ConfigureAwait(false))
            {
                lock (watchLock)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    Profile = rows.FirstOrDefault();
                    Loaded = true;
                }

                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            LogWatchFailed(logger, error.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Watching the profile failed ({Error})")]
    private static partial void LogWatchFailed(ILogger logger, string error);
}
