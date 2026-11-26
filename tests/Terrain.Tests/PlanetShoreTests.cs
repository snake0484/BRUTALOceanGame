using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Terrain.Runtime;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>
/// The ocean's depth source on a planet (PlanetShore) and its GPU cache (ShoreDepthGrid): generated ground plus binned
/// edits, the sea mask never changed by digging (the pit rule), and the camera's depth maps holding the same numbers.
/// </summary>
public sealed class PlanetShoreTests(ITestOutputHelper output)
{
    private static readonly PlanetSurface Surface = TestPlanet.Generator.Surface;
    private static readonly double SeaRadius = TestPlanet.Radius + Surface.SeaHeight;

    /// <summary>A direction on the test planet whose generated ground stands within [lo, hi] m of sea level, found by a fixed scan.</summary>
    private static double3 Find(float lo, float hi)
    {
        var random = new Random(4);

        for (var i = 0; i < 2_000_000; i++)
        {
            var d = double3.Normalize(new double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            var h = Surface.Height(d.X, d.Y, d.Z) - Surface.SeaHeight;

            if (h >= lo && h <= hi)
                return d;
        }

        throw new InvalidOperationException($"no ground between {lo} and {hi} m of sea level");
    }

    private static EditOp At(double3 d, double aboveSea, float radius, BrushMode mode)
    {
        var r = SeaRadius + aboveSea;
        return EditOp.At(d.X * r, d.Y * r, d.Z * r, radius, mode, VoxelMaterial.Rock);
    }

    private static double3 Beside(double3 d, double metres)
    {
        var side = double3.Normalize(double3.Cross(d, Math.Abs(d.Y) < 0.9 ? double3.UnitY : double3.UnitX));
        return double3.Normalize(d * SeaRadius + side * metres);
    }

    [Fact]
    public void Depth_IsSeaLevelMinusTheGeneratedGround()
    {
        var shore = new PlanetShore(Surface);
        var random = new Random(2);

        for (var i = 0; i < 200; i++)
        {
            var d = double3.Normalize(new double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            Assert.Equal(Surface.SeaHeight - Surface.Height(d.X, d.Y, d.Z), shore.Depth(d.X, d.Y, d.Z));
            Assert.Equal(Surface.Height(d.X, d.Y, d.Z) < Surface.SeaHeight, shore.IsSea(d.X, d.Y, d.Z));
        }
    }

    /// <summary>Fill placed on the seabed makes the water shallower under it and nowhere else; fill hanging over the water does not.</summary>
    [Fact]
    public void PlacedFill_RaisesTheGroundWhereItStandsOnIt()
    {
        var shore = new PlanetShore(Surface);
        var d = Find(-6f, -3f);
        var depth = shore.Depth(d.X, d.Y, d.Z);

        // A sphere resting in the seabed, its top 1 m above the sea: land in its column, shallower out to its rim.
        var radius = 0.5f * (depth + 1f) + 1f;
        Assert.True(shore.Add(At(d, 1f - radius, radius, BrushMode.Place)));
        Assert.Equal(-1f, shore.Depth(d.X, d.Y, d.Z), 2e-3f);

        var inside = Beside(d, 0.6 * radius);
        var generated = Surface.SeaHeight - Surface.Height(inside.X, inside.Y, inside.Z);
        Assert.True(shore.Depth(inside.X, inside.Y, inside.Z) < generated, "shallower under the fill");

        var outside = Beside(d, radius + 0.5);
        Assert.Equal(Surface.SeaHeight - Surface.Height(outside.X, outside.Y, outside.Z), shore.Depth(outside.X, outside.Y, outside.Z));

        // A platform 3 m clear of the sea over another column hides no water.
        var other = Beside(d, 40.0);
        var before = shore.Depth(other.X, other.Y, other.Z);
        Assert.True(before > 0f);
        Assert.True(shore.Add(At(other, 4f, 1f, BrushMode.Place)));
        Assert.Equal(before, shore.Depth(other.X, other.Y, other.Z));
    }

    /// <summary>Digging takes placed fill back down to the generated seabed and no further (D30), in the order it happened.</summary>
    [Fact]
    public void Digging_TakesFillBackToTheGeneratedGroundOnly()
    {
        var shore = new PlanetShore(Surface);
        var d = Find(-6f, -3f);
        var generated = shore.Depth(d.X, d.Y, d.Z);
        var radius = 0.5f * (generated + 1f) + 1f;
        shore.Add(At(d, 1f - radius, radius, BrushMode.Place));
        Assert.Equal(-1f, shore.Depth(d.X, d.Y, d.Z), 2e-3f);

        // A dig through the top half metre of the fill.
        shore.Add(At(d, 1f, 0.5f, BrushMode.Dig));
        Assert.Equal(-0.5f, shore.Depth(d.X, d.Y, d.Z), 2e-3f);

        // A dig far bigger than the fill: the seabed is back, no deeper.
        shore.Add(At(d, 0f, 30f, BrushMode.Dig));
        Assert.Equal(generated, shore.Depth(d.X, d.Y, d.Z));

        // Filled again after the dig: the last word wins.
        shore.Add(At(d, 1f - radius, radius, BrushMode.Place));
        Assert.Equal(-1f, shore.Depth(d.X, d.Y, d.Z), 2e-3f);
    }

    /// <summary>The pit rule: the sea mask is the generated one, so a pit dug below sea level inland stays land for the ocean.</summary>
    [Fact]
    public void InlandPit_BelowSeaLevelHoldsNoSea()
    {
        var shore = new PlanetShore(Surface);
        var d = Find(6.9f, 7.6f);
        var before = shore.Depth(d.X, d.Y, d.Z);
        Assert.True(before < -6.8f && Surface.Height(d.X, d.Y, d.Z) > Surface.ProtectHeight, "unprotected land just above the shore band");
        Assert.True(shore.Add(At(d, 0f, 12f, BrushMode.Dig)), "it reaches below sea level, so it is filed");
        Assert.Equal(before, shore.Depth(d.X, d.Y, d.Z));
        Assert.False(shore.IsSea(d.X, d.Y, d.Z));

        // Edits high above the sea are not filed at all.
        var hill = Find(60f, 90f);
        Assert.False(shore.Add(At(hill, 70f, 3f, BrushMode.Place)));
        Assert.Equal(1, shore.IndexedOps);
    }

    /// <summary>The LOD-0 leaf at <paramref name="point"/> as a chunk job builds it; counts its vertices under the sea radius, and those in the sea mask.</summary>
    private static int MeshLeaf(double3 point, EditStore edits, out int below, out int inSeaMask)
    {
        var job = new ChunkJob { Generator = TestPlanet.Generator };
        job.Chunk.Coord = ChunkCoord.FromPlanet(point.X, point.Y, point.Z, 0);
        job.GatherOps(edits, 0.0);
        job.Run();
        (below, inSeaMask) = (0, 0);

        for (var i = 0; i < job.Mesh.VertexCount; i++)
        {
            var local = job.Mesh.Vertices[i].Position;
            var p = job.Chunk.Coord.Origin + new double3(local.X, local.Y, local.Z);
            below += p.Length() < SeaRadius - 0.5 ? 1 : 0;
            inSeaMask += (job.Mesh.Packed[i].Material & PackedTerrainVertex.SeaColumn) != 0 ? 1 : 0;
            Assert.True((job.Mesh.Packed[i].Material & PackedTerrainVertex.MaterialMask) <= (uint)VoxelMaterial.Dirt, "the palette index survives the mask bit");
        }

        return job.Mesh.VertexCount;
    }

    /// <summary>
    /// The pit rule in the terrain's shading (review F3): mesh vertices carry the generated sea mask, which terrain.frag
    /// needs before it treats ground under the sea radius as under water. Seabed has it; land has not, nor a pit dug
    /// below sea level inland.
    /// </summary>
    [Fact]
    public void TerrainVertices_CarryTheGeneratedSeaMask()
    {
        var none = new EditStore();
        var seabed = Find(-60f, -40f);
        var count = MeshLeaf(seabed * (SeaRadius - 50.0), none, out var below, out var inSeaMask);
        output.WriteLine($"seabed: {count} vertices, {below} under the sea radius, {inSeaMask} in the sea mask");
        Assert.True(count > 0 && below == count && inSeaMask == count);

        var hill = Find(60f, 90f);
        count = MeshLeaf(hill * (SeaRadius + 75.0), none, out below, out inSeaMask);
        output.WriteLine($"hill: {count} vertices, {below} under the sea radius, {inSeaMask} in the sea mask");
        Assert.True(count > 0 && below == 0 && inSeaMask == 0);

        var inland = Find(6.9f, 7.6f);
        var pit = new EditStore();
        pit.Add(At(inland, 0f, 12f, BrushMode.Dig));
        count = MeshLeaf(inland * (SeaRadius - 6.0), pit, out below, out inSeaMask);
        output.WriteLine($"inland pit: {count} vertices, {below} under the sea radius, {inSeaMask} in the sea mask");
        Assert.True(below > 50, $"the pit reaches below sea level ({below} vertices)");
        Assert.Equal(0, inSeaMask);
    }

    /// <summary>
    /// Coarse leaves over low land: their skirts would hang a voxel (hundreds of metres) down, under the sea sphere, where
    /// the far ocean surface draws over them along every seam. Skirts hung from land stay above the sea, and below the
    /// vertex they hang from.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public void LandSkirts_StayAboveTheSea(int lod)
    {
        var d = Find(20f, 30f);
        var job = new ChunkJob { Generator = TestPlanet.Generator };
        job.Chunk.Coord = ChunkCoord.FromPlanet(d.X * (SeaRadius + 25.0), d.Y * (SeaRadius + 25.0), d.Z * (SeaRadius + 25.0), lod);
        job.Run();
        var mesh = job.Mesh;
        var origin = job.Chunk.Coord.Origin;
        int land = 0, lifted = 0;
        var lowest = double.MaxValue;

        double Radius(uint i) => (origin + new double3(mesh.Vertices[i].Position.X, mesh.Vertices[i].Position.Y, mesh.Vertices[i].Position.Z)).Length();

        for (var q = mesh.IndexCount - 6; q >= 0 && mesh.Vertices[mesh.Indices[q + 2]].Normal == mesh.Vertices[mesh.Indices[q + 1]].Normal && mesh.Indices[q + 3] == mesh.Indices[q]; q -= 6)
        {
            for (var end = 0; end < 2; end++)
            {
                var top = mesh.Indices[q + 1 - end];
                var skirt = mesh.Indices[q + 2 + 3 * end];

                if ((mesh.Vertices[skirt].Material & PackedTerrainVertex.SeaColumn) != 0)
                    continue;

                land++;
                lowest = Math.Min(lowest, Radius(skirt) - SeaRadius);
                lifted += Radius(skirt) - SeaRadius < 0.06 * job.Chunk.Coord.VoxelSize ? 1 : 0;
                Assert.True(Radius(skirt) <= Radius(top) + 1e-2 * job.Chunk.Coord.VoxelSize, "a skirt never rises above the vertex it hangs from");
                Assert.Equal(mesh.Packed[skirt].DecodePosition(job.Chunk.Coord.VoxelScale), PackedTerrainVertex.Encode(mesh.Vertices[skirt], 1f / job.Chunk.Coord.VoxelScale).DecodePosition(job.Chunk.Coord.VoxelScale));
            }
        }

        output.WriteLine($"LOD {lod}: {land} land skirt vertices, lowest {lowest:F1} m above the sea, {lifted} held up");
        Assert.True(land > 20 && lifted > 0, $"{land} land skirt vertices, {lifted} held up");
        Assert.True(lowest >= 0.0, $"a land skirt hangs {-lowest:F1} m under the sea");

        // The pooled job's next leaf is empty air: nothing meshed, nothing to lift.
        job.Chunk.Coord = ChunkCoord.FromPlanet(d.X * (SeaRadius + 60_000.0), d.Y * (SeaRadius + 60_000.0), d.Z * (SeaRadius + 60_000.0), lod);
        job.Run();
        Assert.Equal(0, job.Mesh.IndexCount);
    }

    /// <summary>Readers on other threads see consistent lists while the one writer files edits and the index grows.</summary>
    [Fact]
    public void Readers_AreSafeWhileEditsAreFiled()
    {
        var shore = new PlanetShore(Surface);
        var d = Find(-6f, -3f);
        var generated = shore.Depth(d.X, d.Y, d.Z);
        var stop = false;
        var bad = 0;
        var reads = 0L;

        var readers = Enumerable.Range(0, 3).Select(seed => new Thread(() =>
        {
            var random = new Random(seed);
            var count = 0L;

            while (!Volatile.Read(ref stop))
            {
                var p = Beside(Beside(d, random.NextDouble() * 400.0 - 200.0), random.NextDouble() * 4.0);
                var depth = shore.Depth(p.X, p.Y, p.Z);
                var open = Surface.SeaHeight - Surface.Height(p.X, p.Y, p.Z);
                count++;

                // Only fill is ever placed here: never deeper than generated, never higher than the tallest mound.
                if (!(depth <= open + 1e-3f) || depth < open - 40f)
                    Interlocked.Increment(ref bad);
            }

            Interlocked.Add(ref reads, count);
        })).ToArray();

        foreach (var reader in readers)
            reader.Start();

        var writer = new Random(9);

        for (var i = 0; i < 20_000; i++)
            shore.Add(At(Beside(d, writer.NextDouble() * 400.0 - 200.0), -2.0, 1f + (float)writer.NextDouble() * (i % 50 == 0 ? 20f : 2f), BrushMode.Place));

        Volatile.Write(ref stop, true);

        foreach (var reader in readers)
            reader.Join();

        output.WriteLine($"{reads} reads beside 20000 writes ({shore.IndexedOps} filed), {bad} inconsistent");
        Assert.Equal(0, bad);
        Assert.Equal(20_000, shore.IndexedOps);
        Assert.True(shore.Depth(d.X, d.Y, d.Z) <= generated);
    }

    /// <summary>
    /// The CPU depth cap's cost (README M0 carry-over: 0.56 us a sample with no edits, 3.3 us with 1000, on the island):
    /// one generated height, plus two bin lookups once edits exist; 1000 edits elsewhere cost nothing, edits over the
    /// column cost their own replay.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Depth_Cost()
    {
        var d = Find(-6f, -3f);
        var points = new double3[4096];
        var random = new Random(3);

        for (var i = 0; i < points.Length; i++)
            points[i] = Beside(Beside(d, random.NextDouble() * 300.0 - 150.0), random.NextDouble() * 300.0 - 150.0);

        var empty = new PlanetShore(Surface);
        var spread = new PlanetShore(Surface);
        var dense = new PlanetShore(Surface);

        for (var i = 0; i < 1000; i++)
        {
            // 1000 edits over 4 km of coast, and 1000 inside the 300 m the samples cover.
            spread.Add(At(Beside(d, 400.0 + i * 4.0), -1.0, 2.5f, BrushMode.Place));
            dense.Add(At(Beside(Beside(d, random.NextDouble() * 300.0 - 150.0), random.NextDouble() * 300.0 - 150.0), -1.0, 2.5f, BrushMode.Place));
        }

        foreach (var (name, shore) in new[] { ("no edits", empty), ("1000 edits along 4 km of coast nearby", spread), ("1000 edits among the samples", dense) })
        {
            var sink = 0f;

            for (var pass = 0; pass < 20; pass++)
                foreach (var p in points)
                    sink += shore.Depth(p.X, p.Y, p.Z);

            var timer = Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var pass = 0; pass < 20; pass++)
                foreach (var p in points)
                    sink += shore.Depth(p.X, p.Y, p.Z);

            var ns = timer.Elapsed.TotalNanoseconds / (20 * points.Length);
            output.WriteLine($"{name}: {ns:F0} ns per depth sample, {GC.GetAllocatedBytesForCurrentThread() - before} bytes allocated");
            Assert.True(float.IsFinite(sink));
        }
    }

    /// <summary>The camera's depth maps hold PlanetShore's depths at their cell centres, through window moves and an edit.</summary>
    [Fact]
    public void DepthGrid_HoldsTheShoresDepths()
    {
        var shore = new PlanetShore(Surface);
        var d = Find(-3f, -1f);
        var up = d;
        var x = double3.Normalize(double3.Cross(Math.Abs(d.Y) < 0.9 ? double3.UnitY : double3.UnitX, up));
        var z = double3.Cross(x, up);
        var frame = new ShoreDepthGrid.Frame(up * SeaRadius, x, up, z, SeaRadius);
        using var grid = new ShoreDepthGrid(shore);

        grid.Prepare(frame, 100.0, -40.0, maps: true, near: true);
        Settle(grid, 100.0, -40.0, swap: true);
        Assert.True(grid.HasMaps);
        Assert.Equal(new System.Numerics.Vector4(-ShoreDepthGrid.FarResolution * ShoreDepthGrid.FarCellSize * 0.5f - 100f, -ShoreDepthGrid.FarResolution * ShoreDepthGrid.FarCellSize * 0.5f + 40f, ShoreDepthGrid.FarResolution * ShoreDepthGrid.FarCellSize, 0f), grid.FarRect(100.0, -40.0));
        var checkedCells = CheckNear(grid, shore, frame, 100.0, -40.0);

        // The far map: generated ground at its coarser detail, cell centres about the frame's origin.
        for (var i = 0; i < 200; i++)
        {
            var (cx, cz) = (i * 37 % ShoreDepthGrid.FarResolution, i * 91 % ShoreDepthGrid.FarResolution);
            var dir = frame.Direction((cx + 0.5 - ShoreDepthGrid.FarResolution / 2) * ShoreDepthGrid.FarCellSize, (cz + 0.5 - ShoreDepthGrid.FarResolution / 2) * ShoreDepthGrid.FarCellSize);
            var expected = Math.Clamp(shore.GeneratedDepth(dir.X, dir.Y, dir.Z, ShoreDepthGrid.FarLod), ShoreDepthGrid.MinDepth, ShoreDepthGrid.MaxDepth);
            Assert.Equal((float)(Half)expected, (float)grid.Far[cx + cz * ShoreDepthGrid.FarResolution]);
        }

        // The window follows the camera: strips come in (first from the far map, then exactly), the rest stays put.
        var jobs = grid.Jobs;
        Settle(grid, 420.0, 310.0, swap: false);
        Assert.True(grid.Jobs > jobs, "moving 400 m brought strips in");
        checkedCells += CheckNear(grid, shore, frame, 420.0, 310.0);
        Settle(grid, -900.0, 5000.0, swap: false);
        checkedCells += CheckNear(grid, shore, frame, -900.0, 5000.0);

        // An edit: fill by the camera shows once its cells are evaluated again.
        var at = frame.Direction(-900.0, 5000.0);
        var depth = shore.Depth(at.X, at.Y, at.Z);
        var op = At(at, -depth, MathF.Abs(depth) + 3f, BrushMode.Place);
        Assert.True(shore.Add(op));
        grid.MarkEdit(op);
        Settle(grid, -900.0, 5000.0, swap: false);
        checkedCells += CheckNear(grid, shore, frame, -900.0, 5000.0);
        Assert.True(shore.Depth(at.X, at.Y, at.Z) < depth - 1f);
        output.WriteLine($"{checkedCells} near cells checked; {grid.Jobs} jobs, last {grid.LastJobMs:F1} ms for {grid.LastJobCells} cells, far bake {grid.LastFarBakeMs:F0} ms worker");
    }

    private static void Settle(ShoreDepthGrid grid, double x, double z, bool swap)
    {
        var timer = Stopwatch.StartNew();

        while (timer.Elapsed.TotalSeconds < 60.0)
        {
            grid.Update(x, z, near: true);

            if (swap && grid.NextReady)
            {
                grid.Swap();
                swap = false;
            }

            if (!swap && grid.Settled)
                return;

            Thread.Sleep(2);
        }

        Assert.Fail("the depth maps did not settle");
    }

    /// <summary>Every 7th cell of the near window against the shore, at the toroidal texel the shader reads it from.</summary>
    private static int CheckNear(ShoreDepthGrid grid, PlanetShore shore, in ShoreDepthGrid.Frame frame, double cameraX, double cameraZ)
    {
        var rect = grid.NearRect(cameraX, cameraZ);
        var (ox, oz) = grid.Origin;
        Assert.Equal(ShoreDepthGrid.Resolution * ShoreDepthGrid.CellSize, rect.Z);
        Assert.Equal(ox * (double)ShoreDepthGrid.CellSize - cameraX, rect.X, 1e-3);
        var valid = grid.NearValid(cameraX, cameraZ);
        Assert.Equal(new System.Numerics.Vector4(rect.X, rect.Y, rect.X + rect.Z, rect.Y + rect.Z), valid);
        Assert.InRange(cameraX - ox * (double)ShoreDepthGrid.CellSize, 300.0, 724.0);
        Assert.InRange(cameraZ - oz * (double)ShoreDepthGrid.CellSize, 300.0, 724.0);
        var count = 0;

        for (var cz = 0; cz < ShoreDepthGrid.Resolution; cz += 7)
        {
            for (var cx = 0; cx < ShoreDepthGrid.Resolution; cx += 7)
            {
                var dir = frame.Direction((ox + cx + 0.5) * ShoreDepthGrid.CellSize, (oz + cz + 0.5) * ShoreDepthGrid.CellSize);
                var expected = Math.Clamp(shore.Depth(dir.X, dir.Y, dir.Z), ShoreDepthGrid.MinDepth, ShoreDepthGrid.MaxDepth);
                var texel = ((ox + cx) & (ShoreDepthGrid.Resolution - 1)) + ((oz + cz) & (ShoreDepthGrid.Resolution - 1)) * ShoreDepthGrid.Resolution;
                Assert.Equal((float)(Half)expected, (float)grid.Near[texel]);
                count++;
            }
        }

        return count;
    }
}
