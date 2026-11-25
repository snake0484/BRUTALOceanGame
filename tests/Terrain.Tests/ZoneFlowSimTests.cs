using System.Numerics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Water;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>The sea-zone sim's bed under terrain over the water.</summary>
public sealed class ZoneFlowSimTests(ITestOutputHelper output)
{
    private static readonly Vector2 Centre = new(500f, 500f);

    /// <summary>A zone over a flat seabed <paramref name="depth"/> metres down.</summary>
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
}
