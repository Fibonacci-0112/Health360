using System.Text;
using System.Text.Json;

namespace Health360.Importers.Tests;

public class FoodDataCentralTests
{
    /// <summary>SR Legacy entries in the download's own shape, trimmed to the fields that matter.</summary>
    internal const string SrLegacySample =
        """
        {"SRLegacyFoods": [
          {"fdcId": 167512, "dataType": "SR Legacy", "description": "Pillsbury  Golden Layer Biscuits, refrigerated dough",
           "foodNutrients": [
             {"nutrient": {"id": 1008, "number": "208", "name": "Energy", "unitName": "kcal"}, "amount": 307},
             {"nutrient": {"id": 1062, "number": "268", "name": "Energy", "unitName": "kJ"}, "amount": 1284},
             {"nutrient": {"id": 1003, "number": "203", "name": "Protein", "unitName": "g"}, "amount": 5.88},
             {"nutrient": {"id": 1004, "number": "204", "name": "Total lipid (fat)", "unitName": "g"}, "amount": 13.2},
             {"nutrient": {"id": 1005, "number": "205", "name": "Carbohydrate, by difference", "unitName": "g"}, "amount": 41.2},
             {"nutrient": {"id": 1093, "number": "307", "name": "Sodium, Na", "unitName": "mg"}, "amount": 1010},
             {"nutrient": {"id": 2000, "number": "269", "name": "Total Sugars", "unitName": "g"}, "amount": 0},
             {"nutrient": {"id": 1110, "number": "324", "name": "Vitamin D (D2 + D3), International Units", "unitName": "IU"}, "amount": 4},
             {"nutrient": {"id": 1114, "number": "328", "name": "Vitamin D (D2 + D3)", "unitName": "µg"}, "amount": 0.1},
             {"nutrient": {"id": 1190, "number": "435", "name": "Folate, DFE", "unitName": "µg"}, "amount": 85},
             {"nutrient": {"id": 1051, "number": "255", "name": "Water", "unitName": "g"}, "amount": 36.1}
           ],
           "foodPortions": [
             {"sequenceNumber": 2, "amount": 1.0, "value": 1.0, "measureUnit": {"name": "undetermined"}, "modifier": "oz", "gramWeight": 28.35},
             {"sequenceNumber": 1, "amount": 1.0, "value": 1.0, "measureUnit": {"name": "undetermined"}, "modifier": "serving", "gramWeight": 34.0},
             {"sequenceNumber": 3, "amount": 1.0, "measureUnit": {"name": "undetermined"}, "modifier": "oz", "gramWeight": 28.4},
             {"sequenceNumber": 4, "amount": 0.5, "measureUnit": {"name": "undetermined"}, "modifier": "cup, chopped", "gramWeight": 80}
           ]},
          {"fdcId": 171705, "dataType": "SR Legacy", "description": "Salt, table",
           "foodNutrients": [
             {"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 0},
             {"nutrient": {"id": 1093, "unitName": "mg"}, "amount": 38758}
           ]}
        ]}
        """;

    internal static ParsedFoods ParseText(string json) => FoodDataCentral.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Maps_a_food_to_its_columns_per_100_g()
    {
        var parsed = ParseText(SrLegacySample);

        Assert.Same(FoodDataCentral.SrLegacy, parsed.Dataset);
        Assert.Empty(parsed.Skipped);
        var biscuits = parsed.Foods[0];
        Assert.Equal(("167512", "Pillsbury Golden Layer Biscuits, refrigerated dough"), (biscuits.ExternalId, biscuits.Name));
        Assert.Equal((307d, 5.88, 41.2, 13.2), (biscuits.Energy100!.Value, biscuits.Protein100!.Value, biscuits.Carbohydrate100!.Value, biscuits.Fat100!.Value));
        // Vitamin D in µg (not the IU entry); water and kJ aren't mapped; a listed 0 is 0, not unknown.
        Assert.Equal(
            new Dictionary<string, double> { ["folate"] = 85, ["sodium"] = 1010, ["sugars"] = 0, ["vitamin_d"] = 0.1 },
            biscuits.Nutrients100);
    }

    [Fact]
    public void A_nutrient_the_dataset_does_not_list_stays_unknown()
    {
        var salt = ParseText(SrLegacySample).Foods[1];

        Assert.Equal(0, salt.Energy100);
        Assert.Null(salt.Protein100);
        Assert.Null(salt.Fat100);
        Assert.Equal(["sodium"], salt.Nutrients100.Keys);
        Assert.Empty(salt.Portions);
    }

