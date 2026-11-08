using Health360.Contracts.Nutrition;
using Health360.Domain.Units;
using Health360.LocalDb;
using PowerSync.Common.Client;

namespace Health360.UI.Data;

/// <summary>
/// The Food tab's access to the signed-in user's food log and the household's foods in the local database. Every
/// write is local first and uploads when online, so a whole day can be logged offline.
/// </summary>
public sealed class FoodService(PowerSyncDatabase db, ProfileService profiles, TimeProvider clock)
{
    public string? ProfileId => profiles.Profile?.Id;

    /// <summary>Today on this device.</summary>
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>The hour on this device, for the meal a new entry starts in.</summary>
    public int Hour => clock.GetLocalNow().Hour;

    public UnitPrefs UnitPrefs => profiles.UnitPrefs;

    public IAsyncEnumerable<FoodLogEntryRow[]> WatchDay(DateOnly date, CancellationToken cancellationToken) =>
        FoodLog.WatchDay(db, RequireProfile(), date, cancellationToken);

    /// <summary>Fires now and whenever the households or their foods change (<see cref="FoodLibrary.WatchChoices"/>).</summary>
    public IAsyncEnumerable<WorkoutLog.ChangeStamp[]> WatchChoices(CancellationToken cancellationToken) =>
        FoodLibrary.WatchChoices(db, RequireProfile(), cancellationToken);

    public Task<FoodRow[]> SearchAsync(string? query, string? householdId = null) => FoodLibrary.SearchAsync(db, query, 20, householdId);

    public Task<FoodRow[]> RecentAsync() => FoodLibrary.RecentAsync(db, RequireProfile(), 8);

    public Task<FoodRow?> FoodAsync(string id) => FoodLibrary.GetAsync(db, id);

    public Task<string> LogAsync(string foodId, string meal, DateOnly date, double quantity, string? unit, string? portionName) =>
        FoodLog.LogFoodAsync(db, RequireProfile(), foodId, meal, date, quantity, unit, portionName, clock);

    public Task<string> QuickAddAsync(string meal, DateOnly date, string? name, double? energy, double? protein, double? carbohydrate, double? fat) =>
        FoodLog.QuickAddAsync(db, RequireProfile(), meal, date, name, energy, protein, carbohydrate, fat, clock);

    public Task<RecipeRow[]> SearchRecipesAsync(string? query) => Recipes.SearchAsync(db, query, 20);

    /// <summary>Every household recipe, for the recipe list.</summary>
    public Task<RecipeRow[]> AllRecipesAsync() => Recipes.SearchAsync(db, null, -1);

    public Task<RecipeDetail?> RecipeAsync(string id) => Recipes.GetAsync(db, id);

    public Task<string> CreateRecipeAsync(string householdId, RecipeInput input) => Recipes.CreateAsync(db, householdId, input, clock);

    public Task UpdateRecipeAsync(string id, RecipeInput input) => Recipes.UpdateAsync(db, id, input, clock);

    public Task DeleteRecipeAsync(string id) => Recipes.DeleteAsync(db, id);

    public Task<string> LogRecipeAsync(string recipeId, string meal, DateOnly date, double quantity, string unit) =>
        FoodLog.LogRecipeAsync(db, RequireProfile(), recipeId, meal, date, quantity, unit, clock);

    public Task DeleteAsync(string entryId) => FoodLog.DeleteAsync(db, RequireProfile(), entryId);

    public Task<string> AddFoodAsync(string householdId, FoodInput input) => FoodLibrary.AddAsync(db, householdId, input, clock);

    public Task<WorkoutService.HouseholdChoice[]> HouseholdsAsync() => db.GetAll<WorkoutService.HouseholdChoice>(
        "SELECT h.id AS Id, h.name AS Name FROM households h JOIN household_members m ON m.household_id = h.id " +
        "WHERE m.profile_id = ? ORDER BY h.name", [RequireProfile()]);

    private string RequireProfile() =>
        profiles.Profile?.Id ?? throw new InvalidOperationException("The profile hasn't synced to this device yet.");
}
