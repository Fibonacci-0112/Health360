using Health360.Server.Auth;
using Health360.Server.Data;
using Health360.Server.Endpoints;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSupabaseAuthentication(builder.Configuration);

// The API's own login role, never the service role: see UserDatabase and docs/phase0b.md.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("Database")
    ?? throw new InvalidOperationException("ConnectionStrings:Database is not set.")));
builder.Services.AddSingleton<IUserDatabase, UserDatabase>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Liveness only: no data, no auth. Every other endpoint requires a signed-in user (the fallback
// policy in SupabaseAuthentication) and queries Postgres as that user so RLS applies.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAccountEndpoints();

app.Run();
