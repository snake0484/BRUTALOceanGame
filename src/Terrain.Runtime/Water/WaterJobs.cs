using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Runtime.Water;

/// <summary>
/// Regenerates a LOD-0 chunk (generator + edit replay) and initialises or refreshes a water block from it.
/// The block is owned by this job until the main thread collects it.
/// </summary>
internal sealed class WaterActivationJob : IJob
{
    private readonly VoxelChunk _chunk = new();
    private BrushOp[] _ops = new BrushOp[16];
    private int _opCount;
    public WaterBlock? Block;
    public bool Refresh;
    private Vector3[] _discs = new Vector3[8];
    private int _discCount;
    public TerrainParams Params;

    /// <param name="zones">Sea zone discs (world x, world z, radius); those touching the block are copied.</param>
    public void Prepare(WaterBlock block, bool refresh, in TerrainParams p, IReadOnlyList<BrushOp> ops, List<Vector3> zones)
    {
        Block = block;
        Refresh = refresh;
        _discCount = 0;

        var origin = block.Coord.WorldOrigin;
        foreach (var disc in zones)
        {
            var nx = Math.Clamp(disc.X, origin.X, origin.X + ChunkConstants.Size);
            var nz = Math.Clamp(disc.Y, origin.Z, origin.Z + ChunkConstants.Size);
            if ((nx - disc.X) * (nx - disc.X) + (nz - disc.Y) * (nz - disc.Y) > disc.Z * disc.Z)
                continue;

            if (_discCount == _discs.Length)
                Array.Resize(ref _discs, _discs.Length * 2);

            _discs[_discCount++] = disc;
        }

        Params = p;
        _chunk.Coord = block.Coord;
        _opCount = 0;

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (!VoxelEditor.Intersects(op, block.Coord))
                continue;

            if (_opCount == _ops.Length)
                Array.Resize(ref _ops, _ops.Length * 2);

            _ops[_opCount++] = op;
        }
    }

    public void Run()
    {
        TerrainGenerator.Generate(_chunk, Params);
        VoxelEditor.Apply(_chunk, _ops.AsSpan(0, _opCount));

        if (Refresh)
            Block!.RefreshFrom(_chunk);
        else
            Block!.InitFrom(_chunk, Params.SeaLevel);

        if (_discCount > 0)
            Block.Unclamp(_discs.AsSpan(0, _discCount));
    }
}

/// <summary>One pass of the two-pass flow step (automaton or pipe model), or a surface remesh, for one block.</summary>
internal sealed class WaterStepJob : IJob
{
    public enum Kind
    {
        OutflowScale,
        ApplyFlows,
        PipeFluxes,
        PipeApply,
        Mesh,
    }

    private readonly WaterSurfaceMesher _mesher = new();
    public WaterBlock? Block;
    public WaterMesh? Mesh;
    public Kind Pass;

    /// <summary>Step length (s) for the pipe model's passes.</summary>
    public float Dt;

    public void Run()
    {
        switch (Pass)
        {
            case Kind.OutflowScale:
                WaterFlow.ComputeOutflowScale(Block!);
                break;
            case Kind.ApplyFlows:
                WaterFlow.ApplyFlows(Block!);
                break;
            case Kind.PipeFluxes:
                WaterPipes.UpdateFluxes(Block!, Dt);
                break;
            case Kind.PipeApply:
                WaterPipes.ApplyFluxes(Block!, Dt);
                break;
            case Kind.Mesh:
                _mesher.Build(Block!, Mesh!);
                break;
        }
    }
}
