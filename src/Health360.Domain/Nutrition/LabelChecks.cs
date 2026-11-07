using Health360.Domain.Units;

namespace Health360.Domain.Nutrition;

/// <summary>A column of a Nutrition Facts label: per serving, or per 100 g (or 100 ml).</summary>
public enum LabelColumn
{
    PerServing,
    Per100,
}

/// <summary>A value read from a label, for pointing the confirm screen at what looks wrong.</summary>
public enum LabelField
{
    EnergyKcal,
    EnergyKj,
    Protein,
    Carbohydrate,
    Fat,
    Fiber,
    SugarAlcohols,
    ServingSize,
}

/// <summary>The values of one label column, as printed. Null means not printed or not readable, never zero.</summary>
public sealed record LabelValues(
    double? EnergyKcal = null,
    double? EnergyKj = null,
    double? Protein = null,
    double? Carbohydrate = null,
    double? Fat = null,
    double? Fiber = null,
    double? SugarAlcohols = null);

/// <summary>
/// What was read from a label: the basis of its per-100 column, the serving size in g (or ml) when printed, and either
/// column or both.
/// </summary>
public sealed record LabelReading(FoodBasis Basis, double? ServingSize, LabelValues? PerServing, LabelValues? Per100);

public enum LabelIssueKind
{
    /// <summary>The stated energy is outside what protein, carbohydrate and fat can supply (4/4/9 kcal per g).</summary>
    EnergyDoesNotMatchMacros,

    /// <summary>The kJ value is outside what any energy conversion factor gives for the kcal value.</summary>
    KilojoulesDoNotMatchKilocalories,

    /// <summary>Protein, carbohydrate and fat together weigh more than the food they are in.</summary>
    MacrosExceedWeight,

    /// <summary>More energy per gram than fat, the densest nutrient, can hold.</summary>
    EnergyTooDense,

    /// <summary>The per-serving column isn't the per-100 column scaled by the serving size.</summary>
    ColumnsDisagree,
}

/// <summary>One suspect finding; <see cref="Column"/> is null when it compares the two columns.</summary>
public sealed record LabelIssue(LabelIssueKind Kind, LabelColumn? Column, IReadOnlyList<LabelField> Fields);

/// <summary>
/// Consistency checks on values read from a Nutrition Facts label, before anything is saved (docs/calculations.md,
/// Label checks). They find misreads, not non-compliant labels: each printed value stands for the interval of true
/// values that round to it under the FDA rounding rules (21 CFR 101.9(c)), which are at least as coarse as a label
/// rounded more finely, and a check fails only when no values in those intervals can agree. A failed check makes the
/// fields suspect for the user to confirm; it never changes or rejects them.
/// </summary>
public static class LabelChecks
{
    /// <summary>
    /// Relative slack on the 4/4/9 energy estimate. A product choice, not a figure from a source: labels may also use
    /// food-specific Atwater factors or bomb calorimetry (21 CFR 101.9(c)(1)(i)), which the general factors can't
    /// reproduce exactly.
    /// </summary>
    public const double EnergyTolerance = 0.10;

    /// <summary>The lowest kJ-per-kcal ratio of the EU conversion factors (fibre, 8 kJ / 2 kcal; Reg. 1169/2011 Annex XIV).</summary>
    public const double MinKjPerKcal = 8.0 / 2;

    /// <summary>The highest kJ-per-kcal ratio of the EU conversion factors (organic acid, 13 kJ / 3 kcal).</summary>
    public const double MaxKjPerKcal = 13.0 / 3;

    /// <summary>Fat's general factor, the most energy any nutrient supplies per gram (21 CFR 101.9(c)(1)(i)(B)).</summary>
    public const double MaxKcalPerGram = 9;

    /// <summary>
    /// Half the rounding step assumed for a printed serving size in g or ml. An assumption, not a rule: labels print
    /// whole grams or millilitres.
    /// </summary>
    public const double ServingHalfStep = 0.5;

