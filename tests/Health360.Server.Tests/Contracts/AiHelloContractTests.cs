using System.Text.Json.Nodes;
using Health360.Contracts.Ai;

namespace Health360.Server.Tests.Contracts;

public sealed class AiHelloContractTests
{
    [Fact]
    public void The_output_schema_is_generated_from_the_contract_type()
    {
        var schema = StructuredOutputSchema.For(AiJsonContext.Default.AiHelloOutput);

        var expected = JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "greeting": { "type": "string" },
                "tip": { "type": "string" }
              },
              "required": ["greeting", "tip"],
              "additionalProperties": false
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, schema), schema.ToJsonString());
    }

    [Theory]
    [InlineData("Hi", "Walk.", true)]
    [InlineData("", "Walk.", false)]
    [InlineData("Hi", "   ", false)]
    [InlineData("Hi", null, false)]
    public void The_output_validator_checks_what_the_schema_cannot(string greeting, string? tip, bool valid)
    {
        var result = new AiHelloOutputValidator().Validate(new AiHelloOutput(greeting, tip!));
        Assert.Equal(valid, result.IsValid);
    }

    [Theory]
    [InlineData(200, 300, true)]
    [InlineData(201, 300, false)]
    [InlineData(200, 301, false)]
    public void The_output_validator_enforces_lengths(int greeting, int tip, bool valid)
    {
        var result = new AiHelloOutputValidator().Validate(new AiHelloOutput(new string('g', greeting), new string('t', tip)));
        Assert.Equal(valid, result.IsValid);
    }

    [Theory]
    [InlineData(80, "kg", true)]
    [InlineData(1000, "lb", true)]
    [InlineData(0, "kg", false)]
    [InlineData(1000.5, "kg", false)]
    [InlineData(double.NaN, "kg", false)]
    [InlineData(double.PositiveInfinity, "kg", false)]
    [InlineData(80, "KG", false)]
    [InlineData(80, "st", false)]
    public void The_request_validator_accepts_body_weights_in_kg_or_lb(double weight, string unit, bool valid)
    {
        var result = new AiHelloRequestValidator().Validate(new AiHelloRequest(weight, unit));
        Assert.Equal(valid, result.IsValid);
    }
}
