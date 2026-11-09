using Health360.Domain.Nutrition;

namespace Health360.Contracts.Nutrition;

/// <summary>Where a barcode match came from.</summary>
public enum BarcodeSource
{
    UsdaBranded,
    OpenFoodFacts,
}

/// <summary>
/// A food found for a barcode, values per 100 g or ml of <paramref name="Basis"/>. Null is unknown, not 0.
/// <paramref name="Gtin"/> is the 14-digit form.
/// </summary>
public sealed record BarcodeMatch(
    BarcodeSource Source,
    string ExternalId,
    string Gtin,
    string Name,
    string? Brand,
    FoodBasis Basis,
    double? Energy,
    double? Protein,
    double? Carbohydrate,
    double? Fat,
    double? ServingSize);
