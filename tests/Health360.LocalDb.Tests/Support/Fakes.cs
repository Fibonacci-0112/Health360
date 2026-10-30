namespace Health360.LocalDb.Tests.Support;

/// <summary>A clock that always reads the same time.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A session whose access token is fixed; null means signed out.</summary>
public sealed class FixedSession(string? token) : ISupabaseSession
{
    public string? Token { get; set; } = token;

    public Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
}
