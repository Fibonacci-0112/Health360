namespace Health360.Sync.Tests.Support;

/// <summary>
/// A minimal JPEG with EXIF holding an orientation and GPS text, as a phone's camera would write it. The app strips
/// it on the device (<c>JpegMetadata</c>), so nothing of the EXIF should ever reach Storage.
/// </summary>
public static class Jpegs
{
    /// <summary>The GPS text inside <see cref="WithGps"/>, to check a stored file for.</summary>
    public static ReadOnlySpan<byte> GpsText => "GPS 51.5007N 0.1246W"u8;

    public static byte[] WithGps() => Build(Exif());

    public static byte[] Plain() => Build([]);

    public static bool HasGps(byte[] jpeg) => jpeg.AsSpan().IndexOf(GpsText) >= 0;

    private static byte[] Segment(byte marker, byte[] payload) =>
        [0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];

    // Little-endian TIFF, one IFD0 entry (orientation 1), then the GPS text.
    private static byte[] Exif() =>
        Segment(0xE1, [
            .. "Exif\0\0II"u8, 42, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            .. GpsText,
        ]);

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
