using PowerSync.Common.DB.Schema;

namespace Health360.LocalDb;

/// <summary>
/// The PowerSync client schema. Client tables mirror the synced Postgres tables column for column
/// (PowerSync stores everything as text, integer or real; jsonb arrives as JSON text and booleans as
/// 0/1). Server-only columns that clients never read can be left out. PowerSync adds the <c>id</c>
/// column itself. <c>tools/drift</c> checks it against the migrations.
/// </summary>
public static class AppSchema
{
    public const string FailedUploads = "failed_uploads";

    private static readonly (string Name, ColumnType Type)[] Timestamps =
    [
        ("created_at", ColumnType.Text),
        ("updated_at", ColumnType.Text),
    ];

    public static Table Profiles { get; } = Synced("profiles",
        ("user_id", ColumnType.Text),
        ("managed_by", ColumnType.Text),
        ("display_name", ColumnType.Text),
        ("birth_date", ColumnType.Text),
        ("sex_for_calcs", ColumnType.Text),
        ("height", ColumnType.Real),
        ("height_unit", ColumnType.Text),
        ("height_cm", ColumnType.Real),
        ("unit_prefs", ColumnType.Text),
        ("timezone", ColumnType.Text),
        ("currency", ColumnType.Text),
        ("activity_level", ColumnType.Text),
        ("onboarded_at", ColumnType.Text),
        ("takes_hr_lowering_medication", ColumnType.Integer),
        ("activity_rating", ColumnType.Integer));

    public static Table Households { get; } = Synced("households",
        ("name", ColumnType.Text));

    public static Table HouseholdMembers { get; } = Synced("household_members",
        ("household_id", ColumnType.Text),
        ("profile_id", ColumnType.Text),
        ("user_id", ColumnType.Text),
        ("role", ColumnType.Text));

    public static Table SharingGrants { get; } = Synced("sharing_grants",
        ("profile_id", ColumnType.Text),
        ("household_id", ColumnType.Text),
        ("grantee_profile_id", ColumnType.Text),
        ("category", ColumnType.Text),
        ("access", ColumnType.Text));

    public static Table Consents { get; } = Synced("consents",
        ("profile_id", ColumnType.Text),
        ("kind", ColumnType.Text),
        ("version", ColumnType.Text),
        ("granted", ColumnType.Integer),
        ("decided_at", ColumnType.Text));

    public static Table ReferenceDatasets { get; } = Synced("reference_datasets",
        ("code", ColumnType.Text),
        ("version", ColumnType.Text),
        ("attribution", ColumnType.Text),
        ("imported_at", ColumnType.Text));

    /// <summary>G: what can be measured, its sharing category, entry units and canonical unit.</summary>
    public static Table MetricDefinitions { get; } = Synced("metric_definitions",
        ("code", ColumnType.Text),
        ("category", ColumnType.Text),
        ("units", ColumnType.Text),
        ("canonical_unit", ColumnType.Text),
        ("valid_min", ColumnType.Real),
        ("valid_max", ColumnType.Real));

    /// <summary>U: one sitting (a DEXA scan, a lab visit, a field test, or values entered together).</summary>
    public static Table MeasurementGroups { get; } = Synced("measurement_groups",
        ("profile_id", ColumnType.Text),
        ("kind", ColumnType.Text),
        ("measured_at", ColumnType.Text),
        ("local_date", ColumnType.Text),
        ("provider", ColumnType.Text),
        ("device_make", ColumnType.Text),
        ("protocol", ColumnType.Text),
        ("report_path", ColumnType.Text));

    /// <summary>
    /// U: one body, vitals or physiology value as entered (value + unit). The server recomputes
    /// <c>value_canonical</c> and copies <c>category</c> from the metric, whatever the client sends.
    /// </summary>
    public static Table Measurements { get; } = Synced("measurements",
        ("profile_id", ColumnType.Text),
        ("metric_code", ColumnType.Text),
        ("category", ColumnType.Text),
        ("value", ColumnType.Real),
        ("unit", ColumnType.Text),
        ("value_canonical", ColumnType.Real),
        ("measured_at", ColumnType.Text),
        ("local_date", ColumnType.Text),
        ("group_id", ColumnType.Text),
        ("source", ColumnType.Text),
        ("method", ColumnType.Text));

