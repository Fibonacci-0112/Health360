namespace Health360.Domain.Physiology;

/// <summary>
/// The sex a published equation was fitted for (<c>profiles.sex_for_calcs</c>). Equations differ by sex,
/// so a calculation that needs it returns nothing when it isn't set.
/// </summary>
public enum Sex
{
    Female,
    Male,
}

public static class Ages
{
    /// <summary>Whole years from <paramref name="birthDate"/> to <paramref name="asOf"/> (both local dates).</summary>
    public static int Years(DateOnly birthDate, DateOnly asOf)
    {
        if (asOf < birthDate)
        {
            throw new ArgumentOutOfRangeException(nameof(asOf), asOf, "The date is before the birth date");
        }

        var years = asOf.Year - birthDate.Year;
        return asOf < birthDate.AddYears(years) ? years - 1 : years;
    }
}
