using System.Numerics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using BrutalGame.Physics;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Pooled generate + replay edits + mesh (+ collision shape) job for one planet leaf at any LOD.
/// Owns all its scratch so it never allocates after warm-up; the collision shape is a pooled one handed in.
/// </summary>
internal sealed class ChunkJob : IJob
{
    /// <summary>Land skirts stay this share of a voxel above the sea sphere.</summary>
    private const double SkirtSeaClearance = 0.05;

    public readonly VoxelChunk Chunk = new();
    public readonly ChunkMesh Mesh = new();
    public readonly PlanetChunkScratch Scratch = new();

    private readonly SurfaceNetsMesher _mesher = new();
    private EditOp[] _ops = new EditOp[16];
    private int _opCount;
    private Vector3[] _positions = new Vector3[4096];

    public PlanetGenerator Generator = null!;

    /// <summary>A pooled shape to build collision into (LOD-0 leaves in the collision ring), else null.</summary>
    public StaticMeshShape? Shape;

    /// <summary>Planet axes to the physics bubble's: collision vertices are built in bubble axes.</summary>
    public Quaternion BubbleRotation = Quaternion.Identity;

    public bool HadEdits => _opCount > 0;

    /// <summary>Main thread, before queueing: copies the ops that show on this leaf so the worker never reads the store.</summary>
    public void GatherOps(EditStore edits, double distance) => _opCount = edits.Gather(Chunk.Coord, distance, ref _ops);

    public void Run()
    {
        Generator.Generate(Chunk, Scratch, withMask: _opCount > 0);
        PlanetEditor.Apply(Chunk, _ops.AsSpan(0, _opCount), Scratch);
        Mesh.Clear();

        if (Chunk.HasSurface)
            _mesher.Mesh(Chunk, Mesh, seaColumns: Scratch.SeaColumns);

        LiftLandSkirts();
        BuildShape();
    }

    /// <summary>
    /// Skirt vertices hanging from land (no sea-mask bit) come back up to just above the sea sphere, never above the
    /// surface vertex they hang from. A coarse leaf's flange drops up to a voxel, kilometres at the far LODs, and the
    /// ocean surface, which knows no land past its depth maps, would draw over it all along the seam.
    /// Skirt quads are the mesh's last indices, six each: (b, a, a2, b, a2, b2), a2 and b2 hanging from a and b.
    /// </summary>
    private void LiftLandSkirts()
    {
        // An empty leaf is not meshed: the mesher still holds the last leaf's count.
        var skirtIndices = Mesh.IndexCount == 0 ? 0 : _mesher.LastSkirtTriangles * 3;

        if (skirtIndices == 0 || !Generator.Params.HasSea)
            return;

        var coord = Chunk.Coord;
        var origin = coord.Origin;
        var floor = Generator.Params.SeaRadius + SkirtSeaClearance * coord.VoxelSize;
        var invScale = 1f / coord.VoxelScale;
        var indices = Mesh.Indices;

        for (var q = Mesh.IndexCount - skirtIndices; q < Mesh.IndexCount; q += 6)
        {
            LiftLandSkirt(indices[q + 1], indices[q + 2], origin, floor, invScale);
            LiftLandSkirt(indices[q], indices[q + 5], origin, floor, invScale);
        }
    }

    private void LiftLandSkirt(uint top, uint skirt, double3 origin, double floor, float invScale)
    {
        ref var v = ref Mesh.Vertices[skirt];

        if ((v.Material & PackedTerrainVertex.SeaColumn) != 0)
            return;

        var p = origin + new double3(v.Position.X, v.Position.Y, v.Position.Z);
        var r = p.Length();

        if (r >= floor)
            return;

        var hung = Mesh.Vertices[top].Position;
        var lift = Math.Min(floor, (origin + new double3(hung.X, hung.Y, hung.Z)).Length()) - r;

        if (lift <= 0.0)
            return;

        var up = p * (lift / r);
        v.Position += new Vector3((float)up.X, (float)up.Y, (float)up.Z);
        Mesh.Packed[skirt] = PackedTerrainVertex.Encode(v, invScale);
    }

    private void BuildShape()
    {
        if (Shape is null)
            return;

        if (Mesh.IndexCount == 0)
        {
            Shape.Release();
            return;
        }

        if (_positions.Length < Mesh.VertexCount)
            _positions = new Vector3[Math.Max(Mesh.VertexCount, _positions.Length * 2)];

        for (var i = 0; i < Mesh.VertexCount; i++)
            _positions[i] = Vector3.Transform(Mesh.Vertices[i].Position, BubbleRotation);

        // Skirts are appended after the base surface; collision only needs the surface.
        var surfaceIndices = Mesh.IndexCount - _mesher.LastSkirtTriangles * 3;
        Shape.Build(_positions.AsSpan(0, Mesh.VertexCount), Mesh.Indices.AsSpan(0, surfaceIndices));
    }
}