    public static IReadOnlyList<LabelIssue> Check(LabelReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (reading.ServingSize is { } s && (!double.IsFinite(s) || s <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(reading), s, "The serving size must be a positive amount");
        }

        Validate(reading.PerServing);
        Validate(reading.Per100);

        var serving = reading.ServingSize is { } size ? new Interval(Math.Max(0, size - ServingHalfStep), size + ServingHalfStep) : (Interval?)null;
        var issues = new List<LabelIssue>();
        if (reading.PerServing is { } perServing)
        {
            CheckColumn(LabelColumn.PerServing, perServing, reading.Basis, serving, issues);
        }

        if (reading.Per100 is { } per100)
        {
            CheckColumn(LabelColumn.Per100, per100, reading.Basis, new Interval(100, 100), issues);
        }

        if (reading.PerServing is { } a && reading.Per100 is { } b && serving is { } amount)
        {
            CheckColumnsAgree(a, b, amount, issues);
        }

        return issues;
    }

    private static void CheckColumn(LabelColumn column, LabelValues v, FoodBasis basis, Interval? amount, List<LabelIssue> issues)
    {
        var kcal = v.EnergyKcal is { } e ? Kilocalories(e) : (Interval?)null;
        var protein = v.Protein is { } p ? WholeGrams(p) : (Interval?)null;
        var carbohydrate = v.Carbohydrate is { } c ? WholeGrams(c) : (Interval?)null;
        var fat = v.Fat is { } f ? FatGrams(f) : (Interval?)null;
        var fiber = v.Fiber is { } fi ? WholeGrams(fi) : (Interval?)null;
        var polyols = v.SugarAlcohols is { } po ? WholeGrams(po) : (Interval?)null;

        if (kcal is { } k && protein is { } pr && carbohydrate is { } cr && fat is { } fa)
        {
            // Lowest: general factors on carbohydrate less what may be non-digestible (method C, which counts
            // insoluble fiber and erythritol at 0). Highest: 4/4/9 on all of it (method B) plus 2 kcal/g for fiber, as
            // EU labels count it outside carbohydrate.
            var nonDigestible = (fiber?.Hi ?? 0) + (polyols?.Hi ?? 0);
            var lowest = (4 * pr.Lo) + (4 * Math.Max(0, cr.Lo - nonDigestible)) + (9 * fa.Lo);
            var highest = (4 * pr.Hi) + (4 * cr.Hi) + (9 * fa.Hi) + (2 * (fiber?.Hi ?? 0));
            if (!k.Overlaps(new Interval(lowest * (1 - EnergyTolerance), highest * (1 + EnergyTolerance))))
            {
                List<LabelField> fields = [LabelField.EnergyKcal, LabelField.Protein, LabelField.Carbohydrate, LabelField.Fat];
                if (fiber is not null)
                {
                    fields.Add(LabelField.Fiber);
                }

                if (polyols is not null)
                {
                    fields.Add(LabelField.SugarAlcohols);
                }

                issues.Add(new(LabelIssueKind.EnergyDoesNotMatchMacros, column, fields));
            }
        }

        if (kcal is { } kc && v.EnergyKj is { } kj && !Kilojoules(kj).Overlaps(new Interval(MinKjPerKcal * kc.Lo, MaxKjPerKcal * kc.Hi)))
        {
            issues.Add(new(LabelIssueKind.KilojoulesDoNotMatchKilocalories, column, [LabelField.EnergyKcal, LabelField.EnergyKj]));
        }

        // Grams can only be compared with grams: a volume's weight depends on the food's density.
        if (basis != FoodBasis.Mass || amount is not { } weight)
        {
            return;
        }

        var withServing = column == LabelColumn.PerServing;
        var macros = new (LabelField Field, Interval? Value)[]
        {
            (LabelField.Protein, protein), (LabelField.Carbohydrate, carbohydrate), (LabelField.Fat, fat),
        }.Where(m => m.Value is not null).ToList();
        if (macros.Count > 0 && macros.Aggregate(0.0, (sum, m) => sum + m.Value!.Value.Lo) > weight.Hi)
        {
            List<LabelField> fields = [.. macros.Select(m => m.Field)];
            if (withServing)
            {
                fields.Add(LabelField.ServingSize);
            }

            issues.Add(new(LabelIssueKind.MacrosExceedWeight, column, fields));
        }

        if (kcal is { } energy && energy.Lo > MaxKcalPerGram * (1 + EnergyTolerance) * weight.Hi)
        {
            issues.Add(new(LabelIssueKind.EnergyTooDense, column, withServing ? [LabelField.EnergyKcal, LabelField.ServingSize] : [LabelField.EnergyKcal]));
        }
    }

