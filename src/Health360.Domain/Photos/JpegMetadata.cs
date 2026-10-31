using System.Buffers.Binary;

namespace Health360.Domain.Photos;

/// <summary>A JPEG with its metadata removed, and the EXIF orientation the original declared.</summary>
/// <param name="Jpeg">The image, holding only what is needed to decode it.</param>
/// <param name="Orientation">The EXIF orientation tag (1–8) of the original, 1 when it had none.</param>
public sealed record StrippedJpeg(byte[] Jpeg, int Orientation)
{
    /// <summary>True when the original was stored upright, so dropping the EXIF orientation changes nothing.</summary>
    public bool IsUpright => Orientation == 1;
}

/// <summary>
/// Removes metadata from a JPEG without re-encoding it (PLAN.md §2.5: EXIF and location stripped on the device).
/// It keeps only the segments a decoder needs: quantisation and Huffman tables, frame and scan headers, restart
/// intervals, the entropy-coded data, a JFIF header rebuilt without its thumbnail, the ICC colour profile and the
/// Adobe colour-transform marker. Everything else goes: EXIF (GPS, camera, times, the embedded thumbnail), XMP,
/// IPTC and Photoshop blocks, comments, every other application segment, and anything after the end-of-image
/// marker (phones append motion-photo videos and vendor trailers there).
/// <para>
/// It fails closed: a file that isn't a complete, well-formed JPEG is rejected rather than passed through.
/// </para>
/// </summary>
public static class JpegMetadata
{
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte App0 = 0xE0;
    private const byte App1 = 0xE1;
    private const byte App2 = 0xE2;
    private const byte App14 = 0xEE;
    private const byte App15 = 0xEF;
    private const byte Com = 0xFE;

    private static ReadOnlySpan<byte> JfifId => "JFIF\0"u8;

    private static ReadOnlySpan<byte> ExifId => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> IccId => "ICC_PROFILE\0"u8;

    private static ReadOnlySpan<byte> AdobeId => "Adobe"u8;

    /// <summary>Strips <paramref name="jpeg"/>, or returns false if it is not a complete, well-formed JPEG.</summary>
    public static bool TryStrip(ReadOnlySpan<byte> jpeg, out StrippedJpeg? result)
    {
        result = null;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != Soi)
        {
            return false;
        }

