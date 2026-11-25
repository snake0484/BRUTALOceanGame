using System.Diagnostics;
using System.Numerics;
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
