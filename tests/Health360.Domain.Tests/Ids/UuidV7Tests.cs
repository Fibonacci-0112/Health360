using System.Text.RegularExpressions;
using FsCheck.Fluent;
using Health360.Domain.Ids;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Ids;

public partial class UuidV7Tests
{
    private const long MaxTimestamp = (1L << 48) - 1;

    // Test randomness only; the app passes RandomNumberGenerator.Fill.
    private static readonly RandomFill TestRandom = buffer => Random.Shared.NextBytes(buffer);

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex V7();

    /// <summary>Timestamps across the whole 48-bit range, including both ends.</summary>
    private static readonly FsCheck.Gen<long> Timestamps = Gen.Frequency(
        (8, Gen.Zip(Int(0, 0xffff), Int(int.MinValue, int.MaxValue)).Select(t => ((long)t.Item1 << 32) | (uint)t.Item2)),
        (1, Gen.Elements(0L, MaxTimestamp)));

    [Fact]
    public void Has_the_v7_version_and_RFC_9562_variant_and_encodes_its_timestamp()
    {
        Holds(Timestamps, ms =>
        {
            var id = UuidV7.Create(ms, TestRandom);
            return V7().IsMatch(id.ToString()) && id.Version == 7 && UuidV7.TimestampOf(id) == ms;
        });
    }

    [Fact]
    public void Sorts_by_creation_time()
    {
        Holds(Gen.Zip(Timestamps, Timestamps).Where(t => t.Item1 < t.Item2), t =>
        {
            var a = UuidV7.Create(t.Item1, TestRandom);
            var b = UuidV7.Create(t.Item2, TestRandom);
            return string.CompareOrdinal(a.ToString(), b.ToString()) < 0 && a.CompareTo(b) < 0;
        });
    }

    [Fact]
    public void Matches_a_fixed_vector()
    {
        var id = UuidV7.Create(0x017f22e279b0, buffer => buffer.Clear());

        Assert.Equal("017f22e2-79b0-7000-8000-000000000000", id.ToString());
    }

    [Fact]
    public void Keeps_the_random_bits_outside_the_version_and_variant()
    {
        var id = UuidV7.Create(0, buffer => buffer.Fill(0xff));

        Assert.Equal("00000000-0000-7fff-bfff-ffffffffffff", id.ToString());
    }

    [Fact]
    public void Reads_the_time_from_a_TimeProvider()
    {
        var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

        var id = UuidV7.Create(new FixedClock(now), TestRandom);

        Assert.Equal(now.ToUnixTimeMilliseconds(), UuidV7.TimestampOf(id));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(MaxTimestamp + 1)]
    public void Rejects_timestamps_outside_48_bits(long ms)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UuidV7.Create(ms, TestRandom));
    }

    [Fact]
    public void Rejects_reading_the_time_from_another_version()
    {
        Assert.Throws<ArgumentException>(() => UuidV7.TimestampOf(Guid.Empty));
        Assert.Throws<ArgumentException>(() => UuidV7.TimestampOf(Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479")));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
