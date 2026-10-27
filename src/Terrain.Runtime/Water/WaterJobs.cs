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

    /// <summary>A new block straight from the generator, no edit, sea zone or body disc in it: its water is at rest (held).</summary>
    public bool AtRest => !Refresh && _opCount == 0 && _discCount == 0 && _bodyDiscCount == 0;
    private Vector3[] _discs = new Vector3[8];
    private int _discCount;
    private Vector4[] _bodyDiscs = new Vector4[8];
    private int _bodyDiscCount;
    private WaterBodyInfo[] _generated = new WaterBodyInfo[4];
    private int _generatedCount;
    public TerrainParams Params;
    public WaterBodies? Bodies;

    /// <param name="zones">Sea zone discs (world x, world z, radius); those touching the block are copied.</param>
    /// <param name="bodyDiscs">Discs around digs into bodies (world x, world z, radius, body id), where the body's water is free; those touching the block are copied.</param>
    /// <param name="generated">The generator's bodies of water; those whose fill disc touches the block are copied.</param>
    public void Prepare(WaterBlock block, bool refresh, in TerrainParams p, IReadOnlyList<BrushOp> ops, List<Vector3> zones, List<Vector4> bodyDiscs, WaterBodyInfo[] generated, WaterBodies bodies)
    {
        Bodies = bodies;
        Block = block;
        Refresh = refresh;
        _discCount = 0;
        _bodyDiscCount = 0;
        _generatedCount = 0;
        var origin = block.Coord.WorldOrigin;

        foreach (var disc in zones)
            if (Touches(origin, disc.X, disc.Y, disc.Z))
                Append(ref _discs, ref _discCount, disc);

        foreach (var disc in bodyDiscs)
            if (Touches(origin, disc.X, disc.Y, disc.Z))
                Append(ref _bodyDiscs, ref _bodyDiscCount, disc);

        foreach (var info in generated)
            if (Touches(origin, info.Centre.X, info.Centre.Y, info.Radius))
                Append(ref _generated, ref _generatedCount, info);

        Params = p;
        _chunk.Coord = block.Coord;
        _opCount = 0;

        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            if (!VoxelEditor.Intersects(op, block.Coord))
                continue;

            Append(ref _ops, ref _opCount, op);
        }
    }

    /// <summary>The disc (world x, z, radius) meets the chunk's footprint.</summary>
    private static bool Touches(Vector3 origin, float x, float z, float radius)
    {
        var nx = Math.Clamp(x, origin.X, origin.X + ChunkConstants.Size);
        var nz = Math.Clamp(z, origin.Z, origin.Z + ChunkConstants.Size);
        return (nx - x) * (nx - x) + (nz - z) * (nz - z) <= radius * radius;
    }

    private static void Append<T>(ref T[] array, ref int count, T item)
    {
        if (count == array.Length)
            Array.Resize(ref array, array.Length * 2);

        array[count++] = item;
    }

    public void Run()
    {
        TerrainGenerator.Generate(_chunk, Params);
        VoxelEditor.Apply(_chunk, _ops.AsSpan(0, _opCount));
        var discs = _bodyDiscs.AsSpan(0, _bodyDiscCount);

        if (Refresh)
            Block!.RefreshFrom(_chunk, discs);
        else
            Block!.InitFrom(_chunk, Bodies!, _generated.AsSpan(0, _generatedCount), discs);

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
