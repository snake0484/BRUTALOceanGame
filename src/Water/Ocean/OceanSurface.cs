using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The queryable ocean surface (CLAUDE.md, Water layers): the swell (layers A and B), the same wave the GPU draws apart
/// from its high-frequency FFT detail and the shore wash. Buoyancy and other gameplay queries go through here rather than
/// a layer. Read-only between ticks (the swell's SetTime runs on the main thread in the tick), so safe to call from jobs
/// then; allocation-free.
/// </summary>
public sealed class OceanSurface(OceanSwell swell)
{
    public OceanSwell Swell => swell;

    /// <summary>Height, normal, orbital velocity and Jacobian at world (x, z).</summary>
    public WaveSample Sample(float x, float z) => swell.Sample(x, z);

    /// <summary>Water surface height at world (x, z).</summary>
    public float SampleHeight(float x, float z) => swell.SampleHeight(x, z);

    public float3 SampleNormal(float x, float z) => swell.SampleNormal(x, z);

    public float3 SampleVelocity(float x, float z) => swell.SampleVelocity(x, z);

    public void SampleHeights(ReadOnlySpan<float2> positions, Span<float> heights) => swell.SampleHeights(positions, heights);

    /// <summary>Surface position of the water particle whose rest position is (x0, z0).</summary>
    public float3 SurfacePoint(float x0, float z0) => swell.SurfacePoint(x0, z0);
}
