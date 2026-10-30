using Health360.Contracts.Account;
using Health360.Server.Auth;
using Health360.Server.Data;
using Npgsql;

namespace Health360.Server.Endpoints;

/// <summary>
/// The first endpoints that read data. They carry no access checks of their own: queries run as the
/// caller, and RLS filters them, so another user's profile is simply not found.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/me", GetMeAsync);
        app.MapGet("/profiles/{id:guid}", GetProfileAsync);
        return app;
    }

    private static Task<MeResponse> GetMeAsync(SignedInUser user, IUserDatabase db, CancellationToken cancellationToken) =>
        db.RunAsync(user, async (connection, ct) =>
        {
            await using var uid = new NpgsqlCommand("select auth.uid()", connection);
            var userId = (Guid)(await uid.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            var profiles = new List<ProfileSummary>();
            await using (var command = new NpgsqlCommand("select id, display_name from public.profiles order by created_at, id", connection))
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    profiles.Add(new ProfileSummary(reader.GetGuid(0), reader.GetString(1)));
                }
            }

            var households = new List<Guid>();
            await using (var command = new NpgsqlCommand("select id from public.households order by id", connection))
            await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    households.Add(reader.GetGuid(0));
                }
            }

            return new MeResponse(userId, profiles, households);
        }, cancellationToken);

    private static async Task<IResult> GetProfileAsync(Guid id, SignedInUser user, IUserDatabase db, CancellationToken cancellationToken)
    {
        var profile = await db.RunAsync(user, async (connection, ct) =>
        {
            await using var command = new NpgsqlCommand("select id, display_name from public.profiles where id = $1", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? new ProfileSummary(reader.GetGuid(0), reader.GetString(1)) : null;
        }, cancellationToken).ConfigureAwait(false);

        return profile is null ? Results.NotFound() : Results.Ok(profile);
    }
}
