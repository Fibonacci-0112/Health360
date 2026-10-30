using System.Security.Cryptography;
using Npgsql;

namespace Health360.Server.Tests.Support;

/// <summary>
/// A migrated database, from <c>HEALTH360_TEST_DATABASE</c>: an Npgsql connection string for a role
/// that can create roles and users (locally <c>postgres</c> on <c>supabase db start</c>, or
/// tools/db/test-local.sh's database). Without it, the database tests are skipped.
/// <para>
/// Creates a throwaway login role set up like the API's (docs/phase0b.md), with a random name and
/// password, and two users, A and B, whose profiles and households come from the sign-up trigger.
/// All of it is removed afterwards, even when setup fails partway, so no known login is left behind
/// and a real <c>health360_api</c> role is never touched.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "HEALTH360_TEST_DATABASE";

    private readonly string? _adminConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
    private bool _roleCreated;

    public bool Available => !string.IsNullOrEmpty(_adminConnectionString);

    /// <summary>This run's stand-in for <c>health360_api</c>.</summary>
    public string ApiRole { get; } = $"health360_api_test_{Guid.NewGuid():N}";

    public TestUser? A { get; private set; }

    public TestUser? B { get; private set; }

    /// <summary>The API's connection string: its own login role, which can only become <c>authenticated</c>.</summary>
    public string ApiConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        try
        {
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            await using var admin = await OpenAdminAsync();
            await ExecuteAsync(admin, $"create role {ApiRole} login noinherit password '{password}'");
            _roleCreated = true;
            await ExecuteAsync(admin, $"grant authenticated to {ApiRole}");

            A = await CreateUserAsync(admin, "Alex");
            B = await CreateUserAsync(admin, "Blake");

            ApiConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Username = ApiRole,
                Password = password,
            }.ConnectionString;
        }
        catch
        {
            await CleanUpAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => Available ? new(CleanUpAsync()) : ValueTask.CompletedTask;

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task CleanUpAsync()
    {
        // Pooled connections of the throwaway role would outlive it.
        NpgsqlConnection.ClearAllPools();
        await using var admin = await OpenAdminAsync();

        var users = new[] { A?.UserId, B?.UserId }.OfType<Guid>().ToArray();
        if (users.Length > 0)
        {
            await using var command = new NpgsqlCommand("delete from auth.users where id = any($1)", admin);
            command.Parameters.Add(new NpgsqlParameter { Value = users });
            await command.ExecuteNonQueryAsync();
        }

        if (_roleCreated)
        {
            await ExecuteAsync(admin, $"drop role if exists {ApiRole}");
            _roleCreated = false;
        }
    }

    private static async Task<TestUser> CreateUserAsync(NpgsqlConnection admin, string name)
    {
        var userId = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand(
            "insert into auth.users (id, email, raw_user_meta_data) values ($1, $2, jsonb_build_object('display_name', $3::text))", admin))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = userId });
            insert.Parameters.Add(new NpgsqlParameter { Value = $"{userId}@example.com" });
            insert.Parameters.Add(new NpgsqlParameter { Value = name });
            await insert.ExecuteNonQueryAsync();
        }

        await using var select = new NpgsqlCommand(
            "select p.id, m.household_id from public.profiles p join public.household_members m on m.profile_id = p.id where p.user_id = $1", admin);
        select.Parameters.Add(new NpgsqlParameter { Value = userId });
        await using var reader = await select.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new TestUser(userId, reader.GetGuid(0), reader.GetGuid(1), name);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

public sealed record TestUser(Guid UserId, Guid ProfileId, Guid HouseholdId, string DisplayName);
