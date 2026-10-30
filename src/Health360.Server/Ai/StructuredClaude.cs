using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using FluentValidation;
using Health360.Contracts.Ai;
using Microsoft.Extensions.Options;

namespace Health360.Server.Ai;

/// <summary>What the model is asked. <see cref="Effort"/> is the only depth control on Opus 5.5.</summary>
public sealed record ClaudeRequest(string System, string Prompt, Effort Effort);

public enum ClaudeOutcome
{
    Ok,
    NotConfigured,
    Refused,
    Truncated,
    /// <summary>The output failed parsing or validation, and so did the one repair.</summary>
    Invalid,
    RateLimited,
    UpstreamError,
}

public sealed record ClaudeResult<T>(ClaudeOutcome Outcome, T? Value, string? Model, AiUsage Usage, IReadOnlyList<string> Errors)
    where T : class;

/// <summary>
/// One structured-output call (PLAN.md §2.4). The JSON schema comes from the <c>Health360.Contracts</c>
/// type the output is parsed into; the parsed value is then checked by that type's validator. If parsing
/// or validation fails, the errors go back to the model for one repair round, and after that the call
/// fails. Refusals and truncation are checked before anything is parsed.
/// </summary>
public sealed partial class StructuredClaude(
    IOptions<AnthropicOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<StructuredClaude> logger)
{
    public const string HttpClientName = "anthropic";

    private const int MaxTokens = 16000;

    private readonly Lazy<AnthropicClient?> _client = new(() => CreateClient(options.Value, httpFactory));

    public async Task<ClaudeResult<T>> GenerateAsync<T>(
        ClaudeRequest request,
        JsonTypeInfo<T> outputType,
        IValidator<T> validator,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outputType);
        ArgumentNullException.ThrowIfNull(validator);

        var usage = new AiUsage(0, 0);
        if (_client.Value is not { } client)
        {
            return Fail<T>(ClaudeOutcome.NotConfigured, null, usage);
        }

        using var schemaDocument = JsonDocument.Parse(StructuredOutputSchema.For(outputType).ToJsonString());
        var schema = schemaDocument.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        var prompt = request.Prompt;
        IReadOnlyList<string> errors = [];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            BetaMessage response;
            try
            {
                response = await client.Beta.Messages.Create(
                    new MessageCreateParams
                    {
                        Model = options.Value.Model,
                        MaxTokens = MaxTokens,
                        System = request.System,
                        Messages = [new BetaUserMessageParam { Content = prompt }],
                        OutputConfig = new BetaOutputConfig
                        {
                            Effort = request.Effort,
                            Format = new BetaJsonOutputFormat { Schema = schema },
                        },
                        // A declined request is re-run server-side on Anthropic's default fallback model.
                        Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                        Fallbacks = new Default(),
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AnthropicRateLimitException)
            {
                return Fail<T>(ClaudeOutcome.RateLimited, null, usage);
            }
            catch (AnthropicException exception)
            {
                // Status and type only: the exception message can echo the request.
                LogUpstreamError(logger, exception.GetType().Name, (exception as AnthropicApiException)?.StatusCode);
                return Fail<T>(ClaudeOutcome.UpstreamError, null, usage);
            }

            usage = new AiUsage(usage.InputTokens + response.Usage.InputTokens, usage.OutputTokens + response.Usage.OutputTokens);
            if (response.StopReason == BetaStopReason.Refusal)
            {
                return Fail<T>(ClaudeOutcome.Refused, response.Model, usage);
            }

            if (response.StopReason == BetaStopReason.MaxTokens)
            {
                return Fail<T>(ClaudeOutcome.Truncated, response.Model, usage);
            }

            var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
            (var value, errors) = ParseAndValidate(text, outputType, validator);
            if (value is not null)
            {
                return new ClaudeResult<T>(ClaudeOutcome.Ok, value, response.Model, usage, []);
            }

            // One repair: the same request again, told what was wrong with the last answer. A fresh
            // conversation, not a reply, so no earlier assistant turn (or its thinking) is resent.
            prompt = RepairPrompt(request.Prompt, text, errors);
        }

        return Fail<T>(ClaudeOutcome.Invalid, null, usage, errors);
    }

    internal static (T? Value, IReadOnlyList<string> Errors) ParseAndValidate<T>(string text, JsonTypeInfo<T> type, IValidator<T> validator)
        where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(text, type);
        }
        catch (JsonException exception)
        {
            // JsonException messages name the path and the problem, not the value.
            return (null, [$"The answer is not valid JSON for the schema: {exception.Message}"]);
        }

        if (value is null)
        {
            return (null, ["The answer is null."]);
        }

        var validation = validator.Validate(value);
        return validation.IsValid ? (value, []) : (null, [.. validation.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")]);
    }

    private static string RepairPrompt(string prompt, string previous, IReadOnlyList<string> errors)
    {
        var builder = new StringBuilder(prompt)
            .Append("\n\nAn earlier answer to this request was rejected:\n")
            .Append(previous)
            .Append("\n\nProblems:\n");
        foreach (var error in errors)
        {
            builder.Append("- ").Append(error).Append('\n');
        }

        return builder.Append("Answer again, fixing these problems.").ToString();
    }

    private static ClaudeResult<T> Fail<T>(ClaudeOutcome outcome, string? model, AiUsage usage, IReadOnlyList<string>? errors = null)
        where T : class => new(outcome, null, model, usage, errors ?? []);

    private static AnthropicClient? CreateClient(AnthropicOptions options, IHttpClientFactory httpFactory) =>
        options.IsConfigured
            ? new AnthropicClient
            {
                ApiKey = options.ApiKey,
                HttpClient = httpFactory.CreateClient(HttpClientName),
                MaxRetries = options.MaxRetries,
                Timeout = options.Timeout,
            }
            : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude call failed: {ExceptionType} {StatusCode}")]
    private static partial void LogUpstreamError(ILogger logger, string exceptionType, System.Net.HttpStatusCode? statusCode);
}
