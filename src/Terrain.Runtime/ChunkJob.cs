using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Physics;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Pooled generate + replay edits + mesh (+ collision shape) job for one planet leaf at any LOD.
/// Owns all its scratch so it never allocates after warm-up; the collision shape is a pooled one handed in.
/// </summary>
internal sealed class ChunkJob : IJob
{
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
            _mesher.Mesh(Chunk, Mesh);

        BuildShape();
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
