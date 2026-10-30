using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Health360.Server.Data;

/// <summary>The <c>ConnectionStrings</c> section. Checked at startup, so a host without it never reports healthy.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "ConnectionStrings";

    /// <summary>Npgsql connection string for the API's login role (<c>health360_api</c>).</summary>
    [Required]
    public string? Database { get; set; }
}

public static class UserDatabaseRegistration
{
    /// <summary>
    /// The API's own login role, never the service role or <c>postgres</c>: see <see cref="UserDatabase"/>
    /// and docs/phase0b.md.
    /// </summary>
    public static IServiceCollection AddUserDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(provider => NpgsqlDataSource.Create(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Database!));
        services.AddSingleton<IUserDatabase, UserDatabase>();
        return services;
    }
}
