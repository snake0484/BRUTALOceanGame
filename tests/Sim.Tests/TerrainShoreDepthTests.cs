using System.Diagnostics;
using System.Numerics;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Sim.Tests;

public sealed class TerrainShoreDepthTests(ITestOutputHelper output)
{
    private const int N = BathymetryGrid.Resolution;

    private static readonly TerrainParams Params = new();

    /// <summary>World x of the main island's waterline on row z, walking east from far out west.</summary>
    internal static float WaterlineX(in TerrainParams p, float z)
    {
        for (var x = -2.5f * p.IslandRadius; x < 0f; x += 0.5f)
            if (TerrainGenerator.SurfaceHeight(x, z, p) >= p.SeaLevel)
                return x;

        throw new InvalidOperationException("no waterline");
    }

    /// <summary>A dig the shore guard allows on low land inshore of the west beach, reaching 3 m below sea level.</summary>
    internal static BrushOp InlandDig(in TerrainParams p)
    {
        for (var z = 0f; z < 400f; z += 40f)
        {
            var shore = WaterlineX(p, z);

            for (var x = shore + ShoreGuard.Band; x < shore + 300f; x += 2f)
            {
                var h = TerrainGenerator.SurfaceHeight(x, z, p);
                var dig = new BrushOp(new Vector3(x, h, z), h - p.SeaLevel + 3f, BrushMode.Dig, VoxelMaterial.Air);

                if (h > p.SeaLevel + ShoreGuard.Margin && dig.Radius <= 12f && ShoreGuard.Allows(dig, p))
                    return dig;
            }
        }

        throw new InvalidOperationException("no inland dig site");
    }

    /// <summary>Land at least <paramref name="height"/> m up, inshore of the west beach on row z 0.</summary>
    private static Vector3 Land(float height)
    {
        for (var x = WaterlineX(Params, 0f); x < 0f; x += 1f)
        {
            var h = TerrainGenerator.SurfaceHeight(x, 0f, Params);
            if (h >= height)
                return new Vector3(x, h, 0f);
        }

        throw new InvalidOperationException("no land that high");
    }

    [Fact]
    public void Depth_IsPositiveAtSeaAndNegativeOnLand()
    {
        var depth = new TerrainShoreDepth(Params);
        var sea = WaterlineX(Params, 0f) - 60f;
        var land = Land(5f);

        Assert.True(depth.Depth(sea, 0f) > 1f);
        Assert.Equal(Params.SeaLevel - TerrainGenerator.SurfaceHeight(sea, 0f, Params), depth.Depth(sea, 0f));
        Assert.Equal(Params.SeaLevel - land.Y, depth.Depth(land.X, land.Z));
        Assert.True(depth.Depth(land.X, land.Z) < 0f);
    }

    [Fact]
    public void Dig_CutsTheTopToTheSphereBottomOnlyWhenItCrossesTheTop()
    {
        var land = Land(12f);
        const float r = 4f;

        var crater = new TerrainShoreDepth(Params);
        crater.Add(new BrushOp(land, r, BrushMode.Dig, VoxelMaterial.Air));
        Assert.Equal(-(land.Y - r), crater.Depth(land.X, land.Z), 4);

        var above = new TerrainShoreDepth(Params);
        above.Add(new BrushOp(land + new Vector3(0f, r + 1f, 0f), r, BrushMode.Dig, VoxelMaterial.Air));
        Assert.Equal(-land.Y, above.Depth(land.X, land.Z), 4);

        var cave = new TerrainShoreDepth(Params);
        cave.Add(new BrushOp(land - new Vector3(0f, r + 1f, 0f), r, BrushMode.Dig, VoxelMaterial.Air));
        Assert.Equal(-land.Y, cave.Depth(land.X, land.Z), 4);
    }

    [Fact]
    public void Place_RaisesTheTopToTheSphereTopOnlyWhenItTouchesTheTop()
    {
        var land = Land(12f);
        const float r = 4f;

        var mound = new TerrainShoreDepth(Params);
        mound.Add(new BrushOp(land, r, BrushMode.Place, VoxelMaterial.Rock));
        Assert.Equal(-(land.Y + r), mound.Depth(land.X, land.Z), 4);

        var floating = new TerrainShoreDepth(Params);
        floating.Add(new BrushOp(land + new Vector3(0f, r + 1f, 0f), r, BrushMode.Place, VoxelMaterial.Rock));
        Assert.Equal(-land.Y, floating.Depth(land.X, land.Z), 4);

        var offset = new TerrainShoreDepth(Params);
        offset.Add(new BrushOp(land + new Vector3(r + 1f, 0f, 0f), r, BrushMode.Place, VoxelMaterial.Rock));
        Assert.Equal(-land.Y, offset.Depth(land.X, land.Z), 4);
    }

