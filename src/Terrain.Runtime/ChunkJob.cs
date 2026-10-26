using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Physics;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Pooled generate + replay edits + mesh + collision shape + column summary job, at any LOD.
/// Owns all its scratch so it never allocates after warm-up.
/// </summary>
internal sealed class ChunkJob : IJob
{
    public const int Columns = ChunkConstants.Size * ChunkConstants.Size;

    public readonly VoxelChunk Chunk = new();
    public readonly ChunkMesh Mesh = new();

    /// <summary>Per voxel column: world y of the topmost solid surface in this chunk, NaN if all air.</summary>
    public readonly float[] ColumnTops = new float[Columns];

    /// <summary>
    /// Per voxel column: world y of the topmost solid surface at or below the voxel just below sea level, NaN if none
    /// (or the chunk lies wholly above it). Land overhead (an overhang, an arch, a placed rock) is not the seabed.
    /// </summary>
    public readonly float[] ColumnBeds = new float[Columns];

    /// <summary>Per voxel column: water in the voxel just below sea level. Only valid when <see cref="HasSeaLayer"/>.</summary>
    public readonly bool[] SeaFlags = new bool[Columns];
    public bool HasSeaLayer;

    private readonly SurfaceNetsMesher _mesher = new();
    private BrushOp[] _ops = new BrushOp[16];
    private int _opCount;
    private Vector3[] _positions = new Vector3[4096];
    public TerrainParams Params;
    public bool BuildCollision;

    public bool HadEdits => _opCount > 0;

    /// <summary>Built on the worker; ownership passes to the physics world on the main thread.</summary>
    public StaticMeshShape? Shape;

    /// <summary>Copies the ops that touch this chunk so the worker never reads shared lists.</summary>
    public void SetOps(List<BrushOp> ops)
    {
        _opCount = 0;

        foreach (var op in ops)
        {
            if (!VoxelEditor.Intersects(op, Chunk.Coord))
                continue;

            if (_opCount == _ops.Length)
                Array.Resize(ref _ops, _ops.Length * 2);

            _ops[_opCount++] = op;
        }
    }

    public void Run()
    {
        TerrainGenerator.Generate(Chunk, Params);
        VoxelEditor.Apply(Chunk, _ops.AsSpan(0, _opCount));
        Mesh.Clear();

        if (Chunk.HasSurface)
            _mesher.Mesh(Chunk, Mesh);

        BuildShape();
        SummariseColumns();
    }

    private void BuildShape()
    {
        Shape = null;
        if (!BuildCollision || Mesh.IndexCount == 0)
            return;

        if (_positions.Length < Mesh.VertexCount)
            _positions = new Vector3[Math.Max(Mesh.VertexCount, _positions.Length * 2)];

        for (var i = 0; i < Mesh.VertexCount; i++)
            _positions[i] = Mesh.Vertices[i].Position;

        // Skirts are appended after the base surface; collision only needs the surface.
        var surfaceIndices = Mesh.IndexCount - _mesher.LastSkirtTriangles * 3;
        Shape = StaticMeshShape.Create(_positions.AsSpan(0, Mesh.VertexCount), Mesh.Indices.AsSpan(0, surfaceIndices));
    }

    /// <summary>
    /// Column tops for bathymetry, beds for the sea and the sea flag from the voxel layer just below sea level, per
    /// voxel column.
    /// </summary>
    private void SummariseColumns()
    {
        const int size = ChunkConstants.Size;
        var scale = Chunk.Coord.VoxelScale;
        var originY = Chunk.Coord.WorldOrigin.Y;
        var seaY = (int)MathF.Floor((Params.SeaLevel - originY) / scale - 1e-4f);
        HasSeaLayer = seaY >= 0 && seaY < size;

        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var column = x + z * size;
                var top = SurfaceFrom(x, z, size - 1);
                ColumnTops[column] = top;

                // Wholly above the sea layer there is no bed; with the sea layer at the top (or above the chunk) the bed
                // is the top, so only a chunk with sea level part way up scans twice.
                ColumnBeds[column] = seaY < 0 ? float.NaN : seaY >= size - 1 ? top : SurfaceFrom(x, z, seaY);

                if (HasSeaLayer)
                    SeaFlags[column] = Chunk.Water[ChunkConstants.Index(x, seaY, z)] > 127;
            }
        }
    }

    /// <summary>World y of the topmost solid surface in column (x, z) at voxel <paramref name="fromY"/> or below, NaN if none.</summary>
    private float SurfaceFrom(int x, int z, int fromY)
    {
        var density = Chunk.Density;

        for (var y = fromY; y >= 0; y--)
        {
            float below = density[ChunkConstants.Index(x, y, z)];
            if (below >= 0f)
                continue;

            float above = density[ChunkConstants.Index(x, y + 1, z)];
            var fraction = above > 0f ? below / (below - above) : 1f;
            return Chunk.Coord.WorldOrigin.Y + (y + fraction) * Chunk.Coord.VoxelScale;
        }

        return float.NaN;
    }
}
