using Health360.UI.Auth;
using Health360.UI.Data;

namespace Health360.UI.Tests.Support;

/// <summary>A clock tests can move.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-30T12:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);

    public DateTimeOffset Now { get; set; } = now;

    public TestClock()
        : this(Start)
    {
    }

    public override DateTimeOffset GetUtcNow() => Now;
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
