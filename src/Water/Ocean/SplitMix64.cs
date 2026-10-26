namespace BrutalGame.Water.Ocean;

/// <summary>
/// Tiny deterministic RNG. Stable across runtimes, unlike System.Random.
/// </summary>
internal struct SplitMix64(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (Next() >> 11) * (1.0 / (1UL << 53));

    public static ulong Mix(ulong a, ulong b) => new SplitMix64(a ^ (b * 0xD1B54A32D192ED03UL)).Next();
}
