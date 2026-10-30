using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Health360.Contracts.Account;
using Health360.Server.Tests.Support;

namespace Health360.Server.Tests;

/// <summary>
/// Which bearer tokens reach an endpoint. Runs without a database: <see cref="FakeUserDatabase"/>
/// records who the API would have queried as.
/// </summary>
public class AuthenticationTests
{
    private static readonly Guid UserA = Guid.Parse("0192f1a2-0000-7000-8000-00000000000a");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> RejectedTokens()
    {
        var expired = TestTokens.User(UserA, expires: DateTimeOffset.UtcNow.AddMinutes(-5));
        return new()
        {
            { "anon key", TestTokens.AnonKey() },
            { "service-role key", TestTokens.ServiceRoleKey() },
            { "expired", expired },
            { "other project's issuer", TestTokens.User(UserA, issuer: "https://other.supabase.co/auth/v1") },
            { "wrong audience", TestTokens.User(UserA, audience: "anon") },
            { "anon role with a user's claims", TestTokens.User(UserA, role: "anon") },
            { "service_role role with a user's claims", TestTokens.User(UserA, role: "service_role") },
            { "sub is not a user id", TestTokens.User(UserA, subject: "anonymous") },
            { "no session_id (Auth would skip its session check)", TestTokens.User(UserA, sessionId: null) },
            { "nil session_id", TestTokens.User(UserA, sessionId: Guid.Empty.ToString()) },
            { "session_id is not a UUID", TestTokens.User(UserA, sessionId: "session") },
            { "signed with another HS256 secret", TestTokens.User(UserA, TestTokens.Hmac("another-secret-that-is-at-least-32-characters")) },
            { "alg none", TestTokens.AlgNone(UserA) },
            { "not a JWT", "not-a-jwt" },
        };
    }

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task Rejects_tokens_that_are_not_a_signed_in_users(string because, string token)
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        using var client = factory.CreateClient(token);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{because}: got {response.StatusCode}");
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Rejects_requests_without_a_token()
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        using var client = factory.CreateClient(token: null);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("ES256 from the JWKS")]
    [InlineData("legacy HS256 secret")]
    public async Task Accepts_a_signed_in_users_token_and_queries_as_that_user(string signing)
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        var token = TestTokens.User(UserA, signing.StartsWith("ES256", StringComparison.Ordinal) ? TestTokens.Es256 : TestTokens.Hs256);
        using var client = factory.CreateClient(token);

        var me = await client.GetFromJsonAsync<MeResponse>(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(UserA, me!.UserId);
        var user = Assert.Single(db.Users);
        Assert.Equal(UserA, user.UserId);
        using var claims = JsonDocument.Parse(user.ClaimsJson);
        Assert.Equal(UserA.ToString(), claims.RootElement.GetProperty("sub").GetString());
        Assert.Equal("authenticated", claims.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Rejects_HS256_when_the_project_signs_with_asymmetric_keys_only()
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db, JwtSecret = null };

        using var hs256 = factory.CreateClient(TestTokens.User(UserA, TestTokens.Hs256));
        Assert.Equal(HttpStatusCode.Unauthorized, (await hs256.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);

        using var es256 = factory.CreateClient(TestTokens.User(UserA, TestTokens.Es256));
        Assert.Equal(HttpStatusCode.OK, (await es256.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task Rejects_a_signed_out_session_even_though_its_token_has_not_expired()
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        var token = TestTokens.User(UserA);
        factory.Auth.SignedOut[token] = true;
        using var client = factory.CreateClient(token);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, factory.Auth.UserCalls);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Answers_503_not_401_when_Auth_is_unreachable()
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        factory.Auth.Down = true;
        using var client = factory.CreateClient(TestTokens.User(UserA));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("""["not","an","object"]""")]
    [InlineData("""{"id":42}""")]
    [InlineData("""{"aud":"authenticated"}""")]
    public async Task Answers_503_when_Auth_answers_with_something_other_than_a_user(string body)
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        factory.Auth.UserBodyOverride = body;
        using var client = factory.CreateClient(TestTokens.User(UserA));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Rejects_a_user_object_for_a_different_user()
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        factory.Auth.UserBodyOverride = $$"""{"id":"{{Guid.CreateVersion7()}}"}""";
        using var client = factory.CreateClient(TestTokens.User(UserA));

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(db.Users);
    }

    [Theory]
    [InlineData("ES256 from the JWKS")]
    [InlineData("legacy HS256 secret")]
    public async Task Answers_503_when_the_signing_keys_cannot_be_loaded(string signing)
    {
        var db = new FakeUserDatabase();
        await using var factory = new ServerFactory { Database = db };
        factory.Auth.JwksDown = true;
        var token = TestTokens.User(UserA, signing.StartsWith("ES256", StringComparison.Ordinal) ? TestTokens.Es256 : TestTokens.Hs256);
        using var client = factory.CreateClient(token);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(db.Users);
    }

    [Fact]
    public async Task Recovers_once_the_signing_keys_load_again()
    {
        await using var factory = new ServerFactory { Database = new FakeUserDatabase() };
        factory.Auth.JwksDown = true;
        using var client = factory.CreateClient(TestTokens.User(UserA));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);

        factory.Auth.JwksDown = false;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_token_are_401_even_when_the_signing_keys_cannot_be_loaded()
    {
        await using var factory = new ServerFactory { Database = new FakeUserDatabase() };
        factory.Auth.JwksDown = true;
        using var client = factory.CreateClient(token: null);

        var response = await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Caches_a_confirmed_session_briefly()
    {
        await using var factory = new ServerFactory { Database = new FakeUserDatabase() };
        using var client = factory.CreateClient(TestTokens.User(UserA));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/me", UriKind.Relative), Ct)).StatusCode);
        }

        Assert.Equal(1, factory.Auth.UserCalls);
    }

    [Fact]
    public async Task Does_not_ask_Auth_about_tokens_that_fail_validation()
    {
        await using var factory = new ServerFactory { Database = new FakeUserDatabase() };
        using var client = factory.CreateClient(TestTokens.AnonKey());

        await client.GetAsync(new Uri("/me", UriKind.Relative), Ct);

        Assert.Equal(0, factory.Auth.UserCalls);
    }
}
