using Health360.Domain.Ids;
using Health360.Domain.Nutrition;
using Health360.Domain.Units;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>One <c>nutrition_targets</c> row: how a profile's daily targets are set.</summary>
public sealed class NutritionTargetRow
{
    public string Id { get; set; } = "";

    public string Method { get; set; } = "";

    public double? EnergyAdjustmentKcal { get; set; }

    public string ProteinBasis { get; set; } = "";

    public double? ProteinRatio { get; set; }

    public string ProteinRatioUnit { get; set; } = "";

    public double? FatPercent { get; set; }

    public double? ManualEnergyKcal { get; set; }

    public double? ManualProteinG { get; set; }

    public double? ManualFatG { get; set; }

    public double? ManualCarbohydrateG { get; set; }

    /// <summary>
    /// The settings the row holds, or null when a synced row can't be read (an unknown method or unit, a missing
    /// value), so one bad row can't break the screen: the caller then treats the profile as having no targets saved.
    /// </summary>
    public TargetSettings? ToSettings()
    {
        TargetMethod? method = Method switch
        {
            NutritionTargetStore.FormulaMethod => TargetMethod.Formula,
            NutritionTargetStore.ManualMethod when ManualEnergyKcal is not null => TargetMethod.Manual,
            _ => null,
        };
        Domain.Nutrition.ProteinBasis? basis = ProteinBasis switch
        {
            NutritionTargetStore.BodyWeightBasis => Domain.Nutrition.ProteinBasis.BodyWeight,
            NutritionTargetStore.LeanMassBasis => Domain.Nutrition.ProteinBasis.LeanMass,
            _ => null,
        };
        if (method is null || basis is null || !UnitPrefOptions.ProteinRatio.TryParse(ProteinRatioUnit, out var unit)
            || EnergyAdjustmentKcal is not { } adjustment || ProteinRatio is not { } ratio || FatPercent is not { } fat)
        {
            return null;
        }

        return new TargetSettings(method.Value, adjustment, ratio, unit, basis.Value, fat, ManualEnergyKcal, ManualProteinG,
            ManualFatG, ManualCarbohydrateG);
    }
}

/// <summary>
/// The profile's targets settings (<c>nutrition_targets</c>, U) in the local database: at most one row per profile.
/// The row id is derived from the profile (<see cref="IdFor"/>), the same on every device and on the server, so
/// targets saved offline on two devices end as one row: the last upload wins.
/// </summary>
public static class NutritionTargetStore
{
    public const string FormulaMethod = "formula";
    public const string ManualMethod = "manual";
    public const string BodyWeightBasis = "body_weight";
    public const string LeanMassBasis = "lean_mass";

    private const string MineSql =
        "SELECT id AS Id, method AS Method, energy_adjustment_kcal AS EnergyAdjustmentKcal, protein_basis AS ProteinBasis, " +
        "protein_ratio AS ProteinRatio, protein_ratio_unit AS ProteinRatioUnit, fat_percent AS FatPercent, " +
        "manual_energy_kcal AS ManualEnergyKcal, manual_protein_g AS ManualProteinG, manual_fat_g AS ManualFatG, " +
        "manual_carbohydrate_g AS ManualCarbohydrateG FROM nutrition_targets WHERE id = ? AND profile_id = ?";

    /// <summary>The row id for a profile; <c>private.nutrition_target_id</c> on the server checks it.</summary>
    public static string IdFor(string profileId)
    {
        // Postgres' uuid::text: lowercase, hyphenated. Parsing first makes an upper-case id give the same name.
        var profile = Guid.Parse(profileId).ToString("D");
        return UuidV8.FromName($"health360/nutrition_targets/{profile}").ToString("D");
    }

    /// <summary>The profile's row (empty when none is saved), now and after every change.</summary>
    public static IAsyncEnumerable<NutritionTargetRow[]> Watch(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<NutritionTargetRow>(MineSql, [IdFor(profileId), profileId],
            new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<NutritionTargetRow?> GetAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetOptional<NutritionTargetRow>(MineSql, [IdFor(profileId), profileId]).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves the profile's targets, replacing what was saved. Throws <see cref="ArgumentException"/> for settings the
    /// server would refuse, so nothing is queued that can only be quarantined.
    /// </summary>
    public static async Task SaveAsync(IPowerSyncDatabase db, string profileId, TargetSettings settings, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        var perKg = Domain.Units.ProteinRatio.ToGPerKg(settings.ProteinRatio, settings.ProteinRatioUnit);
        if (!Fits(settings, perKg))
        {
            throw new ArgumentException("The targets are outside what can be stored.", nameof(settings));
        }

        var id = IdFor(profileId);
        var now = Timestamps.UtcNow(clock);
        var manual = settings.Method == TargetMethod.Manual;
        await db.WriteTransaction(async tx =>
        {
            var created = await tx.GetOptional<Created>("SELECT created_at AS CreatedAt FROM nutrition_targets WHERE id = ?", [id])
                .ConfigureAwait(false);
            // INSERT OR REPLACE queues one PUT of the whole row, whether or not the row exists, and the upload is an
            // upsert, with cleared manual values sent as null (AppSchema.NullableOnPut). So the last save wins whole:
            // not a delete and an insert, two requests a lost connection could split, leaving the server without
            // targets; and not an UPDATE, whose PATCH of only this device's changed columns would merge into what
            // another device saved meanwhile.
            await tx.Execute(
                "INSERT OR REPLACE INTO nutrition_targets (id, profile_id, method, energy_adjustment_kcal, protein_basis, " +
                "protein_ratio, protein_ratio_unit, protein_ratio_g_per_kg, fat_percent, manual_energy_kcal, manual_protein_g, " +
                "manual_fat_g, manual_carbohydrate_g, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                [
                    id, profileId, manual ? ManualMethod : FormulaMethod, settings.EnergyAdjustmentKcal,
                    settings.ProteinBasis == ProteinBasis.LeanMass ? LeanMassBasis : BodyWeightBasis, settings.ProteinRatio,
                    UnitPrefOptions.ProteinRatio.ToCode(settings.ProteinRatioUnit), perKg, settings.FatPercent, settings.ManualEnergyKcal,
                    settings.ManualProteinG, settings.ManualFatG, settings.ManualCarbohydrateG, created?.CreatedAt ?? now, now,
                ]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>The <c>nutrition_targets</c> checks, so a save the server would refuse fails here instead.</summary>
    private static bool Fits(TargetSettings s, double perKg)
    {
        static bool Grams(double? g) => g is null || (double.IsFinite(g.Value) && g.Value is >= 0 and <= 2000);
        return double.IsFinite(s.EnergyAdjustmentKcal) && s.EnergyAdjustmentKcal is >= -5000 and <= 5000
            && double.IsFinite(perKg) && perKg is > 0 and <= 10
            && double.IsFinite(s.FatPercent) && s.FatPercent is > 0 and < 100
            && (s.ManualEnergyKcal is null || (double.IsFinite(s.ManualEnergyKcal.Value) && s.ManualEnergyKcal.Value is > 0 and <= 20000))
            && (s.Method != TargetMethod.Manual || s.ManualEnergyKcal is not null)
            && Grams(s.ManualProteinG) && Grams(s.ManualFatG) && Grams(s.ManualCarbohydrateG);
    }

    internal sealed class Created
    {
        public string CreatedAt { get; set; } = "";
    }
}
