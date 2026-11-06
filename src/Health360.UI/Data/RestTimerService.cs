using System.Globalization;

namespace Health360.UI.Data;

public interface IRestNotifications
{
    Task<bool> ScheduleAsync(DateTimeOffset deadline);
    Task CancelAsync();
}

/// <summary>The platform host supplies OS notifications. Other hosts keep an in-app timer.</summary>
public sealed class InAppRestNotifications : IRestNotifications
{
    public Task<bool> ScheduleAsync(DateTimeOffset deadline) => Task.FromResult(false);
    public Task CancelAsync() => Task.CompletedTask;
}

public sealed class RestTimerService : IDisposable
{
    private readonly WorkoutService workouts;
    private readonly ProfileService profiles;
    private readonly IRestNotifications notifications;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? owner;
    public string? SessionId { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }
    public string? Notice { get; private set; }
    public int RemainingSeconds => Deadline is { } end ? (int)Math.Clamp(Math.Ceiling((end - clock.GetUtcNow()).TotalSeconds), 0, 3600) : 0;
    public event Action? Changed;

    public RestTimerService(WorkoutService workouts, ProfileService profiles, IRestNotifications notifications, TimeProvider clock)
    {
        this.workouts = workouts; this.profiles = profiles; this.notifications = notifications; this.clock = clock;
        profiles.Changed += ProfileChanged;
        ProfileChanged();
    }

    public async Task RestoreAsync(string sessionId)
    {
        await gate.WaitAsync();
        try
        {
            var profile = workouts.ProfileId;
            if (profile is null) return;
            var saved = await workouts.ReadStateAsync("rest");
            if (profile != workouts.ProfileId) return;
            owner = profile;
            var parts = saved?.Split('|');
            if (parts is { Length: 2 } && parts[0] == sessionId
                && DateTimeOffset.TryParseExact(parts[1], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            { SessionId = sessionId; Deadline = end; }
            else { SessionId = null; Deadline = null; }
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    public async Task StartAsync(string sessionId, int seconds)
    {
        if (seconds is < 0 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
        await gate.WaitAsync();
        try
        {
            var profile = workouts.ProfileId ?? throw new InvalidOperationException("Sign in to start a timer.");
            owner = profile;
            var deadline = clock.GetUtcNow().AddSeconds(seconds);
            await workouts.SaveStateAsync("rest", sessionId + "|" + deadline.ToString("O", CultureInfo.InvariantCulture));
            if (profile != workouts.ProfileId) return;
            SessionId = sessionId; Deadline = deadline; Notice = null;
            try
            {
                await notifications.CancelAsync();
                if (seconds > 0 && !await notifications.ScheduleAsync(deadline))
                    Notice = "Notifications are off, unavailable, or may be delayed by device settings. The saved timer still works in the app.";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { Notice = "Couldn't schedule a notification. The saved timer still works in the app."; }
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            Deadline = null; SessionId = null; Notice = null;
            if (workouts.ProfileId is not null) await workouts.DeleteStateAsync("rest");
            try { await notifications.CancelAsync(); }
            catch (Exception e) when (e is not OperationCanceledException) { Notice = "Rest stopped in the app. Couldn't cancel its system notification."; }
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    private void ProfileChanged()
    {
        if (owner == workouts.ProfileId) return;
        owner = workouts.ProfileId; Deadline = null; SessionId = null; Notice = null;
        _ = CancelForOwnerChangeAsync();
        Changed?.Invoke();
    }
    private async Task CancelForOwnerChangeAsync()
    {
        await gate.WaitAsync();
        var profile = workouts.ProfileId;
        try
        {
            await notifications.CancelAsync();
            if (profile is null) return;
            var saved = await workouts.ReadStateAsync("rest");
            if (profile != workouts.ProfileId) return;
            var parts = saved?.Split('|');
            if (parts is { Length: 2 } && DateTimeOffset.TryParseExact(parts[1], "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                SessionId = parts[0]; Deadline = end;
                if (end > clock.GetUtcNow() && !await notifications.ScheduleAsync(end))
                {
                    Notice = "The saved timer is restored. System notifications may be unavailable or delayed.";
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { Notice = "Couldn't restore the system reminder. Open your workout to resume the saved timer."; }
        finally { gate.Release(); Changed?.Invoke(); }
    }
    public void Dispose() => profiles.Changed -= ProfileChanged;
}
