namespace Health360.Domain.Units;

/// <summary>
/// The stored string codes for an enum's values, in display order. Unit preferences are kept as
/// these codes in <c>profiles.unit_prefs</c>.
/// </summary>
public sealed class CodeMap<T>
    where T : struct, Enum
{
    private readonly (T Value, string Code)[] entries;

    public CodeMap(params (T Value, string Code)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        this.entries = entries;
        Values = [.. entries.Select(e => e.Value)];
    }

    /// <summary>The values in the order the settings screen lists them.</summary>
    public IReadOnlyList<T> Values { get; }

    public string ToCode(T value)
    {
        foreach (var (v, code) in entries)
        {
            if (EqualityComparer<T>.Default.Equals(v, value))
            {
                return code;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, $"No code for {typeof(T).Name}");
    }

    public bool TryParse(string? code, out T value)
    {
        foreach (var (v, c) in entries)
        {
            if (string.Equals(c, code, StringComparison.Ordinal))
            {
                value = v;
                return true;
            }
        }

        value = default;
        return false;
    }

    public T Parse(string code) =>
        TryParse(code, out var value) ? value : throw new FormatException($"Unknown {typeof(T).Name} code: {code}");
}
