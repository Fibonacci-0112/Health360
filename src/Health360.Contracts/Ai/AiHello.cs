namespace Health360.Contracts.Ai;

/// <summary>
/// <c>POST /ai/hello</c>: the Phase 0b smoke test for the AI layer (PLAN.md §2.4), ported from the
/// prototype's <c>ai-hello</c> Edge Function. The body weight shows Domain code running on the server.
/// </summary>
/// <param name="Weight">Body weight as typed.</param>
/// <param name="WeightUnit"><c>kg</c> or <c>lb</c>.</param>
public sealed record AiHelloRequest(double Weight, string WeightUnit);

/// <summary>What Claude must return. Numbers the code can compute are never asked of the model.</summary>
public sealed record AiHelloOutput(string Greeting, string Tip);

/// <summary>The validated model output, plus what the server computed itself.</summary>
/// <param name="Output">Claude's output, after the server validated it.</param>
/// <param name="WeightKg">Computed by <c>Health360.Domain</c>, not by the model.</param>
/// <param name="Model">The model that answered (after any server-side fallback).</param>
/// <param name="Usage">Tokens billed.</param>
public sealed record AiHelloResponse(AiHelloOutput Output, double WeightKg, string Model, AiUsage Usage);

/// <summary>Tokens billed for a request, summed over every model call it made (including a repair).</summary>
public sealed record AiUsage(long InputTokens, long OutputTokens);
