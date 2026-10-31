using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Health360.Sync.Tests.Support;

/// <summary>The device's session; another user signing in replaces the token.</summary>
public sealed class SwitchableSession(SessionToken token) : ISupabaseSession
{
    public SessionToken? Token { get; set; } = token;

    public Task<SessionToken?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
}

/// <summary>An owner store in memory, standing in for the app's preferences.</summary>
public sealed class MemoryOwnerStore : IDatabaseOwnerStore
{
    public string? Owner { get; set; }

    public string? Load() => Owner;

    public void Save(string userId) => Owner = userId;

    public void Forget() => Owner = null;
}

/// <summary>Keeps every formatted log message, so tests can check what was (and wasn't) logged.</summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> messages = new();

    public IReadOnlyList<string> Messages => [.. messages];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        messages.Enqueue(formatter(state, exception));
    }
}

public static class Wait
{
    /// <summary>Polls <paramref name="probe"/> until <paramref name="done"/>, or fails after the timeout.</summary>
    public static async Task<T> ForAsync<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(done);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds} s.");
            }

            await Task.Delay(50);
        }
    }
}
