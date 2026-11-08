using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.LocalDb;

/// <summary>What a catalogue search found, or why it couldn't run.</summary>
public enum CatalogueSearchStatus
{
    Found,

    /// <summary>Not signed in as the local database's owner: there is nobody to search as.</summary>
    SignedOut,

    /// <summary>The request failed or timed out, or the answer wasn't readable: offline, most likely. Worth retrying.</summary>
    Unavailable,
}

/// <summary>The catalogue foods a search found (empty unless <see cref="Status"/> is <see cref="CatalogueSearchStatus.Found"/>).</summary>
public sealed record CatalogueSearchResult(CatalogueSearchStatus Status, IReadOnlyList<FoodRow> Foods)
{
    public static CatalogueSearchResult Of(CatalogueSearchStatus status) => new(status, []);
}

/// <summary>
/// Searches the food catalogue online (the USDA FoodData Central import: rows with no household, never synced). A
/// result is used by copying it into a household (<see cref="FoodLibrary.CopyFromCatalogueAsync"/>), which syncs.
/// </summary>
public interface ICatalogueSearch
{
    /// <summary>Up to <paramref name="limit"/> catalogue foods matching every word of <paramref name="query"/>, best first.</summary>
    Task<CatalogueSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ICatalogueSearch"/> through PostgREST's <c>rpc/search_catalogue_foods</c> with the user's own session,
/// so the same row level security as every other read applies (migration <c>20261108000002_catalogue_search.sql</c>).
/// </summary>
/// <param name="http">A client whose base address is the Supabase project URL.</param>
/// <param name="session">The signed-in session whose access token the request carries.</param>
/// <param name="owner">The local database's owner; the request is sent only with their token.</param>
/// <param name="apiKey">The publishable (anon) key, sent as the <c>apikey</c> header.</param>
public sealed class PostgrestCatalogueSearch(HttpClient http, ISupabaseSession session, IDatabaseOwnerStore owner, string apiKey)
    : ICatalogueSearch
{
    /// <summary>The shortest query the server searches for; shorter ones return nothing without a request.</summary>
    public const int MinimumQueryLength = 2;

    public async Task<CatalogueSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var text = (query ?? "").Trim();
        if (text.Length < MinimumQueryLength)
        {
            return new CatalogueSearchResult(CatalogueSearchStatus.Found, []);
        }

        var token = await OwnerSession.TokenAsync(session, owner, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return CatalogueSearchResult.Of(CatalogueSearchStatus.SignedOut);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "rest/v1/rpc/search_catalogue_foods");
            request.Headers.Add("apikey", apiKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new JsonObject { ["query"] = text, ["max_results"] = Math.Clamp(limit, 1, 50) };
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return CatalogueSearchResult.Of(CatalogueSearchStatus.Unavailable);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json) is { } foods
                ? new CatalogueSearchResult(CatalogueSearchStatus.Found, foods)
                : CatalogueSearchResult.Of(CatalogueSearchStatus.Unavailable);
        }
        catch (HttpRequestException)
        {
            return CatalogueSearchResult.Of(CatalogueSearchStatus.Unavailable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's timeout.
            return CatalogueSearchResult.Of(CatalogueSearchStatus.Unavailable);
        }
    }

    /// <summary>
    /// The rows of a search answer as foods with no household, or null when the answer isn't a list (a captive
    /// portal's page, say). A row without an id, name or basis is skipped rather than failing the whole answer.
    /// </summary>
    public static IReadOnlyList<FoodRow>? Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is not JsonArray rows)
        {
            return null;
        }

        var foods = new List<FoodRow>(rows.Count);
        foreach (var row in rows.OfType<JsonObject>())
        {
            if (Text(row, "id") is not { } id || !Guid.TryParse(id, out _) || Text(row, "name") is not { } name
                || Text(row, "basis") is not { } basis)
            {
                continue;
            }

            foods.Add(new FoodRow
            {
                Id = id,
                HouseholdId = null,
                Source = Text(row, "source") ?? "",
                Name = name,
                Brand = Text(row, "brand"),
                Basis = basis,
                Energy100 = Number(row, "energy_100"),
                Protein100 = Number(row, "protein_100"),
                Carbohydrate100 = Number(row, "carbohydrate_100"),
                Fat100 = Number(row, "fat_100"),
                Nutrients100 = row["nutrients_100"] is JsonObject nutrients ? nutrients.ToJsonString() : "{}",
                Portions = row["portions"] is JsonArray portions ? portions.ToJsonString() : "[]",
            });
        }

        return foods;
    }

    private static string? Text(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static double? Number(JsonObject row, string key) =>
        row[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : null;
}
