using Health360.Contracts.Training;

namespace Health360.UI.Tests.Contracts;

/// <summary><c>template_exercises.progression</c> and <c>set_scheme</c> as the template editor writes and reads them.</summary>
public class ProgressionRuleTests
{
    [Fact]
    public void A_rule_round_trips_through_its_json()
    {
        var rule = new ProgressionRule("double", 2.5, "kg", 2);

        Assert.Equal("{\"kind\":\"double\",\"increment\":2.5,\"increment_unit\":\"kg\",\"deload_after\":2}", rule.ToJson());
        Assert.Equal(rule, ProgressionRule.Parse(rule.ToJson()));
    }

    [Theory]
    [InlineData("{\"kind\": \"linear\", \"increment\": 5, \"increment_unit\": \"lb\"}", "linear", 5, "lb", 3)] // as Postgres formats jsonb; deload default
    [InlineData("{\"kind\":\"double\",\"increment\":1.25,\"increment_unit\":\"kg\",\"deload_after\":0}", "double", 1.25, "kg", 0)]
    public void A_stored_rule_parses(string json, string kind, double increment, string unit, int deloadAfter) =>
        Assert.Equal(new ProgressionRule(kind, increment, unit, deloadAfter), ProgressionRule.Parse(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"wave\",\"increment\":2.5,\"increment_unit\":\"kg\"}")]
    [InlineData("{\"kind\":\"double\",\"increment\":0,\"increment_unit\":\"kg\"}")]
    [InlineData("{\"kind\":\"double\",\"increment\":2.5,\"increment_unit\":\"st\"}")]
    [InlineData("{\"kind\":\"double\",\"increment\":\"2.5\",\"increment_unit\":\"kg\"}")]
    [InlineData("{\"kind\":\"double\",\"increment\":2.5,\"increment_unit\":\"kg\",\"deload_after\":11}")]
    [InlineData("{\"kind\":\"double\",\"increment\":2.5,\"increment_unit\":\"kg\",\"deload_after\":1.5}")]
    public void Anything_else_is_no_rule(string? json) => Assert.Null(ProgressionRule.Parse(json));

    [Fact]
    public void Each_rep_range_problem_has_its_own_message()
    {
        var validator = new SetSchemeValidator();

        Assert.Equal("The top of the rep range can't be below the bottom.", Assert.Single(validator.Validate(new SetScheme(3, 12, 8)).Errors).ErrorMessage);
        Assert.Equal("At most 100 reps.", Assert.Single(validator.Validate(new SetScheme(3, 8, 101)).Errors).ErrorMessage);
    }
}