    [Fact]
    public void Portions_follow_the_sequence_and_keep_the_first_of_a_name()
    {
        var parsed = ParseText(SrLegacySample);

        Assert.Equal(
            [new ImportedPortion("1 serving", 34), new ImportedPortion("1 oz", 28.35), new ImportedPortion("0.5 cup, chopped", 80)],
            parsed.Foods[0].Portions);
        Assert.Equal(1, parsed.PortionsDropped);
    }

    [Fact]
    public void Foundation_foods_take_Atwater_energy_and_their_own_sugars_entry()
    {
        var parsed = ParseText(
            """
            {"FoundationFoods": [
              {"fdcId": 321358, "description": "Hummus, commercial",
               "foodNutrients": [
                 {"nutrient": {"id": 2047, "unitName": "kcal"}, "amount": 229.7},
                 {"nutrient": {"id": 2048, "unitName": "kcal"}, "amount": 229},
                 {"nutrient": {"id": 1063, "unitName": "g"}, "amount": 0.34},
                 {"nutrient": {"id": 1177, "name": "Folate, total", "unitName": "µg"}, "amount": 70},
                 {"nutrient": {"id": 2066, "name": "Vitamin A", "unitName": "mg"}}
               ],
               "foodPortions": [
                 {"sequenceNumber": 1, "value": 2.0, "amount": 2.0, "measureUnit": {"name": "tablespoon", "abbreviation": "tbsp"}, "modifier": "", "gramWeight": 33.9},
                 {"sequenceNumber": 2, "value": 1.0, "amount": 1.0, "measureUnit": {"name": "serving"}, "modifier": "NLEA", "gramWeight": 28},
                 {"sequenceNumber": 3, "value": 1.0, "amount": 1.0, "measureUnit": {"name": "cup"}, "portionDescription": "shredded", "gramWeight": 113}
               ]},
              {"fdcId": 746775, "description": "Beans, minerals only",
               "foodNutrients": [{"nutrient": {"id": 1087, "unitName": "mg"}, "amount": 50}]}
            ]}
            """);

        Assert.Same(FoodDataCentral.Foundation, parsed.Dataset);
        var hummus = Assert.Single(parsed.Foods);
        // The specific-factor energy is preferred to the general one; total folate is not folate DFE.
        Assert.Equal(229, hummus.Energy100);
        Assert.Equal(new Dictionary<string, double> { ["sugars"] = 0.34 }, hummus.Nutrients100);
        Assert.Equal(["2 tablespoon", "1 serving, NLEA", "1 cup, shredded"], hummus.Portions.Select(p => p.Name));
        Assert.Equal(("746775", "energy missing"), (parsed.Skipped[0].ExternalId, parsed.Skipped[0].Reason));
    }

    [Fact]
    public void The_dataset_energy_is_preferred_to_Atwater_energy()
    {
        var food = Assert.Single(ParseText(
            """
            {"FoundationFoods": [{"fdcId": 1, "description": "x", "foodNutrients": [
              {"nutrient": {"id": 2048, "unitName": "kcal"}, "amount": 50},
              {"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 52}
            ]}]}
            """).Foods);

        Assert.Equal(52, food.Energy100);
    }

    [Fact]
    public void Survey_portions_use_their_description_without_coding_defaults()
    {
        var parsed = ParseText(
            """
            {"SurveyFoods": [{"fdcId": 2705385, "description": "Milk, whole",
              "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 61}],
              "foodPortions": [
                {"sequenceNumber": 1, "measureUnit": {"name": "undetermined"}, "modifier": "10205", "portionDescription": "1 cup", "gramWeight": 244},
                {"sequenceNumber": 2, "measureUnit": {"name": "undetermined"}, "modifier": "90000", "portionDescription": "Quantity not specified", "gramWeight": 244},
                {"sequenceNumber": 3, "measureUnit": {"name": "undetermined"}, "modifier": "64130", "portionDescription": "Guideline amount per fl oz of beverage", "gramWeight": 2.5},
                {"sequenceNumber": 4, "measureUnit": {"name": "undetermined"}, "modifier": "90000", "portionDescription": "1 glass", "gramWeight": 0}
              ]}]}
            """);

        Assert.Same(FoodDataCentral.Survey, parsed.Dataset);
        Assert.Equal([new ImportedPortion("1 cup", 244)], Assert.Single(parsed.Foods).Portions);
        Assert.Equal(3, parsed.PortionsDropped);
    }

