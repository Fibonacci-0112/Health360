using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.Server.Tests.Support;

/// <summary>
/// Stands in for the Messages API with recorded responses, so no test calls Claude. Records every
/// request it receives; answers from <see cref="Responses"/> in order.
/// </summary>
public sealed class FakeAnthropic
{
    public ConcurrentQueue<HttpResponseMessage> Responses { get; } = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>A fresh handler each time: the HTTP client factory disposes the handlers it is given.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    public FakeAnthropic Reply(string text, string stopReason = "end_turn", string model = "claude-opus-5-5")
    {
        Responses.Enqueue(Json(HttpStatusCode.OK, Message(text, stopReason, model)));
        return this;
    }

    public FakeAnthropic ReplyJson(object output) => Reply(JsonSerializer.Serialize(output));

    public FakeAnthropic Status(HttpStatusCode status, string type)
    {
        var response = Json(status, new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = "test" } });
        response.Headers.Add("retry-after", "0");
        Responses.Enqueue(response);
        return this;
    }

    public static JsonObject Message(string text, string stopReason, string model) => new()
    {
        ["id"] = "msg_test",
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = model,
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "sig" },
            new JsonObject { ["type"] = "text", ["text"] = text }),
        ["stop_reason"] = stopReason,
        ["stop_sequence"] = null,
        ["stop_details"] = stopReason == "refusal"
            ? new JsonObject { ["type"] = "refusal", ["category"] = null, ["explanation"] = null }
            : null,
        ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20 },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    public sealed record RecordedRequest(string Path, IReadOnlyDictionary<string, string> Headers, JsonObject Body);

    private sealed class Handler(FakeAnthropic fake) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
            fake.Requests.Enqueue(new RecordedRequest(request.RequestUri!.AbsolutePath, headers, JsonNode.Parse(body)!.AsObject()));
            return fake.Responses.TryDequeue(out var response)
                ? response
                : new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
