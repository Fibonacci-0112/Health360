using System.Text;
using System.Text.Json;

namespace Health360.Importers.Tests;

public class FreeExerciseDbTests
{
    /// <summary>Entries in the dataset's own shape (dist/exercises.json), trimmed to the fields that matter.</summary>
    internal const string Sample =
        """
        [
          {"id": "Barbell_Squat", "name": "Barbell Squat", "category": "strength", "equipment": "barbell",
           "primaryMuscles": ["quadriceps"], "secondaryMuscles": ["calves", "glutes", "hamstrings", "lower back"],
           "instructions": ["Stand."], "images": ["Barbell_Squat/0.jpg"]},
          {"id": "Pushups", "name": "Pushups", "category": "strength", "equipment": "body only",
           "primaryMuscles": ["chest"], "secondaryMuscles": ["shoulders", "triceps"]},
          {"id": "Plank", "name": "Plank", "force": "static", "level": "beginner", "mechanic": "isolation",
           "category": "strength", "equipment": "body only", "primaryMuscles": ["abdominals"], "secondaryMuscles": []},
          {"id": "Ab_Roller", "name": "Ab Roller", "force": "pull", "category": "strength", "equipment": null,
           "primaryMuscles": ["abdominals"], "secondaryMuscles": ["shoulders"]},
          {"id": "Jogging_Treadmill", "name": "Jogging, Treadmill", "category": "cardio", "equipment": "machine",
           "primaryMuscles": ["quadriceps"], "secondaryMuscles": ["calves", "glutes", "hamstrings"]},
          {"id": "Hamstring_Stretch", "name": "Hamstring Stretch", "category": "stretching", "equipment": "body only",
           "primaryMuscles": ["hamstrings"], "secondaryMuscles": []},
          {"id": "EZ-Bar_Curl", "name": "EZ-Bar Curl", "category": "strength", "equipment": "e-z curl bar",
           "primaryMuscles": ["biceps"], "secondaryMuscles": ["forearms", "forearms"]}
        ]
        """;

    internal static ParsedDataset ParseText(string json) => FreeExerciseDb.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Maps_each_entry_to_an_exercise_row()
    {
        var parsed = ParseText(Sample);

        Assert.Empty(parsed.Skipped);
        var squat = parsed.Exercises[0];
        Assert.Equal(("Barbell_Squat", "Barbell Squat", "weight_reps"), (squat.ExternalId, squat.Name, squat.TrackingType));
        Assert.Equal(["barbell"], squat.Equipment);
        Assert.Equal(["quadriceps"], squat.PrimaryMuscles);
        Assert.Equal(["calves", "glutes", "hamstrings", "lower_back"], squat.SecondaryMuscles);
    }

    [Fact]
    public void The_tracking_type_follows_the_category_and_equipment()
    {
        var types = ParseText(Sample).Exercises.ToDictionary(e => e.ExternalId, e => e.TrackingType);

        Assert.Equal("reps", types["Pushups"]);
        Assert.Equal("duration", types["Plank"]);
        Assert.Equal("reps", types["Ab_Roller"]);
        Assert.Equal("distance_duration", types["Jogging_Treadmill"]);
        Assert.Equal("duration", types["Hamstring_Stretch"]);
        Assert.Equal("weight_reps", types["EZ-Bar_Curl"]);
    }

    [Fact]
    public void Names_become_distinct_codes()
    {
        var curl = ParseText(Sample).Exercises.Single(e => e.ExternalId == "EZ-Bar_Curl");

        Assert.Equal(["e_z_curl_bar"], curl.Equipment);
        Assert.Equal(["forearms"], curl.SecondaryMuscles);
        Assert.Empty(ParseText(Sample).Exercises.Single(e => e.ExternalId == "Ab_Roller").Equipment);
    }

    [Theory]
    [InlineData("body only", "body_only")]
    [InlineData("  Medicine Ball ", "medicine_ball")]
    [InlineData("e-z curl bar", "e_z_curl_bar")]
    [InlineData("3rd", null)]
    [InlineData("---", null)]
    [InlineData("", null)]
    public void Codes_match_what_the_table_accepts(string name, string? code) => Assert.Equal(code, FreeExerciseDb.ToCode(name));

    [Theory]
    [InlineData("""[{"name": "No id", "category": "strength"}]""", "id missing or too long")]
    [InlineData("""[{"id": "x", "name": " ", "category": "strength"}]""", "name missing or too long")]
    [InlineData("""[{"id": "x", "name": "X", "primaryMuscles": ["9th"]}]""", "equipment or muscles not usable as codes")]
    [InlineData("""[{"id": "x", "name": "X", "secondaryMuscles": "chest"}]""", "equipment or muscles not usable as codes")]
    [InlineData("""[42]""", "not an object")]
    public void Entries_the_table_would_reject_are_skipped_with_a_reason(string json, string reason)
    {
        var parsed = ParseText(json);

        Assert.Empty(parsed.Exercises);
        Assert.Equal(reason, Assert.Single(parsed.Skipped).Reason);
    }

    [Fact]
    public void A_repeated_id_is_imported_once()
    {
        var parsed = ParseText("""[{"id": "x", "name": "First"}, {"id": "x", "name": "Second"}]""");

        Assert.Equal("First", Assert.Single(parsed.Exercises).Name);
        Assert.Equal((1, "repeated id"), (Assert.Single(parsed.Skipped).Index, parsed.Skipped[0].Reason));
    }

    [Fact]
    public void A_name_longer_than_the_column_allows_is_skipped() =>
        Assert.Single(ParseText($$"""[{"id": "x", "name": "{{new string('a', 121)}}"}]""").Skipped);

    [Fact]
    public void A_file_that_is_not_an_array_is_refused() =>
        Assert.ThrowsAny<JsonException>(() => ParseText("""{"exercises": []}"""));
}
