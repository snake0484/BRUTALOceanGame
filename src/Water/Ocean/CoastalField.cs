using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// Layer B coastal maps (docs/water-architecture.md): per 4 m cell, depth and, for 12 reference frequencies, the phase lag
/// against deep water (refraction + depth dispersion) and the amplitude factor (shoaling + refraction).
/// Texel layout matches the GPU storage buffer: 28 floats = (depth, active, 0, 0), then (lag, amp) per reference.
/// </summary>
public sealed class CoastalField
{
    public const int Resolution = 256;
    public const float CellSize = 4f;
    public const int RefCount = 12;
    public const int TexelFloats = 28;
    public const float MinOmega = 0.15f;
    public const float MaxOmega = 3.2f;
    public const float BreakingGamma = 0.55f;
    public const float DeepDepth = 1000f;

    /// <summary>Upper clamp on the shoaling/refraction amplitude factor.</summary>
    public const float MaxAmplitudeFactor = 3f;

    private static readonly float LogRange = MathF.Log(MaxOmega / MinOmega);

    public readonly float[] Texels = new float[Resolution * Resolution * TexelFloats];

    /// <summary>World xz of cell (0, 0)'s corner.</summary>
    public Vector2 Origin;

    /// <summary>Direction (radians) the phase maps were solved for.</summary>
    public float Direction;

    public int Version;

    public float WorldSize => Resolution * CellSize;

    public static float RefOmega(int j) => MinOmega * MathF.Exp(LogRange * j / (RefCount - 1));

    /// <summary>Deep-water wavenumber of reference j.</summary>
    public static float RefK0(int j)
    {
        var omega = RefOmega(j);
        return omega * omega / 9.81f;
    }

    /// <summary>Fractional reference index for a frequency, clamped to the table.</summary>
    public static float RefCoord(float omega) =>
        Math.Clamp(MathF.Log(MathF.Max(omega, 1e-4f) / MinOmega) / LogRange * (RefCount - 1), 0f, RefCount - 1.001f);

    /// <summary>
    /// Bilinear sample at world (x, z). Returns false outside the map or where no texel is active (deep water: no lag,
    /// unit amplitude, no cap). Gradients are of the bilinear interpolant, so CPU and GPU agree.
    /// </summary>
    public bool Sample(float x, float z, ref CoastalSample s)
    {
        var fx = (x - Origin.X) / CellSize - 0.5f;
        var fz = (z - Origin.Y) / CellSize - 0.5f;

        if (!(fx >= 0f && fz >= 0f && fx < Resolution - 1 && fz < Resolution - 1))
            return false;

        var ix = (int)fx;
        var iz = (int)fz;
        var tx = fx - ix;
        var tz = fz - iz;
        var t00 = (ix + iz * Resolution) * TexelFloats;
        var t10 = t00 + TexelFloats;
        var t01 = t00 + Resolution * TexelFloats;
        var t11 = t01 + TexelFloats;
        var t = Texels;

        if (t[t00 + 1] + t[t10 + 1] + t[t01 + 1] + t[t11 + 1] <= 0f)
            return false;

        s.Depth = Lerp2(t[t00], t[t10], t[t01], t[t11], tx, tz);

        for (var j = 0; j < RefCount; j++)
        {
            var o = 4 + 2 * j;
            var a = t[t00 + o];
            var b = t[t10 + o];
            var c = t[t01 + o];
            var d = t[t11 + o];
            s.Lag[j] = Lerp2(a, b, c, d, tx, tz);
            s.LagX[j] = float.Lerp(b - a, d - c, tz) / CellSize;
            s.LagZ[j] = float.Lerp(c - a, d - b, tx) / CellSize;
            s.Amp[j] = Lerp2(t[t00 + o + 1], t[t10 + o + 1], t[t01 + o + 1], t[t11 + o + 1], tx, tz);
        }

        return true;
    }

    /// <summary>Bilinear water depth alone at world (x, z), the same read as <see cref="Sample"/>'s; false outside the map or where no texel is active.</summary>
    public bool Depth(float x, float z, out float depth)
    {
        depth = DeepDepth;
        var fx = (x - Origin.X) / CellSize - 0.5f;
        var fz = (z - Origin.Y) / CellSize - 0.5f;

        if (!(fx >= 0f && fz >= 0f && fx < Resolution - 1 && fz < Resolution - 1))
            return false;

        var ix = (int)fx;
        var iz = (int)fz;
        var t00 = (ix + iz * Resolution) * TexelFloats;
        var t10 = t00 + TexelFloats;
        var t01 = t00 + Resolution * TexelFloats;
        var t11 = t01 + TexelFloats;
        var t = Texels;

        if (t[t00 + 1] + t[t10 + 1] + t[t01 + 1] + t[t11 + 1] <= 0f)
            return false;

        depth = Lerp2(t[t00], t[t10], t[t01], t[t11], fx - ix, fz - iz);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp2(float a, float b, float c, float d, float tx, float tz) =>
        float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), tz);
}

[InlineArray(CoastalField.RefCount)]
public struct RefValues
{
    private float _element;
}

public struct CoastalSample
{
    public float Depth;
    public RefValues Lag;
    public RefValues LagX;
    public RefValues LagZ;
    public RefValues Amp;

    /// <summary>Open ocean: no lag, unit amplitude, deep (what a field reports where it has nothing).</summary>
    public void SetDeep()
    {
        Depth = CoastalField.DeepDepth;
        ((Span<float>)Lag).Clear();
        ((Span<float>)LagX).Clear();
        ((Span<float>)LagZ).Clear();
        ((Span<float>)Amp).Fill(1f);
    }

    /// <summary>this = from + (this − from) · t, matching mix(from, this, t) in ocean.vert.</summary>
    public void LerpFrom(in CoastalSample from, float t)
    {
        Depth = float.Lerp(from.Depth, Depth, t);

        for (var j = 0; j < CoastalField.RefCount; j++)
        {
            Lag[j] = float.Lerp(from.Lag[j], Lag[j], t);
            LagX[j] = float.Lerp(from.LagX[j], LagX[j], t);
            LagZ[j] = float.Lerp(from.LagZ[j], LagZ[j], t);
            Amp[j] = float.Lerp(from.Amp[j], Amp[j], t);
        }
    }
}
