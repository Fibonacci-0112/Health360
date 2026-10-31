using System.Security.Cryptography;
using System.Text;

namespace Health360.Domain.Ids;

/// <summary>
/// A name-based UUIDv8 (RFC 9562 §5.8, with the SHA-256 construction of its Appendix B.2): the first 128 bits of
/// SHA-256 of the name's UTF-8 bytes, with the version and variant bits set. The same name always gives the same
/// id, so rows that must be unique per key (one pin per profile and metric) get the same id on every device and an
/// offline write from two of them upserts one row. Postgres derives the same value (private.metric_pin_id).
/// </summary>
public static class UuidV8
{
    public static Guid FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(name), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}
