using System.Numerics;
using System.Runtime.CompilerServices;
using BrutalGame.Terrain.Runtime;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class PackedTerrainVertexTests(ITestOutputHelper output)
{
    // Surface Nets apron cells and skirts reach 2 voxels past the chunk cube (TerrainRenderer.CullMarginVoxels).
    private const float MarginVoxels = 2f;
    private const float MaxNormalErrorDegrees = 0.01f;

    // Half a fixed-point step, plus float rounding of the encode's add (one ulp at 64 voxels).
    private const float BoundVoxels = 0.5f / PackedTerrainVertex.StepsPerVoxel + 64f / (1 << 23);

    [Fact]
    public void LayoutMatchesGpu()
    {
        Assert.Equal(PackedTerrainVertex.SizeInBytes, Unsafe.SizeOf<PackedTerrainVertex>());
        Assert.True(PackedTerrainVertex.OffsetVoxels >= MarginVoxels);
        Assert.True(ushort.MaxValue / (float)PackedTerrainVertex.StepsPerVoxel - PackedTerrainVertex.OffsetVoxels >= ChunkConstants.Size + MarginVoxels);
    }

    [Fact]
    public void PositionsRoundTripOverChunkAndMarginAtEveryLod()
    {
        var random = new Random(1);
        const float lo = -MarginVoxels;
        const float hi = ChunkConstants.Size + MarginVoxels;

        for (var lod = 0; lod <= TerrainWorld.MaxLod; lod++)
        {
            var scale = new ChunkCoord(0, 0, 0, lod).VoxelScale;
            var bound = BoundVoxels * scale;
            var worst = 0f;

            for (var i = 0; i < 200_000; i++)
            {
                var voxels = i < 8
                    ? new Vector3((i & 1) != 0 ? hi : lo, (i & 2) != 0 ? hi : lo, (i & 4) != 0 ? hi : lo)
                    : new Vector3(Uniform(random, lo, hi), Uniform(random, lo, hi), Uniform(random, lo, hi));
                var v = new TerrainVertex { Position = voxels * scale, Normal = Vector3.UnitY };
                var error = MaxAxisError(PackedTerrainVertex.Encode(v, 1f / scale).DecodePosition(scale), v.Position);
                worst = MathF.Max(worst, error);
            }

            output.WriteLine($"LOD {lod}: voxel {scale} m, worst position error {worst * 1000f:F4} mm (bound {bound * 1000f:F4} mm)");
            Assert.True(worst <= bound, $"LOD {lod}: {worst} m > {bound} m");
        }
    }

    [Fact]
    public void MeshedSurfaceVerticesAreExactAndSkirtsWithinHalfAStep()
    {
        var p = new TerrainParams();
        var mesher = new SurfaceNetsMesher();
        var mesh = new ChunkMesh();

        for (var lod = 0; lod <= TerrainWorld.MaxLod; lod++)
        {
            var surface = 0;
            var skirts = 0;
            var worstSkirt = 0f;
            var worstNormal = 0f;

            for (var y = -2; y <= 2; y++)
            for (var z = -2; z <= 2; z++)
            for (var x = -2; x <= 2; x++)
            {
                var chunk = new VoxelChunk { Coord = new ChunkCoord(x, y, z, lod) };
                TerrainGenerator.Generate(chunk, p);
                mesher.Mesh(chunk, mesh);

                var scale = chunk.Coord.VoxelScale;
                var surfaceCount = mesh.VertexCount - mesher.LastSkirtTriangles;

                for (var i = 0; i < mesh.VertexCount; i++)
                {
                    ref readonly var v = ref mesh.Vertices[i];
                    var packed = mesh.Packed[i];
                    var decoded = packed.DecodePosition(scale);
                    Assert.Equal(v.Material, packed.Material);
                    worstNormal = MathF.Max(worstNormal, AngleDegrees(packed.DecodeNormal(), v.Normal));

                    if (i < surfaceCount)
                    {
                        Assert.Equal(v.Position, decoded);
                        surface++;
                    }
                    else
                    {
                        worstSkirt = MathF.Max(worstSkirt, MaxAxisError(decoded, v.Position) / scale);
                        skirts++;
                    }
                }
            }

            output.WriteLine($"LOD {lod}: {surface} surface vertices exact, {skirts} skirt vertices worst {worstSkirt * 1000f:F4} mvoxel, normals worst {worstNormal:F5} deg");
            Assert.True(surface > 0 && skirts > 0, $"LOD {lod}: no terrain meshed");
            Assert.True(worstSkirt <= BoundVoxels, $"LOD {lod}: {worstSkirt} voxels");
            Assert.True(worstNormal < MaxNormalErrorDegrees);
        }
    }

    [Fact]
    public void NormalsRoundTripWithinAHundredthOfADegree()
    {
        var random = new Random(2);
        var worst = 0f;
        Span<Vector3> axes = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

        foreach (var axis in axes)
            worst = MathF.Max(worst, RoundTripAngle(axis));

        for (var i = 0; i < 1_000_000; i++)
        {
            var n = new Vector3(Uniform(random, -1f, 1f), Uniform(random, -1f, 1f), Uniform(random, -1f, 1f));

            if (n.LengthSquared() < 1e-6f)
                continue;

            worst = MathF.Max(worst, RoundTripAngle(Vector3.Normalize(n)));
        }

        output.WriteLine($"worst normal error {worst:F5} deg");
        Assert.True(worst < MaxNormalErrorDegrees, $"{worst} deg");
    }

    private static float RoundTripAngle(Vector3 n) =>
        AngleDegrees(PackedTerrainVertex.Encode(new TerrainVertex { Normal = n }, 1f).DecodeNormal(), n);

    private static float AngleDegrees(Vector3 a, Vector3 b) =>
        MathF.Atan2(Vector3.Cross(a, b).Length(), Vector3.Dot(a, b)) * (180f / MathF.PI);

    private static float MaxAxisError(Vector3 a, Vector3 b)
    {
        var d = Vector3.Abs(a - b);
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
    }

    private static float Uniform(Random random, float lo, float hi) => lo + (float)random.NextDouble() * (hi - lo);
}
