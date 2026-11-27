using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// The authored layer's ops (planet-designer-brief.md §3): painted height maps (bilinear, every point, clamped to the
/// relief range) and flatten sites (compact support: exactly the generated value outside reach). Position-only, so
/// seams and peers agree; add, mul, div, sqrt, min and max only. Bounds hold for every LOD.
/// </summary>
internal static partial class LayerOps
{
    private const int MaxSiteCandidates = 128;

    private static void MapAdd(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var map = p.Maps[(int)p.Params[pb]];
        float min = p.Params[pb + 1], max = p.Params[pb + 2];
        var h = r.Row(p.Dst[layer]);

        for (var i = 0; i < h.Length; i++)
            h[i] = Math.Clamp(h[i] + map.Sample(r.X[i], r.Y[i], r.Z[i]), min, max);
    }

    [SkipLocalsInit]
    private static void Sites(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var sites = p.Sites;
        var h = r.Row(p.Dst[layer]);
        double x0 = r.X[0], y0 = r.Y[0], z0 = r.Z[0], spread = 0.0;

        // One pass for the set's reach from its first point, then only the sites that can touch it.
        for (var i = 1; i < h.Length; i++)
        {
            double dx = r.X[i] - x0, dy = r.Y[i] - y0, dz = r.Z[i] - z0;
            spread = Math.Max(spread, dx * dx + dy * dy + dz * dz);
        }

        spread = Math.Sqrt(spread);
        Span<int> near = stackalloc int[MaxSiteCandidates];
        var count = 0;

        for (var k = 0; k < sites.Length; k++)
        {
            ref readonly var s = ref sites[k];
            double dx = s.X - x0, dy = s.Y - y0, dz = s.Z - z0, reach = s.Reach + spread;

            if (dx * dx + dy * dy + dz * dz >= reach * reach)
                continue;

            if (count == MaxSiteCandidates)
            {
                count = -1;
                break;
            }

            near[count++] = k;
        }

        if (count == 0)
            return;

        for (var i = 0; i < h.Length; i++)
        {
            double px = r.X[i], py = r.Y[i], pz = r.Z[i];
            var v = h[i];
            var n = count < 0 ? sites.Length : count;

            for (var c = 0; c < n; c++)
            {
                ref readonly var s = ref sites[count < 0 ? c : near[c]];
                double dx = px - s.X, dy = py - s.Y, dz = pz - s.Z;
                var d2 = dx * dx + dy * dy + dz * dz;

                if (d2 >= (double)s.Reach * s.Reach)
                    continue;

                var d = (float)Math.Sqrt(d2);
                v = d <= s.Core ? s.Target : v + (1f - Smooth01((d - s.Core) * s.InvBlend)) * (s.Target - v);
            }

            h[i] = v;
        }
    }

    private static void MapAddBounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double rho, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var d = p.Dst[layer];
        p.Maps[(int)p.Params[pb]].Bounds(qx, qy, qz, rho, out var mLo, out var mHi);
        float min = p.Params[pb + 1], max = p.Params[pb + 2];
        lo[d] = Math.Clamp(lo[d] + mLo, min, max);
        hi[d] = Math.Clamp(hi[d] + mHi, min, max);
    }

    /// <summary>Each site in reach of the cap pulls toward its target: the hull with the target, or exactly the target where the core holds the cap.</summary>
    private static void SitesBounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double rho, Span<float> lo, Span<float> hi)
    {
        var d = p.Dst[layer];

        foreach (ref readonly var s in p.Sites.AsSpan())
        {
            double dx = s.X - qx, dy = s.Y - qy, dz = s.Z - qz;
            var dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            if (dist >= s.Reach + rho)
                continue;

            if (dist + rho <= s.Core)
            {
                lo[d] = hi[d] = s.Target;
                continue;
            }

            lo[d] = MathF.Min(lo[d], s.Target);
            hi[d] = MathF.Max(hi[d], s.Target);
        }
    }
}
