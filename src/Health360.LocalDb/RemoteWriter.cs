using System.Text.Json.Nodes;

namespace Health360.LocalDb;

/// <summary>
/// Writes one CRUD op to the server. The app uses <see cref="PostgrestWriter"/> (the user's own
/// session, so RLS applies); tests can swap it.
/// </summary>
public interface IRemoteWriter
{
    Task PutAsync(string table, string id, JsonObject data, CancellationToken cancellationToken);

    Task PatchAsync(string table, string id, JsonObject data, CancellationToken cancellationToken);

    Task DeleteAsync(string table, string id, CancellationToken cancellationToken);
}

/// <summary>An error from the server, carrying its Postgres SQLSTATE or PostgREST code when there is one.</summary>
public sealed class RemoteWriteException : Exception
{
    public RemoteWriteException()
    {
    }

    public RemoteWriteException(string message)
        : base(message)
    {
    }

    public RemoteWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RemoteWriteException(string message, string? code)
        : base(message) => Code = code;

    public string? Code { get; }
}
