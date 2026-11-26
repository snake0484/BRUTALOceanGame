using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The CPU surface gameplay queries (OceanSurface): the swell laid on the sea sphere by tile, depth-capped over a beach,
/// one function of planet position and tick.
/// </summary>
public sealed unsafe class OceanSurfaceTests(ITestOutputHelper output)
{
    private const double SeaRadius = 1_000_150.0;
    private const double TickSeconds = 1.0 / 60.0;
    private const long Tick = 2400;

    private static readonly double3 Spin = double3.UnitY;
    private static readonly SeaTiles Tiles = new(SeaRadius, Spin);

    /// <summary>A direction well inside a tile (no blend), mid latitude.</summary>
    private static readonly double3 Inside = InsideTile();

    /// <summary>A plane beach rising to the east of <paramref name="shoreX"/> m east of a base point (depth 0 there).</summary>
    private sealed class Ramp(double3 @base, float shoreX, float slope) : IShoreDepth
    {
        private readonly double3 _east = East(@base);

        public float Depth(in double3 direction) => slope * (shoreX - (float)(SeaRadius * double3.Dot(direction, _east)));
    }

    private static double3 East(double3 up)
    {
        SeaTiles.EastSouth(up, Spin, out var east, out _);
        return east;
    }

    private static double3 InsideTile()
    {
        var d = SeaTilesTests.Corner(Tiles, 34, 5, 30_000.0, 30_000.0);
        Tiles.Locate(d, out var blend);
        Assert.Equal(1, blend.Count);
        return d;
    }