    [Fact]
    public void Ops_ApplyInOrder()
    {
        var land = Land(12f);
        const float r = 4f;
        var dig = new BrushOp(land, r, BrushMode.Dig, VoxelMaterial.Air);
        var place = new BrushOp(land, r, BrushMode.Place, VoxelMaterial.Rock);

        var digThenPlace = new TerrainShoreDepth(Params);
        digThenPlace.Add(dig);
        digThenPlace.Add(place);

        var placeThenDig = new TerrainShoreDepth(Params);
        placeThenDig.Add(place);
        placeThenDig.Add(dig);

        Assert.Equal(-(land.Y + r), digThenPlace.Depth(land.X, land.Z), 4);
        Assert.Equal(-(land.Y - r), placeThenDig.Depth(land.X, land.Z), 4);
    }

    /// <summary>
    /// A pit dug below sea level inland (the only place the guard lets one be dug) holds no sea on the CPU, as the GPU's
    /// pit rule (ChunkJob's generated sea flags, ShoreDepthMap's reach) leaves it dry; the open sea is wet on both.
    /// </summary>
    [Fact]
    public void InlandPit_IsDryOnCpuAndGpu()
    {
        var dig = InlandDig(Params);
        var cpu = new TerrainShoreDepth(Params);
        cpu.Add(dig);

        var (grid, map) = Bake(Params, [dig], new Vector2(dig.Centre.X, dig.Centre.Z));
        var gpu = map.Depth(grid, Cell(grid, dig.Centre.X, dig.Centre.Z));
        output.WriteLine($"pit at {dig.Centre} r {dig.Radius:F1}: cpu {cpu.Depth(dig.Centre.X, dig.Centre.Z)}, gpu {gpu}");

        Assert.Equal(ShoreDepthMap.Pit, gpu);
        Assert.Equal(TerrainShoreDepth.Pit, cpu.Depth(dig.Centre.X, dig.Centre.Z));
        Assert.Equal(ShoreDepthMap.Pit, TerrainShoreDepth.Pit);

        // Every cell over the pit agrees: dry on both, or (its rim, within a voxel of sea level) wet on both.
        var r = (int)MathF.Ceiling(dig.Radius / BathymetryGrid.CellSize);
        var centre = Cell(grid, dig.Centre.X, dig.Centre.Z);

        for (var dz = -r; dz <= r; dz++)
        {
            for (var dx = -r; dx <= r; dx++)
            {
                var i = centre + dx + dz * N;
                var (x, z) = CellCentre(grid, i);
                var g = map.Depth(grid, i);
                var c = cpu.Depth(x, z);

                // Ground right at the sea-level voxel's floor falls either side of it on the voxel grid.
                if (MathF.Abs(c - ChunkConstants.VoxelSize) < 0.1f)
                    continue;

                Assert.True((g == ShoreDepthMap.Pit) == (c == TerrainShoreDepth.Pit), $"({x}, {z}): cpu {c}, gpu {g}");
            }
        }
    }

    /// <summary>Over the west shore, seabed to beach, the CPU's depth matches the streamed grid's within its quantisation.</summary>
    [Fact]
    public void Depth_AgreesWithBathymetryGrid()
    {
        var shore = WaterlineX(Params, 0f);
        var cpu = new TerrainShoreDepth(Params);
        var (grid, map) = Bake(Params, [], new Vector2(shore, 0f));
        var compared = 0;
        var worst = 0f;

        for (var z = -40f; z <= 40f; z += 2f)
        {
            for (var x = shore - 40f; x <= shore + 40f; x += 2f)
            {
                var i = Cell(grid, x, z);
                var (cx, cz) = CellCentre(grid, i);
                var g = map.Depth(grid, i);

                // Land well above the sea carries overhang noise the analytic height lacks, and never meets the swell.
                if (g < -ShoreGuard.Margin)
                    continue;

                var error = MathF.Abs(g - cpu.Depth(cx, cz));
                worst = MathF.Max(worst, error);
                compared++;
            }
        }

        output.WriteLine($"{compared} cells compared, worst |cpu - gpu| {worst:F3} m");
        Assert.True(compared > 500);
        Assert.True(worst < 0.1f, $"worst {worst} m");
    }

