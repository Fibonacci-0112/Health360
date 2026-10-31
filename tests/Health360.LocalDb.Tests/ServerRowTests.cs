using System.Text.Json.Nodes;

namespace Health360.LocalDb.Tests;

public class ServerRowTests
{
    [Theory]
    [InlineData("23505")]
    [InlineData("23503")]
    [InlineData("22P02")]
    [InlineData("42501")]
    [InlineData("42703")]
    [InlineData("PGRST204")]
    [InlineData("PGRST116")]
    public void PermanentErrors(string code) => Assert.True(Uploads.IsPermanentError(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("08006")]
    [InlineData("53300")]
    [InlineData("57014")]
    [InlineData("PGRST301")]
    [InlineData("40001")]
    [InlineData("2350")]
    [InlineData("235050")]
    public void RetriedErrors(string? code) => Assert.False(Uploads.IsPermanentError(code));

    [Fact]
    public void SendsJsonbColumnsAsJsonNotAsAStringOfJson()
    {
        var row = Uploads.ToServerRow("profiles", new JsonObject { ["unit_prefs"] = """{"load":"kg"}""", ["display_name"] = "A" });

        Assert.Equal("""{"unit_prefs":{"load":"kg"},"display_name":"A"}""", row.ToJsonString());
    }

    [Fact]
    public void SendsZoneBoundariesAsJsonArrays()
    {
        var row = Uploads.ToServerRow("zone_sets", new JsonObject
        {
            ["boundaries"] = "[0,130,145]",
            ["boundaries_canonical"] = "[]",
            ["unit"] = "bpm",
        });

        Assert.Equal("""{"boundaries":[0,130,145],"boundaries_canonical":[],"unit":"bpm"}""", row.ToJsonString());
    }

    [Theory]
    [InlineData(1L, true)]
    [InlineData(0L, false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    public void SendsBooleansAsBooleans(object local, bool expected)
    {
        var value = local is long l ? JsonValue.Create(l) : JsonValue.Create((string)local);

        var row = Uploads.ToServerRow("consents", new JsonObject { ["granted"] = value });

        Assert.Equal(expected, row["granted"]!.GetValue<bool>());
    }

    [Fact]
    public void ReadsBooleansParsedFromJson()
    {
        var local = (JsonObject)JsonNode.Parse("""{"granted":1}""")!;

        Assert.True(Uploads.ToServerRow("consents", local)["granted"]!.GetValue<bool>());
    }

    [Fact]
    public void LeavesNullsAbsentColumnsAndOtherTablesAlone()
    {
        Assert.Equal("""{"unit_prefs":null}""", Uploads.ToServerRow("profiles", new JsonObject { ["unit_prefs"] = null }).ToJsonString());
        Assert.Equal("""{"display_name":"A"}""", Uploads.ToServerRow("profiles", new JsonObject { ["display_name"] = "A" }).ToJsonString());
        Assert.Equal("""{"x":1}""", Uploads.ToServerRow("notes", new JsonObject { ["body"] = """{"x":1}""" })["body"]!.GetValue<string>());
    }

    [Fact]
    public void DoesNotChangeTheLocalRow()
    {
        var local = new JsonObject { ["unit_prefs"] = """{"load":"kg"}""" };

        Uploads.ToServerRow("profiles", local);

        Assert.Equal("""{"load":"kg"}""", local["unit_prefs"]!.GetValue<string>());
    }

    [Fact]
    public void MarksMalformedJsonAsAPermanentError()
    {
        var error = Assert.Throws<RemoteWriteException>(() => Uploads.ToServerRow("profiles", new JsonObject { ["unit_prefs"] = "{oops" }));

        Assert.True(Uploads.IsPermanentError(error.Code));
    }
}
