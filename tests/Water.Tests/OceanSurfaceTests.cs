using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>The CPU surface gameplay queries (OceanSurface), probed across a coastal surf zone.</summary>
public sealed class OceanSurfaceTests(ITestOutputHelper output)
{
    private const int N = CoastalField.Resolution;
    private const float Cell = CoastalField.CellSize;

    /// <summary>
    /// A 1:12 beach rising along +x, shoreline at x = 800 m, under a 3 m, 12 s groundswell and light wind: the coherent
    /// surf wave shoals and breaks 35–60 m out.
    /// </summary>
    private static OceanSwell Beach()
    {
        const float slope = 0.08f;
        var depths = new float[N * N];
        for (var z = 0; z < N; z++)
            for (var x = 0; x < N; x++)
                depths[x + z * N] = slope * (800f - (x + 0.5f) * Cell);

        using var builder = new CoastalBuilder();
        builder.TryStart(depths, Vector2.Zero, 0f);

        var timer = Stopwatch.StartNew();
        while (!builder.Poll())
        {
            Assert.True(timer.Elapsed.TotalSeconds < 30, "coastal build timed out");
            Thread.Sleep(1);
        }

        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = 8f, SwellHeight = 3f, SwellPeriod = 12f });
        swell.Coastal = builder.Current;
        return swell;
    }

    /// <summary>A plane beach rising along +x, shoreline at <paramref name="shoreX"/>.</summary>
    private sealed class Ramp(float shoreX, float slope) : IShoreDepth
    {
        public float Depth(float x, float z) => slope * (shoreX - x);
    }

    private static OceanSwell Swell(double time)
    {
        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = 12f, SwellHeight = 2f, SwellPeriod = 12f });
        swell.SetTime(time);
        return swell;
    }

    /// <summary>Where the water is deeper than Hs / 0.55 the cap is 1: the surface is exactly the open swell.</summary>
    [Fact]
    public void DeepWater_IsTheSwell()
    {
        var swell = Swell(40.0);
        var surface = new OceanSurface(swell, new Ramp(1e6f, 1f));

        for (var i = 0; i < 50; i++)
        {
            var x = i * 13.7f - 300f;
            var z = i * 7.9f - 200f;
            var open = swell.Sample(x, z);
            var capped = surface.Sample(x, z);
            Assert.Equal(open.Height, capped.Height);
            Assert.Equal(open.Normal, capped.Normal);
            Assert.Equal(open.Velocity, capped.Velocity);
        }
    }

    /// <summary>
    /// In shallow water every component is scaled by min(1, 0.55 h / Hs) at the rest position, height and displacement
    /// alike (ocean_common.glsl swellCap), and the height still inverts the displaced surface.
    /// </summary>
    [Fact]
    public void ShallowWater_IsCappedByDepth()
    {
        var swell = Swell(40.0);
        var ramp = new Ramp(500f, 0.05f);
        var surface = new OceanSurface(swell, ramp);
        var hs = swell.SignificantWaveHeight;
        var maxErr = 0f;

        for (var x0 = 440f; x0 < 500f; x0 += 3.1f)
        {
            for (var z0 = -40f; z0 < 40f; z0 += 9.3f)
            {
                var cap = Math.Clamp(OceanSwell.DepthCapGamma * ramp.Depth(x0, z0) / hs, 0f, 1f);
                var open = swell.SurfacePoint(x0, z0);
                var p = surface.SurfacePoint(x0, z0);

                Assert.Equal(cap * open.Y, p.Y, 1e-5f);
                Assert.Equal(x0 + cap * (open.X - x0), p.X, 1e-4f);
                Assert.Equal(z0 + cap * (open.Z - z0), p.Z, 1e-4f);
                maxErr = MathF.Max(maxErr, MathF.Abs(surface.SampleHeight(p.X, p.Z) - p.Y));
            }
        }

        output.WriteLine($"Hs {hs:F2} m, max inversion error over the shallows {maxErr:F5} m");
        Assert.True(maxErr < 0.02f, $"max inversion error {maxErr} m");
    }

    /// <summary>On land the cap is 0: the sea lies flat at sea level under the beach (the shore wash draws the edge).</summary>
    [Fact]
    public void Land_IsFlatAtSeaLevel()
    {
        var surface = new OceanSurface(Swell(40.0), new Ramp(0f, 0.1f));

        for (var x = 1f; x < 100f; x += 7f)
        {
            var s = surface.Sample(x, 3f * x);
            Assert.Equal(0f, s.Height);
            Assert.Equal(new float3(0f, 1f, 0f), s.Normal);
            Assert.Equal(new float3(0f, 0f, 0f), s.Velocity);
        }
    }

    [Fact]
    public void Sampling_IsAllocationFree()
    {
        var surface = new OceanSurface(Beach());
        surface.Swell.SetTime(100.0);
        var points = new List<Vector2>();

        // Across the surf zone and the shoaling water seaward of it.
        for (var x = 700f; x < 800f; x += 2.5f)
            for (var z = 400f; z < 600f; z += 20f)
                points.Add(new Vector2(x, z));

        // Warm up past tiered JIT, then count.
        for (var pass = 0; pass < 5; pass++)
            foreach (var p in points)
                surface.Sample(p.X, p.Y);

        var timer = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var p in points)
            surface.Sample(p.X, p.Y);

        var ns = timer.Elapsed.TotalNanoseconds / points.Count;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"{points.Count} samples across the surf zone: {ns:F0} ns each, {allocated} bytes allocated");
        Assert.Equal(0, allocated);
    }
}
