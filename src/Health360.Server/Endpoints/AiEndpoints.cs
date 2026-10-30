using Anthropic.Models.Beta.Messages;
using Health360.Contracts.Ai;
using Health360.Domain.Units;
using Health360.Server.Ai;

namespace Health360.Server.Endpoints;

/// <summary>
/// AI endpoints. Claude is called from here only, so the API key never reaches a client. The model
/// proposes text; anything the code can compute (here, the weight in kg) is computed by the code.
/// </summary>
public static class AiEndpoints
{
    private const string HelloSystem =
        "You write one short, friendly greeting and one general wellness tip for a fitness app. " +
        "This is a wellness app, not a medical device: never diagnose, and never suggest doses of anything. " +
        "Do not state or estimate any numbers; the app computes all figures itself.";

    private static readonly AiHelloRequestValidator RequestValidator = new();
    private static readonly AiHelloOutputValidator OutputValidator = new();

    public static IEndpointRouteBuilder MapAiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/ai/hello", HelloAsync);
        return app;
    }

    private static async Task<IResult> HelloAsync(AiHelloRequest request, StructuredClaude claude, CancellationToken cancellationToken)
    {
        var validation = await RequestValidator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        var weight = Quantity.Create(QuantityKind.BodyWeight, request.Weight, Unit.Parse(request.WeightUnit));

        var result = await claude.GenerateAsync(
            new ClaudeRequest(HelloSystem, "Greet someone who just set up their health tracker.", Effort.Low),
            AiJsonContext.Default.AiHelloOutput,
            OutputValidator,
            cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            ClaudeOutcome.Ok => Results.Ok(new AiHelloResponse(result.Value!, weight.Canonical, result.Model!, result.Usage)),
            ClaudeOutcome.NotConfigured => Error("not_configured", StatusCodes.Status503ServiceUnavailable),
            ClaudeOutcome.Refused => Error("refused", StatusCodes.Status422UnprocessableEntity),
            ClaudeOutcome.Truncated => Error("truncated", StatusCodes.Status502BadGateway),
            ClaudeOutcome.Invalid => Error("invalid_output", StatusCodes.Status502BadGateway),
            ClaudeOutcome.RateLimited => Error("rate_limited", StatusCodes.Status429TooManyRequests),
            _ => Error("upstream_error", StatusCodes.Status502BadGateway),
        };
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
