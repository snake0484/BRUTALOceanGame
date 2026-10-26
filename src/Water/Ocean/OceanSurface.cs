using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The queryable ocean surface (CLAUDE.md, Water layers): the swell (layers A and B) with the breaker faces drawn on it
/// (<see cref="BreakerFace"/>, with each crest's break progress from <paramref name="breaking"/>), the same wave the GPU
/// draws apart from its high-frequency FFT detail. Buoyancy, surf and
/// other gameplay queries go through here rather than a layer. Read-only between ticks (the swell's SetTime runs on the
/// main thread in the tick), so safe to call from jobs then; allocation-free.
/// </summary>
public sealed class OceanSurface
{
    private readonly OceanSwell swell;
    private readonly BreakerSet? breaking;

    /// <summary>Off: the bare swell, as the GPU draws it with --no-faces.</summary>
    public bool FacesEnabled = true;

    public OceanSurface(OceanSwell swell, BreakerSet? breaking = null)
    {
        this.swell = swell;
        this.breaking = breaking;
        if (breaking is not null)
            swell.Breaking = breaking;
    }

    public OceanSwell Swell => swell;

    /// <summary>Height, normal, orbital velocity, Jacobian and breaker whitewater at world (x, z).</summary>
    public WaveSample Sample(float x, float z)
    {
        var shape = new FaceShape(swell, breaking, FacesEnabled);
        return swell.Sample(x, z, ref shape);
    }

    /// <summary>Water surface height at world (x, z).</summary>
    public float SampleHeight(float x, float z) => Sample(x, z).Height;

    public float3 SampleNormal(float x, float z) => Sample(x, z).Normal;

    public float3 SampleVelocity(float x, float z) => Sample(x, z).Velocity;

    public void SampleHeights(ReadOnlySpan<float2> positions, Span<float> heights)
    {
        for (var i = 0; i < positions.Length; i++)
            heights[i] = SampleHeight(positions[i].X, positions[i].Y);
    }

    /// <summary>The surface at rest position (x0, z0): height, displacement and their partials.</summary>
    public RestPoint SampleRest(float x0, float z0)
    {
        var shape = new FaceShape(swell, breaking, FacesEnabled);
        return swell.SampleRest(x0, z0, ref shape);
    }

    /// <summary>Surface position of the water particle whose rest position is (x0, z0).</summary>
    public float3 SurfacePoint(float x0, float z0)
    {
        var p = SampleRest(x0, z0);
        return new float3(x0 + p.Dx, p.Height, z0 + p.Dz);
    }

    private readonly struct FaceShape(OceanSwell swell, BreakerSet? breaking, bool enabled) : IRestShape
    {
        public void Apply(float x0, float z0, bool previous, ref RestPoint p)
        {
            if (enabled)
                p.Whitewater = MathF.Max(p.Whitewater, BreakerFace.Apply(swell, breaking, x0, z0, ref p, previous));
        }
    }
}
