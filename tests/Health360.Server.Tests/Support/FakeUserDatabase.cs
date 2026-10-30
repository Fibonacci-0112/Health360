using System.Collections.Concurrent;
using Health360.Contracts.Account;
using Health360.Server.Auth;
using Health360.Server.Data;
using Npgsql;

namespace Health360.Server.Tests.Support;

/// <summary>Records who the API queried as, and answers <c>/me</c> without a database.</summary>
public sealed class FakeUserDatabase : IUserDatabase
{
    public ConcurrentQueue<SignedInUser> Users { get; } = new();

    public Task<T> RunAsync<T>(SignedInUser user, Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        Users.Enqueue(user);
        if (typeof(T) != typeof(MeResponse))
        {
            throw new NotSupportedException("The fake database only answers /me.");
        }

        return Task.FromResult((T)(object)new MeResponse(user.UserId, [], []));
    }
}
