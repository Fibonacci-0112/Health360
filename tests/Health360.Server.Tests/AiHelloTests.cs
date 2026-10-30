using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Health360.Contracts.Ai;
using Health360.Server.Tests.Support;

namespace Health360.Server.Tests;

/// <summary>
/// <c>POST /ai/hello</c> against recorded Messages API responses (PLAN.md §5: no live calls in CI).
/// </summary>
public sealed class AiHelloTests
{
    private static readonly Guid UserA = Guid.Parse("018f0000-0000-7000-8000-00000000000a");

    private static readonly object ValidOutput = new { greeting = "Welcome aboard!", tip = "Drink some water today." };

    [Fact]
    public async Task Returns_the_validated_output_and_the_weight_computed_by_Domain()
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.ReplyJson(ValidOutput);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 180, weightUnit = "lb" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AiHelloResponse>(TestContext.Current.CancellationToken);
        Assert.Equal(new AiHelloOutput("Welcome aboard!", "Drink some water today."), body!.Output);
        Assert.Equal(180 * 0.45359237, body.WeightKg, 12);
        Assert.Equal("claude-opus-5-5", body.Model);
        Assert.Equal(new AiUsage(100, 20), body.Usage);
    }

    [Fact]
    public async Task Sends_a_structured_output_request_with_the_schema_from_Contracts()
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.ReplyJson(ValidOutput);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(factory.Anthropic.Requests);
        Assert.EndsWith("/v1/messages", request.Path, StringComparison.Ordinal);
        // The SDK sends the API key as a bearer token.
        Assert.Equal("Bearer test-anthropic-key", request.Headers["authorization"]);
        Assert.Contains("server-side-fallback-2026-07-01", request.Headers["anthropic-beta"], StringComparison.Ordinal);

        var body = request.Body;
        Assert.Equal("claude-opus-5-5", (string?)body["model"]);
        Assert.Equal("default", (string?)body["fallbacks"]);
        Assert.Equal("low", (string?)body["output_config"]!["effort"]);
        Assert.Equal("json_schema", (string?)body["output_config"]!["format"]!["type"]);
        Assert.True(JsonNode.DeepEquals(
            StructuredOutputSchema.For(AiJsonContext.Default.AiHelloOutput),
            body["output_config"]!["format"]!["schema"]));

        // PLAN.md §2.4: no sampling parameters, and the model is never asked for the weight.
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("top_p"));
        Assert.False(body.ContainsKey("top_k"));
        Assert.DoesNotContain("80", body["messages"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refusal", HttpStatusCode.UnprocessableEntity, "refused")]
    [InlineData("max_tokens", HttpStatusCode.BadGateway, "truncated")]
    public async Task Checks_the_stop_reason_before_parsing(string stopReason, HttpStatusCode status, string error)
    {
        await using var factory = new ServerFactory();
        // Valid JSON, so only the stop reason can reject it.
        factory.Anthropic.Reply(JsonSerializer.Serialize(ValidOutput), stopReason);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, await ErrorCode(response));
        Assert.Single(factory.Anthropic.Requests);
    }

    [Fact]
    public async Task Repairs_an_invalid_output_once_with_the_validation_errors()
    {
        await using var factory = new ServerFactory();
        factory.Anthropic
            .ReplyJson(new { greeting = new string('x', 201), tip = "Sleep well." })
            .ReplyJson(ValidOutput);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AiHelloResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("Welcome aboard!", body!.Output.Greeting);
        Assert.Equal(new AiUsage(200, 40), body.Usage);

        var requests = factory.Anthropic.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        var repair = requests[1].Body["messages"]!.AsArray();
        // A fresh request, not a reply: no assistant turn is resent.
        var message = Assert.Single(repair);
        Assert.Equal("user", (string?)message!["role"]);
        var content = message!["content"] is JsonArray blocks ? (string?)blocks[0]!["text"] : (string?)message["content"];
        Assert.StartsWith("Greet someone who just set up their health tracker.", content, StringComparison.Ordinal);
        Assert.Contains("Greeting: The length of 'Greeting' must be 200 characters or fewer", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_which_rules_failed_but_never_the_output()
    {
        const string Secret = "do-not-log-this-output";
        await using var factory = new ServerFactory();
        factory.Anthropic
            .ReplyJson(new { greeting = Secret + new string('x', 201), tip = "Sleep well." })
            .Reply(Secret);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var logs = factory.Logs.Messages.ToArray();
        Assert.Contains(logs, m => m.Contains("attempt 1, failed Greeting:MaximumLengthValidator", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("attempt 2, failed $:Json", StringComparison.Ordinal));
        Assert.Contains(logs, m => m.Contains("still invalid after repair", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, m => m.Contains("health tracker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logs_refusals()
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.Reply("", "refusal");
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(factory.Logs.Messages, m => m.StartsWith("Claude refused: model claude-opus-5-5, attempt 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Limits_each_user_to_their_hourly_AI_requests()
    {
        await using var factory = new ServerFactory { AiRequestsPerUserPerHour = 2 };
        factory.Anthropic.ReplyJson(ValidOutput).ReplyJson(ValidOutput).ReplyJson(ValidOutput);
        using var userA = factory.CreateClient(TestTokens.User(UserA));
        using var userB = factory.CreateClient(TestTokens.User(Guid.Parse("018f0000-0000-7000-8000-00000000000b")));
        var body = new { weight = 80, weightUnit = "kg" };
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 2; i++)
        {
            using var ok = await userA.PostAsJsonAsync("/ai/hello", body, ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var limited = await userA.PostAsJsonAsync("/ai/hello", body, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await ErrorCode(limited));
        Assert.Equal(2, factory.Anthropic.Requests.Count);

        // Another user has their own allowance.
        using var other = await userB.PostAsJsonAsync("/ai/hello", body, ct);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"greeting":"Hi","tip":"Walk."}""" + "trailing")]
    [InlineData("""{"greeting":"Hi"}""")]
    [InlineData("""{"greeting":"Hi","tip":null}""")]
    [InlineData("""{"greeting":"Hi","tip":"Walk.","weightKg":80}""")]
    [InlineData("""{"greeting":" ","tip":"Walk."}""")]
    public async Task Fails_when_the_repair_is_invalid_too(string output)
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.Reply(output).Reply(output);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("invalid_output", await ErrorCode(response));
        Assert.Equal(2, factory.Anthropic.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limit_error", HttpStatusCode.TooManyRequests, "rate_limited")]
    [InlineData(HttpStatusCode.InternalServerError, "api_error", HttpStatusCode.BadGateway, "upstream_error")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request_error", HttpStatusCode.BadGateway, "upstream_error")]
    public async Task Maps_API_errors(HttpStatusCode upstream, string type, HttpStatusCode status, string error)
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.Status(upstream, type);
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(error, await ErrorCode(response));
    }

    [Theory]
    [InlineData(0, "kg")]
    [InlineData(-5, "kg")]
    [InlineData(1001, "kg")]
    [InlineData(80, "st")]
    [InlineData(80, null)]
    public async Task Rejects_invalid_requests_without_calling_the_model(double weight, string? unit)
    {
        await using var factory = new ServerFactory();
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight, weightUnit = unit }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Anthropic.Requests);
    }

    [Fact]
    public async Task Requires_a_signed_in_user_before_calling_the_model()
    {
        await using var factory = new ServerFactory();
        factory.Anthropic.ReplyJson(ValidOutput);

        using var anonymous = factory.CreateClient(token: null);
        using var anonKey = factory.CreateClient(TestTokens.AnonKey());
        foreach (var client in new[] { anonymous, anonKey })
        {
            using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(factory.Anthropic.Requests);
    }

    [Fact]
    public async Task Answers_503_to_signed_in_users_when_no_API_key_is_configured()
    {
        await using var factory = new ServerFactory { AnthropicApiKey = null };
        using var client = factory.CreateClient(TestTokens.User(UserA));

        using var response = await client.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("not_configured", await ErrorCode(response));

        using var anonymous = factory.CreateClient(token: null);
        using var unauthorized = await anonymous.PostAsJsonAsync("/ai/hello", new { weight = 80, weightUnit = "kg" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["error"];
}
