var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Liveness only: no data, no auth. Every other endpoint must validate the Supabase JWT and the
// signed-in user, and query Postgres as that user so RLS applies (PLAN.md §2.1).
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
