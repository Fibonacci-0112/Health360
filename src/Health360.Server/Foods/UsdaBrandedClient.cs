using System.Text.Json.Serialization;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>Finds USDA branded foods by GTIN through the FoodData Central search API.</summary>
public sealed class UsdaBrandedClient(IHttpClientFactory httpClientFactory, IOptions<UsdaOptions> options)
{
    public const string HttpClientName = "usda";

    // FoodData Central nutrient ids.
    private const int EnergyKcal = 1008;
    private const int Protein = 1003;
    private const int Fat = 1004;
    private const int Carbohydrate = 1005;

    public bool IsConfigured => options.Value.IsConfigured;

    /// <summary>
    /// The branded food with this GTIN, or null. Search is by text, so results are filtered to the exact GTIN:
    /// FoodData Central writes UPC-A codes without a leading zero, hence the 14-digit comparison. A product
    /// update is a new record under the same GTIN, so of the exact matches the most recently published wins.
    /// At most <see cref="UsdaOptions.MaxPages"/> pages are read; a product beyond them reads as not found.
    /// </summary>
    public async Task<BarcodeMatch?> FindAsync(string gtin14, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = options.Value.BaseAddress;

        SearchFood? best = null;
        for (var page = 1; page <= options.Value.MaxPages; page++)
        {
            // The key goes in a header, not the query string, so it never lands in a URL that gets logged.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"foods/search?query={Uri.EscapeDataString(gtin14.TrimStart('0'))}&dataType=Branded&pageSize=50&pageNumber={page}");
            request.Headers.Add("X-Api-Key", options.Value.ApiKey);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken).ConfigureAwait(false);

            foreach (var food in body?.Foods ?? [])
            {
                if (food.GtinUpc?.Trim().PadLeft(14, '0') == gtin14
                    && (best is null || string.CompareOrdinal(food.PublishedDate, best.PublishedDate) > 0))
                {
                    best = food;
                }
            }

            if (page >= (body?.TotalPages ?? 1))
            {
                break;
            }
        }

        if (best is null)
        {
            return null;
        }

        double? Value(int id) => best.FoodNutrients?.FirstOrDefault(n => n.NutrientId == id)?.Value;

        // Branded values are per 100 g, or per 100 ml for foods labelled in ml.
        var basis = string.Equals(best.ServingSizeUnit, "ml", StringComparison.OrdinalIgnoreCase) ? FoodBasis.Volume : FoodBasis.Mass;
        return new BarcodeMatch(
            BarcodeSource.UsdaBranded,
            best.FdcId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            gtin14,
            best.Description ?? "",
            best.BrandOwner,
            basis,
            Value(EnergyKcal),
            Value(Protein),
            Value(Carbohydrate),
            Value(Fat),
            best.ServingSize);
    }

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
