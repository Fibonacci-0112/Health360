using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>Finds Open Food Facts products by barcode through its product API (v2).</summary>
public sealed partial class OpenFoodFactsClient(IHttpClientFactory httpClientFactory, IOptions<OpenFoodFactsOptions> options)
{
    public const string HttpClientName = "off";

    private const double KilojoulesPerKilocalorie = 4.184;

    private const string Fields = "code,product_name,brands,quantity,product_quantity_unit,serving_quantity,nutriments";

    /// <summary>
    /// The product with this GTIN, or null. Open Food Facts stores a UPC-A as 13 digits (with one leading zero) and
    /// an EAN-8 as 8, so a code is asked for in each form it fits (<see cref="LookupForms"/>) until one is found.
    /// A product whose basis can't be told (<see cref="BasisOf"/>) is skipped, which reads as not found, so the
    /// app offers a label scan instead of values per the wrong basis. Its values are the crowd-sourced ones as
    /// entered; the confirm screen's checks apply to them as to any label.
    /// </summary>
    public async Task<BarcodeMatch?> FindAsync(string gtin14, CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        http.BaseAddress = options.Value.BaseAddress;

        foreach (var form in LookupForms(gtin14))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"product/{form}.json?fields={Fields}");
            request.Headers.UserAgent.ParseAdd(options.Value.UserAgent);

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ProductResponse>(cancellationToken).ConfigureAwait(false);
            if (body?.Status != 1 || body.Product is not { } product || BasisOf(product) is not { } basis)
            {
                continue;
            }

            var nutriments = product.Nutriments;
            var energy = Amount(nutriments?.EnergyKcal) ?? Amount(nutriments?.EnergyKj) / KilojoulesPerKilocalorie;
            return new BarcodeMatch(
                BarcodeSource.OpenFoodFacts,
                body.Code ?? form,
                gtin14,
                product.Name?.Trim() ?? "",
                FirstBrand(product.Brands),
                basis,
                energy,
                Amount(nutriments?.Protein),
                Amount(nutriments?.Carbohydrate),
                Amount(nutriments?.Fat),
                Amount(product.ServingQuantity) is { } serving && serving > 0 ? serving : null);
        }

        return null;
    }

    /// <summary>The forms of a 14-digit GTIN Open Food Facts may hold: 13 digits (or 14 when it needs all of them), and 8 for an EAN-8.</summary>
    internal static IEnumerable<string> LookupForms(string gtin14)
    {
        var significant = gtin14.TrimStart('0').Length;
        yield return significant <= 13 ? gtin14[1..] : gtin14;
        if (significant <= 8)
        {
            yield return gtin14[6..];
        }
    }

    /// <summary>
    /// What a product's values are per 100 of. Open Food Facts names every column <c>_100g</c> but holds liquids
    /// per 100 ml, so the basis comes from the product's quantity: a volume unit is volume, a mass unit is mass.
    /// A product with neither is unknown (null), never assumed.
    /// </summary>
    internal static FoodBasis? BasisOf(Product product)
    {
        var unit = product.QuantityUnit?.Trim().ToLowerInvariant();
        if (unit is "ml" or "cl" or "dl" or "l")
        {
            return FoodBasis.Volume;
        }

        if (unit is "g" or "kg" or "mg" or "oz" or "lb")
        {
            return FoodBasis.Mass;
        }

        var quantity = product.Quantity;
        if (string.IsNullOrWhiteSpace(quantity))
        {
            return null;
        }

        // Volume first: "fl oz" also contains "oz".
        if (VolumeText().IsMatch(quantity))
        {
            return FoodBasis.Volume;
        }

        return MassText().IsMatch(quantity) ? FoodBasis.Mass : null;
    }

    private static double? Amount(double? value) => value is { } v && double.IsFinite(v) && v >= 0 ? v : null;

    private static string? FirstBrand(string? brands) =>
        brands?.Split(',', 2, StringSplitOptions.TrimEntries).FirstOrDefault(b => b.Length > 0);

    [GeneratedRegex(@"(?<![a-z])(ml|cl|dl|l|litres?|liters?|fl\.?\s*oz)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeText();

    [GeneratedRegex(@"(?<![a-z])(g|kg|mg|gr|grams?|oz|lbs?)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MassText();

    internal sealed record ProductResponse(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("product")] Product? Product);

    internal sealed record Product(
        [property: JsonPropertyName("product_name")] string? Name,
        [property: JsonPropertyName("brands")] string? Brands,
        [property: JsonPropertyName("quantity")] string? Quantity,
        [property: JsonPropertyName("product_quantity_unit")] string? QuantityUnit,
        [property: JsonPropertyName("serving_quantity"), JsonConverter(typeof(LenientNumber))] double? ServingQuantity,
        [property: JsonPropertyName("nutriments")] Nutriments? Nutriments);

    internal sealed record Nutriments(
        [property: JsonPropertyName("energy-kcal_100g"), JsonConverter(typeof(LenientNumber))] double? EnergyKcal,
        [property: JsonPropertyName("energy_100g"), JsonConverter(typeof(LenientNumber))] double? EnergyKj,
        [property: JsonPropertyName("proteins_100g"), JsonConverter(typeof(LenientNumber))] double? Protein,
        [property: JsonPropertyName("carbohydrates_100g"), JsonConverter(typeof(LenientNumber))] double? Carbohydrate,
        [property: JsonPropertyName("fat_100g"), JsonConverter(typeof(LenientNumber))] double? Fat);

    /// <summary>
    /// Open Food Facts is crowd-sourced: a number may arrive as a string, or as text that is no number at all. Either
    /// reads as a number or as unknown, so one bad field never fails the lookup.
    /// </summary>
    private sealed class LenientNumber : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.GetDouble();
                case JsonTokenType.String:
                    return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
