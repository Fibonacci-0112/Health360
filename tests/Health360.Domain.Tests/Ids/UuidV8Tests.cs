using System.Text.RegularExpressions;
using FsCheck.Fluent;
using Health360.Domain.Ids;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Ids;

public partial class UuidV8Tests
{
    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-8[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex V8();

    [Theory]
    // SHA-256("abc") starts ba7816bf 8f01cfea 414140de 5dae2223 (FIPS 180-2 example); the version and variant bits
    // change bytes 6 and 8.
    [InlineData("abc", "ba7816bf-8f01-8fea-8141-40de5dae2223")]
    // The pin id private.metric_pin_id derives in supabase/tests/database/05_metric_pins.test.sql.
    [InlineData("health360/metric_pins/0190a0b0-0000-7000-8000-000000000001/max_hr", "6a8b1fc1-d36f-8c03-ba62-7ced888bd0da")]
    public void Matches_golden_values(string name, string expected) =>
        Assert.Equal(Guid.Parse(expected), UuidV8.FromName(name));

    [Fact]
    public void Is_a_deterministic_v8_with_the_RFC_9562_variant()
    {
        Holds(Gen.Elements("", "a", "max_hr", "ünïcödé", new string('x', 1000)), name =>
        {
            var id = UuidV8.FromName(name);
            return V8().IsMatch(id.ToString()) && id.Version == 8 && id == UuidV8.FromName(name);
        });
    }

    [Fact]
    public void Different_names_give_different_ids() =>
        Assert.NotEqual(UuidV8.FromName("health360/metric_pins/p/max_hr"), UuidV8.FromName("health360/metric_pins/p/rmr"));
}
