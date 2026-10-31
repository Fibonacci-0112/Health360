namespace Health360.Domain.Physiology;

/// <summary>
/// The <c>metric_definitions.code</c> values the calculations read. The table defines every metric; these are
/// the ones Domain needs by name.
/// </summary>
public static class MetricCodes
{
    public const string Weight = "weight";
    public const string BodyFatPercent = "body_fat_pct";
    public const string Neck = "circ_neck";

    /// <summary>Abdomen II, at the navel: the men's Navy-method site.</summary>
    public const string WaistAtNavel = "circ_waist_navel";

    /// <summary>Abdomen I, at the narrowest point: the women's Navy-method site.</summary>
    public const string WaistAtNarrowest = "circ_waist_narrowest";

    public const string Hip = "circ_hip";
    public const string RestingHr = "resting_hr";
    public const string MaxHr = "max_hr";
    public const string Lthr = "lthr";
    public const string Rmr = "rmr";
    public const string Vo2Max = "vo2max";
    public const string Lt1Hr = "lt1_hr";
    public const string Lt2Hr = "lt2_hr";
    public const string Vt1Hr = "vt1_hr";
    public const string Vt2Hr = "vt2_hr";
}

/// <summary>The stored codes of <see cref="MeasurementSource"/> (<c>measurements.source</c>).</summary>
public static class MeasurementSources
{
    public static string Code(MeasurementSource source) => source switch
    {
        MeasurementSource.Lab => "lab",
        MeasurementSource.FieldTest => "field_test",
        MeasurementSource.Device => "device",
        MeasurementSource.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown source"),
    };

    public static bool TryParse(string? code, out MeasurementSource source)
    {
        foreach (var candidate in Enum.GetValues<MeasurementSource>())
        {
            if (Code(candidate) == code)
            {
                source = candidate;
                return true;
            }
        }

        source = default;
        return false;
    }
}
