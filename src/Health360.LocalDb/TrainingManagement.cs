using FluentValidation;
using Health360.Contracts.Training;
using Health360.Domain.Units;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

public static partial class TrainingLocations
{
    public static async Task UpdateAsync(IPowerSyncDatabase db, string id, TrainingLocationInput input, TimeProvider clock)
    {
        new TrainingLocationInputValidator().ValidateAndThrow(input);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM training_locations WHERE id = ?", [id]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The location is no longer on this device.");
            await tx.Execute("UPDATE training_locations SET name = ?, equipment = ?, plate_inventory = ?, bar_weight = ?, bar_unit = ?, " +
                "bar_weight_kg = ?, updated_at = ? WHERE id = ?", [input.Name.Trim(), input.EquipmentJson(), input.PlateInventoryJson(),
                input.BarWeight, input.BarUnit, input.BarWeight is { } w ? UnitCodes.Convert(w, input.BarUnit!, "kg") : null, Timestamps.UtcNow(clock), id])
                .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

public sealed class ScheduleEntryRow
{
    public string Id { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public long? Weekday { get; set; }
    public string? ScheduledOn { get; set; }
}

public static partial class WorkoutPlans
{
    public static Task<ScheduleEntryRow[]> ScheduleEntriesAsync(IPowerSyncDatabase db, string profileId, string? programId) =>
        db.GetAll<ScheduleEntryRow>("SELECT s.id AS Id, s.template_id AS TemplateId, t.name AS TemplateName, s.weekday AS Weekday, " +
            "s.scheduled_on AS ScheduledOn FROM scheduled_workouts s LEFT JOIN workout_templates t ON t.id = s.template_id " +
            "WHERE s.profile_id = ? AND s.program_id IS ? ORDER BY s.weekday, s.scheduled_on, s.id", [profileId, programId]);

    public static async Task UpdateProgramAsync(IPowerSyncDatabase db, string profileId, string id, string name, string? locationId,
        DateOnly startsOn, DateOnly? endsOn, TimeProvider clock)
    {
        if (!WorkoutCodes.IsName(name) || endsOn < startsOn) throw new ArgumentException("Enter a name and valid program dates.");
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM programs WHERE id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The program is no longer on this device.");
            if (locationId is not null)
            {
                _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM training_locations WHERE id = ?", [locationId]).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The location is no longer on this device.");
            }

            await tx.Execute("UPDATE programs SET name = ?, location_id = ?, starts_on = ?, ends_on = ?, updated_at = ? WHERE id = ? AND profile_id = ?",
                [name.Trim(), locationId, Day(startsOn), endsOn is { } end ? Day(end) : null, Timestamps.UtcNow(clock), id, profileId]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static async Task RequireScheduleParentsAsync(PowerSync.Common.DB.ITransaction tx, string profileId, string templateId, string? programId)
    {
        _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM workout_templates WHERE id = ? AND profile_id = ?", [templateId, profileId]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The template is no longer on this device.");
        if (programId is not null)
        {
            _ = await tx.GetOptional<IdOnly>("SELECT id AS Id FROM programs WHERE id = ? AND profile_id = ?", [programId, profileId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The program is no longer on this device.");
        }
    }

    public static Task DeleteProgramAsync(IPowerSyncDatabase db, string profileId, string id) => db.WriteTransaction(async tx =>
    {
        await tx.Execute("DELETE FROM scheduled_workouts WHERE program_id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false);
        await tx.Execute("DELETE FROM programs WHERE id = ? AND profile_id = ?", [id, profileId]).ConfigureAwait(false);
    });

    public static Task DeleteScheduleAsync(IPowerSyncDatabase db, string profileId, string id) =>
        db.Execute("DELETE FROM scheduled_workouts WHERE id = ? AND profile_id = ?", [id, profileId]);
}
