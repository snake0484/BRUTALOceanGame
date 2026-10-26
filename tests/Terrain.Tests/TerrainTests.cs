using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain.Tests;

public sealed class TerrainTests
{
    private const int P = ChunkConstants.Padded;

    private static readonly ChunkCoord[] SampleCoords =
    [
        new(0, 0, 0), new(0, 1, 0), new(6, 0, 0), new(-7, -1, 2), new(3, 0, -5), new(-2, 1, 1), new(12, -1, 9), new(0, -2, 0),
    ];

    private static VoxelChunk Generate(ChunkCoord coord, in TerrainParams p)
    {
        var chunk = new VoxelChunk { Coord = coord };
        TerrainGenerator.Generate(chunk, p);
        return chunk;
    }

    private static void FillSphere(VoxelChunk chunk, Vector3 worldCentre, float radius)
    {
        var origin = chunk.Coord.WorldOrigin;
        var s = chunk.Coord.VoxelScale;

        for (var z = -1; z <= ChunkConstants.Size; z++)
        {
            for (var y = -1; y <= ChunkConstants.Size; y++)
            {
                for (var x = -1; x <= ChunkConstants.Size; x++)
                {
                    var dist = Vector3.Distance(origin + new Vector3(x, y, z) * s, worldCentre) - radius;
                    var q = Math.Clamp((int)MathF.Round(dist / s * ChunkConstants.DensityScale), -127, 127);
                    var i = ChunkConstants.Index(x, y, z);
                    chunk.Density[i] = (sbyte)q;
                    chunk.Material[i] = (byte)(q < 0 ? VoxelMaterial.Rock : VoxelMaterial.Air);
                }
            }
        }

        chunk.RecomputeHasSurface();
    }

    private static void AssertEdgeManifold(IEnumerable<(ChunkMesh Mesh, Vector3 Offset)> meshes)
    {
        var ids = new Dictionary<Vector3, int>();
        var edges = new Dictionary<(int, int), int>();

        foreach (var (mesh, offset) in meshes)
        {
            var map = new int[mesh.VertexCount];

            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var pos = mesh.Vertices[v].Position + offset;

                if (!ids.TryGetValue(pos, out var id))
                {
                    id = ids.Count;
                    ids.Add(pos, id);
                }

                map[v] = id;
            }

            for (var t = 0; t < mesh.IndexCount; t += 3)
            {
                var a = map[mesh.Indices[t]];
                var b = map[mesh.Indices[t + 1]];
                var c = map[mesh.Indices[t + 2]];
                AddEdge(edges, a, b);
                AddEdge(edges, b, c);
                AddEdge(edges, c, a);
            }
        }

