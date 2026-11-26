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
    /// |Δn| ≤ this × |Δx| for <see cref="Gradient3(double, double, double, uint)"/>. ∇n is linear in the eight corner
    /// gradients, each one of the 12 edge vectors, so the steepest slope along a direction e at an in-cell point is the
    /// sum over corners of the two largest |components| of (w·I + (f − c)·∇wᵀ)·e. Maximised over the cell and e it is
    /// 3.75 = 2 × the fade's steepest slope, at the cell centre along an axis (hill climb, 400 restarts; the sampled
    /// maximum over 2 × 10⁶ points is 3.20). Height bounds rely on it.
    /// </summary>
    public const float GradientLipschitz = 3.8f;

    /// <summary>
    /// |Δ∇n| ≤ this × |Δx|: the Hessian's largest stretch, found the same way (the sum over corners of the two largest
    /// |components| of ∂(w·e₁ + (f − c)·∇w·e₁)/∂e₂; hill climbs from 2,600 starts reach 14.47). Gradient bounds rely on it.
    /// </summary>
    public const float SlopeLipschitz = 15f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Fold(long v) => (uint)v ^ ((uint)(v >> 32) * 0x9E3779B1u);

    /// <summary><see cref="Grad3"/>'s 16 gradients as a table (x, y, z, pad): a dot product instead of unpredictable branches.</summary>
    internal static readonly float[] GradientTable =
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FadeSlope(float t) => 30f * t * t * (t - 1f) * (t - 1f);

    /// <summary><see cref="Gradient3(double, double, double, uint)"/> with its analytic derivative in lattice units; the value is the same bits.</summary>
    public static float Gradient3(double x, double y, double z, uint seed, out float dx, out float dy, out float dz)
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
        var g = GradientTable;
        var i000 = (int)(Mix(hx0 ^ hy0 ^ hz0) & 15) * 4;
        var i100 = (int)(Mix(hx1 ^ hy0 ^ hz0) & 15) * 4;
        var i010 = (int)(Mix(hx0 ^ hy1 ^ hz0) & 15) * 4;
        var i110 = (int)(Mix(hx1 ^ hy1 ^ hz0) & 15) * 4;
        var i001 = (int)(Mix(hx0 ^ hy0 ^ hz1) & 15) * 4;
        var i101 = (int)(Mix(hx1 ^ hy0 ^ hz1) & 15) * 4;
        var i011 = (int)(Mix(hx0 ^ hy1 ^ hz1) & 15) * 4;
        var i111 = (int)(Mix(hx1 ^ hy1 ^ hz1) & 15) * 4;
        var n000 = g[i000] * fx + g[i000 + 1] * fy + g[i000 + 2] * fz;
        var n100 = g[i100] * (fx - 1f) + g[i100 + 1] * fy + g[i100 + 2] * fz;
        var n010 = g[i010] * fx + g[i010 + 1] * (fy - 1f) + g[i010 + 2] * fz;
        var n110 = g[i110] * (fx - 1f) + g[i110 + 1] * (fy - 1f) + g[i110 + 2] * fz;
        var n001 = g[i001] * fx + g[i001 + 1] * fy + g[i001 + 2] * (fz - 1f);
        var n101 = g[i101] * (fx - 1f) + g[i101 + 1] * fy + g[i101 + 2] * (fz - 1f);
        var n011 = g[i011] * fx + g[i011 + 1] * (fy - 1f) + g[i011 + 2] * (fz - 1f);
        var n111 = g[i111] * (fx - 1f) + g[i111 + 1] * (fy - 1f) + g[i111 + 2] * (fz - 1f);
        var a = Lerp(Lerp(n000, n100, u), Lerp(n010, n110, u), v);
        var b = Lerp(Lerp(n001, n101, u), Lerp(n011, n111, u), v);

        dx = Blend(g[i000], g[i100], g[i010], g[i110], g[i001], g[i101], g[i011], g[i111], u, v, w)
            + FadeSlope(fx) * Lerp(Lerp(n100 - n000, n110 - n010, v), Lerp(n101 - n001, n111 - n011, v), w);
        dy = Blend(g[i000 + 1], g[i100 + 1], g[i010 + 1], g[i110 + 1], g[i001 + 1], g[i101 + 1], g[i011 + 1], g[i111 + 1], u, v, w)
            + FadeSlope(fy) * Lerp(Lerp(n010 - n000, n110 - n100, u), Lerp(n011 - n001, n111 - n101, u), w);
        dz = Blend(g[i000 + 2], g[i100 + 2], g[i010 + 2], g[i110 + 2], g[i001 + 2], g[i101 + 2], g[i011 + 2], g[i111 + 2], u, v, w)
            + FadeSlope(fz) * (b - a);
        return Lerp(a, b, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Blend(float c000, float c100, float c010, float c110, float c001, float c101, float c011, float c111, float u, float v, float w) =>
        Lerp(Lerp(Lerp(c000, c100, u), Lerp(c010, c110, u), v), Lerp(Lerp(c001, c101, u), Lerp(c011, c111, u), v), w);

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
