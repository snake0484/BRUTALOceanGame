using System.Runtime.CompilerServices;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The queryable ocean surface, one definition for the whole planet (review C5): the swell laid on the sea sphere by
/// <see cref="SeaTiles"/>, depth-limited by min(1, 0.55 h / Hs) from the world's analytic depth (review C7). Server and
/// clients evaluate the same function, so buoyancy can be predicted; the GPU's low band mirrors it
/// (<see cref="SeaSlots"/>), its FFT detail and shore wash are not queryable. Read-only between ticks (the swell's
/// SetTime runs on the main thread in the tick), so safe to call from jobs then; allocation-free.
/// </summary>
public sealed unsafe class OceanSurface
{
    /// <summary>
    /// Depth (m) past which the cap is settled at the query point without looking at the rest position: land this far
    /// above the sea is flat, and water this much deeper than Hs / 0.55 is the open swell. The chop moves a point a few
    /// metres at most, so only shores steeper than 1 in 1 could differ.
    /// </summary>
    public const float CapMargin = 4f;

    private readonly OceanSwell _swell;
    private readonly SeaTiles _tiles;
    private readonly IShoreDepth? _shore;
    private readonly double _tickSeconds;

    public OceanSurface(OceanSwell swell, SeaTiles tiles, double tickSeconds, IShoreDepth? shore = null)
    {
        _swell = swell;
        _tiles = tiles;
        _tickSeconds = tickSeconds;
        _shore = shore;
    }

    public OceanSwell Swell => _swell;

    public SeaTiles Tiles => _tiles;

    /// <summary>The query point on the sea: the tiles there, the point in each tile's plane and each tile's turn into the first's.</summary>
    private struct Site
    {
        public int Count;
        public fixed float U[SeaTiles.MaxBlend];
        public fixed float V[SeaTiles.MaxBlend];
        public fixed float Weight[SeaTiles.MaxBlend];
        public fixed float Turn[4 * SeaTiles.MaxBlend];
        public double3 Dir;
        public double3 E1;
        public double3 E2;
        public float Lead;
    }

    /// <summary>
    /// Height above the sea sphere, normal, orbital velocity (planet frame) and Jacobian along the radial through
    /// <paramref name="planetLocal"/> at <paramref name="tick"/> (the swell's own tick, or near it).
    /// </summary>
    public WaveSample Sample(in double3 planetLocal, long tick)
    {
        Locate(planetLocal, tick, out var site);
        var cap = Invert(ref site, out var du, out var dv);

        if (cap <= 0f)
            return new WaveSample { Height = 0f, Normal = Float(site.Dir), Velocity = default, Jacobian = 1f };

        Sum(site, du, dv, out var sums);
        var slopeX = -cap * sums.Hx;
        var slopeZ = -cap * sums.Hz;
        var dxx = 1f - cap * sums.Jxx;
        var dzz = 1f - cap * sums.Jzz;
        var dxz = -cap * sums.Jxz;
        var tx = site.E1 * dxx + site.E2 * dxz + site.Dir * slopeX;
        var tz = site.E1 * dxz + site.E2 * dzz + site.Dir * slopeZ;

        return new WaveSample
        {
            Height = cap * sums.Height,
            Normal = Float(double3.Normalize(double3.Cross(tz, tx))),
            Velocity = Float((site.E1 * sums.Ux + site.E2 * sums.Uz + site.Dir * sums.Uy) * cap),
            Jacobian = dxx * dzz - dxz * dxz,
        };
    }

    /// <summary>Water surface height above the sea sphere along the radial through <paramref name="planetLocal"/>.</summary>
    public float SampleHeight(in double3 planetLocal, long tick)
    {
        Locate(planetLocal, tick, out var site);
        var cap = Invert(ref site, out var du, out var dv);

        if (cap <= 0f)
            return 0f;

        var height = 0f;

        for (var k = 0; k < site.Count; k++)
        {
            Rest(site, k, du, dv, out var u, out var v);
            height += site.Weight[k] * _swell.SumHeight(u, v, site.Lead);
        }

        return cap * height;
    }

    /// <summary>
    /// Where the water particle resting on the sea sphere at unit direction <paramref name="rest"/> is (planet frame),
    /// and the depth cap there.
    /// </summary>
    public double3 SurfacePoint(in double3 rest, long tick, out float cap)
    {
        Locate(rest, tick, out var site);
        cap = Cap(site.Dir);
        Sum(site, 0f, 0f, out var sums);
        var radius = _tiles.SeaRadius;
        var moved = double3.Normalize(site.Dir * radius + (site.E1 * sums.Dx + site.E2 * sums.Dz) * cap);
        return moved * (radius + cap * sums.Height);
    }

