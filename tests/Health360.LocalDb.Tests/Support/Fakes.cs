namespace Health360.LocalDb.Tests.Support;

/// <summary>A clock that always reads the same time.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>A session whose token is fixed; null means signed out.</summary>
public sealed class FixedSession(SessionToken? token) : ISupabaseSession
{
    public SessionToken? Token { get; set; } = token;

    public Task<SessionToken?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);
}
