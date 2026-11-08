using Health360.Domain.Nutrition;
using Health360.Server.Foods;

namespace Health360.Server.Endpoints;

/// <summary>
/// Barcode lookup. A household's own foods are looked up on the device first (they sync), so the server answers
/// only for the online steps; it reads no user data.
/// </summary>
public static class FoodEndpoints
{
    public static IEndpointRouteBuilder MapFoodEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/foods/barcode/{code}/usda", UsdaAsync).RequireRateLimiting(FoodServices.RateLimitPolicy);
        return app;
    }

    private static readonly Action<ILogger, System.Net.HttpStatusCode?, Exception?> LogUsdaFailure =
        LoggerMessage.Define<System.Net.HttpStatusCode?>(LogLevel.Warning, new EventId(1, "UsdaLookupFailed"), "USDA barcode lookup failed with status {Status}.");

    private static async Task<IResult> UsdaAsync(string code, UsdaBrandedClient usda, ILogger<UsdaBrandedClient> logger, CancellationToken cancellationToken)
    {
        var gtin = Gtin.Normalize(code);
        if (gtin is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Not a valid barcode."] });
        }

        if (!usda.IsConfigured)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "USDA lookup is not configured.");
        }

        try
        {
            var match = await usda.FindAsync(gtin, cancellationToken).ConfigureAwait(false);
            return match is null ? Results.NotFound() : Results.Ok(match);
        }
        catch (UsdaBudgetExhaustedException)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "USDA lookups are busy. Try again later.");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A refused or failed request, an unreadable body, or the client's own timeout (not the caller giving
            // up). Status only is logged: the request carries the key and the response may echo the barcode.
            LogUsdaFailure(logger, (ex as HttpRequestException)?.StatusCode, null);
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "USDA lookup is unavailable.");
        }
    }
}