        Assert.NotEmpty(edges);
        Assert.All(edges, e => Assert.Equal(2, e.Value));
    }

    private static void AddEdge(Dictionary<(int, int), int> edges, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        edges[key] = edges.GetValueOrDefault(key) + 1;
    }

    private static ulong Fnv(ulong h, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            h = (h ^ b) * 1099511628211UL;

        return h;
    }

    private static (ulong Voxels, ulong Mesh) Lod0Hashes()
    {
        var p = new TerrainParams { Seed = 1234 };
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        var hv = 14695981039346656037UL;
        var hm = 14695981039346656037UL;

        foreach (var coord in SampleCoords)
        {
            var c = Generate(coord, p);
            hv = Fnv(hv, MemoryMarshal.AsBytes(c.Density.AsSpan()));
            hv = Fnv(hv, c.Material);
            hv = Fnv(hv, c.Water);
            hv = Fnv(hv, [c.HasSurface ? (byte)1 : (byte)0]);
            mesher.Mesh(c, mesh, skirts: false);
            hm = Fnv(hm, MemoryMarshal.AsBytes(mesh.VertexSpan));
            hm = Fnv(hm, MemoryMarshal.AsBytes(mesh.IndexSpan));
        }

        return (hv, hm);
    }

    private static ulong Lod0EditHash()
    {
        BrushOp[] ops =
        [
            new(new Vector3(200f, 5f, 10f), 6f, BrushMode.Dig, VoxelMaterial.Air),
            new(new Vector3(205f, 8f, 12f), 3f, BrushMode.Place, VoxelMaterial.Dirt),
            new(new Vector3(190.3f, -2.7f, 25.1f), 4.4f, BrushMode.Place, VoxelMaterial.Sand),
        ];

        var c = Generate(new ChunkCoord(6, 0, 0), new TerrainParams());
        VoxelEditor.Apply(c, ops);
        var h = Fnv(14695981039346656037UL, MemoryMarshal.AsBytes(c.Density.AsSpan()));
        h = Fnv(h, c.Material);
        return Fnv(h, c.Water);
    }

    [Fact]
    public void Lod0OutputMatchesReference()
    {
        var (voxels, mesh) = Lod0Hashes();
        Assert.Equal(15090781701742964954UL, voxels);
        Assert.Equal(3896220001399264571UL, mesh);
        Assert.Equal(14746362846408289604UL, Lod0EditHash());
    }

    [Fact]
    public void GenerationIsDeterministicAndThreadSafe()
    {
        var p = new TerrainParams { Seed = 1234 };
        var serial = SampleCoords.Select(c => Generate(c, p)).ToArray();
        var again = SampleCoords.Select(c => Generate(c, p)).ToArray();
        var parallel = new VoxelChunk[SampleCoords.Length * 8];
        Parallel.For(0, parallel.Length, i => parallel[i] = Generate(SampleCoords[i % SampleCoords.Length], p));

        Assert.Contains(serial, c => c.HasSurface);

        for (var i = 0; i < parallel.Length; i++)
        {
            var s = serial[i % SampleCoords.Length];
            var r = i < serial.Length ? again[i] : s;
            Assert.Equal(s.Density, r.Density);
            Assert.Equal(s.Material, r.Material);
            Assert.Equal(s.Density, parallel[i].Density);
            Assert.Equal(s.Material, parallel[i].Material);
            Assert.Equal(s.HasSurface, parallel[i].HasSurface);
        }

        var other = Generate(SampleCoords[2], new TerrainParams { Seed = 99 });
        Assert.NotEqual(serial[2].Density, other.Density);
    }

    [Fact]
    public void GeneratedNeighboursAgreeOnSharedApron()
    {
        var p = new TerrainParams();
        var a = Generate(new ChunkCoord(6, 0, 0), p);
        var b = Generate(new ChunkCoord(7, 0, 0), p);
        Assert.True(a.HasSurface || b.HasSurface);

        for (var z = -1; z <= ChunkConstants.Size; z++)
        {
            for (var y = -1; y <= ChunkConstants.Size; y++)
            {
                for (var k = 0; k < 2; k++)
                {
                    var ia = ChunkConstants.Index(ChunkConstants.Size - 1 + k, y, z);
                    var ib = ChunkConstants.Index(-1 + k, y, z);
                    Assert.Equal(a.Density[ia], b.Density[ib]);
                    Assert.Equal(a.Material[ia], b.Material[ib]);
                }
            }
        }
    }

    [Fact]
    public void SphereMeshIsAccurateOutwardAndClosed()
    {
        var chunk = new VoxelChunk();
        var centre = new Vector3(16f);
        FillSphere(chunk, centre, 10f);
        var mesh = new ChunkMesh();
        new SurfaceNetsMesher().Mesh(chunk, mesh, skirts: false);

        Assert.True(mesh.VertexCount > 100);
        Assert.Equal(0, mesh.IndexCount % 3);

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var vert = mesh.Vertices[v];
            var radial = vert.Position - centre;
            Assert.InRange(radial.Length(), 9.4f, 10.6f);
            Assert.InRange(vert.Normal.Length(), 0.999f, 1.001f);
            Assert.True(Vector3.Dot(vert.Normal, Vector3.Normalize(radial)) > 0.8f);
            Assert.Equal((uint)VoxelMaterial.Rock, vert.Material);
        }

        for (var t = 0; t < mesh.IndexCount; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]];
            var b = mesh.Vertices[mesh.Indices[t + 1]];
            var c = mesh.Vertices[mesh.Indices[t + 2]];
            var faceNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Assert.True(Vector3.Dot(faceNormal, a.Normal + b.Normal + c.Normal) > 0f);
        }

        AssertEdgeManifold([(mesh, Vector3.Zero)]);
    }

    [Fact]
    public void SeamBetweenChunksIsWatertight()
    {
        var centre = new Vector3(32f, 16f, 16f);
        var a = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        var b = new VoxelChunk { Coord = new ChunkCoord(1, 0, 0) };
        FillSphere(a, centre, 10f);
        FillSphere(b, centre, 10f);
        var mesher = new SurfaceNetsMesher();
        var ma = new ChunkMesh();
        var mb = new ChunkMesh();
        mesher.Mesh(a, ma, skirts: false);
        mesher.Mesh(b, mb, skirts: false);

        Assert.False(ma.IsEmpty);
        Assert.False(mb.IsEmpty);

        var offset = new Vector3(ChunkConstants.Size * ChunkConstants.VoxelSize, 0f, 0f);
        var positionsA = ma.VertexSpan.ToArray().Select(v => v.Position).ToHashSet();
        var shared = mb.VertexSpan.ToArray().Count(v => positionsA.Contains(v.Position + offset));
        Assert.True(shared > 20);

        AssertEdgeManifold([(ma, Vector3.Zero), (mb, offset)]);
    }

    [Fact]
    public void EmptyAndFullChunksProduceNoGeometry()
    {
        var p = new TerrainParams { SeabedDepth = -10f };
        var air = Generate(new ChunkCoord(100, ChunkConstants.MaxChunkY, 100), p);
        var solid = Generate(new ChunkCoord(100, ChunkConstants.MinChunkY, 100), p);
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();

        Assert.False(air.HasSurface);
        Assert.False(solid.HasSurface);
        Assert.All(air.Density, d => Assert.True(d >= 0));
        Assert.All(solid.Density, d => Assert.True(d < 0));

        mesher.Mesh(air, mesh);
        Assert.True(mesh.IsEmpty);
        Assert.Equal(0, mesh.VertexCount);

        mesher.Mesh(solid, mesh);
        Assert.True(mesh.IsEmpty);
        Assert.Equal(0, mesh.VertexCount);

        var manual = new VoxelChunk();
        manual.Fill(-20, VoxelMaterial.Rock);
        manual.RecomputeHasSurface();
        Assert.False(manual.HasSurface);
        mesher.Mesh(manual, mesh);
        Assert.True(mesh.IsEmpty);
    }

    [Fact]
    public void MesherDoesNotAllocateAfterWarmUp()
    {
        var p = new TerrainParams();
        var chunk = Generate(new ChunkCoord(6, 0, 0), p);
        Assert.True(chunk.HasSurface);
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        mesher.Mesh(chunk, mesh);
        mesher.Mesh(chunk, mesh);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 10; i++)
            mesher.Mesh(chunk, mesh);

        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
        Assert.True(mesh.IndexCount > 0);
    }

    [Fact]
    public void GeneratorDoesNotAllocate()
    {
        var p = new TerrainParams();
        var chunk = new VoxelChunk { Coord = new ChunkCoord(6, 0, 0) };
        TerrainGenerator.Generate(chunk, p);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 5; i++)
            TerrainGenerator.Generate(chunk, p);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void VertexLayoutMatchesGpu()
    {
        Assert.Equal(TerrainVertex.SizeInBytes, Unsafe.SizeOf<TerrainVertex>());
    }

    [Fact]
    public void IslandHasBeachesAndSeabed()
    {
        var p = new TerrainParams();
        Assert.True(TerrainGenerator.SurfaceHeight(0f, 0f, p) > 30f);
        Assert.InRange(TerrainGenerator.SurfaceHeight(2000f, 2000f, p), p.SeabedDepth - p.SeabedUndulation - 1f, p.SeabedDepth + p.SeabedUndulation + 1f);
    }

    [Fact]
    public void PlaceAcrossSeamIsWatertightAndApronsAgree()
    {
        var op = new BrushOp(new Vector3(32f, 16f, 16f), 9f, BrushMode.Place, VoxelMaterial.Rock);
        Span<ChunkCoord> affected = stackalloc ChunkCoord[64];
        var count = VoxelEditor.AffectedChunks(op, affected);
        Assert.Contains(new ChunkCoord(0, 0, 0), affected[..count].ToArray());
        Assert.Contains(new ChunkCoord(1, 0, 0), affected[..count].ToArray());

        var a = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        var b = new VoxelChunk { Coord = new ChunkCoord(1, 0, 0) };
        a.Fill(127, VoxelMaterial.Air);
        b.Fill(127, VoxelMaterial.Air);
        VoxelEditor.Apply(a, [op]);
        VoxelEditor.Apply(b, [op]);
        Assert.True(a.HasSurface && b.HasSurface);

        for (var z = -1; z <= ChunkConstants.Size; z++)
        for (var y = -1; y <= ChunkConstants.Size; y++)
        for (var k = 0; k < 2; k++)
        {
            var ia = ChunkConstants.Index(ChunkConstants.Size - 1 + k, y, z);
            var ib = ChunkConstants.Index(-1 + k, y, z);
            Assert.Equal(a.Density[ia], b.Density[ib]);
            Assert.Equal(a.Material[ia], b.Material[ib]);
        }

        var mesher = new SurfaceNetsMesher();
        var ma = new ChunkMesh();
        var mb = new ChunkMesh();
        mesher.Mesh(a, ma, skirts: false);
        mesher.Mesh(b, mb, skirts: false);
        AssertEdgeManifold([(ma, Vector3.Zero), (mb, new Vector3(ChunkConstants.Size * ChunkConstants.VoxelSize, 0f, 0f))]);

        foreach (var v in ma.VertexSpan)
            Assert.Equal((uint)VoxelMaterial.Rock, v.Material);
    }

    [Fact]
    public void DigCarvesClosedCavityWithInwardFacingAirSide()
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        chunk.Fill(-127, VoxelMaterial.Rock);
        var centre = new Vector3(16f, 16f, 16f);
        VoxelEditor.Apply(chunk, [new BrushOp(centre, 8.3f, BrushMode.Dig, VoxelMaterial.Air)]);
        Assert.True(chunk.HasSurface);

        var mesh = new ChunkMesh();
        new SurfaceNetsMesher().Mesh(chunk, mesh, skirts: false);
        Assert.False(mesh.IsEmpty);

        foreach (var v in mesh.VertexSpan)
        {
            var radial = v.Position - centre;
            Assert.InRange(radial.Length(), 7.7f, 8.9f);
            Assert.True(Vector3.Dot(Vector3.Normalize(radial), v.Normal) < -0.8f, "cavity normals must point into the air (toward the centre)");
        }

        AssertEdgeManifold([(mesh, Vector3.Zero)]);
    }

    [Fact]
    public void EditsReplayDeterministically()
    {
        var p = new TerrainParams();
        BrushOp[] ops =
        [
            new(new Vector3(200f, 5f, 10f), 6f, BrushMode.Dig, VoxelMaterial.Air),
            new(new Vector3(205f, 8f, 12f), 3f, BrushMode.Place, VoxelMaterial.Dirt),
        ];

        var a = Generate(new ChunkCoord(6, 0, 0), p);
        var b = Generate(new ChunkCoord(6, 0, 0), p);
        VoxelEditor.Apply(a, ops);
        VoxelEditor.Apply(b, ops);
        Assert.Equal(a.Density, b.Density);
        Assert.Equal(a.Material, b.Material);
    }

    [Fact]
    public void GenerationFillsSeaOnlyInAirBelowSeaLevel()
    {
        var p = new TerrainParams();
        var chunk = Generate(new ChunkCoord(20, -1, 0), p); // open sea, well off the coast
        var origin = chunk.Coord.WorldOrigin;
        var wet = 0;

        for (var z = -1; z <= ChunkConstants.Size; z++)
        for (var y = -1; y <= ChunkConstants.Size; y++)
        for (var x = -1; x <= ChunkConstants.Size; x++)
        {
            var i = ChunkConstants.Index(x, y, z);
            var expected = chunk.Density[i] >= 0 && origin.Y + y < p.SeaLevel;
            Assert.Equal(expected ? byte.MaxValue : (byte)0, chunk.Water[i]);
            wet += expected ? 1 : 0;
        }

        Assert.True(wet > 0);
        Assert.All(Generate(new ChunkCoord(0, 1, 0), p).Water, w => Assert.Equal(0, w));
    }

    [Fact]
    public void DigKeepsOpenWaterAndPlaceDisplacesIt()
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, -1, 0) };
        chunk.Fill(127, VoxelMaterial.Air);
        TerrainGenerator.FillSea(chunk, 0f);
        var probe = ChunkConstants.Index(16, 28, 16);
        Assert.Equal(byte.MaxValue, chunk.Water[probe]);

        VoxelEditor.Apply(chunk, [new BrushOp(new Vector3(16f, -4f, 16f), 3f, BrushMode.Dig, VoxelMaterial.Air)]);
        Assert.Equal(byte.MaxValue, chunk.Water[probe]);

        VoxelEditor.Apply(chunk, [new BrushOp(new Vector3(16f, -4f, 16f), 3f, BrushMode.Place, VoxelMaterial.Rock)]);
        Assert.Equal(0, chunk.Water[probe]);
        Assert.True(chunk.Density[probe] < 0);
    }

    [Fact]
    public void InlandPitDugBelowSeaLevelIsDry()
    {
        var p = new TerrainParams();
        var land = new Vector3(0f, TerrainGenerator.SurfaceHeight(0f, 0f, p), 0f);
        Assert.True(land.Y > 10f, "island centre should be well above sea level");

        var op = new BrushOp(new Vector3(0f, -2f, 0f), land.Y + 4f, BrushMode.Dig, VoxelMaterial.Air);
        var chunk = Generate(new ChunkCoord(0, -1, 0), p);
        VoxelEditor.Apply(chunk, [op]);

        var i = ChunkConstants.Index(0, ChunkConstants.Size - 1, 0);
        Assert.True(chunk.Density[i] >= 0, "the pit must be carved through the sea-level layer");
        Assert.Equal(0, chunk.Water[i]);
    }

    private static void FillPlane(VoxelChunk chunk, float height)
    {
        var originY = chunk.Coord.WorldOrigin.Y;
        var s = chunk.Coord.VoxelScale;

        for (var z = -1; z <= ChunkConstants.Size; z++)
        for (var y = -1; y <= ChunkConstants.Size; y++)
        for (var x = -1; x <= ChunkConstants.Size; x++)
        {
            var q = Math.Clamp((int)MathF.Round((originY + y * s - height) / s * ChunkConstants.DensityScale), -127, 127);
            var i = ChunkConstants.Index(x, y, z);
            chunk.Density[i] = (sbyte)q;
            chunk.Material[i] = (byte)(q < 0 ? VoxelMaterial.Rock : VoxelMaterial.Air);
        }

        chunk.RecomputeHasSurface();
    }

    private static float ColumnSurface(VoxelChunk chunk, float worldX, float worldZ)
    {
        var origin = chunk.Coord.WorldOrigin;
        var s = chunk.Coord.VoxelScale;
        var x = (int)MathF.Round((worldX - origin.X) / s);
        var z = (int)MathF.Round((worldZ - origin.Z) / s);

        for (var y = ChunkConstants.Size - 1; y >= -1; y--)
        {
            int below = chunk.Density[ChunkConstants.Index(x, y, z)];
            int above = chunk.Density[ChunkConstants.Index(x, y + 1, z)];

            if (below < 0 && above >= 0)
                return origin.Y + (y - below / (float)(above - below)) * s;
        }

        return float.NaN;
    }

    [Fact]
    public void ChunkCoordLodMaths()
    {
        var c = new ChunkCoord(1, -1, 2, 3);
        Assert.Equal(8f, c.VoxelScale);
        Assert.Equal(256f, c.SizeMetres);
        Assert.Equal(new Vector3(256f, -256f, 512f), c.WorldOrigin);
        Assert.Equal(new ChunkCoord(-2, 2, 0, 2), new ChunkCoord(-3, 5, 0, 1).Parent);
        Assert.Equal(new ChunkCoord(-1, 1, 2, 3), ChunkCoord.FromWorld(new Vector3(-1f, 300f, 600f), 3));
        Assert.Equal(new ChunkCoord(-1, 9, 18), ChunkCoord.FromWorld(new Vector3(-1f, 300f, 600f)));
        Assert.Equal(new ChunkCoord(4, 5, 6, 0), new ChunkCoord(4, 5, 6));
    }

    [Fact]
    public void CoarseSphereIsAccurateAndOutward()
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, 2) };
        const float radius = 100f;
        FillSphere(chunk, Vector3.Zero, radius);
        var mesh = new ChunkMesh();
        new SurfaceNetsMesher().Mesh(chunk, mesh, skirts: false);

        Assert.True(mesh.VertexCount > 100);

        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var vert = mesh.Vertices[v];
            var world = chunk.Coord.WorldOrigin + vert.Position;
            Assert.InRange(world.Length(), radius - 0.5f * chunk.Coord.VoxelScale, radius + 0.5f * chunk.Coord.VoxelScale);
            Assert.True(Vector3.Dot(vert.Normal, Vector3.Normalize(world)) > 0.9f);
        }

        for (var t = 0; t < mesh.IndexCount; t += 3)
        {
            var a = mesh.Vertices[mesh.Indices[t]];
            var b = mesh.Vertices[mesh.Indices[t + 1]];
            var c = mesh.Vertices[mesh.Indices[t + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal + b.Normal + c.Normal) > 0f);
        }
    }

    [Fact]
    public void CoarseGeneratedSurfaceFollowsHeightField()
    {
        var p = new TerrainParams { OverhangAmplitude = 0f };
        var chunk = Generate(new ChunkCoord(0, 0, 0, 2), p);
        Assert.True(chunk.HasSurface);

        var mesh = new ChunkMesh();
        new SurfaceNetsMesher().Mesh(chunk, mesh, skirts: false);
        var checkedCount = 0;

        foreach (var v in mesh.VertexSpan)
        {
            if (v.Normal.Y < 0.8f)
                continue;

            var world = chunk.Coord.WorldOrigin + v.Position;
            Assert.InRange(world.Y - TerrainGenerator.SurfaceHeight(world.X, world.Z, p), -chunk.Coord.VoxelScale, chunk.Coord.VoxelScale);
            checkedCount++;
        }

        Assert.True(checkedCount > 50);
    }

    [Fact]
    public void BrushCraterLandsInSamePlaceAcrossLods()
    {
        var op = new BrushOp(new Vector3(16f, 16f, 16f), 6f, BrushMode.Dig, VoxelMaterial.Air);
        var fine = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        var coarse = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, 1) };
        FillPlane(fine, 16f);
        FillPlane(coarse, 16f);

        Assert.True(VoxelEditor.Intersects(op, fine.Coord));
        Assert.True(VoxelEditor.Intersects(op, coarse.Coord));

        VoxelEditor.Apply(fine, [op]);
        VoxelEditor.Apply(coarse, [op]);

        var s = coarse.Coord.VoxelScale;
        var h0 = ColumnSurface(fine, 16f, 16f);
        var h1 = ColumnSurface(coarse, 16f, 16f);
        Assert.InRange(h0, 10f - 0.5f, 10f + 0.5f);
        Assert.InRange(h1, h0 - s, h0 + s);
        Assert.InRange(ColumnSurface(fine, 28f, 16f), 15.9f, 16.1f);
        Assert.InRange(ColumnSurface(coarse, 28f, 16f), 15.9f, 16.1f);
    }

    [Fact]
    public void AffectedChunksAtLodCoverIntersectingChunks()
    {
        Span<ChunkCoord> affected = stackalloc ChunkCoord[256];
        var rng = new Random(7);

        for (var n = 0; n < 200; n++)
        {
            var lod = n % 4;
            var op = new BrushOp(new Vector3(rng.NextSingle() * 400f - 200f, rng.NextSingle() * 100f - 50f, rng.NextSingle() * 400f - 200f),
                1f + rng.NextSingle() * 20f, BrushMode.Dig, VoxelMaterial.Air);
            var count = VoxelEditor.AffectedChunks(op, lod, affected);
            var list = affected[..count].ToArray();
            Assert.All(list, c => Assert.Equal(lod, c.Lod));

            var centre = ChunkCoord.FromWorld(op.Centre, lod);
            Assert.Contains(centre, list);

            for (var dz = -2; dz <= 2; dz++)
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                var c = centre with { X = centre.X + dx, Y = centre.Y + dy, Z = centre.Z + dz };

                if (c.Y >= ChunkConstants.MinChunkY >> lod && c.Y <= ChunkConstants.MaxChunkY >> lod && VoxelEditor.Intersects(op, c))
                    Assert.Contains(c, list);
            }
        }

        var small = new BrushOp(new Vector3(16f), 3f, BrushMode.Dig, VoxelMaterial.Air);
        Assert.False(VoxelEditor.Intersects(small, new ChunkCoord(1, 0, 0)));
        Assert.False(VoxelEditor.Intersects(small, new ChunkCoord(2, 0, 0, 1)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FlatPlaneGetsSkirtsOnAllFourSides(int lod)
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, lod) };
        FillPlane(chunk, 16f);
        var s = chunk.Coord.VoxelScale;
        var mesher = new SurfaceNetsMesher();
        var plain = new ChunkMesh();
        var skirted = new ChunkMesh();
        mesher.Mesh(chunk, plain, skirts: false);
        Assert.Equal(0, mesher.LastSkirtTriangles);
        mesher.Mesh(chunk, skirted);

        const int edgesPerSide = ChunkConstants.Size;
        Assert.Equal(4 * edgesPerSide * 2, mesher.LastSkirtTriangles);
        Assert.Equal(plain.IndexCount + mesher.LastSkirtTriangles * 3, skirted.IndexCount);
        Assert.Equal(plain.VertexCount + mesher.LastSkirtTriangles, skirted.VertexCount);
        Assert.True(plain.VertexSpan.SequenceEqual(skirted.VertexSpan[..plain.VertexCount]));
        Assert.True(plain.IndexSpan.SequenceEqual(skirted.IndexSpan[..plain.IndexCount]));

        foreach (var v in plain.VertexSpan)
            Assert.Equal(16f, v.Position.Y);

        var lo = -0.5f * s;
        var hi = (ChunkConstants.Size - 0.5f) * s;
        Span<int> sides = stackalloc int[4];
        var centre = new Vector3(ChunkConstants.Size * s * 0.5f, 0f, ChunkConstants.Size * s * 0.5f);

        for (var v = plain.VertexCount; v < skirted.VertexCount; v++)
        {
            var pos = skirted.Vertices[v].Position;
            Assert.Equal(16f - s, pos.Y, 4);
            Assert.Equal(Vector3.UnitY, skirted.Vertices[v].Normal);
            Assert.True(pos.X == lo || pos.X == hi || pos.Z == lo || pos.Z == hi);
            sides[0] += pos.X == lo ? 1 : 0;
            sides[1] += pos.X == hi ? 1 : 0;
            sides[2] += pos.Z == lo ? 1 : 0;
            sides[3] += pos.Z == hi ? 1 : 0;
        }

        foreach (var count in sides)
            Assert.True(count >= edgesPerSide * 2);

        for (var t = plain.IndexCount; t < skirted.IndexCount; t += 3)
        {
            var a = skirted.Vertices[skirted.Indices[t]].Position;
            var b = skirted.Vertices[skirted.Indices[t + 1]].Position;
            var c = skirted.Vertices[skirted.Indices[t + 2]].Position;
            var faceNormal = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3f - centre;
            outward.Y = 0f;
            Assert.True(Vector3.Dot(faceNormal, outward) > 0f, "skirts must face out of the chunk");
        }
    }

    [Fact]
    public void ClosedSurfaceGetsNoSkirts()
    {
        var chunk = new VoxelChunk();
        FillSphere(chunk, new Vector3(16f), 10f);
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        mesher.Mesh(chunk, mesh);
        Assert.Equal(0, mesher.LastSkirtTriangles);
    }

    [Fact]
    public void CoarseGenerateAndMeshDoNotAllocateAfterWarmUp()
    {
        var p = new TerrainParams();
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, 3) };
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        TerrainGenerator.Generate(chunk, p);
        Assert.True(chunk.HasSurface);
        mesher.Mesh(chunk, mesh);
        mesher.Mesh(chunk, mesh);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 5; i++)
        {
            TerrainGenerator.Generate(chunk, p);
            mesher.Mesh(chunk, mesh);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(mesh.IndexCount > 0);
        Assert.True(mesher.LastSkirtTriangles > 0);
    }

    [Fact]
    public void UniformCoarseChunksExitEarly()
    {
        var p = new TerrainParams();
        ChunkCoord[] coords = [new(0, 1, 0, 4), new(0, -2, 0, 4), new(-3, 2, 7, 4), new(5, -3, -4, 4)];
        var chunk = new VoxelChunk();

        foreach (var c in coords)
        {
            chunk.Coord = c;
            TerrainGenerator.Generate(chunk, p);
            Assert.False(chunk.HasSurface);
        }

        chunk.Coord = coords[0];
        TerrainGenerator.Generate(chunk, p);
        Assert.All(chunk.Density, d => Assert.Equal(sbyte.MaxValue, d));
        chunk.Coord = coords[1];
        TerrainGenerator.Generate(chunk, p);
        Assert.All(chunk.Density, d => Assert.Equal(-sbyte.MaxValue, d));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 400; i++)
        {
            chunk.Coord = coords[i & 3];
            TerrainGenerator.Generate(chunk, p);
        }

        Assert.True(sw.Elapsed.TotalMilliseconds < 200, $"400 uniform chunks took {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}
