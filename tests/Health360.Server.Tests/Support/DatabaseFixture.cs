using Npgsql;

namespace Health360.Server.Tests.Support;

/// <summary>
/// A migrated database, from <c>HEALTH360_TEST_DATABASE</c>: an Npgsql connection string for a role
/// that can create roles and users (locally <c>postgres</c> on <c>supabase db start</c>, or
/// tools/db/test-local.sh's database). Without it, the database tests are skipped.
/// <para>
/// Creates the API's login role the way docs/phase0b.md says to, and two users, A and B, whose
/// profiles and households come from the sign-up trigger. The users are deleted afterwards.
/// </para>
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "HEALTH360_TEST_DATABASE";
    private const string ApiRole = "health360_api";
    private const string ApiPassword = "health360-api-local-test";

    private readonly string? _adminConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);

    public bool Available => !string.IsNullOrEmpty(_adminConnectionString);

    public TestUser A { get; private set; } = null!;

    public TestUser B { get; private set; } = null!;

    /// <summary>The API's connection string: its own login role, which can only become <c>authenticated</c>.</summary>
    public string ApiConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        await using var admin = await OpenAdminAsync();
        await ExecuteAsync(admin, $"""
            do $$ begin
              if not exists (select from pg_roles where rolname = '{ApiRole}') then
                create role {ApiRole} login noinherit password '{ApiPassword}';
              end if;
            end $$;
            grant authenticated to {ApiRole};
            """);

        A = await CreateUserAsync(admin, "Alex");
        B = await CreateUserAsync(admin, "Blake");

        ApiConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Username = ApiRole,
            Password = ApiPassword,
        }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (!Available || A is null)
        {
            return;
        }

        await using var admin = await OpenAdminAsync();
        await using var command = new NpgsqlCommand("delete from auth.users where id = any($1)", admin);
        command.Parameters.Add(new NpgsqlParameter { Value = new[] { A.UserId, B?.UserId ?? Guid.Empty } });
        await command.ExecuteNonQueryAsync();
    }

    public async Task<NpgsqlConnection> OpenAdminAsync()
    {
        var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        return connection;
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
