using System.Net;
using System.Net.Http.Json;
using Health360.Contracts.Account;
using Health360.Server.Tests.Support;
using Npgsql;

namespace Health360.Server.Tests;

/// <summary>
/// The API against a real migrated database (see <see cref="DatabaseFixture"/>): each request sees
/// only what RLS lets its signed-in user see.
/// </summary>
[Trait("Category", "Database")]
public sealed class UserDatabaseTests(DatabaseFixture db) : IClassFixture<DatabaseFixture>, IAsyncLifetime
{
    private ServerFactory _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        Assert.SkipUnless(db.Available, $"Set {DatabaseFixture.EnvironmentVariable} to run the database tests.");
        _factory = new ServerFactory { ConnectionString = db.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task Me_reads_as_the_signed_in_user()
    {
        using var client = _factory.CreateClient(TestTokens.User(db.A.UserId));

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative), Ct);

        // auth.uid() inside the database session, not the token's sub echoed back.
        Assert.Equal(db.A.UserId, me!.UserId);
        Assert.Equal([new ProfileSummary(db.A.ProfileId, db.A.DisplayName)], me.Profiles);
        Assert.Equal([db.A.HouseholdId], me.HouseholdIds);
    }

    [Fact]
    public async Task Users_never_see_each_others_profiles_or_households()
    {
        using var asA = _factory.CreateClient(TestTokens.User(db.A.UserId));
        using var asB = _factory.CreateClient(TestTokens.User(db.B.UserId));

        var meA = await asA.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative), Ct);
        var meB = await asB.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative), Ct);

        Assert.DoesNotContain(meA!.Profiles, p => p.Id == db.B.ProfileId);
        Assert.DoesNotContain(db.B.HouseholdId, meA.HouseholdIds);
        Assert.DoesNotContain(meB!.Profiles, p => p.Id == db.A.ProfileId);
        Assert.DoesNotContain(db.A.HouseholdId, meB.HouseholdIds);
    }

    [Fact]
    public async Task Another_users_profile_id_is_not_found()
    {
        using var asA = _factory.CreateClient(TestTokens.User(db.A.UserId));

        var own = await asA.GetAsync(new Uri($"/profiles/{db.A.ProfileId}", UriKind.Relative), Ct);
        var other = await asA.GetAsync(new Uri($"/profiles/{db.B.ProfileId}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
    }

    [Fact]
    public async Task A_valid_token_for_a_user_with_no_account_sees_nothing()
    {
        using var client = _factory.CreateClient(TestTokens.User(Guid.CreateVersion7()));

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative), Ct);

        Assert.Empty(me!.Profiles);
        Assert.Empty(me.HouseholdIds);
    }

    [Fact]
    public async Task The_anon_key_is_rejected_before_the_database()
    {
        using var client = _factory.CreateClient(TestTokens.AnonKey());

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_api_login_role_reads_nothing_on_its_own()
    {
        // Guards the role setup: without UserDatabase's SET ROLE, a query has no table privileges.
        await using var connection = new NpgsqlConnection(db.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from public.profiles", connection);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task The_user_role_does_not_outlive_the_request_on_a_pooled_connection()
    {
        using (var client = _factory.CreateClient(TestTokens.User(db.A.UserId)))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);
        }

        // The same data source the API uses, so this likely gets the connection the request returned.
        var dataSource = (NpgsqlDataSource)_factory.Services.GetService(typeof(NpgsqlDataSource))!;
        await using var connection = await dataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select current_user::text, coalesce(current_setting('request.jwt.claims', true), '')", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        await reader.ReadAsync(Ct);

        Assert.Equal("health360_api", reader.GetString(0));
        Assert.Equal("", reader.GetString(1));
    }
}
