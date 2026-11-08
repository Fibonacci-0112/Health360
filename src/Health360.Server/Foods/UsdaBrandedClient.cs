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
    /// The branded food with this GTIN, or null. Search is by text, so the results are filtered to the exact
    /// GTIN: FoodData Central writes UPC-A codes without a leading zero, hence the 14-digit comparison.
    /// </summary>
    public async Task<BarcodeMatch?> FindAsync(string gtin14, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = options.Value.BaseAddress;

        // The key goes in a header, not the query string, so it never lands in a URL that gets logged.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"foods/search?query={Uri.EscapeDataString(gtin14.TrimStart('0'))}&dataType=Branded&pageSize=25");
        request.Headers.Add("X-Api-Key", options.Value.ApiKey);

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken).ConfigureAwait(false);

        foreach (var food in body?.Foods ?? [])
        {
            if (food.GtinUpc?.Trim().PadLeft(14, '0') != gtin14)
            {
                continue;
            }

            double? Value(int id) => food.FoodNutrients?.FirstOrDefault(n => n.NutrientId == id)?.Value;

            // Branded values are per 100 g, or per 100 ml for foods labelled in ml.
            var basis = string.Equals(food.ServingSizeUnit, "ml", StringComparison.OrdinalIgnoreCase) ? FoodBasis.Volume : FoodBasis.Mass;
            return new BarcodeMatch(
                BarcodeSource.UsdaBranded,
                food.FdcId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                gtin14,
                food.Description ?? "",
                food.BrandOwner,
                basis,
                Value(EnergyKcal),
                Value(Protein),
                Value(Carbohydrate),
                Value(Fat),
                food.ServingSize);
        }

        return null;
    }

    private sealed record SearchResponse([property: JsonPropertyName("foods")] List<SearchFood>? Foods);

    private sealed record SearchFood(
        [property: JsonPropertyName("fdcId")] long FdcId,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("brandOwner")] string? BrandOwner,
        [property: JsonPropertyName("gtinUpc")] string? GtinUpc,
        [property: JsonPropertyName("servingSize")] double? ServingSize,
        [property: JsonPropertyName("servingSizeUnit")] string? ServingSizeUnit,
        [property: JsonPropertyName("foodNutrients")] List<SearchNutrient>? FoodNutrients);

    private sealed record SearchNutrient(
        [property: JsonPropertyName("nutrientId")] int NutrientId,
        [property: JsonPropertyName("value")] double? Value);
}