    /// <summary>U: heart-rate, power or pace zones per sport. The server recomputes <c>boundaries_canonical</c>.</summary>
    public static Table ZoneSets { get; } = Synced("zone_sets",
        ("profile_id", ColumnType.Text),
        ("sport", ColumnType.Text),
        ("metric", ColumnType.Text),
        ("basis", ColumnType.Text),
        ("boundaries", ColumnType.Text),
        ("unit", ColumnType.Text),
        ("boundaries_canonical", ColumnType.Text),
        ("effective_from", ColumnType.Text),
        ("group_id", ColumnType.Text));

    /// <summary>
    /// U: the value the user chose for one metric, a measurement or the estimate (PLAN.md §2.8). One row per profile
    /// and metric, with an id derived from both (<see cref="MetricPins.IdFor"/>). The server copies <c>category</c>
    /// from the metric.
    /// </summary>
    public static Table MetricPinsTable { get; } = Synced("metric_pins",
        ("profile_id", ColumnType.Text),
        ("metric_code", ColumnType.Text),
        ("category", ColumnType.Text),
        ("target", ColumnType.Text),
        ("measurement_id", ColumnType.Text));

    /// <summary>
    /// U: one progress photo's metadata (PLAN.md §2.3). The files live in the private <c>progress-photos</c> bucket at
    /// <c>storage_path</c> and <c>thumb_path</c>, which the server requires to be derived from the profile and row id.
    /// The files are not synced rows.
    /// </summary>
    public static Table ProgressPhotos { get; } = Synced("progress_photos",
        ("profile_id", ColumnType.Text),
        ("pose", ColumnType.Text),
        ("taken_at", ColumnType.Text),
        ("local_date", ColumnType.Text),
        ("storage_path", ColumnType.Text),
        ("thumb_path", ColumnType.Text));

