using FsCheck.Fluent;
using Health360.Domain.Nutrition;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Nutrition;

public class RecipeNutritionTests
{
    private static readonly NutrientAmounts Oats = NutrientAmounts.Create(
        (Nutrient.Energy, 379), (Nutrient.Protein, 13.2), (Nutrient.Carbohydrate, 67.7), (Nutrient.Fat, 6.5), (Nutrient.Fiber, 10.1));

    private static readonly NutrientAmounts Banana = NutrientAmounts.Create(
        (Nutrient.Energy, 89), (Nutrient.Protein, 1.09), (Nutrient.Carbohydrate, 22.84), (Nutrient.Fat, 0.33), (Nutrient.Fiber, 2.6));

    // No fiber value: its label didn't list one.
    private static readonly NutrientAmounts PeanutButter = NutrientAmounts.Create(
        (Nutrient.Energy, 588), (Nutrient.Protein, 25.1), (Nutrient.Carbohydrate, 19.6), (Nutrient.Fat, 50.4));

    private static readonly RecipeIngredient[] Porridge =
    [
        new(Oats, FoodBasis.Mass, 80),
        new(Banana, FoodBasis.Mass, 120),
        new(PeanutButter, FoodBasis.Mass, 32),
    ];

    // 1.5 servings of the porridge: 299.08 × 1.5 = 448.62 kcal; fiber stays a lower bound.
    [Fact]
    public void Servings_eaten_scale_a_serving_and_keep_what_is_incomplete()
    {
        var eaten = RecipeNutrition.Compute(Porridge, servings: 2).PerServing.Times(1.5);

        Assert.True(Close(eaten.Amounts[Nutrient.Energy]!.Value, 448.62));
        Assert.Equal(["fiber"], eaten.Incomplete);
        Assert.Equal(0, NutrientAmounts.Create((Nutrient.Iron, 2.0)).Times(0)[Nutrient.Iron]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Oats.Times(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Oats.Times(double.NaN));
    }

    // Hand calculation (docs/calculations.md, Recipes): energy 379 × 80/100 + 89 × 120/100 + 588 × 32/100
    // = 303.2 + 106.8 + 188.16 = 598.16 kcal; protein 10.56 + 1.308 + 8.032 = 19.9 g; raw weight 232 g.
    [Fact]
    public void Totals_match_a_hand_calculation()
    {
        var r = RecipeNutrition.Compute(Porridge, servings: 2);

        Assert.True(Close(r.Total.Amounts[Nutrient.Energy]!.Value, 598.16));
        Assert.True(Close(r.Total.Amounts[Nutrient.Protein]!.Value, 19.9));
        Assert.True(Close(r.PerServing.Amounts[Nutrient.Energy]!.Value, 299.08));
        Assert.True(Close(r.PerServing.Amounts[Nutrient.Protein]!.Value, 9.95));
        Assert.Equal(232, r.DishWeightG);
        Assert.True(Close(r.Per100g!.Amounts[Nutrient.Energy]!.Value, 257.82758620689657)); // 598.16 × 100 / 232
    }

    [Fact]
    public void A_weighed_dish_is_per_100_g_of_its_cooked_weight()
    {
        var r = RecipeNutrition.Compute(Porridge, servings: 2, cookedWeightG: 400);

        Assert.Equal(400, r.DishWeightG);
        Assert.True(Close(r.Per100g!.Amounts[Nutrient.Energy]!.Value, 149.54)); // 598.16 × 100 / 400
        Assert.True(Close(r.Total.Amounts[Nutrient.Energy]!.Value, 598.16)); // cooking changes the weight, not the energy
    }

    [Fact]
    public void A_nutrient_some_ingredients_lack_is_a_lower_bound_marked_incomplete()
    {
        var r = RecipeNutrition.Compute(Porridge, servings: 1);

        Assert.True(Close(r.Total.Amounts[Nutrient.Fiber]!.Value, 11.2)); // 8.08 + 3.12; the peanut butter's is unknown
        Assert.Equal(["fiber"], r.Total.Incomplete);
        Assert.Equal(["fiber"], r.PerServing.Incomplete);
        Assert.Null(r.Total.Amounts[Nutrient.Sodium]); // no ingredient knew it: absent, not 0
    }

    [Fact]
    public void A_volume_ingredient_leaves_the_raw_weight_unknown()
    {
        var milk = NutrientAmounts.Create((Nutrient.Energy, 64));
        var r = RecipeNutrition.Compute([new(Oats, FoodBasis.Mass, 80), new(milk, FoodBasis.Volume, 250)], servings: 1);

        Assert.Null(r.DishWeightG);
        Assert.Null(r.Per100g);
        Assert.True(Close(r.Total.Amounts[Nutrient.Energy]!.Value, 463.2)); // 303.2 + 64 × 250/100
    }

    [Fact]
    public void An_ingredient_with_no_amount_changes_nothing()
    {
        var milk = NutrientAmounts.Create((Nutrient.Energy, 64));
        var oatsOnly = RecipeNutrition.Compute([new(Oats, FoodBasis.Mass, 80)], servings: 1);
        var withNothing = RecipeNutrition.Compute([new(Oats, FoodBasis.Mass, 80), new(milk, FoodBasis.Volume, 0)], servings: 1);

        Assert.Equal(oatsOnly.Total.Amounts, withNothing.Total.Amounts);
        Assert.Empty(withNothing.Total.Incomplete); // milk lists no fiber, but none of it went in
        Assert.Equal(80, withNothing.DishWeightG);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Ingredient_amounts_must_be_finite_and_non_negative(double amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeNutrition.Compute([new(Oats, FoodBasis.Mass, amount)], servings: 1));

    [Fact]
    public void An_empty_recipe_has_nothing()
    {
        var r = RecipeNutrition.Compute([], servings: 1);

        Assert.Empty(r.Total.Amounts.Values);
        Assert.Null(r.Per100g);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Servings_must_be_positive(double servings) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RecipeNutrition.Compute(Porridge, servings));

    [Fact]
    public void Scaling_to_an_amount_and_back_returns_the_per_100_values() =>
        Holds(Gen.Zip(Double(0, 900), Double(0.1, 5000)), t =>
        {
            var (per100, amount) = t;
            var food = NutrientAmounts.Create((Nutrient.Energy, per100));
            return Close(food.ForAmount(amount).Per100(amount)[Nutrient.Energy]!.Value, per100, 1e-12);
        });

    [Fact]
    public void Servings_add_back_up_to_the_total() =>
        Holds(Gen.Zip(Double(1, 2000), Int(1, 12)), t =>
        {
            var (grams, servings) = t;
            var r = RecipeNutrition.Compute([new(Oats, FoodBasis.Mass, grams)], servings);
            return Close(r.PerServing.Amounts[Nutrient.Energy]!.Value * servings, r.Total.Amounts[Nutrient.Energy]!.Value, 1e-12);
        });
}

public class NutrientAmountsTests
{
    [Fact]
    public void Unknown_is_not_zero()
    {
        var a = NutrientAmounts.Create((Nutrient.Protein, 0));

        Assert.Equal(0, a[Nutrient.Protein]);
        Assert.Null(a[Nutrient.Iron]);
    }

    [Theory]
    [InlineData("Protein")]
    [InlineData("1protein")]
    [InlineData("")]
    [InlineData("vitamin-d")]
    public void Codes_must_be_database_codes(string code) =>
        Assert.Throws<ArgumentException>(() => NutrientAmounts.Create([KeyValuePair.Create(code, 1.0)]));

    [Fact]
    public void Codes_outside_the_catalogue_are_kept() =>
        Assert.Equal(2.5, NutrientAmounts.Create([KeyValuePair.Create("vitamin_k1", 2.5)])["vitamin_k1"]);

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Amounts_must_be_finite_and_non_negative(double amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NutrientAmounts.Create((Nutrient.Fat, amount)));

    [Fact]
    public void A_code_appears_once() =>
        Assert.Throws<ArgumentException>(() => NutrientAmounts.Create((Nutrient.Fat, 1), (Nutrient.Fat, 2)));

    [Fact]
    public void Equal_amounts_are_equal_whatever_the_order()
    {
        var a = NutrientAmounts.Create((Nutrient.Fat, 1), (Nutrient.Protein, 2));
        var b = NutrientAmounts.Create((Nutrient.Protein, 2), (Nutrient.Fat, 1));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void The_catalogue_codes_are_valid_and_distinct()
    {
        Assert.All(Nutrient.All, n => Assert.True(NutrientAmounts.IsValidCode(n.Code), n.Code));
        Assert.Equal(Nutrient.All.Count, Nutrient.All.Select(n => n.Code).Distinct().Count());
        Assert.Subset(Nutrient.All.ToHashSet(), Nutrient.Macros.ToHashSet());
    }
}

public class FoodAmountTests
{
    [Fact]
    public void Ounces_of_a_mass_food_are_exact_grams() =>
        Assert.Equal(28.349523125 * 4, FoodAmount.ToBase(4, Unit.Ounce, FoodBasis.Mass), 12);

    [Fact]
    public void Cups_of_a_drink_are_millilitres() =>
        Assert.Equal(29.5735295625 * 8, FoodAmount.ToBase(1, Unit.Cup, FoodBasis.Volume), 12);

    [Fact]
    public void A_volume_of_a_mass_food_needs_a_portion() =>
        Assert.Throws<ArgumentException>(() => FoodAmount.ToBase(1, Unit.Cup, FoodBasis.Mass));

    [Fact]
    public void Portions_multiply() =>
        Assert.Equal(90, FoodAmount.ToBase(1.5, new FoodPortion("1 slice", 60)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_portion_holds_a_positive_amount(double grams) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FoodPortion("1 slice", grams));

    [Fact]
    public void Basis_codes_round_trip()
    {
        foreach (var basis in Enum.GetValues<FoodBasis>())
        {
            Assert.True(FoodBases.TryParse(FoodBases.Code(basis), out var parsed));
            Assert.Equal(basis, parsed);
        }

        Assert.False(FoodBases.TryParse("kg", out _));
    }
}

public class FoodCopyTests
{
    private static readonly Guid Household = Guid.Parse("01900000-0000-7000-8000-000000000001");
    private static readonly Guid Oats = Guid.Parse("70000000-0000-7000-8000-000000000001");

    [Fact]
    public void Every_device_gets_the_same_copy_id() =>
        Assert.Equal(FoodCopy.IdFor(Household, Oats), FoodCopy.IdFor(Household, Oats));

    [Fact]
    public void Each_household_and_food_gets_its_own_copy()
    {
        var other = Guid.Parse("01900000-0000-7000-8000-000000000002");

        Assert.NotEqual(FoodCopy.IdFor(Household, Oats), FoodCopy.IdFor(other, Oats));
        Assert.NotEqual(FoodCopy.IdFor(Household, Oats), FoodCopy.IdFor(Household, other));
    }

    [Fact]
    public void The_id_is_a_version_8_uuid()
    {
        var id = FoodCopy.IdFor(Household, Oats).ToString("D");

        Assert.Equal('8', id[14]);
        Assert.Contains(id[19], "89ab");
    }
}
