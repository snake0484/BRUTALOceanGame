using BrutalGame.Water.Coast;

namespace BrutalGame.Water.Ocean;

/// <summary>The surf carrier at a point of a coast map (<see cref="SurfCarrier.At"/>).</summary>
public struct SurfCarrierPoint
{
    /// <summary>Phase wrapped to [-π, π): 0 on a crest.</summary>
    public float Phase;

    /// <summary>Unit direction of travel in the window's plane, and the local wavenumber k(h).</summary>
    public float DirX;
    public float DirZ;
    public float K;

    /// <summary>Height crest to trough before breaking (m), water depth (m), travel since the break began (m) and breaker type (0 spilling .. 1 plunging).</summary>
    public float Unbroken;
    public float Depth;
    public float BreakDistance;
    public float Plunge;
}

/// <summary>
/// The surf carrier: the swell's peak followed as one coherent wave over a coast map (<see cref="CoastAtlas"/>). Its
/// phase is the map's spatial phase plus the map's phase at the time; crests are where it is a multiple of 2π. The
/// random swell never reaches breaking height, so the carrier is what breaks (<see cref="BreakerFace"/>). Pure
/// functions of the map, the same on CPU and GPU (ocean_breaker.glsl): keep the two in step.
/// </summary>
public static class SurfCarrier
{
    public const float Gravity = 9.81f;

    /// <summary>The coherent wave breaks at this height / depth and is held there.</summary>
    public const float BreakingGamma = 0.78f;

    /// <summary>The carrier comes out of the random swell as it shoals: none of it at k h ≥ <see cref="IncoherentKh"/>, all by <see cref="CoherentKh"/>.</summary>
    public const float CoherentKh = 0.4f;
    public const float IncoherentKh = 1.2f;

    /// <summary>The carrier's wavenumber is taken at no shallower than this (m).</summary>
    public const float SpentDepthK = 0.3f;

    /// <summary>Crest peaking s of the bound harmonics (1, s, s²) from H / h over [<see cref="SharpFrom"/>, <see cref="SharpFull"/>].</summary>
    public const float MaxSharpness = 0.55f;
    public const float SharpFrom = 0.1f;
    public const float SharpFull = 0.45f;

    /// <summary>Explicit wavenumber (Fenton and McKee 1990): k0 / tanh((k0 h)^(3/4))^(2/3), within about 1.5% of <see cref="Jonswap.WaveNumber"/>.</summary>
    public static float WaveNumber(float omega, float depth)
    {
        var deep = omega * omega / Gravity;
        var kh = deep * MathF.Max(depth, 1e-3f);
        return kh > 10f ? deep : deep / MathF.Pow(MathF.Tanh(MathF.Pow(kh, 0.75f)), 2f / 3f);
    }

    /// <summary>Share of the carrier's energy that is coherent at local k h.</summary>
    public static float Coherence(float kh) => 1f - Smooth((kh - CoherentKh) / (IncoherentKh - CoherentKh));

    /// <summary>The carrier's height crest to trough before breaking, from its deep-water height and the map's shoaling factor.</summary>
    public static float Unbroken(float deepHeight, float omega, float depth, float shoaling)
    {
        var h = MathF.Max(depth, 0f);
        return deepHeight * MathF.Sqrt(Coherence(WaveNumber(omega, h) * h)) * shoaling;
    }

    /// <summary>γ h / H unclamped: below 1 the wave is past its breaking limit.</summary>
    public static float RawCap(float unbroken, float depth) => unbroken > 1e-4f ? BreakingGamma * MathF.Max(depth, 0f) / unbroken : 2f;

    public static float Sharpness(float height, float depth) =>
        MaxSharpness * Smooth((height / MathF.Max(depth, 0.1f) - SharpFrom) / (SharpFull - SharpFrom));

    /// <summary>
    /// The carrier at local (x, z) metres of the map, <paramref name="phase0"/> being the map's phase at the time
    /// (<see cref="CoastAtlas.PhaseAt"/>).
    /// </summary>
    public static SurfCarrierPoint At(CoastAtlas atlas, float x, float z, float phase0)
    {
        atlas.Sample(x, z, out var s);
        var carrier = atlas.Key.Carrier;
        var depth = MathF.Max(s.Depth, 0f);
        var length = MathF.Max(MathF.Sqrt(s.DirX * s.DirX + s.DirZ * s.DirZ), 1e-6f);

        return new SurfCarrierPoint
        {
            Phase = Wrap(s.Phase + phase0),
            DirX = s.DirX / length,
            DirZ = s.DirZ / length,
            K = WaveNumber(carrier.Omega, MathF.Max(depth, SpentDepthK)),
            Unbroken = Unbroken(atlas.Height, carrier.Omega, depth, s.Amplitude),
            Depth = s.Depth,
            BreakDistance = s.BreakDistance,
            Plunge = s.Plunge,
        };
    }

    /// <summary>Phase wrapped to [-π, π): 0 on the nearest crest.</summary>
    public static float Wrap(float phase) => phase - MathF.Tau * MathF.Floor(phase / MathF.Tau + 0.5f);

    internal static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
