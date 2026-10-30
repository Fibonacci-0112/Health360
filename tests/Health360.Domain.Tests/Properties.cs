using FsCheck;
using FsCheck.Fluent;

namespace Health360.Domain.Tests;

/// <summary>FsCheck generators and a runner shared by the property tests.</summary>
internal static class Properties
{
    /// <summary>Runs <paramref name="property"/> on 100 generated values and fails with the shrunk counterexample.</summary>
    public static void Holds<T>(Gen<T> gen, Func<T, bool> property) =>
        Prop.ForAll(gen.ToArbitrary(), property).QuickCheckThrowOnFailure();

    /// <summary>
    /// Doubles in [min, max]: mostly uniform, plus the endpoints and values spread over many orders of
    /// magnitude, so tiny and huge values get tested as well as typical ones.
    /// </summary>
    public static Gen<double> Double(double min, double max)
    {
        var uniform = Gen.Choose(0, int.MaxValue).Select(i => min + ((max - min) * i / int.MaxValue));
        var maxExponent = Math.Log10(Math.Max(Math.Abs(min), Math.Abs(max)));
        var logScale = Gen.Zip(Gen.Choose(0, int.MaxValue), Gen.Elements(-1.0, 1.0))
            .Select(t => t.Item2 * Math.Pow(10, -300 + ((maxExponent + 300) * t.Item1 / int.MaxValue)))
            .Where(v => v >= min && v <= max);
        return Gen.Frequency((6, uniform), (2, logScale), (1, Gen.Elements(min, max)));
    }

    public static Gen<int> Int(int min, int max) => Gen.Choose(min, max);

    /// <summary>Close within a relative tolerance, with an absolute floor of <paramref name="rel"/> near zero.</summary>
    public static bool Close(double a, double b, double rel = 1e-12) =>
        Math.Abs(a - b) <= rel * Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), 1);
}
