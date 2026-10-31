namespace Health360.Domain.Physiology;

/// <summary>Answer to the first PA-R question: the person's regular physical activity.</summary>
public enum RegularActivity
{
    /// <summary>Little or no regular recreation, sport, or physical activity.</summary>
    LittleOrNone,

    /// <summary>Regular recreation or work requiring modest physical activity.</summary>
    Modest,

    /// <summary>Regular heavy physical exercise.</summary>
    Heavy,
}

/// <summary>Follow-up for <see cref="RegularActivity.LittleOrNone"/>.</summary>
public enum DailyExertion
{
    /// <summary>Avoids walking or exertion (always uses the elevator, drives instead of walking).</summary>
    AvoidsWalkingOrExertion,

    /// <summary>Walks for pleasure, routinely uses stairs, occasionally exercises enough to breathe heavily or sweat.</summary>
    WalksForPleasure,
}

/// <summary>Follow-up for <see cref="RegularActivity.Modest"/>: time per week.</summary>
public enum ModestActivityTime
{
    TenToSixtyMinutes,
    MoreThanSixtyMinutes,
}

/// <summary>Follow-up for <see cref="RegularActivity.Heavy"/>: time per week.</summary>
public enum HeavyActivityTime
{
    LessThanThirtyMinutes,
    ThirtyToSixtyMinutes,
    OneToThreeHours,
    MoreThanThreeHours,
}

/// <summary>
/// The NASA/JSC physical activity rating (PA-R, 0–7) that <see cref="Vo2Max"/>'s Jackson models take, from the
/// answers to its questionnaire. Mapping as in the NHANES Cardiovascular Fitness Procedures Manual (1999–2004),
/// Appendix E, which cites Jackson et al. (1990) and Ross and Jackson (1990). Each overload is one branch of the
/// questionnaire, so an impossible combination of answers can't be expressed.
/// </summary>
public static class PhysicalActivityRating
{
    /// <summary>Little or no regular activity: 0 if the person avoids walking or exertion, 1 if they walk for pleasure.</summary>
    public static int Code(DailyExertion exertion) => exertion switch
    {
        DailyExertion.AvoidsWalkingOrExertion => 0,
        DailyExertion.WalksForPleasure => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(exertion), exertion, "Unknown answer"),
    };

    /// <summary>Regular modest activity: 2 for 10–60 minutes a week, 3 for more than 60.</summary>
    public static int Code(ModestActivityTime time) => time switch
    {
        ModestActivityTime.TenToSixtyMinutes => 2,
        ModestActivityTime.MoreThanSixtyMinutes => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(time), time, "Unknown answer"),
    };

    /// <summary>Regular heavy exercise: 4 for under 30 minutes a week, 5 for 30–60, 6 for 1–3 hours, 7 for more than 3 hours.</summary>
    public static int Code(HeavyActivityTime time) => time switch
    {
        HeavyActivityTime.LessThanThirtyMinutes => 4,
        HeavyActivityTime.ThirtyToSixtyMinutes => 5,
        HeavyActivityTime.OneToThreeHours => 6,
        HeavyActivityTime.MoreThanThreeHours => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(time), time, "Unknown answer"),
    };
}
