namespace Health360.Domain.Nutrition;

/// <summary>One ingredient of a recipe: its food's values per 100 g (or ml) and how much of it goes in.</summary>
public sealed record RecipeIngredient(NutrientAmounts Per100, FoodBasis Basis, double BaseAmount);

/// <summary>A recipe's nutrition: the whole dish, one serving, and 100 g of the finished dish when its weight is known.</summary>
public sealed record RecipeNutritionResult(NutrientTotal Total, NutrientTotal PerServing, NutrientTotal? Per100g, double? DishWeightG);

/// <summary>Recipe nutrition as the sum of its ingredients (docs/calculations.md, Recipes).</summary>
public static class RecipeNutrition
{
    /// <summary>
    /// Adds each ingredient's nutrients for its amount (per-100 value × amount / 100), then divides by
    /// <paramref name="servings"/>. Per 100 g of the dish uses <paramref name="cookedWeightG"/> when it was weighed
    /// (cooking changes the weight, not the nutrients), otherwise the raw ingredients' total mass, which is only known
    /// when every ingredient is measured by mass. Nutrients some ingredients don't list are marked incomplete. An
    /// ingredient with a zero amount is left out.
    /// </summary>
    public static RecipeNutritionResult Compute(IReadOnlyList<RecipeIngredient> ingredients, double servings, double? cookedWeightG = null)
    {
        ArgumentNullException.ThrowIfNull(ingredients);
        if (!double.IsFinite(servings) || servings <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(servings), servings, "Must be a positive number of servings");
        }

        if (cookedWeightG is { } w && (!double.IsFinite(w) || w <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(cookedWeightG), w, "Must be a positive weight");
        }

        // An ingredient with no amount adds nothing: it can't make a nutrient incomplete or the raw weight unknown.
        var used = ingredients.Where(i =>
        {
            ArgumentNullException.ThrowIfNull(i, nameof(ingredients));
            if (!double.IsFinite(i.BaseAmount) || i.BaseAmount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ingredients), i.BaseAmount, "Amounts must be finite and non-negative");
            }

            return i.BaseAmount > 0;
        }).ToList();
        var total = NutrientTotal.Sum(used.Select(i => i.Per100.ForAmount(i.BaseAmount)));

        var dishWeight = cookedWeightG
            ?? (used.Count > 0 && used.All(i => i.Basis == FoodBasis.Mass)
                ? used.Aggregate(0.0, (sum, i) => sum + i.BaseAmount)
                : null);

        var per100 = dishWeight is > 0 ? total with { Amounts = total.Amounts.Per100(dishWeight.Value) } : null;
        return new(total, total.DividedBy(servings), per100, dishWeight);
    }
}
