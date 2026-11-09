using FluentValidation;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>
/// The on-device barcode chain: the household's own foods first, then the online sources in their fixed order, then
/// nothing (the app offers to add the food from its label).
/// </summary>
public sealed class BarcodeLookupTests
{
    private const string Profile = "10000000-0000-7000-8000-000000000001";
    private const string Household = "20000000-0000-7000-8000-000000000001";
    private const string OtherHousehold = "20000000-0000-7000-8000-000000000002";
    private const string Upc = "036000291452";
    private const string Ean13 = "0036000291452";
    private const string Gtin14 = "00036000291452";
    private const string OtherCode = "4006381333931";
    private static readonly DateOnly Day = new(2026, 11, 9);
    private readonly ZonedClock clock = new(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc, TimeSpan.FromSeconds(1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FoodInput Cereal(string? barcode, string name = "Cereal") =>
        new(name, "Oat Co", FoodBasis.Mass, 100, 380, 10, 70, 6, []) { Barcode = barcode };

    private static BarcodeMatch Match(BarcodeSource source) =>
        new(source, "ext-1", Gtin14, "Online cereal", null, FoodBasis.Mass, 370, 9, 72, 5, null);

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Cabin')", [OtherHousehold]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('m1', ?, ?)", [Household, Profile]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('m2', ?, ?)", [OtherHousehold, Profile]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    [Fact]
    public async Task A_household_food_is_found_by_its_barcode_in_any_GTIN_length_and_the_barcode_uploads_as_entered()
    {
        await using var t = await OpenAsync();
        var cereal = await FoodLibrary.AddAsync(t.Db, Household, Cereal($" {Upc} "), clock);
        await FoodLibrary.AddAsync(t.Db, Household, Cereal(OtherCode, "Muesli"), clock);
        await FoodLibrary.AddAsync(t.Db, Household, Cereal(null, "Plain oats"), clock);

        await t.ReopenAsync();
        foreach (var code in new[] { Upc, Ean13, Gtin14 })
        {
            Assert.Equal([cereal], (await FoodLibrary.FindByBarcodeAsync(t.Db, code, 10)).Select(f => f.Id));
        }

        Assert.Equal(Upc, (await FoodLibrary.GetAsync(t.Db, cereal))!.Barcode);
        Assert.Empty(await FoodLibrary.FindByBarcodeAsync(t.Db, "036000291453", 10)); // wrong check digit
        Assert.Empty(await FoodLibrary.FindByBarcodeAsync(t.Db, "96385074", 10));
        Assert.Empty(await FoodLibrary.FindByBarcodeAsync(t.Db, Upc, 10, OtherHousehold));

        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        var upload = Assert.Single(writer.Writes, w => w.Table == "foods" && w.Id == cereal);
        Assert.Equal(Upc, upload.Data!["barcode"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_barcode_that_does_not_read_as_a_GTIN_is_refused_and_nothing_is_written()
    {
        await using var t = await OpenAsync();

        foreach (var code in new[] { "036000291453", "12345", "", "03600029145a" })
        {
            await Assert.ThrowsAsync<ValidationException>(() => FoodLibrary.AddAsync(t.Db, Household, Cereal(code), clock));
        }

        Assert.Empty(await FoodLibrary.SearchAsync(t.Db, "", 10));
    }

    [Fact]
    public async Task The_household_comes_first_and_no_online_source_is_asked()
    {
        await using var t = await OpenAsync();
        var a = await FoodLibrary.AddAsync(t.Db, Household, Cereal(Upc, "B cereal"), clock);
        var b = await FoodLibrary.AddAsync(t.Db, OtherHousehold, Cereal(Ean13, "A cereal"), clock);
        var off = new FakeSource(BarcodeSource.OpenFoodFacts, BarcodeStepResult.Found(Match(BarcodeSource.OpenFoodFacts)));

        var found = await new BarcodeLookup(t.Db, [off]).LookUpAsync(Gtin14, Ct);

        Assert.Equal((BarcodeLookupStatus.Household, Gtin14, 1), (found.Status, found.Gtin, found.OnlineSources));
        Assert.Equal([b, a], found.HouseholdFoods.Select(f => f.Id));
        Assert.Empty(off.Asked);
    }

    [Fact]
    public async Task Open_Food_Facts_is_asked_before_USDA_and_a_source_that_cannot_answer_does_not_stop_the_next()
    {
        await using var t = await OpenAsync();
        var usda = new FakeSource(BarcodeSource.UsdaBranded, BarcodeStepResult.Found(Match(BarcodeSource.UsdaBranded)));
        var off = new FakeSource(BarcodeSource.OpenFoodFacts, BarcodeStepResult.Unavailable);
        var asked = new List<BarcodeSource>();
        usda.Log = off.Log = asked;

        // Given USDA first: the order is the chain's, not the registration's.
        var found = await new BarcodeLookup(t.Db, [usda, off]).LookUpAsync(Upc, Ct);

        Assert.Equal([BarcodeSource.OpenFoodFacts, BarcodeSource.UsdaBranded], asked);
        Assert.Equal([Gtin14], off.Asked);
        Assert.Equal((BarcodeLookupStatus.Online, BarcodeSource.UsdaBranded), (found.Status, found.Match!.Source));
        Assert.Equal([BarcodeSource.OpenFoodFacts], found.Unreached);
    }

    [Fact]
    public async Task A_match_from_Open_Food_Facts_ends_the_chain()
    {
        await using var t = await OpenAsync();
        var off = new FakeSource(BarcodeSource.OpenFoodFacts, BarcodeStepResult.Found(Match(BarcodeSource.OpenFoodFacts)));
        var usda = new FakeSource(BarcodeSource.UsdaBranded, BarcodeStepResult.NotFound);

        var found = await new BarcodeLookup(t.Db, [off, usda]).LookUpAsync(Ean13, Ct);

        Assert.Equal((BarcodeLookupStatus.Online, "Online cereal"), (found.Status, found.Match!.Name));
        Assert.Empty(found.Unreached);
        Assert.Empty(usda.Asked);
    }

    [Fact]
    public async Task Nothing_found_says_which_sources_could_not_answer_and_a_throwing_source_counts_as_one()
    {
        await using var t = await OpenAsync();
        var off = new FakeSource(BarcodeSource.OpenFoodFacts, BarcodeStepResult.NotFound);
        var usda = new FakeSource(BarcodeSource.UsdaBranded, null) { Throws = new HttpRequestException("offline") };

        var found = await new BarcodeLookup(t.Db, [off, usda]).LookUpAsync(Upc, Ct);

        Assert.Equal((BarcodeLookupStatus.NotFound, Gtin14, 2), (found.Status, found.Gtin, found.OnlineSources));
        Assert.Equal([BarcodeSource.UsdaBranded], found.Unreached);
        Assert.Null(found.Match);

        // With no online source (the app as it ships now) only the household's foods are searched.
        var local = await new BarcodeLookup(t.Db, []).LookUpAsync(Upc, Ct);
        Assert.Equal((BarcodeLookupStatus.NotFound, 0), (local.Status, local.OnlineSources));
        Assert.Empty(local.Unreached);
    }

    [Fact]
    public async Task A_misread_code_asks_nobody_and_a_cancelled_lookup_is_not_reported_as_unavailable()
    {
        await using var t = await OpenAsync();
        var off = new FakeSource(BarcodeSource.OpenFoodFacts, BarcodeStepResult.NotFound);
        var lookup = new BarcodeLookup(t.Db, [off]);

        foreach (var code in new[] { "036000291453", "", null, "abc" })
        {
            Assert.Equal(BarcodeLookupStatus.InvalidCode, (await lookup.LookUpAsync(code, Ct)).Status);
        }

        Assert.Empty(off.Asked);

        using var cancel = new CancellationTokenSource();
        var cancelling = new FakeSource(BarcodeSource.OpenFoodFacts, null) { OnAsk = cancel.Cancel, Throws = new OperationCanceledException() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BarcodeLookup(t.Db, [cancelling]).LookUpAsync(Upc, cancel.Token));
    }

    [Fact]
    public async Task A_food_found_by_barcode_is_logged_as_a_barcode_entry()
    {
        await using var t = await OpenAsync();
        var cereal = await FoodLibrary.AddAsync(t.Db, Household, Cereal(Upc), clock);

        var entry = await FoodLog.LogFoodAsync(t.Db, Profile, cereal, "breakfast", Day, 40, "g", null, clock, FoodLog.BarcodeEntry);

        Assert.Equal((entry, "barcode"), (await FoodLog.DayAsync(t.Db, Profile, Day)).Select(e => (e.Id, e.EntryMethod)).Single());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            FoodLog.LogFoodAsync(t.Db, Profile, cereal, "breakfast", Day, 40, "g", null, clock, "quick_add"));
    }

    /// <summary>A UTC clock that moves a second on at each read, so writes made one after another get distinct times.</summary>
    private sealed class ZonedClock(DateTimeOffset start, TimeZoneInfo zone, TimeSpan step) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = now;
            now += step;
            return current;
        }

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class FakeSource(BarcodeSource source, BarcodeStepResult? answer) : IBarcodeOnlineSource
    {
        public BarcodeSource Source => source;

        public List<string> Asked { get; } = [];

        public List<BarcodeSource>? Log { get; set; }

        public Exception? Throws { get; init; }

        public Action? OnAsk { get; init; }

        public Task<BarcodeStepResult> FindAsync(string gtin, CancellationToken cancellationToken)
        {
            Asked.Add(gtin);
            Log?.Add(source);
            OnAsk?.Invoke();
            return Throws is not null ? Task.FromException<BarcodeStepResult>(Throws) : Task.FromResult(answer!);
        }
    }
}
