using FluentValidation;

namespace Health360.Contracts.Ai;

public sealed class AiHelloRequestValidator : AbstractValidator<AiHelloRequest>
{
    public AiHelloRequestValidator()
    {
        RuleFor(x => x.Weight).Must(double.IsFinite).WithMessage("'Weight' must be a number.")
            .GreaterThan(0).LessThanOrEqualTo(1000);
        RuleFor(x => x.WeightUnit).Must(unit => unit is "kg" or "lb").WithMessage("'Weight Unit' must be kg or lb.");
    }
}

/// <summary>
/// Checks Claude's output again on the server. The JSON schema sent with the request fixes the shape;
/// lengths and blank strings are checked only here, because structured outputs don't enforce them.
/// </summary>
public sealed class AiHelloOutputValidator : AbstractValidator<AiHelloOutput>
{
    public AiHelloOutputValidator()
    {
        RuleFor(x => x.Greeting).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Tip).NotEmpty().MaximumLength(300);
    }
}
