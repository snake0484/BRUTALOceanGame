using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

internal static class Noise
{
    private const float Diag = 0.70710678f;
    private const uint OctaveSalt = 0x9E3779B9u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return h;
    }

    public static uint SeedOf(ulong seed) => Mix((uint)seed ^ Mix((uint)(seed >> 32) + 0x632BE5ABu));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int x, int y, uint seed) => Mix(seed ^ ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Hash(int x, int y, int z, uint seed) =>
        Mix(seed ^ ((uint)x * 0x8DA6B343u) ^ ((uint)y * 0xD8163841u) ^ ((uint)z * 0xCB1AB31Fu));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad2(uint h, float x, float y) => (h & 7) switch
    {
        0 => x,
        1 => -x,
        2 => y,
        3 => -y,
        4 => (x + y) * Diag,
        5 => (y - x) * Diag,
        6 => (x - y) * Diag,
        _ => -(x + y) * Diag,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Grad3(uint hash, float x, float y, float z)
    {
        var h = (int)(hash & 15);
        var u = h < 8 ? x : y;
        var v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    public static float Gradient2(float x, float y, uint seed)
    {
        var fx0 = MathF.Floor(x);
        var fy0 = MathF.Floor(y);
        var x0 = (int)fx0;
        var y0 = (int)fy0;
        var fx = x - fx0;
        var fy = y - fy0;
        var u = Fade(fx);
        var v = Fade(fy);
        var n00 = Grad2(Hash(x0, y0, seed), fx, fy);
        var n10 = Grad2(Hash(x0 + 1, y0, seed), fx - 1f, fy);
        var n01 = Grad2(Hash(x0, y0 + 1, seed), fx, fy - 1f);
        var n11 = Grad2(Hash(x0 + 1, y0 + 1, seed), fx - 1f, fy - 1f);
        return Lerp(Lerp(n00, n10, u), Lerp(n01, n11, u), v) * 1.41421356f;
    }

    public static float Gradient3(float x, float y, float z, uint seed)
    {
        var fx0 = MathF.Floor(x);
        var fy0 = MathF.Floor(y);
        var fz0 = MathF.Floor(z);
        var x0 = (int)fx0;
        var y0 = (int)fy0;
        var z0 = (int)fz0;
        var fx = x - fx0;
        var fy = y - fy0;
        var fz = z - fz0;
        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);
        var n000 = Grad3(Hash(x0, y0, z0, seed), fx, fy, fz);
        var n100 = Grad3(Hash(x0 + 1, y0, z0, seed), fx - 1f, fy, fz);
        var n010 = Grad3(Hash(x0, y0 + 1, z0, seed), fx, fy - 1f, fz);
        var n110 = Grad3(Hash(x0 + 1, y0 + 1, z0, seed), fx - 1f, fy - 1f, fz);
        var n001 = Grad3(Hash(x0, y0, z0 + 1, seed), fx, fy, fz - 1f);
        var n101 = Grad3(Hash(x0 + 1, y0, z0 + 1, seed), fx - 1f, fy, fz - 1f);
        var n011 = Grad3(Hash(x0, y0 + 1, z0 + 1, seed), fx, fy - 1f, fz - 1f);
        var n111 = Grad3(Hash(x0 + 1, y0 + 1, z0 + 1, seed), fx - 1f, fy - 1f, fz - 1f);
        var a = Lerp(Lerp(n000, n100, u), Lerp(n010, n110, u), v);
        var b = Lerp(Lerp(n001, n101, u), Lerp(n011, n111, u), v);
        return Lerp(a, b, w);
    }

    /// <summary>
    /// |Δn| ≤ this × |Δx| for <see cref="Gradient3(double, double, double, uint)"/>: per axis |∂n| ≤ 1.875 (quintic fade
    /// slope) × 4 (two |g·d| ≤ 2) + 1 = 8.5, times √3 for the L2 step. Loose but rigorous; height bounds rely on it.
    /// </summary>
    public const float GradientLipschitz = 14.73f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Fold(long v) => (uint)v ^ ((uint)(v >> 32) * 0x9E3779B1u);

    /// <summary><see cref="Grad3"/>'s 16 gradients as a table (x, y, z, pad): a dot product instead of unpredictable branches.</summary>
    private static readonly float[] GradientTable =
    [
        1f, 1f, 0f, 0f, -1f, 1f, 0f, 0f, 1f, -1f, 0f, 0f, -1f, -1f, 0f, 0f,
        1f, 0f, 1f, 0f, -1f, 0f, 1f, 0f, 1f, 0f, -1f, 0f, -1f, 0f, -1f, 0f,
        0f, 1f, 1f, 0f, 0f, -1f, 1f, 0f, 0f, 1f, -1f, 0f, 0f, -1f, -1f, 0f,
        1f, 1f, 0f, 0f, 0f, -1f, 1f, 0f, -1f, 1f, 0f, 0f, 0f, -1f, -1f, 0f,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dot(uint hash, float x, float y, float z)
    {
        var i = (int)(hash & 15) * 4;
        var g = GradientTable;
        return g[i] * x + g[i + 1] * y + g[i + 2] * z;
    }

    /// <summary>
    /// Double-lattice gradient noise for planet-scale coordinates: the cell index comes from a double floor as long and is
    /// hashed, only the in-cell fraction is float (a float position steps by 6 cm at 1e6 m). Branch-free gradients.
    /// </summary>
    public static float Gradient3(double x, double y, double z, uint seed)
    {
        var fx0 = Math.Floor(x);
        var fy0 = Math.Floor(y);
        var fz0 = Math.Floor(z);
        var x0 = (long)fx0;
        var y0 = (long)fy0;
        var z0 = (long)fz0;
        var fx = (float)(x - fx0);
        var fy = (float)(y - fy0);
        var fz = (float)(z - fz0);
        var hx0 = seed ^ (Fold(x0) * 0x8DA6B343u);
        var hx1 = seed ^ (Fold(x0 + 1) * 0x8DA6B343u);
        var hy0 = Fold(y0) * 0xD8163841u;
        var hy1 = Fold(y0 + 1) * 0xD8163841u;
        var hz0 = Fold(z0) * 0xCB1AB31Fu;
        var hz1 = Fold(z0 + 1) * 0xCB1AB31Fu;
        var u = Fade(fx);
        var v = Fade(fy);
        var w = Fade(fz);
        var n000 = Dot(Mix(hx0 ^ hy0 ^ hz0), fx, fy, fz);
        var n100 = Dot(Mix(hx1 ^ hy0 ^ hz0), fx - 1f, fy, fz);
        var n010 = Dot(Mix(hx0 ^ hy1 ^ hz0), fx, fy - 1f, fz);
        var n110 = Dot(Mix(hx1 ^ hy1 ^ hz0), fx - 1f, fy - 1f, fz);
        var n001 = Dot(Mix(hx0 ^ hy0 ^ hz1), fx, fy, fz - 1f);
        var n101 = Dot(Mix(hx1 ^ hy0 ^ hz1), fx - 1f, fy, fz - 1f);
        var n011 = Dot(Mix(hx0 ^ hy1 ^ hz1), fx, fy - 1f, fz - 1f);
        var n111 = Dot(Mix(hx1 ^ hy1 ^ hz1), fx - 1f, fy - 1f, fz - 1f);
        var a = Lerp(Lerp(n000, n100, u), Lerp(n010, n110, u), v);
        var b = Lerp(Lerp(n001, n101, u), Lerp(n011, n111, u), v);
        return Lerp(a, b, w);
    }

    public static float Fbm2(float x, float y, int octaves, uint seed)
    {
        var sum = 0f;
        var amp = 1f;
        var norm = 0f;

        for (var i = 0; i < octaves; i++)
        {
            sum += Gradient2(x, y, seed) * amp;
            norm += amp;
            amp *= 0.5f;
            x = x * 2.03f + 17.31f;
            y = y * 2.03f - 9.17f;
            seed += OctaveSalt;
        }

        return sum / norm;
    }

    public static float Fbm3(float x, float y, float z, int octaves, uint seed)
    {
        var sum = 0f;
        var amp = 1f;
        var norm = 0f;

        for (var i = 0; i < octaves; i++)
        {
            sum += Gradient3(x, y, z, seed) * amp;
            norm += amp;
            amp *= 0.5f;
            x = x * 2.03f + 17.31f;
            y = y * 2.03f - 9.17f;
            z = z * 2.03f + 5.53f;
            seed += OctaveSalt;
        }

        return sum / norm;
    }
}
