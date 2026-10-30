using Health360.Server.Ai;
using Health360.Server.Auth;
using Health360.Server.Data;
using Health360.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupabaseAuthentication(builder.Configuration);
builder.Services.AddUserDatabase(builder.Configuration);
builder.Services.AddClaude(builder.Configuration);

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Liveness only: no data, no auth. Every other endpoint requires a signed-in user (the fallback
// policy in SupabaseAuthentication) and queries Postgres as that user so RLS applies.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAccountEndpoints();
app.MapAiEndpoints();

app.Run();
