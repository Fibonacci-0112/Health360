using System.Collections.Frozen;

namespace Health360.Domain.Nutrition;

/// <summary>
/// Amounts of nutrients, keyed by nutrient code, each in its nutrient's fixed unit (<see cref="Nutrient"/>). A code
/// that isn't present is unknown, which is not the same as zero: a food whose label doesn't list iron has no iron
/// value, not 0 mg. Codes are not limited to <see cref="Nutrient.All"/>, so an older app keeps nutrients a newer one
/// or the server added instead of dropping them. Immutable.
/// </summary>
public sealed class NutrientAmounts : IEquatable<NutrientAmounts>
{
    public static readonly NutrientAmounts Empty = new(FrozenDictionary<string, double>.Empty);

    private readonly FrozenDictionary<string, double> values;

    private NutrientAmounts(FrozenDictionary<string, double> values) => this.values = values;

    /// <summary>The known amounts, keyed by code.</summary>
    public IReadOnlyDictionary<string, double> Values => values;

    /// <summary>The amount of <paramref name="code"/>, or null when it is unknown.</summary>
    public double? this[string code] => values.TryGetValue(code, out var v) ? v : null;

    public double? this[Nutrient nutrient] => this[nutrient.Code];

    /// <summary>
    /// Builds amounts from code/amount pairs. Every amount must be finite and non-negative and every code a valid
    /// nutrient code (<see cref="IsValidCode"/>); a code may appear once.
    /// </summary>
    public static NutrientAmounts Create(IEnumerable<KeyValuePair<string, double>> amounts)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, amount) in amounts)
        {
            if (!IsValidCode(code))
            {
                throw new ArgumentException($"Not a nutrient code: {code}", nameof(amounts));
            }

            if (!double.IsFinite(amount) || amount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(amounts), amount, $"{code} must be a finite, non-negative amount");
            }

            if (!map.TryAdd(code, amount))
            {
                throw new ArgumentException($"{code} appears twice", nameof(amounts));
            }
        }

        return map.Count == 0 ? Empty : new(map.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public static NutrientAmounts Create(params (Nutrient Nutrient, double Amount)[] amounts) =>
        Create(amounts.Select(a => KeyValuePair.Create(a.Nutrient.Code, a.Amount)));

    /// <summary>
    /// A nutrient code as the database checks it (<c>private.is_code_array</c>): a lowercase letter, then up to 39
    /// lowercase letters, digits or underscores.
    /// </summary>
    public static bool IsValidCode(string? code) =>
        code is { Length: >= 1 and <= 40 }
        && code[0] is >= 'a' and <= 'z'
        && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    /// <summary>
    /// Amounts per 100 g (or 100 ml) scaled to <paramref name="baseAmount"/> g (or ml): each value × baseAmount / 100,
    /// evaluated in that order. Unknown nutrients stay unknown.
    /// </summary>
    public NutrientAmounts ForAmount(double baseAmount)
    {
        if (!double.IsFinite(baseAmount) || baseAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseAmount), baseAmount, "Must be a finite, non-negative amount");
        }

        return Map(v => v * baseAmount / 100);
    }

    /// <summary>
    /// The reverse of <see cref="ForAmount"/>: amounts in <paramref name="baseAmount"/> g (or ml) expressed per 100,
    /// each value × 100 / baseAmount, evaluated in that order.
    /// </summary>
    public NutrientAmounts Per100(double baseAmount)
    {
        if (!double.IsFinite(baseAmount) || baseAmount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseAmount), baseAmount, "Must be a positive amount");
        }

        return Map(v => v * 100 / baseAmount);
    }

    /// <summary>Each amount divided by <paramref name="divisor"/>, for example a recipe's total by its servings.</summary>
    public NutrientAmounts DividedBy(double divisor)
    {
        if (!double.IsFinite(divisor) || divisor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisor), divisor, "Must be a positive number");
        }

        return Map(v => v / divisor);
    }

    /// <summary>
    /// Each amount × <paramref name="factor"/>, for example a recipe's per-serving values × the servings eaten. A
    /// factor of 0 gives zero amounts, not unknown ones.
    /// </summary>
    public NutrientAmounts Times(double factor)
    {
        if (!double.IsFinite(factor) || factor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "Must be a finite, non-negative number");
        }

        return Map(v => v * factor);
    }

    private NutrientAmounts Map(Func<double, double> f) =>
        values.Count == 0 ? Empty : new(values.ToFrozenDictionary(kv => kv.Key, kv => f(kv.Value), StringComparer.Ordinal));

    public bool Equals(NutrientAmounts? other) =>
        other is not null
        && values.Count == other.values.Count
        && values.All(kv => other.values.TryGetValue(kv.Key, out var v) && v.Equals(kv.Value));

    public override bool Equals(object? obj) => Equals(obj as NutrientAmounts);

    public override int GetHashCode()
    {
        // Order-independent: the frozen dictionary doesn't promise an order.
        var hash = 0;
        foreach (var (code, amount) in values)
        {
            hash ^= HashCode.Combine(code, amount);
        }

        return hash;
    }
}

/// <summary>
/// A sum of nutrient amounts. <see cref="Incomplete"/> lists the codes some parts knew and others didn't: for those,
/// <see cref="Amounts"/> holds the sum of the known parts, which is only a lower bound, and the app must say so. A code
/// no part knew is simply absent.
/// </summary>
public sealed record NutrientTotal(NutrientAmounts Amounts, IReadOnlySet<string> Incomplete)
{
    public static readonly NutrientTotal Empty = new(NutrientAmounts.Empty, FrozenSet<string>.Empty);

    /// <summary>
    /// Adds the parts nutrient by nutrient, in the order given (floating-point addition isn't associative, so the
    /// order is fixed for reproducible totals).
    /// </summary>
    public static NutrientTotal Sum(IEnumerable<NutrientAmounts> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var list = parts.ToList();
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var part in list)
        {
            ArgumentNullException.ThrowIfNull(part, nameof(parts));
            foreach (var (code, amount) in part.Values)
            {
                sums[code] = sums.TryGetValue(code, out var s) ? s + amount : amount;
            }
        }

        var incomplete = sums.Keys.Where(code => list.Any(p => p[code] is null)).ToFrozenSet(StringComparer.Ordinal);
        return new(NutrientAmounts.Create(sums), incomplete);
    }

    /// <summary>Each amount divided by <paramref name="divisor"/>; what is incomplete stays incomplete.</summary>
    public NutrientTotal DividedBy(double divisor) => this with { Amounts = Amounts.DividedBy(divisor) };

    /// <summary>Each amount × <paramref name="factor"/>; what is incomplete stays incomplete.</summary>
    public NutrientTotal Times(double factor) => this with { Amounts = Amounts.Times(factor) };
}
