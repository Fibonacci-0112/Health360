using Health360.Server.Auth;
using Npgsql;

namespace Health360.Server.Data;

/// <summary>Database access on behalf of the signed-in user, so Postgres RLS decides what they see.</summary>
public interface IUserDatabase
{
    /// <summary>
    /// Runs <paramref name="work"/> in a transaction as the <c>authenticated</c> role with the user's
    /// JWT claims, as PostgREST does. It commits if <paramref name="work"/> returns, and rolls back if
    /// it throws.
    /// </summary>
    Task<T> RunAsync<T>(SignedInUser user, Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>
/// Connects as the API's login role (<c>health360_api</c>: <c>noinherit</c>, a member of
/// <c>authenticated</c> and nothing else; see docs/phase0b.md). That role has no table privileges of
/// its own, so a query that skipped <see cref="RunAsync"/> could read nothing. Both settings are
/// transaction-local, so nothing leaks to the next user of a pooled connection.
/// </summary>
public sealed class UserDatabase(NpgsqlDataSource dataSource) : IUserDatabase
{
    public async Task<T> RunAsync<T>(SignedInUser user, Func<NpgsqlConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(work);

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                await using (var impersonate = new NpgsqlCommand(
                    "select set_config('role', 'authenticated', true), set_config('request.jwt.claims', $1, true)", connection, transaction))
                {
                    impersonate.Parameters.Add(new NpgsqlParameter { Value = user.ClaimsJson });
                    await impersonate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                var result = await work(connection, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
    }
}