    private static OceanSwell Swell(double time = Tick * TickSeconds)
    {
        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = 12f, SwellHeight = 2f, SwellPeriod = 12f });
        swell.SetTime(time);
        return swell;
    }

    /// <summary>The point (x east, z south) m from <paramref name="base"/> on the sea sphere.</summary>
    private static double3 Offset(double3 @base, double x, double z)
    {
        SeaTiles.EastSouth(@base, Spin, out var east, out var south);
        return double3.Normalize(@base * SeaRadius + east * x + south * z) * SeaRadius;
    }

    /// <summary>Inside a tile, in deep water, the sea is the open swell in the tile's plane, its vectors lifted onto the sphere.</summary>
    [Fact]
    public void DeepWater_IsTheSwellInItsTile()
    {
        var swell = Swell();
        var surface = new OceanSurface(swell, Tiles, TickSeconds, new Ramp(Inside, 1e6f, 1f));
        Tiles.Locate(Inside, out var blend);
        Assert.Equal(1, blend.Count);

        for (var i = 0; i < 50; i++)
        {
            var p = Offset(Inside, i * 13.7 - 300.0, i * 7.9 - 200.0);
            var d = double3.Normalize(p);
            Tiles.Plane(blend.Tile[0], d, out var u, out var v);
            Tiles.Lift(blend.Tile[0], d, out var e1, out var e2);
            var open = swell.Sample((float)u, (float)v);
            var sea = surface.Sample(p, Tick);
            var normal = double3.Normalize(e1 * open.Normal.X + d * open.Normal.Y + e2 * open.Normal.Z);
            var velocity = e1 * open.Velocity.X + d * open.Velocity.Y + e2 * open.Velocity.Z;

            Assert.Equal(open.Height, sea.Height, 1e-4f);
            Assert.Equal(open.Jacobian, sea.Jacobian, 1e-4f);
            Assert.True(double3.Dot(normal, new double3(sea.Normal.X, sea.Normal.Y, sea.Normal.Z)) > 0.999999, "normal");
            Assert.True((velocity - new double3(sea.Velocity.X, sea.Velocity.Y, sea.Velocity.Z)).Length() < 1e-3, "velocity");
            Assert.Equal(sea.Height, surface.SampleHeight(p, Tick), 1e-5f);
        }
    }

    /// <summary>The sea depends on the direction from the planet's centre alone, and a tick ahead of the swell's is the swell advanced to it.</summary>
    [Fact]
    public void Sample_IsAFunctionOfDirectionAndTick()
    {
        var swell = Swell();
        var surface = new OceanSurface(swell, Tiles, TickSeconds);
        var later = new OceanSurface(Swell((Tick + 7) * TickSeconds), Tiles, TickSeconds);

        for (var i = 0; i < 20; i++)
        {
            var p = Offset(Inside, i * 31.0 - 300.0, 200.0 - i * 17.0);
            Assert.Equal(surface.SampleHeight(p, Tick), surface.SampleHeight(p * 1.0007, Tick));
            Assert.Equal(later.SampleHeight(p, Tick + 7), surface.SampleHeight(p, Tick + 7), 2e-3f);
            Assert.NotEqual(surface.SampleHeight(p, Tick), surface.SampleHeight(p, Tick + 7));
        }
    }

    /// <summary>
    /// In shallow water every component is scaled by min(1, 0.55 h / Hs) at the rest position, height and displacement
    /// alike (ocean_common.glsl oceanAt), and the height still inverts the displaced surface.
    /// </summary>
    [Fact]
    public void ShallowWater_IsCappedByDepth()
    {
        var swell = Swell();
        var ramp = new Ramp(Inside, 500f, 0.05f);
        var surface = new OceanSurface(swell, Tiles, TickSeconds, ramp);
        var open = new OceanSurface(swell, Tiles, TickSeconds);
        var hs = swell.SignificantWaveHeight;
        var maxErr = 0.0;
        var capped = 0;

        for (var x0 = 440.0; x0 < 500.0; x0 += 3.1)
        {
            for (var z0 = -40.0; z0 < 40.0; z0 += 9.3)
            {
                var rest = Offset(Inside, x0, z0);
                var d = double3.Normalize(rest);
                var expected = Math.Clamp(OceanSwell.DepthCapGamma * ramp.Depth(d) / hs, 0f, 1f);
                var free = open.SurfacePoint(d, Tick, out _);
                var p = surface.SurfacePoint(d, Tick, out var cap);

                Assert.Equal(expected, cap, 1e-6f);
                Assert.Equal(cap * (free.Length() - SeaRadius), p.Length() - SeaRadius, 1e-4);
                Assert.True((rest + (free * (SeaRadius / free.Length()) - rest) * cap - p * (SeaRadius / p.Length())).Length() < 1e-3, "displacement scales with the cap");
                maxErr = Math.Max(maxErr, Math.Abs(surface.SampleHeight(p, Tick) - (p.Length() - SeaRadius)));
                capped += cap < 1f ? 1 : 0;
            }
        }

        output.WriteLine($"Hs {hs:F2} m, {capped} capped points, max inversion error over the shallows {maxErr:F5} m");
        Assert.True(capped > 50);
        Assert.True(maxErr < 0.02, $"max inversion error {maxErr} m");
    }

    /// <summary>On land the cap is 0: the sea lies flat on the sea sphere under the beach (the shore wash draws the edge).</summary>
    [Fact]
    public void Land_IsFlatAtSeaLevel()
    {
        var surface = new OceanSurface(Swell(), Tiles, TickSeconds, new Ramp(Inside, 0f, 0.1f));

        for (var x = 1.0; x < 100.0; x += 7.0)
        {
            var p = Offset(Inside, x, 3.0 * x);
            var d = double3.Normalize(p);
            var s = surface.Sample(p, Tick);
            Assert.Equal(0f, s.Height);
            Assert.Equal(new float3((float)d.X, (float)d.Y, (float)d.Z), s.Normal);
            Assert.Equal(new float3(0f, 0f, 0f), s.Velocity);
        }
    }

    [Fact]
    public void Sampling_IsAllocationFree()
    {
        var surface = new OceanSurface(Swell(), Tiles, TickSeconds, new Ramp(Inside, 800f, 0.08f));
        var points = new List<double3>();

        // Across the capped shallows and the open water seaward of them.
        for (var x = 700.0; x < 800.0; x += 2.5)
            for (var z = 400.0; z < 600.0; z += 20.0)
                points.Add(Offset(Inside, x, z));

        // Warm up past tiered JIT, then count.
        for (var pass = 0; pass < 5; pass++)
            foreach (var p in points)
                surface.Sample(p, Tick);

        var timer = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var p in points)
            surface.Sample(p, Tick);

        var ns = timer.Elapsed.TotalNanoseconds / points.Count;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{points.Count} samples across the shallows: {ns:F0} ns each, {allocated} bytes allocated");
        Assert.Equal(0, allocated);
    }

    /// <summary>Sampling cost in open water inside a tile, in a two-tile blend and at a four-tile corner.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Sampling_Cost()
    {
        var surface = new OceanSurface(Swell(), Tiles, TickSeconds);
        var corner = SeaTilesTests.Corner(Tiles, 34, 5);
        var edge = SeaTilesTests.Corner(Tiles, 34, 5, 30_000.0);
        var sink = 0f;

        foreach (var (name, at) in new[] { ("inside a tile", Inside), ("on a tile edge", edge), ("at a tile corner", corner) })
        {
            for (var pass = 0; pass < 3; pass++)
                for (var i = 0; i < 2000; i++)
                    sink += surface.Sample(Offset(at, i % 40 * 3.0 - 60.0, i / 40 * 3.0 - 75.0), Tick).Height;

            var timer = Stopwatch.StartNew();

            for (var i = 0; i < 2000; i++)
                sink += surface.Sample(Offset(at, i % 40 * 3.0 - 60.0, i / 40 * 3.0 - 75.0), Tick).Height;

            output.WriteLine($"{name}: {timer.Elapsed.TotalNanoseconds / 2000:F0} ns per sample ({surface.Swell.ComponentCount} components)");
        }

        Assert.True(float.IsFinite(sink));
    }
}