        var output = new List<byte>(jpeg.Length);
        output.Add(0xFF);
        output.Add(Soi);
        var orientation = 1;
        var sawFrame = false;
        var sawScan = false;
        var i = 2;
        while (true)
        {
            if (!TryReadMarker(jpeg, ref i, out var marker))
            {
                return false;
            }

            if (marker == Eoi)
            {
                if (!sawFrame || !sawScan)
                {
                    return false;
                }

                output.Add(0xFF);
                output.Add(Eoi);
                result = new StrippedJpeg([.. output], orientation);
                return true;
            }

            if (marker == Soi || IsStandalone(marker))
            {
                // A second SOI, or a restart or TEM marker outside entropy-coded data, is not a valid file.
                return false;
            }

            if (i + 2 > jpeg.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[i..]);
            if (length < 2 || i + length > jpeg.Length)
            {
                return false;
            }

            var payload = jpeg.Slice(i + 2, length - 2);
            var segment = jpeg.Slice(i - 2, length + 2);
            i += length;

            if (marker is >= App0 and <= App15 || marker == Com)
            {
                if (marker == App0 && payload.StartsWith(JfifId))
                {
                    if (payload.Length < 14)
                    {
                        return false;
                    }

                    AppendJfifWithoutThumbnail(output, payload);
                }
                else if (marker == App1 && payload.StartsWith(ExifId))
                {
                    orientation = ExifOrientation(payload[ExifId.Length..]) ?? orientation;
                }
                else if ((marker == App2 && payload.StartsWith(IccId)) || (marker == App14 && payload.StartsWith(AdobeId)))
                {
                    Append(output, segment);
                }

                continue;
            }

            if (IsFrame(marker))
            {
                sawFrame = true;
            }

            Append(output, segment);
            if (marker == Sos)
            {
                if (!sawFrame)
                {
                    return false;
                }

                sawScan = true;
                var end = EntropyDataEnd(jpeg, i);
                if (end < 0)
                {
                    return false;
                }

                Append(output, jpeg[i..end]);
                i = end;
            }
        }
    }

    /// <summary>
    /// The markers of a well-formed JPEG in order, up to and including end-of-image (restart markers inside scans
    /// are left out), or null when it isn't one. For checking what a file carries.
    /// </summary>
    public static IReadOnlyList<byte>? Markers(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != Soi)
        {
            return null;
        }

        var markers = new List<byte> { Soi };
        var i = 2;
        while (TryReadMarker(jpeg, ref i, out var marker))
        {
            markers.Add(marker);
            if (marker == Eoi)
            {
                return markers;
            }

            if (marker == Soi || IsStandalone(marker) || i + 2 > jpeg.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[i..]);
            if (length < 2 || i + length > jpeg.Length)
            {
                return null;
            }

            i += length;
            if (marker == Sos)
            {
                i = EntropyDataEnd(jpeg, i);
                if (i < 0)
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="jpeg"/> is a well-formed JPEG that stripping would leave unchanged: it carries no
    /// metadata. The upload queue checks this again before anything leaves the device.
    /// </summary>
    public static bool IsStripped(ReadOnlySpan<byte> jpeg) =>
        TryStrip(jpeg, out var stripped) && stripped!.Jpeg.AsSpan().SequenceEqual(jpeg);

    // Reads 0xFF, any fill bytes, then the marker code; i ends just past the code.
    private static bool TryReadMarker(ReadOnlySpan<byte> jpeg, ref int i, out byte marker)
    {
        marker = 0;
        if (i >= jpeg.Length || jpeg[i] != 0xFF)
        {
            return false;
        }

        while (i < jpeg.Length && jpeg[i] == 0xFF)
        {
            i++;
        }

        if (i >= jpeg.Length || jpeg[i] == 0x00)
        {
            return false;
        }

        marker = jpeg[i++];
        return true;
    }

    // RSTn and TEM have no length field.
    private static bool IsStandalone(byte marker) => marker is (>= 0xD0 and <= 0xD7) or 0x01;

    // SOF0–SOF15, leaving out DHT (C4), JPG (C8) and DAC (CC), which share the range.
    private static bool IsFrame(byte marker) => marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    /// <summary>
    /// Where the entropy-coded data starting at <paramref name="start"/> ends: the index of the 0xFF that begins the
    /// next real marker. Stuffed bytes (FF 00), restart markers (FF D0–D7) and fill bytes belong to the data. -1 if the
    /// file ends first.
    /// </summary>
    private static int EntropyDataEnd(ReadOnlySpan<byte> jpeg, int start)
    {
        var i = start;
        while (i < jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                i++;
                continue;
            }

            var j = i + 1;
            while (j < jpeg.Length && jpeg[j] == 0xFF)
            {
                j++;
            }

            if (j >= jpeg.Length)
            {
                return -1;
            }

            if (jpeg[j] == 0x00 || jpeg[j] is >= 0xD0 and <= 0xD7)
            {
                i = j + 1;
                continue;
            }

            return i;
        }

        return -1;
    }

    // The JFIF header with its version, density unit and density, but no thumbnail (it is a copy of the photo).
    private static void AppendJfifWithoutThumbnail(List<byte> output, ReadOnlySpan<byte> payload)
    {
        output.AddRange([0xFF, App0, 0x00, 0x10]);
        Append(output, payload[..12]);
        output.Add(0);
        output.Add(0);
    }

    /// <summary>The orientation tag (0x0112) from IFD0 of an EXIF TIFF block, or null if absent or unreadable.</summary>
    private static int? ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return null;
        }

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            return null;
        }

        if (U16(tiff, 2, little) != 42)
        {
            return null;
        }

        var ifd = U32(tiff, 4, little);
        if (ifd < 8 || ifd > tiff.Length - 2)
        {
            return null;
        }

        var offset = (int)ifd;
        int count = U16(tiff, offset, little);
        for (var n = 0; n < count; n++)
        {
            var entry = offset + 2 + (n * 12);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (U16(tiff, entry, little) == 0x0112)
            {
                // SHORT, one value, stored in the first two bytes of the value field.
                int value = U16(tiff, entry + 8, little);
                return U16(tiff, entry + 2, little) == 3 && value is >= 1 and <= 8 ? value : null;
            }
        }

        return null;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt16BigEndian(b[at..]);

    private static uint U32(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt32BigEndian(b[at..]);

    private static void Append(List<byte> output, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            output.Add(b);
        }
    }
}
