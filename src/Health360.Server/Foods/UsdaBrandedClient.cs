using System.Text.Json.Serialization;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>Finds USDA branded foods by GTIN through the FoodData Central search API.</summary>
public sealed class UsdaBrandedClient(IHttpClientFactory httpClientFactory, IOptions<UsdaOptions> options, UsdaUpstreamBudget budget)
{
    public const string HttpClientName = "usda";

    // FoodData Central nutrient ids.
    private const int EnergyKcal = 1008;
    private const int Protein = 1003;
    private const int Fat = 1004;
    private const int Carbohydrate = 1005;

    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>
    /// The branded food with this GTIN, or null. Search is by text and the index holds each code exactly as USDA
    /// stores it (a UPC-A may be 12, 13 or 14 digits, with its leading zeros), so a code is searched in each
    /// zero-padded form it fits (<see cref="SearchForms"/>) until one finds it, then results are filtered to the
    /// exact GTIN by their 14-digit form. A product update is a new record under the same GTIN, so of the exact
    /// matches the most recently published wins. At most <see cref="UsdaOptions.MaxPages"/> pages are read per
    /// form; a product beyond them reads as not found. A match with a unit this code doesn't recognise is skipped
    /// (<see cref="BasisOf"/>), which reads as not found, so the app offers a label scan instead of a wrong basis.
    /// </summary>
    public async Task<BarcodeMatch?> FindAsync(string gtin14, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = options.Value.BaseAddress;

        SearchFood? best = null;
        FoodBasis bestBasis = default;
        foreach (var form in SearchForms(gtin14))
        {
            for (var page = 1; page <= options.Value.MaxPages; page++)
            {
                if (!budget.TryTake())
                {
                    throw new UsdaBudgetExhaustedException();
                }

                // The key goes in a header, not the query string, so it never lands in a URL that gets logged.
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"foods/search?query={form}&dataType=Branded&pageSize=50&pageNumber={page}");
                request.Headers.Add("X-Api-Key", options.Value.ApiKey);

                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken).ConfigureAwait(false);

                foreach (var food in body?.Foods ?? [])
                {
                    if (food.GtinUpc?.Trim().PadLeft(14, '0') == gtin14
                        && BasisOf(food.ServingSizeUnit) is { } basis
                        && (best is null || string.CompareOrdinal(food.PublishedDate, best.PublishedDate) > 0))
                    {
                        best = food;
                        bestBasis = basis;
                    }
                }

                if (page >= (body?.TotalPages ?? 1))
                {
                    break;
                }
            }

            if (best is not null)
            {
                break;
            }
        }

        if (best is null)
        {
            return null;
        }

        double? Value(int id) => best.FoodNutrients?.FirstOrDefault(n => n.NutrientId == id)?.Value;

        return new BarcodeMatch(
            BarcodeSource.UsdaBranded,
            best.FdcId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gtin14,
            best.Description ?? "",
            best.BrandOwner,
            bestBasis,
            Value(EnergyKcal),
            Value(Protein),
            Value(Carbohydrate),
            Value(Fat),
            best.ServingSize);
    }

    /// <summary>
    /// The forms of a 14-digit GTIN as USDA may store it: padded to 14, 13, 12 or 8 digits, without dropping a
    /// significant digit (so an EAN-8 is searched only when the 14-digit form has six leading zeros).
    /// </summary>
    internal static IEnumerable<string> SearchForms(string gtin14)
    {
        var significant = gtin14.TrimStart('0').Length;
        foreach (var length in new[] { 14, 13, 12, 8 })
        {
            if (significant <= length)
            {
                yield return gtin14[(14 - length)..];
            }
        }
    }

    /// <summary>
    /// What a branded food's values are per 100 of, from its serving size unit. USDA writes the units both as
    /// "g"/"ml" and as "GRM"/"MLT"; no unit means grams. Any other unit is unknown (null), never assumed.
    /// </summary>
    internal static FoodBasis? BasisOf(string? unit) => unit?.Trim().ToUpperInvariant() switch
    {
        null or "" or "G" or "GRM" => FoodBasis.Mass,
        "ML" or "MLT" => FoodBasis.Volume,
        _ => null,
    };

    private sealed record SearchResponse(
        [property: JsonPropertyName("totalPages")] int? TotalPages,
        [property: JsonPropertyName("foods")] List<SearchFood>? Foods);

    private sealed record SearchFood(
        [property: JsonPropertyName("fdcId")] long FdcId,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("brandOwner")] string? BrandOwner,
        [property: JsonPropertyName("gtinUpc")] string? GtinUpc,
        [property: JsonPropertyName("publishedDate")] string? PublishedDate,
        [property: JsonPropertyName("servingSize")] double? ServingSize,
        [property: JsonPropertyName("servingSizeUnit")] string? ServingSizeUnit,
        [property: JsonPropertyName("foodNutrients")] List<SearchNutrient>? FoodNutrients);

    private sealed record SearchNutrient(
        [property: JsonPropertyName("nutrientId")] int NutrientId,
        [property: JsonPropertyName("value")] double? Value);
}