    /// <summary>
    /// G/H: the exercise library. System rows have no <c>household_id</c> and are read-only; a household's custom
    /// exercises are written by its members. <c>equipment</c> and the muscle lists are JSON arrays of codes.
    /// </summary>
    public static Table Exercises { get; } = Synced("exercises",
        ("household_id", ColumnType.Text),
        ("external_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("tracking_type", ColumnType.Text),
        ("equipment", ColumnType.Text),
        ("primary_muscles", ColumnType.Text),
        ("secondary_muscles", ColumnType.Text));

    /// <summary>U: a named workout routine.</summary>
    public static Table WorkoutTemplates { get; } = Synced("workout_templates",
        ("profile_id", ColumnType.Text),
        ("name", ColumnType.Text));

    /// <summary>U: one exercise in a template, in order, with its set scheme, rest and progression rule (JSON objects).</summary>
    public static Table TemplateExercises { get; } = Synced("template_exercises",
        ("profile_id", ColumnType.Text),
        ("template_id", ColumnType.Text),
        ("exercise_id", ColumnType.Text),
        ("position", ColumnType.Integer),
        ("set_scheme", ColumnType.Text),
        ("rest_seconds", ColumnType.Integer),
        ("progression", ColumnType.Text));

    /// <summary>
    /// U: one workout as performed. <c>energy_estimate</c> is a redacted summary; full cardio snapshots live in
    /// <see cref="CardioEstimates"/>. <c>ended_at</c> is null while the session is in progress.
    /// </summary>
    public static Table WorkoutSessions { get; } = Synced("workout_sessions",
        ("profile_id", ColumnType.Text),
        ("template_id", ColumnType.Text),
        ("kind", ColumnType.Text),
        ("sport", ColumnType.Text),
        ("started_at", ColumnType.Text),
        ("ended_at", ColumnType.Text),
        ("local_date", ColumnType.Text),
        ("avg_hr", ColumnType.Real),
        ("max_hr", ColumnType.Real),
        ("zone_seconds", ColumnType.Text),
        ("energy_estimate", ColumnType.Text),
        ("rpe", ColumnType.Real));

    /// <summary>U: full cardio estimate inputs and provenance, private to the profile owner.</summary>
    public static Table CardioEstimates { get; } = Synced("cardio_estimates",
        ("profile_id", ColumnType.Text),
        ("estimate", ColumnType.Text));

    /// <summary>
    /// U: one exercise in a session, in order, with the template row it was planned from (null when added by hand;
    /// a plain link, not a foreign key, so it may name a row deleted since).
    /// </summary>
    public static Table SessionExercises { get; } = Synced("session_exercises",
        ("profile_id", ColumnType.Text),
        ("session_id", ColumnType.Text),
        ("exercise_id", ColumnType.Text),
        ("position", ColumnType.Integer),
        ("template_exercise_id", ColumnType.Text));

    /// <summary>
    /// U: one set as entered. The server recomputes <c>weight_kg</c>, <c>distance_m</c> and <c>e1rm_kg</c> (for a completed
    /// set that isn't a warm-up, with the formula the client named) whatever the client sends; the client computes
    /// them the same way. <c>is_pr</c> is 1 when the set made a <c>personal_records</c> row.
    /// </summary>
    public static Table SetLogs { get; } = Synced("set_logs",
        ("profile_id", ColumnType.Text),
        ("session_exercise_id", ColumnType.Text),
        ("position", ColumnType.Integer),
        ("set_type", ColumnType.Text),
        ("reps", ColumnType.Integer),
        ("weight", ColumnType.Real),
        ("weight_unit", ColumnType.Text),
        ("weight_kg", ColumnType.Real),
        ("rpe", ColumnType.Real),
        ("rir", ColumnType.Integer),
        ("duration_seconds", ColumnType.Real),
        ("distance", ColumnType.Real),
        ("distance_unit", ColumnType.Text),
        ("distance_m", ColumnType.Real),
        ("completed_at", ColumnType.Text),
        ("e1rm_formula", ColumnType.Text),
        ("e1rm_kg", ColumnType.Real),
        ("is_pr", ColumnType.Integer));

    /// <summary>
    /// H: a gym or home setup: equipment codes, the plates it has (a JSON array of <c>{"weight", "unit", "count"}</c>,
    /// each plate in its own unit) and the bar as entered. The server recomputes <c>bar_weight_kg</c>.
    /// </summary>
    public static Table TrainingLocations { get; } = Synced("training_locations",
        ("household_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("equipment", ColumnType.Text),
        ("plate_inventory", ColumnType.Text),
        ("bar_weight", ColumnType.Real),
        ("bar_unit", ColumnType.Text),
        ("bar_weight_kg", ColumnType.Real));

    /// <summary>U: a dated block of training, optionally at one location.</summary>
    public static Table Programs { get; } = Synced("programs",
        ("profile_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("location_id", ColumnType.Text),
        ("starts_on", ColumnType.Text),
        ("ends_on", ColumnType.Text));

    /// <summary>
    /// U: a template planned every <c>weekday</c> (ISO, 1 = Monday) of a program, or on one date
    /// (<c>scheduled_on</c>), with the session that did it.
    /// </summary>
    public static Table ScheduledWorkouts { get; } = Synced("scheduled_workouts",
        ("profile_id", ColumnType.Text),
        ("program_id", ColumnType.Text),
        ("template_id", ColumnType.Text),
        ("weekday", ColumnType.Integer),
        ("scheduled_on", ColumnType.Text),
        ("session_id", ColumnType.Text));

    /// <summary>
    /// U: a set that beat the profile's earlier best for an exercise (<c>kind</c> e1rm, weight, reps or volume), one row
    /// per record, so the rows are the history. Values are canonical: kg, or a rep count at <c>weight_kg</c>.
    /// </summary>
    public static Table PersonalRecords { get; } = Synced("personal_records",
        ("profile_id", ColumnType.Text),
        ("exercise_id", ColumnType.Text),
        ("kind", ColumnType.Text),
        ("value", ColumnType.Real),
        ("weight_kg", ColumnType.Real),
        ("reps", ColumnType.Integer),
        ("e1rm_formula", ColumnType.Text),
        ("set_log_id", ColumnType.Text),
        ("achieved_at", ColumnType.Text),
        ("local_date", ColumnType.Text));

    /// <summary>G: a nutrient's code, the fixed unit of all its amounts (kcal, g, mg, ug) and its display order.</summary>
    public static Table Nutrients { get; } = Synced("nutrients",
        ("code", ColumnType.Text),
        ("unit", ColumnType.Text),
        ("position", ColumnType.Integer));

    /// <summary>
    /// H: a household's own food (catalogue foods are searched online, not synced), or its copy of a catalogue food
    /// (<c>copied_from</c>, with the id <see cref="Domain.Nutrition.FoodCopy.IdFor"/>; the server fills its values from
    /// the catalogue). Values are per 100 g or 100 ml (<c>basis</c>): the macronutrients in their own columns,
    /// everything else in <c>nutrients_100</c> by code, and named <c>portions</c> as a JSON array of
    /// <c>{"name", "amount"}</c> in g or ml. Null means unknown, not 0.
    /// </summary>
    public static Table Foods { get; } = Synced("foods",
        ("household_id", ColumnType.Text),
        ("source", ColumnType.Text),
        ("external_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("brand", ColumnType.Text),
        ("barcode", ColumnType.Text),
        ("basis", ColumnType.Text),
        ("energy_100", ColumnType.Real),
        ("protein_100", ColumnType.Real),
        ("carbohydrate_100", ColumnType.Real),
        ("fat_100", ColumnType.Real),
        ("nutrients_100", ColumnType.Text),
        ("portions", ColumnType.Text),
        ("verified", ColumnType.Integer),
        ("copied_from", ColumnType.Text));

    /// <summary>H: a recipe's servings and cooked weight as entered; the server recomputes <c>cooked_weight_g</c>.</summary>
    public static Table Recipes { get; } = Synced("recipes",
        ("household_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("servings", ColumnType.Real),
        ("cooked_weight", ColumnType.Real),
        ("cooked_weight_unit", ColumnType.Text),
        ("cooked_weight_g", ColumnType.Real));

    /// <summary>
    /// H: one food in a recipe. The amount as entered (a unit, or a number of the food's named portions) and in g or
    /// ml of the food's <c>basis</c>; the server recomputes <c>base_amount</c> for a unit, as the client does.
    /// </summary>
    public static Table RecipeIngredients { get; } = Synced("recipe_ingredients",
        ("household_id", ColumnType.Text),
        ("recipe_id", ColumnType.Text),
        ("position", ColumnType.Integer),
        ("food_id", ColumnType.Text),
        ("quantity", ColumnType.Real),
        ("quantity_unit", ColumnType.Text),
        ("portion_name", ColumnType.Text),
        ("basis", ColumnType.Text),
        ("base_amount", ColumnType.Real));

    /// <summary>
    /// U: one thing eaten, with a snapshot of its name and the nutrients in the amount eaten, so the log works offline
    /// and doesn't change when a food is edited. <c>incomplete_nutrients</c> lists codes that are only lower bounds.
    /// </summary>
    public static Table FoodLogEntries { get; } = Synced("food_log_entries",
        ("profile_id", ColumnType.Text),
        ("logged_at", ColumnType.Text),
        ("local_date", ColumnType.Text),
        ("meal", ColumnType.Text),
        ("entry_method", ColumnType.Text),
        ("food_id", ColumnType.Text),
        ("recipe_id", ColumnType.Text),
        ("name", ColumnType.Text),
        ("brand", ColumnType.Text),
        ("quantity", ColumnType.Real),
        ("quantity_unit", ColumnType.Text),
        ("portion_name", ColumnType.Text),
        ("basis", ColumnType.Text),
        ("base_amount", ColumnType.Real),
        ("energy", ColumnType.Real),
        ("protein", ColumnType.Real),
        ("carbohydrate", ColumnType.Real),
        ("fat", ColumnType.Real),
        ("nutrients", ColumnType.Text),
        ("incomplete_nutrients", ColumnType.Text),
        ("is_estimate", ColumnType.Integer));

    /// <summary>
    /// Local only, never uploaded. When the server permanently rejects a write (constraint or RLS
    /// violation), the connector parks it here instead of blocking the upload queue (PLAN.md §4).
    /// <c>data</c> is the CRUD payload as JSON, so the user can retry or discard it.
    /// </summary>
    public static Table FailedUploadsTable { get; } = new(
        FailedUploads,
        Columns(
            ("table_name", ColumnType.Text),
            ("row_id", ColumnType.Text),
            ("op", ColumnType.Text),
            ("data", ColumnType.Text),
            ("error_code", ColumnType.Text),
            ("error_message", ColumnType.Text),
            ("failed_at", ColumnType.Text)),
        new TableOptions(localOnly: true));

    /// <summary>
    /// Local only, never uploaded: progress photos whose files haven't reached Storage yet (<see cref="PhotoUploader"/>).
    /// The id is the photo's. Cleared with the rest of the database when the owner changes, and the files with it.
    /// </summary>
    public static Table PhotoUploadsTable { get; } = new(
        PhotoUploader.Table,
        Columns(
            ("profile_id", ColumnType.Text),
            ("attempts", ColumnType.Integer),
            ("error_code", ColumnType.Text),
            ("failed_at", ColumnType.Text),
            ("created_at", ColumnType.Text)),
        new TableOptions(localOnly: true));

    /// <summary>
    /// Local only: files of deleted photos still to be removed from Storage (<see cref="PhotoUploader.DeleteAsync"/>).
    /// The id is the photo's. Cleared with the rest of the database when the owner changes.
    /// </summary>
    public static Table PhotoDeletionsTable { get; } = new(
        PhotoUploader.DeletionsTable,
        Columns(
            ("profile_id", ColumnType.Text),
            ("attempts", ColumnType.Integer),
            ("error_code", ColumnType.Text),
            ("created_at", ColumnType.Text)),
        new TableOptions(localOnly: true));

    /// <summary>Owner-bound local drafts and rest deadlines; never uploaded, cleared on account switch.</summary>
    public static Table WorkoutState { get; } = new("workout_state",
        Columns(("profile_id", ColumnType.Text), ("value", ColumnType.Text)), new TableOptions(localOnly: true));

    /// <summary>
    /// U: how the profile's daily targets are set, one row per profile with an id derived from it
    /// (<see cref="NutritionTargetStore.IdFor"/>). It holds the settings, not computed grams: a formula target is
    /// computed from the physiology profile whenever it is shown. Energy is in kcal; the protein ratio is as entered.
    /// </summary>
    public static Table NutritionTargets { get; } = Synced("nutrition_targets",
        ("profile_id", ColumnType.Text),
        ("method", ColumnType.Text),
        ("energy_adjustment_kcal", ColumnType.Real),
        ("protein_basis", ColumnType.Text),
        ("protein_ratio", ColumnType.Real),
        ("protein_ratio_unit", ColumnType.Text),
        ("protein_ratio_g_per_kg", ColumnType.Real),
        ("fat_percent", ColumnType.Real),
        ("manual_energy_kcal", ColumnType.Real),
        ("manual_protein_g", ColumnType.Real),
        ("manual_fat_g", ColumnType.Real),
        ("manual_carbohydrate_g", ColumnType.Real));

    public static IReadOnlyList<Table> Tables { get; } =
        [
            Profiles, Households, HouseholdMembers, SharingGrants, Consents, ReferenceDatasets, MetricDefinitions,
            MeasurementGroups, Measurements, ZoneSets, MetricPinsTable, ProgressPhotos, Exercises, WorkoutTemplates,
            TemplateExercises, WorkoutSessions, CardioEstimates, SessionExercises, SetLogs, TrainingLocations, Programs, ScheduledWorkouts,
            PersonalRecords, Nutrients, Foods, Recipes, RecipeIngredients, FoodLogEntries, NutritionTargets, FailedUploadsTable, PhotoUploadsTable,
            PhotoDeletionsTable, WorkoutState,
        ];

    public static Schema Schema { get; } = new([.. Tables]);

    /// <summary>
    /// Columns stored differently in SQLite than in Postgres: jsonb is JSON text locally and boolean is
    /// 0/1. Uploads convert them back so the server receives real JSON values and booleans. Every jsonb
    /// and boolean column of a synced table must be listed.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServerColumnType>> ServerColumnTypes { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>>(StringComparer.Ordinal)
        {
            ["profiles"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["unit_prefs"] = ServerColumnType.Json,
                ["takes_hr_lowering_medication"] = ServerColumnType.Boolean,
            },
            ["consents"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["granted"] = ServerColumnType.Boolean,
            },
            ["metric_definitions"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["units"] = ServerColumnType.Json,
            },
            ["zone_sets"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["boundaries"] = ServerColumnType.Json,
                ["boundaries_canonical"] = ServerColumnType.Json,
            },
            ["exercises"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["equipment"] = ServerColumnType.Json,
                ["primary_muscles"] = ServerColumnType.Json,
                ["secondary_muscles"] = ServerColumnType.Json,
            },
            ["template_exercises"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["set_scheme"] = ServerColumnType.Json,
                ["progression"] = ServerColumnType.Json,
            },
            ["workout_sessions"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["zone_seconds"] = ServerColumnType.Json,
                ["energy_estimate"] = ServerColumnType.Json,
            },
            ["cardio_estimates"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["estimate"] = ServerColumnType.Json,
            },
            ["set_logs"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["is_pr"] = ServerColumnType.Boolean,
            },
            ["training_locations"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["equipment"] = ServerColumnType.Json,
                ["plate_inventory"] = ServerColumnType.Json,
            },
            ["foods"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["nutrients_100"] = ServerColumnType.Json,
                ["portions"] = ServerColumnType.Json,
                ["verified"] = ServerColumnType.Boolean,
            },
            ["food_log_entries"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["nutrients"] = ServerColumnType.Json,
                ["incomplete_nutrients"] = ServerColumnType.Json,
                ["is_estimate"] = ServerColumnType.Boolean,
            },
        };

    /// <summary>
    /// Tables whose rows are a record: clients may add rows but never change them (no UPDATE grant), so
    /// uploads insert and ignore duplicates instead of upserting, which would need UPDATE.
    /// </summary>
    public static IReadOnlySet<string> InsertOnlyTables { get; } = new HashSet<string>(["consents"], StringComparer.Ordinal);

    /// <summary>
    /// Columns a PUT must send even when null. PowerSync leaves null columns out of a queued insert, and the upload
    /// is an upsert that merges into an existing row, so a column left out keeps the server's old value. For these
    /// columns that would be wrong: re-pinning a metric to the estimate on a device that hadn't seen another
    /// device's pin to a measurement would keep that measurement_id, fail the target check and be quarantined.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> NullableOnPut { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["metric_pins"] = ["measurement_id"],
            // Clearing a manual value on one device must clear it on the server, not keep the old one.
            ["nutrition_targets"] = ["manual_energy_kcal", "manual_protein_g", "manual_fat_g", "manual_carbohydrate_g"],
        };

    /// <summary>Tables the client may write. Everything else synced is read-only on the client.</summary>
    public static IReadOnlySet<string> UploadableTables { get; } =
        new HashSet<string>(
            [
                "profiles", "consents", "sharing_grants", "measurement_groups", "measurements", "zone_sets", "metric_pins",
                "progress_photos", "exercises", "workout_templates", "template_exercises", "workout_sessions",
                "session_exercises", "set_logs", "cardio_estimates", "training_locations", "programs", "scheduled_workouts", "personal_records",
                "foods", "recipes", "recipe_ingredients", "food_log_entries", "nutrition_targets",
            ],
            StringComparer.Ordinal);

    private static Table Synced(string name, params (string Name, ColumnType Type)[] columns) =>
        new(name, Columns([.. columns, .. Timestamps]), new TableOptions());

    private static Dictionary<string, ColumnType> Columns(params (string Name, ColumnType Type)[] columns) =>
        columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
}

/// <summary>How a column's local SQLite value maps back to its Postgres type on upload.</summary>
public enum ServerColumnType
{
    Json,
    Boolean,
}
