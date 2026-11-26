namespace BrutalGame.Crafting;

/// <summary>
/// Integer fixed point. Fractions (purity, potency, yields, spreads) are parts per million; products go through
/// <see cref="Int128"/> so masses up to 9e18 mg never overflow and a gate compares exactly, with no rounding.
/// </summary>
public static class Fixed
{
    public const int One = 1_000_000;
    public const long MgPerKg = 1_000_000;
    public const long NgPerMg = 1_000_000;

    public static int Ppm(double fraction) => (int)Math.Round(fraction * One, MidpointRounding.AwayFromZero);

    public static long Mg(double kg) => (long)Math.Round(kg * MgPerKg, MidpointRounding.AwayFromZero);

    public static long MulDiv(long a, long b, long c) => (long)((Int128)a * b / c);

    public static long MulDivCeil(long a, long b, long c) => CeilDiv((Int128)a * b, c);

    /// <summary>Ceiling of a non-negative numerator over a positive denominator.</summary>
    public static long CeilDiv(Int128 numerator, Int128 denominator) => (long)((numerator + denominator - 1) / denominator);

    /// <summary><paramref name="part"/> / <paramref name="whole"/> in ppm, floored; 0 for an empty whole.</summary>
    public static int Fraction(long part, long whole) => whole <= 0 ? 0 : (int)((Int128)part * One / whole);

    /// <summary>True when part / whole ≥ ppm / 1e6, exactly.</summary>
    public static bool AtLeast(long part, long whole, int ppm) => (Int128)part * One >= (Int128)ppm * whole;

    /// <summary>True when part / whole ≤ ppm / 1e6, exactly.</summary>
    public static bool AtMost(long part, long whole, int ppm) => (Int128)part * One <= (Int128)ppm * whole;

    /// <summary>SplitMix64: deterministic noise for crude-refinery errors, identical on every machine.</summary>
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary><c>2·hash01(seed, id) − 1</c> in ppm, uniform over [−1e6, 1e6].</summary>
    public static long SignedNoisePpm(ulong seed, long id) => (long)(Mix(seed ^ Mix((ulong)id)) % (2UL * One + 1)) - One;
}
