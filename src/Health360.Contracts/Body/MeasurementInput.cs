using FluentValidation;
using Health360.Domain.Physiology;
using Health360.Domain.Units;

namespace Health360.Contracts.Body;

/// <summary>
/// A metric as <c>metric_definitions</c> defines it: its sharing category, the units a value may be entered in,
/// the canonical unit, and definitional limits on the canonical value (inclusive; not clinical ranges).
/// </summary>
public sealed record MetricDefinition(
    string Code,
    string Category,
    IReadOnlyList<string> Units,
    string CanonicalUnit,
    double? ValidMin,
    double? ValidMax)
{
    /// <summary>The value in the canonical unit, as the server's trigger recomputes it; null if the unit isn't allowed.</summary>
    public double? ToCanonical(double value, string unit) =>
        Units.Contains(unit) ? UnitCodes.Convert(value, unit, CanonicalUnit) : null;
}

/// <summary>
/// One value as the user enters it. <paramref name="Value"/> is null when the field is empty or unreadable.
/// <paramref name="Source"/> is a <c>measurements.source</c> code; <paramref name="LocalDate"/> the day it was measured.
/// </summary>
public sealed record MeasurementInput(string MetricCode, double? Value, string Unit, string Source, DateOnly LocalDate);

/// <summary>
/// Checks an entry against its metric before it is written: the same rules as the <c>measurements</c> table and its
/// trigger (allowed unit, finite value, the metric's limits on the canonical value), plus no date after today and
/// a weight above 0 (the weight trend needs one).
/// The server checks again; this lets the form say what's wrong instead of the write being quarantined.
/// </summary>
public sealed class MeasurementInputValidator : AbstractValidator<MeasurementInput>
{
    public MeasurementInputValidator(MetricDefinition metric, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(metric);
        RuleFor(x => x.MetricCode).Equal(metric.Code);
        RuleFor(x => x.Source).Must(code => MeasurementSources.TryParse(code, out _)).WithMessage("Choose where the value came from.");
        RuleFor(x => x.Unit).Must(metric.Units.Contains).WithMessage("Choose a unit.");
        RuleFor(x => x.LocalDate).LessThanOrEqualTo(today).WithMessage("The date can't be in the future.");
        RuleFor(x => x.Value)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("Enter a number.")
            .Must(v => double.IsFinite(v!.Value)).WithMessage("Enter a number.")
            .Must((input, v) => InRange(metric, v!.Value, input.Unit)).WithMessage(_ => RangeMessage(metric))
            .Must(v => metric.Code != MetricCodes.Weight || v!.Value > 0).WithMessage("Weight must be more than 0.")
            .When(x => metric.Units.Contains(x.Unit));
    }

    private static bool InRange(MetricDefinition metric, double value, string unit) =>
        metric.ToCanonical(value, unit) is { } canonical
        && double.IsFinite(canonical)
        && (metric.ValidMin is not { } min || canonical >= min)
        && (metric.ValidMax is not { } max || canonical <= max);

    private static string RangeMessage(MetricDefinition metric) => (metric.ValidMin, metric.ValidMax) switch
    {
        (0, 100) when metric.CanonicalUnit == "%" => "Enter a percentage from 0 to 100.",
        (0, null) => "The value can't be negative.",
        _ => "That value is out of range.",
    };
}