    private void Locate(in double3 planetLocal, long tick, out Site site)
    {
        site = default;
        var dir = double3.Normalize(planetLocal);
        _tiles.Locate(dir, out var blend);
        var first = blend.Tile[0];
        _tiles.Lift(first, dir, out site.E1, out site.E2);
        _tiles.Plane(first, dir, out var u, out var v);
        site.Dir = dir;
        site.Lead = (float)(tick * _tickSeconds - _swell.Time);
        site.Count = blend.Count;
        site.U[0] = (float)u;
        site.V[0] = (float)v;
        site.Weight[0] = blend.Weight[0];
        site.Turn[0] = 1f;
        site.Turn[3] = 1f;

        if (blend.Count == 1)
            return;

        _tiles.Axes(first, out var east, out _, out var south);

        for (var k = 1; k < blend.Count; k++)
        {
            var tile = blend.Tile[k];
            _tiles.Plane(tile, dir, out u, out v);
            _tiles.Lift(tile, dir, out var e1, out var e2);
            site.U[k] = (float)u;
            site.V[k] = (float)v;
            site.Weight[k] = blend.Weight[k];
            site.Turn[4 * k] = (float)double3.Dot(e1, east);
            site.Turn[4 * k + 1] = (float)double3.Dot(e2, east);
            site.Turn[4 * k + 2] = (float)double3.Dot(e1, south);
            site.Turn[4 * k + 3] = (float)double3.Dot(e2, south);
        }
    }

    /// <summary>
    /// The rest position under the query point, as an offset (m) in the first tile's plane, with the depth cap there
    /// (it scales the displacement too).
    /// </summary>
    private float Invert(ref Site site, out float du, out float dv)
    {
        du = 0f;
        dv = 0f;
        var cap = 1f;
        var settled = true;

        if (_shore is not null && _swell.SignificantWaveHeight > 1e-4f)
        {
            var depth = _shore.Depth(site.Dir);

            if (depth <= -CapMargin)
                return 0f;

            var scale = OceanSwell.DepthCapGamma / _swell.SignificantWaveHeight;
            settled = scale * (depth - CapMargin) >= 1f;
            cap = Math.Clamp(scale * depth, 0f, 1f);
        }

        if (_swell.Choppiness <= 0f)
            return cap;

        for (var i = 0; i < OceanSwell.InversionIterations; i++)
        {
            Displacement(site, du, dv, out var dx, out var dz);
            du = -cap * dx;
            dv = -cap * dz;

            if (!settled)
                cap = Cap(double3.Normalize(site.Dir * _tiles.SeaRadius + site.E1 * du + site.E2 * dv));
        }

        return cap;
    }

    private float Cap(in double3 dir)
    {
        if (_shore is null || _swell.SignificantWaveHeight <= 1e-4f)
            return 1f;

        return Math.Clamp(OceanSwell.DepthCapGamma * _shore.Depth(dir) / _swell.SignificantWaveHeight, 0f, 1f);
    }

    /// <summary>The rest position (offset du, dv in the first tile's plane) in tile k's plane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Rest(in Site site, int k, float du, float dv, out float u, out float v)
    {
        u = site.U[k] + site.Turn[4 * k] * du + site.Turn[4 * k + 2] * dv;
        v = site.V[k] + site.Turn[4 * k + 1] * du + site.Turn[4 * k + 3] * dv;
    }

    /// <summary>The blended chop displacement at a rest position, in the first tile's plane.</summary>
    private void Displacement(in Site site, float du, float dv, out float dx, out float dz)
    {
        _swell.SumDisplacement(site.U[0] + du, site.V[0] + dv, site.Lead, out dx, out dz);

        if (site.Count == 1)
            return;

        dx *= site.Weight[0];
        dz *= site.Weight[0];

        for (var k = 1; k < site.Count; k++)
        {
            Rest(site, k, du, dv, out var u, out var v);
            _swell.SumDisplacement(u, v, site.Lead, out var x, out var z);
            var w = site.Weight[k];
            dx += w * (site.Turn[4 * k] * x + site.Turn[4 * k + 1] * z);
            dz += w * (site.Turn[4 * k + 2] * x + site.Turn[4 * k + 3] * z);
        }
    }

    /// <summary>Every tile's sums at a rest position, blended into the first tile's plane.</summary>
    private void Sum(in Site site, float du, float dv, out SwellSums sums)
    {
        _swell.Sum(site.U[0] + du, site.V[0] + dv, site.Lead, out sums);

        if (site.Count == 1)
            return;

        var first = sums;
        sums = default;
        sums.Add(first, site.Weight[0], 1f, 0f, 0f, 1f);

        for (var k = 1; k < site.Count; k++)
        {
            Rest(site, k, du, dv, out var u, out var v);
            _swell.Sum(u, v, site.Lead, out var s);
            sums.Add(s, site.Weight[k], site.Turn[4 * k], site.Turn[4 * k + 1], site.Turn[4 * k + 2], site.Turn[4 * k + 3]);
        }
    }

    private static float3 Float(in double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
