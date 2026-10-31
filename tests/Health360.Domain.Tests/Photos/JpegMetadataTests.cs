using System.Text;
using FsCheck.Fluent;
using Health360.Domain.Photos;

namespace Health360.Domain.Tests.Photos;

/// <summary>
/// Metadata stripping for progress photos (PLAN.md §2.5). The files are built segment by segment, so each test says
/// exactly what the input carried; <see cref="Jpeg.Build(byte[][])"/> with no metadata is the part a decoder needs, which must come out intact.
/// </summary>
public class JpegMetadataTests
{
    [Fact]
    public void RemovesExifXmpIptcCommentsAndOtherAppSegments()
    {
        var input = Jpeg.Build(
            Jpeg.Jfif(thumbnail: false),
            Jpeg.Exif(orientation: 1, gps: true),
            Jpeg.Xmp(),
            Jpeg.Segment(0xED, "Photoshop 3.0\08BIM location"u8.ToArray()),
            Jpeg.Segment(0xFE, "Taken at 51.5007 N, 0.1246 W"u8.ToArray()),
            Jpeg.Segment(0xE9, "vendor maker note"u8.ToArray()));

        Assert.True(JpegMetadata.TryStrip(input, out var stripped));

        Assert.Equal(Jpeg.Build(Jpeg.Jfif(thumbnail: false)), stripped!.Jpeg);
        Assert.Equal([0xD8, 0xE0, 0xDB, 0xC0, 0xC4, 0xDA, 0xD9], JpegMetadata.Markers(stripped.Jpeg));
        AssertNoText(stripped.Jpeg, "Exif", "GPS", "xmp", "8BIM", "51.5007", "maker");
    }

    [Fact]
    public void KeepsTheImageDataByteForByte()
    {
        var input = Jpeg.Build(Jpeg.Exif(orientation: 1, gps: true));

        Assert.True(JpegMetadata.TryStrip(input, out var stripped));

        Assert.Equal(Jpeg.Build(), stripped!.Jpeg);
    }

