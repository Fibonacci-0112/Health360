using Health360.Domain.Physiology;

namespace Health360.Domain.Tests.Physiology;

public class PhysicalActivityRatingTests
{
    // NHANES Cardiovascular Fitness Procedures Manual (1999–2004), Appendix E, p. E-3.
    [Theory]
    [InlineData(DailyExertion.AvoidsWalkingOrExertion, 0)]
    [InlineData(DailyExertion.WalksForPleasure, 1)]
    public void Little_or_no_activity_gives_0_or_1(DailyExertion answer, int code) =>
        Assert.Equal(code, PhysicalActivityRating.Code(answer));

    [Theory]
    [InlineData(ModestActivityTime.TenToSixtyMinutes, 2)]
    [InlineData(ModestActivityTime.MoreThanSixtyMinutes, 3)]
    public void Modest_activity_gives_2_or_3(ModestActivityTime answer, int code) =>
        Assert.Equal(code, PhysicalActivityRating.Code(answer));

    [Theory]
    [InlineData(HeavyActivityTime.LessThanThirtyMinutes, 4)]
    [InlineData(HeavyActivityTime.ThirtyToSixtyMinutes, 5)]
    [InlineData(HeavyActivityTime.OneToThreeHours, 6)]
    [InlineData(HeavyActivityTime.MoreThanThreeHours, 7)]
    public void Heavy_exercise_gives_4_to_7(HeavyActivityTime answer, int code) =>
        Assert.Equal(code, PhysicalActivityRating.Code(answer));

    [Fact]
    public void Every_answer_path_gives_a_distinct_code_and_together_they_cover_0_to_7()
    {
        int[] codes =
        [
            .. Enum.GetValues<DailyExertion>().Select(PhysicalActivityRating.Code),
            .. Enum.GetValues<ModestActivityTime>().Select(PhysicalActivityRating.Code),
            .. Enum.GetValues<HeavyActivityTime>().Select(PhysicalActivityRating.Code),
        ];

        Assert.Equal(Enumerable.Range(0, Vo2Max.MaxActivityCode + 1), codes.Order());
    }

    [Fact]
    public void Every_code_is_accepted_by_the_jackson_models()
    {
        foreach (var code in Enumerable.Range(0, Vo2Max.MaxActivityCode + 1))
        {
            Assert.NotNull(Vo2Max.JacksonBmi(Sex.Female, code, 40, 24));
        }
    }
}
