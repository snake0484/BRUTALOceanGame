using System.Numerics;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame.Terrain.Tests;

/// <summary>Re-centring moves the seabed data with the grid instead of discarding it.</summary>
public sealed class BathymetryGridTests
{
    private const int N = BathymetryGrid.Resolution;

    [Theory]
    [InlineData(16, 0)]
    [InlineData(-16, 0)]
    [InlineData(0, 32)]
    [InlineData(-48, 16)]
    public void ShiftCells_MovesDataBySignedCells(int dx, int dz)
    {
        var cells = new int[N * N];
        for (var i = 0; i < cells.Length; i++)
            cells[i] = i;

        BathymetryGrid.ShiftCells(cells, dx, dz, -1);

        for (var z = 0; z < N; z++)
        {
            for (var x = 0; x < N; x++)
            {
                var (sx, sz) = (x + dx, z + dz);
                var expected = sx is >= 0 and < N && sz is >= 0 and < N ? sx + sz * N : -1;
                Assert.Equal(expected, cells[x + z * N]);
            }
        }
    }

    [Fact]
    public void Recenter_KeepsTopsAtTheirWorldPosition()
    {
        var grid = new BathymetryGrid();
        grid.Recenter(Vector3.Zero, out _, out _);

        // A recognisable seabed: each cell's top encodes its world position.
        const int size = ChunkConstants.Size;
        var tops = new float[size * size];
        var sea = new bool[size * size];
        grid.BeginRebuild();
        for (var cz = -2; cz < 2; cz++)
        {
            for (var cx = -2; cx < 2; cx++)
            {
                var coord = new ChunkCoord(cx, -1, cz, 3);
                for (var vz = 0; vz < size; vz++)
                    for (var vx = 0; vx < size; vx++)
                        tops[vx + vz * size] = -(coord.WorldOrigin.X + vx * coord.VoxelScale) * 0.001f - (coord.WorldOrigin.Z + vz * coord.VoxelScale) * 0.000001f;

                grid.MergeLeaf(coord, tops, tops, sea, false, BathymetryGrid.Region.All);
            }
        }

        grid.EndRebuild(0.0);
        var probe = new Vector2(40f, -24f);
        var before = grid.SampleTop(probe.X, probe.Y);

        Assert.True(grid.Recenter(new Vector3(200f, 0f, 150f), out var dx, out var dz));
        Assert.True(dx != 0 || dz != 0);
        Assert.Equal(before, grid.SampleTop(probe.X, probe.Y));
    }

    /// <summary>
    /// A seabed 8 m down under a rock roof at +6 m: the sea sees the seabed (the water under the roof is drawn and
    /// simulated), rain still sees the roof. Land rising through sea level keeps its own top.
    /// </summary>
    [Fact]
    public void Ground_IgnoresLandOverTheSea()
    {
        const int size = ChunkConstants.Size;
        var grid = new BathymetryGrid();
        grid.Recenter(Vector3.Zero, out _, out _);

        var below = new ChunkCoord(0, -1, 0, 0);
        var above = new ChunkCoord(0, 0, 0, 0);
        var seabed = new float[size * size];
        var roof = new float[size * size];
        var none = new float[size * size];
        var water = new bool[size * size];
        Array.Fill(seabed, -8f);
        Array.Fill(roof, 6f);
        Array.Fill(none, float.NaN);
        Array.Fill(water, true);

        // Column x < 16: land through sea level (the sea-layer leaf's surface is its top boundary, sea level), rising to +3.
        for (var z = 0; z < size; z++)
            for (var x = 0; x < size / 2; x++)
            {
                seabed[x + z * size] = 0f;
                roof[x + z * size] = 3f;
                water[x + z * size] = false;
            }

        grid.BeginRebuild();
        grid.MergeLeaf(below, seabed, seabed, water, true, BathymetryGrid.Region.All);
        grid.MergeLeaf(above, roof, none, new bool[size * size], false, BathymetryGrid.Region.All);
        grid.EndRebuild(0.0);

        var sea = below.WorldOrigin + new Vector3(24.5f, 0f, 16.5f) * below.VoxelScale;
        var land = below.WorldOrigin + new Vector3(4.5f, 0f, 16.5f) * below.VoxelScale;
        Assert.Equal(-8f, grid.SampleGround(sea.X, sea.Z), 3);
        Assert.Equal(8f, grid.SampleDepth(sea.X, sea.Z), 3);
        Assert.Equal(6f, grid.SampleTop(sea.X, sea.Z), 3);
        Assert.Equal(3f, grid.SampleGround(land.X, land.Z), 3);
        Assert.Equal(0f, grid.SampleDepth(land.X, land.Z), 3);
    }
}
