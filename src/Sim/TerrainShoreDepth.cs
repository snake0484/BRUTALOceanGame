using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>
/// Water depth from the analytic terrain surface plus the sim's brush edits (spheres cut into or added onto the column
/// top), for the surface's depth cap. Unlike the streamed bathymetry it is the same at every LOD and needs no camera, so
/// the CPU never reads the camera window (docs/pivot/review.md C7).
/// <para>
/// Pits: ground dug below sea level in a column that was land as generated is dry (<see cref="Pit"/>), the GPU's rule
/// (ShoreDepthMap). Exact because shores and seabeds are unminable (<see cref="ShoreGuard"/>): every such pit is cut off
/// from the sea by undug land. Like the GPU's sea-level voxel, a pit within a voxel of sea level still reads its depth.
/// </para>
/// </summary>
internal sealed class TerrainShoreDepth : IShoreDepth
{
    /// <summary>Depth of a dry pit, ShoreDepthMap.Pit's value.</summary>
    public const float Pit = -1000f;

    private readonly TerrainParams _params;
    private BrushOp[] _ops = new BrushOp[64];
    private int _opCount;

    public TerrainShoreDepth(in TerrainParams terrain) => _params = terrain;

    public ReadOnlySpan<BrushOp> Ops => _ops.AsSpan(0, _opCount);

    /// <summary>Main thread, in the tick; samples between ticks see every op added so far.</summary>
    public void Add(in BrushOp op)
    {
        if (_opCount == _ops.Length)
            Array.Resize(ref _ops, _ops.Length * 2);

        _ops[_opCount++] = op;
    }

    public float Depth(float x, float z)
    {
        var generated = TerrainGenerator.SurfaceHeight(x, z, _params);
        var top = Top(x, z, generated);

        if (generated >= _params.SeaLevel && top < _params.SeaLevel - ChunkConstants.VoxelSize)
            return Pit;

        return _params.SeaLevel - top;
    }

    private float Top(float x, float z, float top)
    {
        for (var i = 0; i < _opCount; i++)
        {
            ref readonly var op = ref _ops[i];
            var dx = x - op.Centre.X;
            var dz = z - op.Centre.Z;
            var d2 = dx * dx + dz * dz;
            if (d2 >= op.Radius * op.Radius)
                continue;

            var h = MathF.Sqrt(op.Radius * op.Radius - d2);
            var low = op.Centre.Y - h;
            var high = op.Centre.Y + h;

            if (op.Mode == BrushMode.Dig && high >= top && low < top)
                top = low;
            else if (op.Mode == BrushMode.Place && low <= top && high > top)
                top = high;
        }

        return top;
    }
}
