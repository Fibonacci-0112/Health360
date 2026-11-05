using Health360.UI.Auth;
using Health360.UI.Data;

namespace Health360.UI.Tests.Support;

/// <summary>A clock tests can move.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    private readonly object timerLock = new();
    private readonly List<TestTimer> timers = [];

    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-30T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);

    public DateTimeOffset Now { get; set; } = now;

    public TestClock()
        : this(Start)
    {
    }

    /// <summary>The device's time zone; UTC unless a test sets one.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;

    public override TimeZoneInfo LocalTimeZone => Zone;

    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state, dueTime, period);
        lock (timerLock)
        {
            timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan amount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

        Now = Now.Add(amount);
        TestTimer[] active;
        lock (timerLock)
        {
            active = [.. timers];
        }

        foreach (var timer in active)
        {
            timer.FireIfDue(Now);
        }
    }

    private void Remove(TestTimer timer)
    {
        lock (timerLock)
        {
            timers.Remove(timer);
        }
    }

    private sealed class TestTimer : ITimer
    {
        private readonly TestClock owner;
        private readonly TimerCallback callback;
        private readonly object? state;
        private DateTimeOffset? due;
        private TimeSpan period;
        private bool disposed;

        public TestTimer(TestClock owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            this.owner = owner;
            this.callback = callback;
            this.state = state;
            Change(dueTime, period);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (this)
            {
                if (disposed)
                {
                    return false;
                }

                due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.Now.Add(dueTime);
                this.period = period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                disposed = true;
                due = null;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal void FireIfDue(DateTimeOffset now)
        {
            lock (this)
            {
                if (disposed || due is not { } scheduled || now < scheduled)
                {
                    return;
                }

                due = period == Timeout.InfiniteTimeSpan ? null : now.Add(period);
            }

            callback(state);
        }
    }
}

/// <summary>The device's secure storage, in memory.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    public AuthSession? Saved { get; set; }

    public int Saves { get; private set; }

    public Task<AuthSession?> LoadAsync() => Task.FromResult(Saved);

    public Task SaveAsync(AuthSession session)
    {
        Saved = session;
        Saves++;
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        Saved = null;
        return Task.CompletedTask;
    }
}

/// <summary>Records what sign-in asks of the local database, in order.</summary>
public sealed class FakeLocalAccount : ILocalAccount
{
    public List<string> Calls { get; } = [];

    public UnsyncedChanges Unsynced { get; set; } = new(0, 0);

    public Task BindAsync(string userId) => Record($"bind {userId}");

    public Task ConnectAsync(string userId) => Record($"connect {userId}");

    public Task DisconnectAsync() => Record("disconnect");

    public Task ClearAsync() => Record("clear");

    public Task<UnsyncedChanges> UnsyncedAsync() => Task.FromResult(Unsynced);

    private Task Record(string call)
    {
        lock (Calls)
        {
            Calls.Add(call);
        }

        return Task.CompletedTask;
    }
}

public sealed class FixedLocale(string culture = "en-US", string timeZone = "America/Chicago", string currency = "USD") : IDeviceLocale
{
    public string CultureName => culture;

    public string TimeZone => timeZone;

    public string Currency => currency;
}

/// <summary>The database owner, in memory instead of app preferences.</summary>
public sealed class MemoryOwnerStore : Health360.LocalDb.IDatabaseOwnerStore
{
    public string? Owner { get; set; }

    public string? Load() => Owner;

    public void Save(string userId) => Owner = userId;

    public void Forget() => Owner = null;
}
