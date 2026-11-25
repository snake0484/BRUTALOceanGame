using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The queryable ocean surface (CLAUDE.md, Water layers): the swell, depth-limited by min(1, 0.55 h / Hs) from the
/// world's analytic depth. The GPU applies the same cap from its 1 km bathymetry map, so the two agree only inside that
/// window (dry pits included); the GPU's FFT detail and shore wash are not queryable. Buoyancy and other gameplay queries go through here rather than the swell. Read-only between ticks
/// (the swell's SetTime runs on the main thread in the tick), so safe to call from jobs then; allocation-free.
/// </summary>
public sealed class OceanSurface(OceanSwell swell, IShoreDepth? shore = null)
{
    public OceanSwell Swell => swell;

    /// <summary>Height, normal, orbital velocity and Jacobian at world (x, z).</summary>
    public WaveSample Sample(float x, float z) => swell.Sample(x, z, shore);

    /// <summary>Water surface height at world (x, z).</summary>
    public float SampleHeight(float x, float z) => swell.SampleHeight(x, z, shore);

    public float3 SampleNormal(float x, float z) => swell.SampleNormal(x, z, shore);

    public float3 SampleVelocity(float x, float z) => swell.SampleVelocity(x, z, shore);

    public void SampleHeights(ReadOnlySpan<float2> positions, Span<float> heights) => swell.SampleHeights(positions, heights, shore);

    /// <summary>Surface position of the water particle whose rest position is (x0, z0).</summary>
    public float3 SurfacePoint(float x0, float z0) => swell.SurfacePoint(x0, z0, shore);
}
