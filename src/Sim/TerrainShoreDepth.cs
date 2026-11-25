using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>
/// Water depth from the analytic terrain surface plus a snapshot of the brush edits (spheres cut into or added onto the
/// column top), for the surface's depth cap. Unlike the streamed bathymetry it is the same at every LOD and needs no
/// camera, so the CPU never reads the camera window (docs/pivot/review.md C7).
/// </summary>
internal sealed class TerrainShoreDepth : IShoreDepth
{
    private BrushOp[] _ops = new BrushOp[64];
    private int _opCount;
    private TerrainParams _params;

    /// <summary>Main thread, between ticks; samples read this snapshot.</summary>
    public void Snapshot(in TerrainParams p, IReadOnlyList<BrushOp> ops)
    {
        _params = p;
        _opCount = ops.Count;

        if (_ops.Length < _opCount)
            Array.Resize(ref _ops, Math.Max(_opCount, _ops.Length * 2));

        for (var i = 0; i < _opCount; i++)
            _ops[i] = ops[i];
    }

    public float Depth(float x, float z) => _params.SeaLevel - Top(x, z);

    private float Top(float x, float z)
    {
        var top = TerrainGenerator.SurfaceHeight(x, z, _params);

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
