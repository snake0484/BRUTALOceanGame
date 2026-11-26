using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrutalGame.Terrain.Tests;

public sealed class TerrainTests
{
    private const int P = ChunkConstants.Padded;

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
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        var air = new VoxelChunk();
        var solid = new VoxelChunk();
        air.Fill(127, VoxelMaterial.Air);
        solid.Fill(-127, VoxelMaterial.Rock);
        air.RecomputeHasSurface();
        solid.RecomputeHasSurface();

        Assert.False(air.HasSurface);
        Assert.False(solid.HasSurface);

        mesher.Mesh(air, mesh);
        Assert.True(mesh.IsEmpty);
        Assert.Equal(0, mesh.VertexCount);

        mesher.Mesh(solid, mesh);
        Assert.True(mesh.IsEmpty);
        Assert.Equal(0, mesh.VertexCount);
    }

    [Fact]
    public void MesherDoesNotAllocateAfterWarmUp()
    {
        var chunk = new VoxelChunk();
        FillSphere(chunk, new Vector3(16f, 40f, 16f), 30f);
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
    public void VertexLayoutMatchesGpu()
    {
        Assert.Equal(TerrainVertex.SizeInBytes, Unsafe.SizeOf<TerrainVertex>());
    }

    [Fact]
    public void PlaceAcrossSeamIsWatertightAndApronsAgree()
    {
        var op = EditOp.At(32.0, 16.0, 16.0, 9f, BrushMode.Place, VoxelMaterial.Rock);
        Assert.True(PlanetEditor.Intersects(op, new ChunkCoord(0, 0, 0)));
        Assert.True(PlanetEditor.Intersects(op, new ChunkCoord(1, 0, 0)));

        var a = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        var b = new VoxelChunk { Coord = new ChunkCoord(1, 0, 0) };
        a.Fill(127, VoxelMaterial.Air);
        b.Fill(127, VoxelMaterial.Air);
        PlanetEditor.Apply(a, [op]);
        PlanetEditor.Apply(b, [op]);
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
        PlanetEditor.Apply(chunk, [EditOp.At(centre.X, centre.Y, centre.Z, 8.3f, BrushMode.Dig, VoxelMaterial.Air)]);
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
    public void BrushCraterLandsInSamePlaceAcrossLods()
    {
        var op = EditOp.At(16.0, 16.0, 16.0, 6f, BrushMode.Dig, VoxelMaterial.Air);
        var fine = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0) };
        var coarse = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, 1) };
        FillPlane(fine, 16f);
        FillPlane(coarse, 16f);

        Assert.True(PlanetEditor.Intersects(op, fine.Coord));
        Assert.True(PlanetEditor.Intersects(op, coarse.Coord));

        PlanetEditor.Apply(fine, [op]);
        PlanetEditor.Apply(coarse, [op]);

        var s = coarse.Coord.VoxelScale;
        var h0 = ColumnSurface(fine, 16f, 16f);
        var h1 = ColumnSurface(coarse, 16f, 16f);
        Assert.InRange(h0, 10f - 0.5f, 10f + 0.5f);
        Assert.InRange(h1, h0 - s, h0 + s);
        Assert.InRange(ColumnSurface(fine, 28f, 16f), 15.9f, 16.1f);
        Assert.InRange(ColumnSurface(coarse, 28f, 16f), 15.9f, 16.1f);
    }

    [Fact]
    public void EditReachStopsAtTheApron()
    {
        var small = EditOp.At(16.0, 16.0, 16.0, 3f, BrushMode.Dig, VoxelMaterial.Air);
        Assert.True(PlanetEditor.Intersects(small, new ChunkCoord(0, 0, 0)));
        Assert.False(PlanetEditor.Intersects(small, new ChunkCoord(1, 0, 0)));
        Assert.False(PlanetEditor.Intersects(small, new ChunkCoord(2, 0, 0, 1)));
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

        // The flange: 1 voxel down and 1 voxel out past the surface's edge, corners mitred onto both sides' lines.
        var lo = -1.5f * s;
        var hi = (ChunkConstants.Size + 0.5f) * s;
        var tolerance = 1e-4f * s;
        Span<int> sides = stackalloc int[4];
        var centre = new Vector3(ChunkConstants.Size * s * 0.5f, 0f, ChunkConstants.Size * s * 0.5f);

        for (var v = plain.VertexCount; v < skirted.VertexCount; v++)
        {
            var pos = skirted.Vertices[v].Position;
            Assert.Equal(16f - s, pos.Y, 4);
            Assert.Equal(Vector3.UnitY, skirted.Vertices[v].Normal);
            Assert.InRange(pos.X, lo - tolerance, hi + tolerance);
            Assert.InRange(pos.Z, lo - tolerance, hi + tolerance);
            var before = sides[0] + sides[1] + sides[2] + sides[3];
            sides[0] += MathF.Abs(pos.X - lo) <= tolerance ? 1 : 0;
            sides[1] += MathF.Abs(pos.X - hi) <= tolerance ? 1 : 0;
            sides[2] += MathF.Abs(pos.Z - lo) <= tolerance ? 1 : 0;
            sides[3] += MathF.Abs(pos.Z - hi) <= tolerance ? 1 : 0;
            Assert.True(sides[0] + sides[1] + sides[2] + sides[3] > before, $"skirt vertex {pos} is on no side's line");
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
    public void CoarseFillAndMeshDoNotAllocateAfterWarmUp()
    {
        var chunk = new VoxelChunk { Coord = new ChunkCoord(0, 0, 0, 3) };
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();
        FillPlane(chunk, 100f);
        Assert.True(chunk.HasSurface);
        mesher.Mesh(chunk, mesh);
        mesher.Mesh(chunk, mesh);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 5; i++)
        {
            FillPlane(chunk, 100f);
            mesher.Mesh(chunk, mesh);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(mesh.IndexCount > 0);
        Assert.True(mesher.LastSkirtTriangles > 0);
    }
}
