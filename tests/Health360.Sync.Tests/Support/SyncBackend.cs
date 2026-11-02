using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Health360.Sync.Tests.Support;

/// <summary>
/// The local stack (<c>supabase start</c> plus <c>powersync/docker-compose.yml</c>), from environment
/// variables. Users are made through the Auth admin API with the service-role
/// key, which only this test harness holds; every device signs in with a password and from then on
/// uses the publishable key and its own access token, as the app does. Without <c>SYNC_E2E=1</c> the
/// tests are skipped.
/// </summary>
public sealed class SyncBackend : IAsyncDisposable
{
    public const string EnableVariable = "SYNC_E2E";

    private readonly HttpClient http;
    private readonly string serviceRoleKey;
    private readonly List<string> createdUsers = [];

    private SyncBackend(SyncEndpoints endpoints, string serviceRoleKey)
    {
        Endpoints = endpoints;
        this.serviceRoleKey = serviceRoleKey;
        http = new HttpClient { BaseAddress = endpoints.SupabaseUrl };
    }

    public static bool Enabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    public SyncEndpoints Endpoints { get; }

    public static SyncBackend FromEnvironment() =>
        new(
            new SyncEndpoints(new Uri(Env("SUPABASE_URL")), Env("SUPABASE_ANON_KEY"), new Uri(Env("POWERSYNC_URL"))),
            Env("SUPABASE_SERVICE_ROLE_KEY"));

    /// <summary>A new confirmed user, signed in. The sign-up trigger creates their profile and household.</summary>
    public async Task<TestUser> CreateUserAsync(string name, CancellationToken cancellationToken)
    {
        var email = $"{name}-{Guid.NewGuid():N}@example.test";
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

        using (var create = new HttpRequestMessage(HttpMethod.Post, "auth/v1/admin/users"))
        {
            create.Headers.Add("apikey", serviceRoleKey);
            create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
            create.Content = JsonContent.Create(new JsonObject
            {
                ["email"] = email,
                ["password"] = password,
                ["email_confirm"] = true,
                ["user_metadata"] = new JsonObject { ["display_name"] = name },
            });
            var created = await SendAsync(create, cancellationToken);
            createdUsers.Add(Required(created, "id"));
        }

        string userId;
        string token;
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, "auth/v1/token?grant_type=password"))
        {
            signIn.Headers.Add("apikey", Endpoints.SupabaseApiKey);
            signIn.Content = JsonContent.Create(new JsonObject { ["email"] = email, ["password"] = password });
            var session = await SendAsync(signIn, cancellationToken);
            token = Required(session, "access_token");
            userId = Required(session["user"]!, "id");
        }

        // Read as the user, so RLS decides what comes back: exactly their own profile.
        using var profiles = new HttpRequestMessage(HttpMethod.Get, "rest/v1/profiles?select=id");
        profiles.Headers.Add("apikey", Endpoints.SupabaseApiKey);
        profiles.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var rows = (JsonArray)(await SendAsync(profiles, cancellationToken));
        Assert.Single(rows);
        return new TestUser(name, userId, Required(rows[0]!, "id"), new SessionToken(userId, token));
    }

    /// <summary>
    /// Makes <paramref name="member"/> an adult member of <paramref name="owner"/>'s personal household, as an accepted
    /// invite will. Only the server adds members (clients can't insert into <c>household_members</c>), so this uses
    /// the service-role key.
    /// </summary>
    public async Task AddToHouseholdAsync(TestUser owner, TestUser member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(member);
        string household;
        using (var find = new HttpRequestMessage(
            HttpMethod.Get, $"rest/v1/household_members?select=household_id&role=eq.owner&profile_id=eq.{owner.ProfileId}"))
        {
            find.Headers.Add("apikey", serviceRoleKey);
            find.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
            var rows = (JsonArray)await SendAsync(find, cancellationToken);
            household = Required(Assert.Single(rows)!, "household_id");
        }

        using var add = new HttpRequestMessage(HttpMethod.Post, "rest/v1/household_members");
        add.Headers.Add("apikey", serviceRoleKey);
        add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
        add.Headers.Add("Prefer", "return=representation");
        add.Content = JsonContent.Create(new JsonObject { ["household_id"] = household, ["profile_id"] = member.ProfileId, ["role"] = "adult" });
        await SendAsync(add, cancellationToken);
    }

    /// <summary>
    /// One Storage request in the <c>progress-photos</c> bucket as <paramref name="user"/>, with the publishable key and
    /// their own token, the way any client of theirs could send it. <paramref name="route"/> is <c>"authenticated/"</c>
    /// for a download. The answer's status, Storage's own status in the body when it has one, and the body.
    /// </summary>
    public async Task<StorageAnswer> StorageAsync(
        TestUser user, HttpMethod method, string path, byte[]? jpeg, CancellationToken cancellationToken, string route = "")
    {
        ArgumentNullException.ThrowIfNull(user);
        using var request = new HttpRequestMessage(method, $"storage/v1/object/{route}{SupabasePhotoStorage.Bucket}/{path}");
        request.Headers.Add("apikey", Endpoints.SupabaseApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Session.AccessToken);
        if (jpeg is not null)
        {
            request.Content = new ByteArrayContent(jpeg);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new StorageAnswer((int)response.StatusCode, body);
    }

    /// <summary>Deletes the users this run created; their profiles, households and rows go with them.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var id in createdUsers)
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"auth/v1/admin/users/{id}");
            delete.Headers.Add("apikey", serviceRoleKey);
            delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
            using var response = await http.SendAsync(delete);
        }

        http.Dispose();
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        // The status only: a body can hold a token or an email address.
        Assert.True(response.IsSuccessStatusCode, $"{request.Method} {request.RequestUri?.AbsolutePath} answered {(int)response.StatusCode}");
        return JsonNode.Parse(body)!;
    }

    private static string Required(JsonNode node, string property) =>
        node[property]?.GetValue<string>() ?? throw new InvalidOperationException($"Auth's response has no {property}.");

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is not set (see docs/phase0b.md, \"Two-client sync test\").");
}

/// <summary>What Storage answered. <see cref="Body"/> may be a photo: assert on it, never log it.</summary>
public sealed record StorageAnswer(int Status, byte[] Body)
{
    /// <summary>
    /// The status Storage means: some versions answer a refusal with HTTP 400 and the real status (403, 404) as
    /// <c>statusCode</c> in the JSON body.
    /// </summary>
    public int Effective
    {
        get
        {
            if (Status != 400)
            {
                return Status;
            }

            try
            {
                return JsonNode.Parse(Body) is JsonObject error
                    && int.TryParse((error["statusCode"] as JsonValue)?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var inner)
                    ? inner
                    : Status;
            }
            catch (System.Text.Json.JsonException)
            {
                return Status;
            }
        }
    }

    public bool Succeeded => Status is >= 200 and < 300;
}

/// <summary>A signed-in user. <see cref="Session"/> holds their access token; never log it.</summary>
public sealed record TestUser(string Name, string UserId, string ProfileId, SessionToken Session)
{
    public override string ToString() => Name;
}
