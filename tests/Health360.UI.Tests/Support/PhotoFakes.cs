using Health360.LocalDb;
using Health360.UI.Data;

namespace Health360.UI.Tests.Support;

/// <summary>The camera and library: hands back <see cref="Next"/>, or null as if the user cancelled.</summary>
public sealed class FakePhotoCapture : IPhotoCapture
{
    public bool CanTakePhoto { get; set; } = true;

    public CapturedPhoto? Next { get; set; } = new(Jpegs.WithGps(), Jpegs.WithGps());

    public List<string> Calls { get; } = [];

    public Task<CapturedPhoto?> TakeAsync()
    {
        Calls.Add("take");
        return Task.FromResult(Next);
    }

    public Task<CapturedPhoto?> PickAsync()
    {
        Calls.Add("pick");
        return Task.FromResult(Next);
    }
}

/// <summary>Photo files in memory.</summary>
public sealed class MemoryPhotoFiles : IPhotoFileStore
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Task WriteAsync(string name, byte[] jpeg, CancellationToken cancellationToken)
    {
        Files[name] = jpeg;
        return Task.CompletedTask;
    }

    public Task<byte[]?> ReadAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Files.TryGetValue(name, out var bytes) ? bytes : null);

    public void Delete(string name) => Files.Remove(name);

    public IReadOnlyList<string> List() => [.. Files.Keys];

    public void DeleteAll() => Files.Clear();
}

/// <summary>
/// Storage that can't be reached (so photos stay waiting, whatever the background loop does), or that refuses every
/// file for good, as a policy would, once <see cref="Refuse"/> is set.
/// </summary>
public sealed class TestPhotoStorage : IPhotoStorage
{
    public bool Refuse { get; set; }

    /// <summary>False until the server can be reached for deletes (discarding needs it).</summary>
    public bool Reachable { get; set; }

    public List<string> Deleted { get; } = [];

    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        if (!Reachable)
        {
            throw new PhotoUploadException("offline", permanent: false, code: null);
        }

        Deleted.Add(path);
        return Task.CompletedTask;
    }

    public Task UploadAsync(string path, byte[] jpeg, CancellationToken cancellationToken) =>
        throw (Refuse
            ? new PhotoUploadException("denied", permanent: true, code: "403")
            : new PhotoUploadException("offline", permanent: false, code: null));
}

/// <summary>Small JPEG byte streams: a minimal image with EXIF and GPS text.</summary>
public static class Jpegs
{
    public static byte[] WithGps() => Build(Segment(0xE1, [
        .. "Exif\0\0II"u8, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        .. "GPS 51.5007N 0.1246W"u8,
    ]));

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    private static byte[] Build(byte[] metadata) =>
    [
        0xFF, 0xD8, .. metadata,
        .. Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]),
        .. Segment(0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]),
        .. Segment(0xC4, [0, 1, .. new byte[15], 0]),
        .. Segment(0xDA, [1, 1, 0, 0, 63, 0]),
        0x12, 0x34, 0xFF, 0xD9,
    ];
}
