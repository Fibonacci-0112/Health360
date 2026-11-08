using Health360.Domain.Nutrition;
using Health360.Domain.Units;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

/// <summary>Targets settings saved and replaced in a real PowerSync database, and what they upload.</summary>
public class NutritionTargetStoreTests
{
    private const string Profile = "0190a0b0-0000-7000-8000-000000000001";
    private const string Other = "0190a0b0-0000-7000-8000-000000000002";

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private static async Task<List<FakeWriter.Write>> UploadAsync(TestDatabase t)
    {
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, Clock, cancellationToken: TestContext.Current.CancellationToken);
        return writer.Writes;
    }

    [Fact]
    public void TheIdIsDerivedFromTheProfileAsTheServerDerivesIt()
    {
        // The golden value supabase/tests/database/12_nutrition_targets.test.sql checks private.nutrition_target_id against.
        Assert.Equal("9f85d5f3-b85c-8b18-aa4b-9ea16e5769d0", NutritionTargetStore.IdFor(Profile));
        Assert.Equal(NutritionTargetStore.IdFor(Profile), NutritionTargetStore.IdFor(Profile.ToUpperInvariant()));
        Assert.NotEqual(NutritionTargetStore.IdFor(Profile), NutritionTargetStore.IdFor(Other));
    }

    [Fact]
    public async Task SavingStoresOneRowThatReadsBackAndUploadsAsAnUpsertWithTheCanonicalRatio()
    {
        await using var t = await TestDatabase.OpenAsync();
        Assert.Null(await NutritionTargetStore.GetAsync(t.Db, Profile));

        var settings = TargetSettings.Default with
        {
            EnergyAdjustmentKcal = -500,
            ProteinRatio = 0.8,
            ProteinRatioUnit = ProteinRatioUnit.GramsPerPound,
            ProteinBasis = ProteinBasis.LeanMass,
        };
        await NutritionTargetStore.SaveAsync(t.Db, Profile, settings, Clock);

        var row = await NutritionTargetStore.GetAsync(t.Db, Profile);
        Assert.Equal(settings, row!.ToSettings());

        var put = Assert.Single(await UploadAsync(t));
        Assert.Equal(("PUT", "nutrition_targets", NutritionTargetStore.IdFor(Profile)), (put.Op, put.Table, put.Id));
        Assert.Equal(
            (Profile, "formula", "lean_mass", "g_per_lb", 0.8 / 0.45359237),
            (put.Data!["profile_id"]!.GetValue<string>(), put.Data["method"]!.GetValue<string>(),
             put.Data["protein_basis"]!.GetValue<string>(), put.Data["protein_ratio_unit"]!.GetValue<string>(),
             put.Data["protein_ratio_g_per_kg"]!.GetValue<double>()));
    }

    [Fact]
    public async Task SavingAgainUpdatesTheRowInOnePatchThatSendsClearedManualValues()
    {
        await using var t = await TestDatabase.OpenAsync();
        var manual = TargetSettings.Default with { Method = TargetMethod.Manual, ManualEnergyKcal = 2400, ManualProteinG = 150 };
        await NutritionTargetStore.SaveAsync(t.Db, Profile, manual, Clock);
        await UploadAsync(t);
        var created = await t.Db.Get<Stamp>("SELECT created_at AS CreatedAt FROM nutrition_targets");

        var back = manual with { Method = TargetMethod.Formula, ManualEnergyKcal = null, ManualProteinG = null };
        await NutritionTargetStore.SaveAsync(t.Db, Profile, back, Clock);

        Assert.Equal(back, (await NutritionTargetStore.GetAsync(t.Db, Profile))!.ToSettings());
        Assert.Single(await t.Db.GetAll<Stamp>("SELECT created_at AS CreatedAt FROM nutrition_targets"));
        Assert.Equal(created.CreatedAt, (await t.Db.Get<Stamp>("SELECT created_at AS CreatedAt FROM nutrition_targets")).CreatedAt);

        // One request, never a DELETE: a connection lost between a delete and a re-insert would leave the server
        // without targets until the retry.
        var patch = Assert.Single(await UploadAsync(t));
        Assert.Equal(("PATCH", "nutrition_targets", NutritionTargetStore.IdFor(Profile)), (patch.Op, patch.Table, patch.Id));
        Assert.Equal("formula", patch.Data!["method"]!.GetValue<string>());
        foreach (var column in new[] { "manual_energy_kcal", "manual_protein_g" })
        {
            Assert.True(patch.Data.ContainsKey(column), column + " must be sent: " + patch.Data.ToJsonString());
            Assert.Null(patch.Data[column]);
        }
    }

    [Fact]
    public async Task SettingsTheServerWouldRefuseAreNotQueued()
    {
        await using var t = await TestDatabase.OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => NutritionTargetStore.SaveAsync(t.Db, Profile,
            TargetSettings.Default with { Method = TargetMethod.Manual }, Clock));
        await Assert.ThrowsAsync<ArgumentException>(() => NutritionTargetStore.SaveAsync(t.Db, Profile,
            TargetSettings.Default with { ProteinRatio = 5, ProteinRatioUnit = ProteinRatioUnit.GramsPerPound }, Clock));
        await Assert.ThrowsAsync<ArgumentException>(() => NutritionTargetStore.SaveAsync(t.Db, Profile,
            TargetSettings.Default with { FatPercent = 100 }, Clock));
        await Assert.ThrowsAsync<ArgumentException>(() => NutritionTargetStore.SaveAsync(t.Db, Profile,
            TargetSettings.Default with { EnergyAdjustmentKcal = double.NaN }, Clock));

        Assert.Null(await NutritionTargetStore.GetAsync(t.Db, Profile));
        Assert.Empty(await UploadAsync(t));
    }

    [Fact]
    public async Task AnUnreadableSyncedRowReadsAsNoSettingsAndAnotherProfilesRowIsNotRead()
    {
        await using var t = await TestDatabase.OpenAsync();
        await t.Db.Execute(
            "INSERT INTO nutrition_targets (id, profile_id, method, energy_adjustment_kcal, protein_basis, protein_ratio, " +
            "protein_ratio_unit, fat_percent) VALUES (?, ?, 'adaptive', 0, 'body_weight', 1.6, 'g_per_kg', 30)",
            [NutritionTargetStore.IdFor(Profile), Profile]);
        await NutritionTargetStore.SaveAsync(t.Db, Other, TargetSettings.Default, Clock);

        Assert.Null((await NutritionTargetStore.GetAsync(t.Db, Profile))!.ToSettings());
        Assert.Equal(TargetSettings.Default, (await NutritionTargetStore.GetAsync(t.Db, Other))!.ToSettings());
    }

    private sealed class Stamp
    {
        public string CreatedAt { get; set; } = "";
    }
}
