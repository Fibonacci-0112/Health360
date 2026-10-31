using Health360.Domain.Units;
using Npgsql;

namespace Health360.Server.Tests;

/// <summary>
/// The database recomputes canonical values on write (PLAN.md §2.6), so its conversion must give the same
/// doubles as <c>Health360.Domain/Units</c>, bit for bit, or a value would change when it syncs back. Runs
/// against a migrated database from <c>HEALTH360_TEST_DATABASE</c>; skipped without it.
/// </summary>
[Trait("Category", "Database")]
public sealed class CanonicalUnitParityTests
{
    private const string EnvironmentVariable = "HEALTH360_TEST_DATABASE";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Hand-picked values (typical entries, zero, negatives, extremes) plus seeded random ones.</summary>
    private static double[] Values()
    {
        var random = new Random(20261031);
        double[] fixedValues =
            [0, 1, -1, 0.1, 0.3, 1.5, 2.54, 12, 34.5, 70, 98.6, 99, 100, 180, 225, 420, 1720, 7200, 1e-9, 1e9, -40, -273.15];
        return [.. fixedValues, .. Enumerable.Range(0, 200).Select(_ => Math.Round((random.NextDouble() * 1000) - 100, random.Next(0, 6)))];
    }

    [Fact]
    public async Task EveryUnitPairConvertsExactlyAsDomainDoes()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        var cases = (
            from source in Unit.All
            from target in Unit.OfDimension(source.Dimension)
            from value in Values()
            select (Value: value, From: source.Code, To: target.Code, Expected: Conversion.Convert(value, source, target))).ToList();

        await AssertSameAsync(cases);
    }

    [Fact]
    public async Task PaceConvertsExactlyAsDomainDoes()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        var cases = Values().SelectMany(v => new[]
        {
            (Value: v, From: "s_per_mi", To: "s_per_km", Expected: Pace.ToSecondsPerKm(v, PaceUnit.MinutesPerMile)),
            (Value: v, From: "s_per_km", To: "s_per_mi", Expected: Pace.FromSecondsPerKm(v, PaceUnit.MinutesPerMile)),
            (Value: v, From: "s_per_km", To: "s_per_km", Expected: Pace.ToSecondsPerKm(v, PaceUnit.MinutesPerKilometre)),
        }).ToList();

        await AssertSameAsync(cases);
    }

    /// <summary>
    /// <c>profiles.height_cm</c> comes from <c>private.length_to_cm</c>; the app computes it with
    /// <see cref="QuantityKinds.ToCanonical"/>. They once differed in the last bit for many inch entries.
    /// </summary>
    [Fact]
    public async Task HeightConvertsExactlyAsDomainDoes()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        var heightsIn = Enumerable.Range(200, 1000).Select(i => i / 10.0);
        var cases = (
            from unit in Unit.OfDimension(Dimension.Length)
            from value in Values().Concat(heightsIn)
            select (Value: value, From: unit.Code, To: "cm", Expected: QuantityKinds.ToCanonical(QuantityKind.Height, value, unit))).ToList();

        await AssertSameAsync(cases, "select private.length_to_cm(c.value, c.from_unit)");
    }

    [Fact]
    public async Task UnitsOfDifferentDimensionsDoNotConvert()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select private.convert_unit(1, 'kg', 'cm'), private.convert_unit(1, 'bpm', 'W'), private.convert_unit(1, 'C', 'kg'), private.convert_unit(1, 'nope', 'kg')",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(reader.IsDBNull(i), $"column {i}"));
    }

    /// <summary>
    /// Every entry unit a metric allows converts to its canonical unit; a metric measured in a Domain unit
    /// uses the canonical unit Domain uses for that dimension.
    /// </summary>
    [Fact]
    public async Task MetricDefinitionsUseTheAppsUnitCodes()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        var domainCanonical = new Dictionary<Dimension, string>
        {
            [Dimension.Mass] = QuantityKinds.CanonicalUnit(QuantityKind.BodyWeight).Code,
            [Dimension.Length] = QuantityKinds.CanonicalUnit(QuantityKind.Circumference).Code,
            [Dimension.Energy] = QuantityKinds.CanonicalUnit(QuantityKind.Energy).Code,
            [Dimension.Temperature] = QuantityKinds.CanonicalUnit(QuantityKind.Temperature).Code,
            [Dimension.Glucose] = QuantityKinds.CanonicalUnit(QuantityKind.Glucose).Code,
        };

        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select d.code, u.unit, d.canonical_unit, private.convert_unit(1, u.unit, d.canonical_unit) is not null
            from public.metric_definitions d, jsonb_array_elements_text(d.units) as u (unit)
            order by d.code, u.unit
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = 0;
        while (await reader.ReadAsync(Ct))
        {
            rows++;
            var (metric, unit, canonical, converts) = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
            Assert.True(converts, $"{metric}: {unit} does not convert to {canonical}");
            if (Unit.TryParse(unit, out var domainUnit))
            {
                Assert.Equal(domainCanonical[domainUnit.Dimension], canonical);
            }
        }

        Assert.True(rows > 0, "metric_definitions is empty");
    }

    /// <summary>
    /// The app writes <c>measurements.value_canonical</c> with <see cref="UnitCodes.Convert"/> and the trigger
    /// recomputes it with <c>private.convert_unit</c>: for every entry unit of every metric, the two must agree, or a
    /// value would change when it syncs back.
    /// </summary>
    [Fact]
    public async Task MetricEntryUnitsConvertToCanonicalExactlyAsTheAppDoes()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");

        var pairs = new List<(string Unit, string Canonical)>();
        await using (var connection = await OpenAsync())
        await using (var command = new NpgsqlCommand(
            "select distinct u.unit, d.canonical_unit from public.metric_definitions d, jsonb_array_elements_text(d.units) as u (unit)",
            connection))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                pairs.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.NotEmpty(pairs);
        var cases = (
            from pair in pairs
            from value in Values()
            select (Value: value, From: pair.Unit, To: pair.Canonical,
                Expected: UnitCodes.Convert(value, pair.Unit, pair.Canonical) ?? double.NaN)).ToList();

        await AssertSameAsync(cases);
    }

    private static async Task AssertSameAsync(
        List<(double Value, string From, string To, double Expected)> cases,
        string select = "select private.convert_unit(c.value, c.from_unit, c.to_unit)")
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            {select}
            from unnest($1::double precision[], $2::text[], $3::text[]) with ordinality as c (value, from_unit, to_unit, n)
            order by c.n
            """,
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = cases.Select(c => c.Value).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = cases.Select(c => c.From).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = cases.Select(c => c.To).ToArray() });

        var mismatches = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        foreach (var c in cases)
        {
            Assert.True(await reader.ReadAsync(Ct));
            var actual = reader.IsDBNull(0) ? double.NaN : reader.GetDouble(0);
            if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(c.Expected))
            {
                mismatches.Add($"{c.Value:R} {c.From} → {c.To}: database {actual:R}, Domain {c.Expected:R}");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    private static async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        return connection;
    }
}