    [Fact]
    public void KeepsTheColourProfileAndAdobeMarker()
    {
        var icc = Jpeg.Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, 0xAA, 0xBB]);
        var adobe = Jpeg.Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]);

        Assert.True(JpegMetadata.TryStrip(Jpeg.Build(icc, Jpeg.Exif(1, gps: true), adobe), out var stripped));

        Assert.Equal(Jpeg.Build(icc, adobe), stripped!.Jpeg);
    }

    [Fact]
    public void RebuildsTheJfifHeaderWithoutItsThumbnail()
    {
        Assert.True(JpegMetadata.TryStrip(Jpeg.Build(Jpeg.Jfif(thumbnail: true)), out var stripped));

        Assert.Equal(Jpeg.Build(Jpeg.Jfif(thumbnail: false)), stripped!.Jpeg);
    }

    [Fact]
    public void DropsAnythingAfterTheEndOfTheImage()
    {
        // Motion photos append an MP4 after EOI.
        byte[] input = [.. Jpeg.Build(), .. "ftypmp42 moov gps"u8];

        Assert.True(JpegMetadata.TryStrip(input, out var stripped));

        Assert.Equal(Jpeg.Build(), stripped!.Jpeg);
    }

    [Fact]
    public void KeepsStuffedBytesAndRestartMarkersInsideTheScan()
    {
        Assert.True(JpegMetadata.TryStrip(Jpeg.Build(), out var stripped));

        // The default scan holds FF 00 and FF D0; both must survive, and a second scan (progressive) too.
        Assert.Equal(Jpeg.Build(), stripped!.Jpeg);
        var progressive = Jpeg.BuildWith(scans: 3);
        Assert.True(JpegMetadata.TryStrip(progressive, out var second));
        Assert.Equal(progressive, second!.Jpeg);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    public void ReportsTheOrientationItRemoved(int orientation, bool upright)
    {
        foreach (var bigEndian in new[] { false, true })
        {
            Assert.True(JpegMetadata.TryStrip(Jpeg.Build(Jpeg.Exif(orientation, gps: false, bigEndian)), out var stripped));
            Assert.Equal(orientation, stripped!.Orientation);
            Assert.Equal(upright, stripped.IsUpright);
        }
    }

    [Fact]
    public void TreatsAMissingOrUnreadableOrientationAsUpright()
    {
        Assert.True(JpegMetadata.TryStrip(Jpeg.Build(), out var none));
        Assert.True(JpegMetadata.TryStrip(Jpeg.Build(Jpeg.Segment(0xE1, "Exif\0\0garbage"u8.ToArray())), out var garbage));

        Assert.Equal(1, none!.Orientation);
        Assert.Equal(1, garbage!.Orientation);
    }

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "empty", [] },
        { "PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] },
        { "SOI only", [0xFF, 0xD8, 0xFF, 0xD9] },
        { "truncated in the scan", Jpeg.Build()[..^4] },
        { "no EOI", Jpeg.Build()[..^2] },
        { "segment longer than the file", [0xFF, 0xD8, 0xFF, 0xE1, 0x7F, 0xFF, 0x00] },
        { "length below two", [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x01, 0xFF, 0xD9] },
        { "garbage between segments", [0xFF, 0xD8, 0x12, 0x34, 0xFF, 0xD9] },
        { "scan before frame", Jpeg.BuildWith(frame: false) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void RejectsAnythingThatIsNotACompleteJpeg(string name, byte[] input)
    {
        Assert.False(JpegMetadata.TryStrip(input, out var stripped), name);
        Assert.Null(stripped);
        Assert.False(JpegMetadata.IsStripped(input), name);
    }

    [Fact]
    public void IsStrippedOnlyWhenNothingWouldChange()
    {
        Assert.True(JpegMetadata.IsStripped(Jpeg.Build(Jpeg.Jfif(thumbnail: false))));
        Assert.False(JpegMetadata.IsStripped(Jpeg.Build(Jpeg.Exif(1, gps: true))));
        Assert.False(JpegMetadata.IsStripped([.. Jpeg.Build(), 0x00]));
    }

    [Fact]
    public void StrippingIsIdempotentAndRemovesAnyMixOfMetadata()
    {
        var metadata = Gen.Elements(
            Jpeg.Exif(1, gps: true),
            Jpeg.Xmp(),
            Jpeg.Segment(0xED, "8BIM"u8.ToArray()),
            Jpeg.Segment(0xFE, "comment"u8.ToArray()),
            Jpeg.Segment(0xE3, [1, 2, 3]),
            Jpeg.Segment(0xEF, []));
        var gen = Gen.Zip(metadata.ArrayOf(), Gen.Choose(1, 3));

        Properties.Holds(gen, t =>
        {
            var (segments, scans) = t;
            var input = Jpeg.BuildWith(scans, true, segments);
            return JpegMetadata.TryStrip(input, out var once)
                && once!.Jpeg.AsSpan().SequenceEqual(Jpeg.BuildWith(scans: scans))
                && JpegMetadata.TryStrip(once.Jpeg, out var twice)
                && twice!.Jpeg.AsSpan().SequenceEqual(once.Jpeg)
                && JpegMetadata.IsStripped(once.Jpeg);
        });
    }

    private static void AssertNoText(byte[] jpeg, params string[] texts)
    {
        var latin = Encoding.Latin1.GetString(jpeg);
        foreach (var text in texts)
        {
            Assert.DoesNotContain(text, latin, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>Builds JPEG byte streams segment by segment.</summary>
internal static class Jpeg
{
    public static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. payload];
    }

    /// <summary>JFIF 1.02, 72 dpi, with or without a 1×1 RGB thumbnail.</summary>
    public static byte[] Jfif(bool thumbnail) =>
        Segment(0xE0, thumbnail
            ? [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 1, 1, 0xC0, 0xFF, 0xEE]
            : [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 0, 0]);

    /// <summary>An EXIF block with an orientation tag and, optionally, a GPS IFD pointer and coordinates.</summary>
    public static byte[] Exif(int orientation, bool gps, bool bigEndian = false)
    {
        var tiff = new List<byte>();
        void U16(int v)
        {
            if (bigEndian)
            {
                tiff.Add((byte)(v >> 8));
                tiff.Add((byte)v);
            }
            else
            {
                tiff.Add((byte)v);
                tiff.Add((byte)(v >> 8));
            }
        }

        void U32(int v)
        {
            if (bigEndian)
            {
                U16(v >> 16);
                U16(v & 0xFFFF);
            }
            else
            {
                U16(v & 0xFFFF);
                U16(v >> 16);
            }
        }

        tiff.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42);
        U32(8);
        U16(gps ? 2 : 1);
        U16(0x0112);
        U16(3);
        U32(1);
        U16(orientation);
        U16(0);
        if (gps)
        {
            U16(0x8825);
            U16(4);
            U32(1);
            U32(38);
        }

        U32(0);
        if (gps)
        {
            tiff.AddRange("GPS 51.5007N 0.1246W"u8.ToArray());
        }

        return Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
    }

    public static byte[] Xmp() =>
        Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta><exif:GPSLatitude>51,30N</exif:GPSLatitude></x:xmpmeta>"u8]);

    public static byte[] Build(params byte[][] metadata) => BuildWith(1, true, metadata);

    public static byte[] BuildWith(int scans = 1, bool frame = true, params byte[][] metadata)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        foreach (var segment in metadata)
        {
            bytes.AddRange(segment);
        }

        bytes.AddRange(Segment(0xDB, [0x00, .. Enumerable.Repeat((byte)1, 64)]));
        if (frame)
        {
            bytes.AddRange(Segment(0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]));
        }

        bytes.AddRange(Segment(0xC4, [0x00, 1, .. new byte[15], 0]));
        for (var s = 0; s < scans; s++)
        {
            bytes.AddRange(Segment(0xDA, [1, 1, 0, 0, 63, 0]));
            // Entropy-coded data with a stuffed FF 00 and a restart marker.
            bytes.AddRange([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56]);
        }

        bytes.AddRange([0xFF, 0xD9]);
        return [.. bytes];
    }
}