    private static void CheckColumnsAgree(LabelValues perServing, LabelValues per100, Interval serving, List<LabelIssue> issues)
    {
        var pairs = new (LabelField Field, double? Serving, double? Per100, Func<double, Interval> Round)[]
        {
            (LabelField.EnergyKcal, perServing.EnergyKcal, per100.EnergyKcal, Kilocalories),
            (LabelField.EnergyKj, perServing.EnergyKj, per100.EnergyKj, Kilojoules),
            (LabelField.Protein, perServing.Protein, per100.Protein, WholeGrams),
            (LabelField.Carbohydrate, perServing.Carbohydrate, per100.Carbohydrate, WholeGrams),
            (LabelField.Fat, perServing.Fat, per100.Fat, FatGrams),
            (LabelField.Fiber, perServing.Fiber, per100.Fiber, WholeGrams),
            (LabelField.SugarAlcohols, perServing.SugarAlcohols, per100.SugarAlcohols, WholeGrams),
        };

        var fields = new List<LabelField>();
        foreach (var (field, a, b, round) in pairs)
        {
            if (a is not { } servingValue || b is not { } per100Value)
            {
                continue;
            }

            var x = round(per100Value);
            var expected = new Interval(x.Lo * serving.Lo / 100, x.Hi * serving.Hi / 100);
            if (!round(servingValue).Overlaps(expected))
            {
                fields.Add(field);
            }
        }

        if (fields.Count > 0)
        {
            fields.Add(LabelField.ServingSize);
            issues.Add(new(LabelIssueKind.ColumnsDisagree, null, fields));
        }
    }

    /// <summary>
    /// 21 CFR 101.9(c)(1): nearest 5 kcal up to and including 50, nearest 10 above, and under 5 may be printed as 0.
    /// </summary>
    internal static Interval Kilocalories(double printed) =>
        printed == 0
            ? new(0, 5)
            : new(Math.Max(0, printed - (printed <= 50 ? 2.5 : 5)), printed + (printed < 50 ? 2.5 : 5));

    /// <summary>
    /// The FDA has no rounding rule for kJ, so a kJ value is given the interval of the kcal value it stands for,
    /// scaled by 4.184 kJ/kcal.
    /// </summary>
    internal static Interval Kilojoules(double printed)
    {
        var asKcal = Kilocalories(printed / Factors.KJPerKcal);
        return new(asKcal.Lo * Factors.KJPerKcal, asKcal.Hi * Factors.KJPerKcal);
    }

    /// <summary>21 CFR 101.9(c)(2): fat to the nearest 0.5 g below 5 g and the nearest gram above; under 0.5 g as 0.</summary>
    internal static Interval FatGrams(double printed) =>
        printed == 0
            ? new(0, 0.5)
            : new(Math.Max(0, printed - (printed <= 5 ? 0.25 : 0.5)), printed + (printed < 5 ? 0.25 : 0.5));

    /// <summary>
    /// 21 CFR 101.9(c)(6) and (c)(7): carbohydrate, fiber and protein to the nearest gram, and under 1 g may be printed
    /// as "less than 1 gram". A 0 is taken to mean anything below 1 g, in case "less than 1 g" was read as 0. Sugar
    /// alcohols are treated the same way.
    /// </summary>
    internal static Interval WholeGrams(double printed) =>
        printed == 0 ? new(0, 1) : new(Math.Max(0, printed - 0.5), printed + 0.5);

    private static void Validate(LabelValues? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var v in new[] { values.EnergyKcal, values.EnergyKj, values.Protein, values.Carbohydrate, values.Fat, values.Fiber, values.SugarAlcohols })
        {
            if (v is { } x && (!double.IsFinite(x) || x < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(values), x, "Label values must be finite and non-negative");
            }
        }
    }

    internal readonly record struct Interval(double Lo, double Hi)
    {
        public bool Overlaps(Interval other) => Lo <= other.Hi && other.Lo <= Hi;
    }
}
