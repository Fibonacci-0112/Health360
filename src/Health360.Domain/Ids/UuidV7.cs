using System.Buffers.Binary;

namespace Health360.Domain.Ids;

/// <summary>Fills a buffer with random bytes. Production callers pass a cryptographically secure source.</summary>
public delegate void RandomFill(Span<byte> buffer);

/// <summary>
/// UUIDv7 (RFC 9562): a 48-bit Unix-millisecond timestamp, then random bits. Ids are generated on the
/// client so offline rows have their final id, and v7 keeps them roughly time-ordered in indexes.
/// The clock and randomness are passed in so this stays pure; each platform supplies its own secure source
/// (<c>RandomNumberGenerator.Fill</c> in .NET).
/// </summary>
public static class UuidV7
{
    private const long MaxTimestamp = (1L << 48) - 1;

    public static Guid Create(TimeProvider clock, RandomFill fillRandom)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Create(clock.GetUtcNow().ToUnixTimeMilliseconds(), fillRandom);
    }

    public static Guid Create(long unixTimeMilliseconds, RandomFill fillRandom)
    {
        ArgumentNullException.ThrowIfNull(fillRandom);
        ArgumentOutOfRangeException.ThrowIfNegative(unixTimeMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unixTimeMilliseconds, MaxTimestamp);

        Span<byte> bytes = stackalloc byte[16];
        fillRandom(bytes[6..]);
        // 48-bit big-endian timestamp in bytes 0-5: write 64 bits, then drop the top two (zero) bytes.
        Span<byte> ms = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(ms, unixTimeMilliseconds);
        ms[2..].CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>The creation time encoded in a UUIDv7, in Unix milliseconds.</summary>
    public static long TimestampOf(Guid id)
    {
        if (id.Version != 7)
        {
            throw new ArgumentException($"Not a UUIDv7 (version {id.Version})", nameof(id));
        }

        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        Span<byte> ms = stackalloc byte[8];
        bytes[..6].CopyTo(ms[2..]);
        return BinaryPrimitives.ReadInt64BigEndian(ms);
    }
}
