using System.Text.Json.Nodes;
using FluentValidation;
using Health360.Domain.Nutrition;

namespace Health360.Contracts.Nutrition;

/// <summary>A named measure of a food: <paramref name="Amount"/> g or ml of the food's basis.</summary>
public sealed record PortionInput(string Name, double Amount);

/// <summary>
/// A household's own food as entered from a label or a recipe card: the values for <paramref name="ValuesPer"/> g or
/// ml (100, or a serving such as 30 g), and named portions. Null is unknown, not 0. Stored per 100 of the basis
/// (<see cref="Per100"/>).
/// </summary>
public sealed record FoodInput(
    string Name,
    string? Brand,
    FoodBasis Basis,
    double ValuesPer,
    double? Energy,
    double? Protein,
    double? Carbohydrate,
    double? Fat,
    IReadOnlyList<PortionInput> Portions)
{
    /// <summary>
    /// The entered values per 100 g or ml: each value × 100 / <see cref="ValuesPer"/>
    /// (<see cref="NutrientAmounts.Per100"/>). Unknown values stay unknown.
    /// </summary>
    public NutrientAmounts Per100()
    {
        var entered = new List<(Nutrient, double)>();
        void Add(Nutrient nutrient, double? value)
        {
            if (value is { } v)
            {
                entered.Add((nutrient, v));
            }
        }

        Add(Nutrient.Energy, Energy);
        Add(Nutrient.Protein, Protein);
        Add(Nutrient.Carbohydrate, Carbohydrate);
        Add(Nutrient.Fat, Fat);
        return NutrientAmounts.Create([.. entered]).Per100(ValuesPer);
    }

    /// <summary><c>portions</c> as stored: <c>[{"name": "1 slice", "amount": 30}, …]</c>.</summary>
    public string PortionsJson() =>
        new JsonArray([.. Portions.Select(p => new JsonObject { ["name"] = p.Name.Trim(), ["amount"] = p.Amount })]).ToJsonString();
}

/// <summary>The <c>foods</c> checks a household food must pass before it is written.</summary>
public sealed class FoodInputValidator : AbstractValidator<FoodInput>
{
    public FoodInputValidator()
    {
        RuleFor(x => x.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 200).WithMessage("Give the food a name.");
        RuleFor(x => x.Brand).Must(b => b is null || b.Trim().Length is >= 1 and <= 120).WithMessage("Keep the brand to 120 characters.");
        RuleFor(x => x.Basis).IsInEnum();
        RuleFor(x => x.ValuesPer)
            .Must(v => double.IsFinite(v) && v > 0 && v <= 100000)
            .WithMessage("Enter the amount the values are for, more than 0.");
        RuleFor(x => new[] { x.Energy, x.Protein, x.Carbohydrate, x.Fat })
            .Must(values => values.All(v => v is null || (double.IsFinite(v.Value) && v.Value >= 0)))
            .WithMessage("Nutrient values can't be negative.")
            .OverridePropertyName("Nutrients");
        RuleFor(x => x)
            .Must(x => x.Energy is not null || x.Protein is not null || x.Carbohydrate is not null || x.Fat is not null)
            .WithMessage("Enter at least calories or one macronutrient.")
            .OverridePropertyName("Nutrients");
        // foods_mass_macros: 100 g of food can't hold more than 100 g of one macronutrient.
        RuleFor(x => x)
            .Must(x => x.Basis != FoodBasis.Mass || !double.IsFinite(x.ValuesPer) || x.ValuesPer <= 0
                || new[] { x.Protein, x.Carbohydrate, x.Fat }.All(v => v is not { } g || !double.IsFinite(g) || g <= x.ValuesPer))
            .WithMessage("A macronutrient can't weigh more than the food.")
            .OverridePropertyName("Nutrients");
        RuleFor(x => x.Portions).Must(p => p.Count <= 50).WithMessage("List at most 50 portions.");
        RuleFor(x => x.Portions)
            .Must(p => p.Select(x => x.Name?.Trim()).Distinct(StringComparer.Ordinal).Count() == p.Count)
            .WithMessage("Give each portion a different name.");
        RuleForEach(x => x.Portions).ChildRules(portion =>
        {
            portion.RuleFor(p => p.Name).Must(n => n is not null && n.Trim().Length is >= 1 and <= 80).WithMessage("Give the portion a name.");
            portion.RuleFor(p => p.Amount)
                .Must(a => double.IsFinite(a) && a > 0 && a <= 100000)
                .WithMessage("A portion holds more than 0.");
        });
    }
}
