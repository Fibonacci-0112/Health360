using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>What one online step of barcode lookup found.</summary>
public enum BarcodeStepStatus
{
    Found,

    /// <summary>The source answered and doesn't list the code (or lists it in a form the app can't use).</summary>
    NotFound,

    /// <summary>The source couldn't answer: offline, signed out, busy or failing. It may still list the code.</summary>
    Unavailable,
}

/// <summary>One online step's answer; <see cref="Match"/> is set only when <see cref="Status"/> is <see cref="BarcodeStepStatus.Found"/>.</summary>
public sealed record BarcodeStepResult(BarcodeStepStatus Status, BarcodeMatch? Match)
{
    public static BarcodeStepResult Found(BarcodeMatch match) => new(BarcodeStepStatus.Found, match ?? throw new ArgumentNullException(nameof(match)));

    public static BarcodeStepResult NotFound { get; } = new(BarcodeStepStatus.NotFound, null);

    public static BarcodeStepResult Unavailable { get; } = new(BarcodeStepStatus.Unavailable, null);
}

/// <summary>
/// An online step of barcode lookup: one of the API's <c>/foods/barcode/{code}/…</c> endpoints. Given the 14-digit
/// form of a code that has already passed <see cref="Gtin.Normalize"/>.
/// </summary>
public interface IBarcodeOnlineSource
{
    BarcodeSource Source { get; }

    Task<BarcodeStepResult> FindAsync(string gtin, CancellationToken cancellationToken);
}

/// <summary>Where barcode lookup stopped.</summary>
public enum BarcodeLookupStatus
{
    /// <summary>Not 8, 12, 13 or 14 digits, or the check digit is wrong: a misread scan or a typo.</summary>
    InvalidCode,

    /// <summary>One or more of the household's own foods carry the code (<see cref="BarcodeLookupResult.HouseholdFoods"/>).</summary>
    Household,

    /// <summary>An online source lists it (<see cref="BarcodeLookupResult.Match"/>).</summary>
    Online,

    /// <summary>
    /// Nothing found: the app offers to add the food from its label. <see cref="BarcodeLookupResult.Unreached"/>
    /// names the sources that couldn't be asked.
    /// </summary>
    NotFound,
}

/// <summary>The outcome of <see cref="BarcodeLookup.LookUpAsync"/>.</summary>
/// <param name="Status">Where the lookup stopped.</param>
/// <param name="Gtin">The code's 14-digit form; null for <see cref="BarcodeLookupStatus.InvalidCode"/>.</param>
/// <param name="HouseholdFoods">The household's foods with the code, by name; empty unless <see cref="BarcodeLookupStatus.Household"/>.</param>
/// <param name="Match">The online match; null unless <see cref="BarcodeLookupStatus.Online"/>.</param>
/// <param name="Unreached">The online sources asked that couldn't answer, in the order they were asked.</param>
/// <param name="OnlineSources">How many online sources the app has; with none, only the household's foods were searched.</param>
public sealed record BarcodeLookupResult(
    BarcodeLookupStatus Status,
    string? Gtin,
    IReadOnlyList<FoodRow> HouseholdFoods,
    BarcodeMatch? Match,
    IReadOnlyList<BarcodeSource> Unreached,
    int OnlineSources);

/// <summary>
/// The on-device barcode lookup chain (docs/FEATURES.md, Nutrition): the household's own foods first, which are on the
/// device and work offline; then Open Food Facts; then USDA branded foods by GTIN; and when nothing matches, the app
/// offers to add the food from its label. Each step runs only when the ones before it found nothing, and an online
/// step that can't answer doesn't stop the next one being asked.
/// </summary>
/// <param name="db">The local database, whose household foods are searched first.</param>
/// <param name="sources">
/// The online steps, asked in <see cref="Order"/> whatever order they are given in; enumerated on every lookup.
/// </param>
public sealed class BarcodeLookup(IPowerSyncDatabase db, IEnumerable<IBarcodeOnlineSource> sources)
{
    /// <summary>The order the online sources are asked in.</summary>
    public static IReadOnlyList<BarcodeSource> Order { get; } = [BarcodeSource.OpenFoodFacts, BarcodeSource.UsdaBranded];

    /// <summary>The most household foods returned for one code.</summary>
    public const int HouseholdLimit = 20;

    /// <summary>
    /// Looks <paramref name="code"/> up. A failure to read the local database is thrown (it is not "not found"); an
    /// online source that throws counts as unavailable, unless <paramref name="cancellationToken"/> was cancelled.
    /// </summary>
    public async Task<BarcodeLookupResult> LookUpAsync(string? code, CancellationToken cancellationToken = default)
    {
        if (Gtin.Normalize(code) is not { } gtin)
        {
            return new BarcodeLookupResult(BarcodeLookupStatus.InvalidCode, null, [], null, [], 0);
        }

        var household = await FoodLibrary.FindByBarcodeAsync(db, gtin, HouseholdLimit).ConfigureAwait(false);
        var online = sources
            .Select((source, index) => (source, index))
            .OrderBy(s => Rank(s.source.Source))
            .ThenBy(s => s.index)
            .Select(s => s.source)
            .ToList();
        if (household.Length > 0)
        {
            return new BarcodeLookupResult(BarcodeLookupStatus.Household, gtin, household, null, [], online.Count);
        }

        var unreached = new List<BarcodeSource>();
        foreach (var source in online)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BarcodeStepResult step;
            try
            {
                step = await source.FindAsync(gtin, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                step = BarcodeStepResult.Unavailable;
            }

            switch (step.Status)
            {
                case BarcodeStepStatus.Found when step.Match is not null:
                    return new BarcodeLookupResult(BarcodeLookupStatus.Online, gtin, [], step.Match, unreached, online.Count);
                case BarcodeStepStatus.Unavailable:
                    unreached.Add(source.Source);
                    break;
                default:
                    break;
            }
        }

        return new BarcodeLookupResult(BarcodeLookupStatus.NotFound, gtin, [], null, unreached, online.Count);
    }

    private static int Rank(BarcodeSource source)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == source)
            {
                return i;
            }
        }

        return Order.Count;
    }
}
