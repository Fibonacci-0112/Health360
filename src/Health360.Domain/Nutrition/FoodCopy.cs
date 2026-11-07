using Health360.Domain.Ids;

namespace Health360.Domain.Nutrition;

/// <summary>
/// A household's copy of a catalogue food. Recipes use only their household's foods, so a catalogue food (searched
/// online, never synced) is copied into the household the first time it is used, and the copy syncs to every
/// member's devices. The server fills the copy's values from the catalogue row.
/// </summary>
public static class FoodCopy
{
    /// <summary>
    /// The copy's row id, the same on every device, so two members copying the same food offline upsert one row.
    /// <c>private.food_copy_id</c> on the server derives and checks the same value.
    /// </summary>
    public static Guid IdFor(Guid householdId, Guid catalogueFoodId) =>
        UuidV8.FromName($"health360/food_copies/{householdId:D}/{catalogueFoodId:D}");
}