    [Theory]
    [InlineData("""{"description": "x", "foodNutrients": []}""", "fdcId missing or not a positive integer")]
    [InlineData("""{"fdcId": 1, "description": " ", "foodNutrients": []}""", "description missing or too long")]
    [InlineData("""{"fdcId": 1, "description": "x"}""", "foodNutrients missing")]
    [InlineData("""{"fdcId": 1, "description": "x", "foodNutrients": [{"nutrient": {"id": 1003, "unitName": "g"}, "amount": 5}]}""", "energy missing")]
    [InlineData("""{"fdcId": 1, "description": "x", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kJ"}, "amount": 5}]}""", "nutrient 1008 not in kcal")]
    [InlineData("""{"fdcId": 1, "description": "x", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": -1}]}""", "nutrient 1008 amount not a non-negative number")]
    [InlineData(
        """{"fdcId": 1, "description": "x", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 5}, {"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 6}]}""",
        "nutrient 1008 listed twice")]
    [InlineData(
        """{"fdcId": 1, "description": "x", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 900}, {"nutrient": {"id": 1004, "unitName": "g"}, "amount": 100.1}]}""",
        "macronutrient over 100 g per 100 g")]
    [InlineData("""42""", "not an object")]
    public void Foods_the_table_would_reject_or_that_cannot_be_trusted_are_skipped(string food, string reason)
    {
        var parsed = ParseText($$"""{"SRLegacyFoods": [{{food}}]}""");

        Assert.Empty(parsed.Foods);
        Assert.Equal(reason, Assert.Single(parsed.Skipped).Reason);
    }

    [Fact]
    public void A_repeated_id_is_imported_once()
    {
        var parsed = ParseText(
            """
            {"SRLegacyFoods": [
              {"fdcId": 1, "description": "First", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 1}]},
              {"fdcId": 1, "description": "Second", "foodNutrients": [{"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 1}]}
            ]}
            """);

        Assert.Equal("First", Assert.Single(parsed.Foods).Name);
        Assert.Equal((1, "repeated id"), (Assert.Single(parsed.Skipped).Index, parsed.Skipped[0].Reason));
    }

    [Fact]
    public void Every_mapped_code_is_a_catalogue_nutrient_outside_the_macro_columns()
    {
        var food = ParseText(SrLegacySample).Foods[0];

        Assert.All(food.Nutrients100.Keys, code =>
        {
            Assert.True(Domain.Nutrition.Nutrient.TryParse(code, out var nutrient), code);
            Assert.DoesNotContain(nutrient, Domain.Nutrition.Nutrient.Macros);
        });
    }

    [Fact]
    public void A_nutrient_or_portion_with_an_unusable_number_is_passed_over_not_fatal()
    {
        var food = Assert.Single(ParseText(
            """
            {"SRLegacyFoods": [{"fdcId": 1, "description": "x",
              "foodNutrients": [
                {"nutrient": {"id": null, "unitName": "g"}, "amount": 5},
                {"nutrient": {"id": "1003", "unitName": "g"}, "amount": 5},
                {"nutrient": {"id": 1008, "unitName": "kcal"}, "amount": 40}
              ],
              "foodPortions": [
                {"sequenceNumber": "2", "amount": 1, "measureUnit": {"name": "undetermined"}, "modifier": "slice", "gramWeight": 20},
                {"sequenceNumber": 1, "amount": 1, "measureUnit": {"name": "undetermined"}, "modifier": "cup", "gramWeight": 100}
              ]}]}
            """).Foods);

        Assert.Equal((40d, (double?)null), (food.Energy100!.Value, food.Protein100));
        Assert.Equal(["1 cup", "1 slice"], food.Portions.Select(p => p.Name));
    }

    [Theory]
    [InlineData("""{"BrandedFoods": []}""")]
    [InlineData("""{"SomethingElse": []}""")]
    [InlineData("""{"SRLegacyFoods": {}}""")]
    [InlineData("""[]""")]
    public void A_file_that_is_not_a_supported_download_is_refused(string json) =>
        Assert.ThrowsAny<JsonException>(() => ParseText(json));
}
