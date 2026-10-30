using System.Text.Json.Nodes;

namespace Health360.LocalDb.Tests.Support;

/// <summary>Records every write it is given; <see cref="Fail"/> decides which ones throw.</summary>
public sealed class FakeWriter : IRemoteWriter
{
    public List<Write> Writes { get; } = [];

    /// <summary>Returns the exception to throw for a write, or null to accept it.</summary>
    public Func<Write, Exception?> Fail { get; set; } = _ => null;

    public Task PutAsync(string table, string id, JsonObject data, CancellationToken cancellationToken) =>
        Record(new Write("PUT", table, id, data));

    public Task PatchAsync(string table, string id, JsonObject data, CancellationToken cancellationToken) =>
        Record(new Write("PATCH", table, id, data));

    public Task DeleteAsync(string table, string id, CancellationToken cancellationToken) =>
        Record(new Write("DELETE", table, id, null));

    private Task Record(Write write)
    {
        Writes.Add(write);
        return Fail(write) is { } error ? Task.FromException(error) : Task.CompletedTask;
    }

    public sealed record Write(string Op, string Table, string Id, JsonObject? Data);
}