    [Fact]
    public void Sampling_IsAllocationFreeWithEdits()
    {
        var surface = Surface(Edits(1000));
        var shore = WaterlineX(Params, 0f);

        for (var i = 0; i < 100; i++)
            surface.SampleHeight(shore - 50f + i, 0f);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 1000; i++)
        {
            surface.SampleHeight(shore - 50f + 0.1f * i, 3f);
            surface.Sample(shore - 50f + 0.1f * i, 3f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// The CPU depth cap's cost (review finding 3, deferred to M2b): OceanSurface.SampleHeight with TerrainShoreDepth and
    /// 1000 edits against none and against no depth source. Reported, not asserted; run in Release for real numbers.
    /// </summary>
    [Fact]
    public void Benchmark_SampleHeightWithTerrainShoreDepth()
    {
        const int samples = 20_000;
        var shore = WaterlineX(Params, 0f);
        var none = new OceanSurface(Surface(Edits(0)).Swell);
        var bare = Surface(Edits(0));
        var edited = Surface(Edits(1000));

        // Interleaved rounds, best of each, so tiering and the first touches don't count.
        double noneNs = double.MaxValue, bareNs = double.MaxValue, editedNs = double.MaxValue;
        for (var round = 0; round < 6; round++)
        {
            noneNs = Math.Min(noneNs, Time(none));
            bareNs = Math.Min(bareNs, Time(bare));
            editedNs = Math.Min(editedNs, Time(edited));
        }

        output.WriteLine($"SampleHeight: no depth {noneNs:F0} ns, TerrainShoreDepth {bareNs:F0} ns, with 1000 edits {editedNs:F0} ns");

        var guard = Stopwatch.StartNew();
        var allowed = 0;
        for (var i = 0; i < 20; i++)
            allowed += ShoreGuard.Allows(new BrushOp(new Vector3(shore + 100f + i, 10f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air), Params) ? 1 : 0;

        output.WriteLine($"ShoreGuard.Allows r 3: {guard.Elapsed.TotalMilliseconds / 20.0:F3} ms each ({allowed} allowed)");
        Assert.True(double.IsFinite(editedNs));

        double Time(OceanSurface surface)
        {
            var timer = Stopwatch.StartNew();
            var sum = 0f;
            for (var i = 0; i < samples; i++)
                sum += surface.SampleHeight(shore - 100f + 0.01f * i, 0.003f * i);

            Assert.True(float.IsFinite(sum));
            return timer.Elapsed.TotalMilliseconds * 1e6 / samples;
        }
    }

    /// <summary>A storm-ish swell over the analytic terrain with the given edits.</summary>
    private static OceanSurface Surface(List<BrushOp> edits)
    {
        var depth = new TerrainShoreDepth(Params);
        foreach (var op in edits)
            depth.Add(op);

        var swell = new OceanSwell(1);
        swell.SetWind(new SwellParams { WindSpeed = 12f, SwellHeight = 1.5f, SwellPeriod = 13f });
        swell.SetTime(100.0);
        return new OceanSurface(swell, depth);
    }

    /// <summary>Digs and places scattered over the main island, as a long session might leave them.</summary>
    private static List<BrushOp> Edits(int count)
    {
        var random = new Random(7);
        var edits = new List<BrushOp>(count);

        for (var i = 0; i < count; i++)
        {
            var angle = random.NextSingle() * MathF.Tau;
            var distance = random.NextSingle() * Params.IslandRadius * 0.8f;
            var x = distance * MathF.Cos(angle);
            var z = distance * MathF.Sin(angle);
            var y = TerrainGenerator.SurfaceHeight(x, z, Params);
            var mode = (i & 1) == 0 ? BrushMode.Dig : BrushMode.Place;
            edits.Add(new BrushOp(new Vector3(x, y, z), 2f + 4f * random.NextSingle(), mode, VoxelMaterial.Rock));
        }

        return edits;
    }

    /// <summary>
    /// The client's bathymetry over 5 × 5 LOD-0 chunk columns around <paramref name="centre"/>, as TerrainWorld merges it,
    /// and the depth map the GPU would get from it.
    /// </summary>
    private static (BathymetryGrid, ShoreDepthMap) Bake(in TerrainParams p, List<BrushOp> ops, Vector2 centre)
    {
        var grid = new BathymetryGrid();
        grid.Recenter(new Vector3(centre.X, 0f, centre.Y), out _, out _);
        grid.BeginRebuild();

        var job = new ChunkJob { Params = p };
        var home = ChunkCoord.FromWorld(new Vector3(centre.X, 0f, centre.Y));

        for (var cz = home.Z - 2; cz <= home.Z + 2; cz++)
        {
            for (var cx = home.X - 2; cx <= home.X + 2; cx++)
            {
                for (var cy = ChunkConstants.MinChunkY; cy <= ChunkConstants.MaxChunkY; cy++)
                {
                    job.Chunk.Coord = new ChunkCoord(cx, cy, cz);
                    job.SetOps(ops);
                    job.Run();
                    grid.MergeLeaf(job.Chunk.Coord, job.ColumnTops, job.ColumnBeds, job.SeaFlags, job.HasSeaLayer, BathymetryGrid.Region.All);
                }
            }
        }

        grid.EndRebuild(0.0);
        var map = new ShoreDepthMap();
        map.Update(grid, all: true);
        return (grid, map);
    }

    private static int Cell(BathymetryGrid grid, float x, float z) =>
        (int)MathF.Floor((x - grid.Origin.X) / BathymetryGrid.CellSize) + (int)MathF.Floor((z - grid.Origin.Y) / BathymetryGrid.CellSize) * N;

    private static (float X, float Z) CellCentre(BathymetryGrid grid, int i) =>
        (grid.Origin.X + (i % N + 0.5f) * BathymetryGrid.CellSize, grid.Origin.Y + (i / N + 0.5f) * BathymetryGrid.CellSize);
}
