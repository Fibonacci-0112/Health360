namespace Health360.Domain.Body;

/// <summary>Definitions, not estimates: they hold exactly for whatever inputs they're given.</summary>
public static class BodyComposition
{
    /// <summary>Body mass index, kg/m².</summary>
    public static double Bmi(double weightKg, double heightCm)
    {
        Positive(weightKg, nameof(weightKg));
        Positive(heightCm, nameof(heightCm));
        var metres = heightCm / 100;
        return weightKg / (metres * metres);
    }

    /// <summary>
    /// Fat-free mass in kg from body weight and a body-fat percentage. It is only as good as the
    /// percentage: from DEXA it is a measured value, from the Navy method or a scale it is an estimate.
    /// </summary>
    public static double FatFreeMassKg(double weightKg, double percentFat)
    {
        Positive(weightKg, nameof(weightKg));
        if (!double.IsFinite(percentFat) || percentFat is < 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentFat), percentFat, "Body fat must be in [0, 100)");
        }

        return weightKg * (1 - (percentFat / 100));
    }

    private static void Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be a positive number");
        }
    }
}
