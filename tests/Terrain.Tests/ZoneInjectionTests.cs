using System.Numerics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>Breaker bores handed to the zone sim (docs/breaking-waves.md, rendering step 5).</summary>
public sealed class ZoneInjectionTests(ITestOutputHelper output)
{
    private static readonly Vector2 Centre = new(500f, 500f);

    /// <summary>A zone over open sea: the empty grid reads as unknown, i.e. 50 m deep sea, at flat sea level.</summary>
    private static ZoneFlowSim Zone() => Zone(new BathymetryGrid());

    /// <summary>A zone over a flat seabed <paramref name="depth"/> metres down: surf-zone water, where bores happen.</summary>
    private static ZoneFlowSim ShallowZone(float depth)
    {
        const int size = ChunkConstants.Size;
        var grid = new BathymetryGrid();
        var tops = new float[size * size];
        var sea = new bool[size * size];
        Array.Fill(tops, -depth);
        Array.Fill(sea, true);

        grid.BeginRebuild();
        grid.MergeLeaf(new ChunkCoord(0, -1, 0, 5), tops, tops, sea, true, BathymetryGrid.Region.All);
        grid.EndRebuild(0.0);
        return Zone(grid);
    }

    /// <summary>
    /// The shallow seabed with a rock roof over the whole zone at +6 m: the sim's bed is still the seabed, not the roof,
    /// so the zone holds the same water (a placed roof used to read as dry ground and the water under it vanished).
    /// </summary>
    [Fact]
    public void Zone_UnderARoof_KeepsItsWater()
    {
        const int size = ChunkConstants.Size;
        const float depth = 2f;
        var open = ShallowZone(depth).Volume;

        var grid = new BathymetryGrid();
        var seabed = new float[size * size];
        var roof = new float[size * size];
        var none = new float[size * size];
        var sea = new bool[size * size];
        Array.Fill(seabed, -depth);
        Array.Fill(roof, 6f);
        Array.Fill(none, float.NaN);
        Array.Fill(sea, true);

        grid.BeginRebuild();
        grid.MergeLeaf(new ChunkCoord(0, -1, 0, 5), seabed, seabed, sea, true, BathymetryGrid.Region.All);
        grid.MergeLeaf(new ChunkCoord(0, 0, 0, 5), roof, none, new bool[size * size], false, BathymetryGrid.Region.All);
        grid.EndRebuild(0.0);
        var roofed = Zone(grid).Volume;

        output.WriteLine($"zone volume {open:F1} m3 open, {roofed:F1} m3 under the roof");
        Assert.True(open > 0f);
        Assert.Equal(open, roofed, 3);
    }

    private static ZoneFlowSim Zone(BathymetryGrid grid)
    {
        var zones = new ZoneFlowSim();
        zones.AddDisc(new Vector3(Centre, 30f), grid);
        zones.UpdateBoundary();
        zones.Step(1f / 240f);
        return zones;
    }

    [Fact]
    public void Inject_MovesWaterWithoutCreatingIt()
    {
        var zones = Zone();
        var before = zones.Volume;

        Assert.True(zones.Inject(Centre, 12f, new Vector2(4f, 0f), 1f, 0.5f, 2f));
        zones.Step(1f / 240f);

        output.WriteLine($"volume {before:F1} -> {zones.Volume:F1} m3, moved {zones.InjectedVolume:F1}");
        Assert.Equal(12f, zones.InjectedVolume);
        Assert.InRange(zones.Volume - before, -0.5f, 0.5f);
    }

    [Fact]
    public void Inject_CarriesTheSurgeAlongItsVelocity()
    {
        const float depth = 2f;
        var zones = ShallowZone(depth);
        zones.Inject(Centre, 20f, new Vector2(5f, 0f), 1f, 0f, 2f);
        zones.Step(1f / 240f);
        var start = MassMoment(zones, depth);

        for (var i = 0; i < 60; i++)
            zones.Step(1f / 240f);

        // The hump itself also spreads both ways at √(g h); the injected momentum shows as the water's centre of mass
        // moving on along the velocity.
        var end = MassMoment(zones, depth);
        output.WriteLine($"first moment of the surge along +x: {start:F2} m^4 after injection, {end:F2} m^4 0.25 s later");
        Assert.True(end > start, "the injected water should move with its velocity");
    }

    /// <summary>∫ (depth − rest) (x − centre) over the zone: the surge's volume times how far ahead of the centre it sits.</summary>
    private static float MassMoment(ZoneFlowSim zones, float rest)
    {
        var moment = 0f;

        for (var z = -28; z <= 28; z++)
            for (var x = -28; x <= 28; x++)
                if (x * x + z * z <= 28 * 28)
                    moment += (zones.DepthAt(Centre + new Vector2(x, z)) - rest) * x;

        return moment;
    }

    /// <summary>
    /// Surf is the ocean's normal state: bores hammering a zone's edge must not grow it. The quiet time after a bore keeps
    /// them from it, and so does reading the exchange with the open sea averaged over a wave period, which a bore's
    /// in-and-out doesn't sustain; a control run with neither (a period short enough to read it nearly raw) shows they would.
    /// </summary>
    [Theory]
    [InlineData(2f, 8f, false)]
    [InlineData(0f, 8f, false)]
    [InlineData(0f, 1f, true)]
    public void Bores_DoNotGrowTheZone(float quiet, float period, bool expectGrowth)
    {
        var zones = Zone();
        zones.BoreQuiet = quiet;
        zones.WavePeriod = period;
        var edge = Centre + new Vector2(26f, 0f);
        var grew = false;

        for (var t = 0; t < 480; t++)
        {
            if (t % 4 == 0)
                zones.Inject(edge, 6f, new Vector2(-5f, 0f), 1.5f, 0.05f, 2f);

            zones.UpdateBoundary();
            zones.Step(1f / 240f);
            grew |= zones.Growth.Count > 0;
            zones.Growth.Clear();
        }

        output.WriteLine($"quiet {quiet} s, period {period} s: grew {grew}");
        Assert.Equal(expectGrowth, grew);
    }

    [Fact]
    public void Inject_OutsideTheZone_IsRefused()
    {
        var zones = Zone();
        Assert.False(zones.Inject(Centre + new Vector2(200f, 0f), 5f, Vector2.Zero, 1f, 0f, 2f));
        Assert.Equal(0f, zones.InjectedVolume);
    }
}
