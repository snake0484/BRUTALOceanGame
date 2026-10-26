using System.Numerics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// Layer B seabed from the analytic terrain surface plus a snapshot of the brush edits (as spheres cut into or added
/// onto the column top). Unlike the streamed bathymetry it is the same at every LOD, so flying around never changes the
/// coastal maps. Each 4 m cell averages 2×2 samples.
/// </summary>
public sealed class TerrainCoastalDepth : ICoastalDepth
{
    private const float Cell = CoastalField.CellSize;

    private BrushOp[] _ops = new BrushOp[64];
    private int _opCount;
    private TerrainParams _params;

    public int OpCount => _opCount;

    /// <summary>Main thread, before a build starts; the jobs read this snapshot.</summary>
    public void Snapshot(in TerrainParams p, IReadOnlyList<BrushOp> ops)
    {
        _params = p;
        _opCount = ops.Count;

        if (_ops.Length < _opCount)
            Array.Resize(ref _ops, Math.Max(_opCount, _ops.Length * 2));

        for (var i = 0; i < _opCount; i++)
            _ops[i] = ops[i];
    }

    public void Fill(Span<float> depth, Vector2 origin, int z0, int z1)
    {
        const int n = CoastalField.Resolution;

        for (var z = z0; z < z1; z++)
        {
            for (var x = 0; x < n; x++)
            {
                var corner = origin + new Vector2(x * Cell, z * Cell);
                var sum = 0f;

                for (var k = 0; k < 4; k++)
                    sum += Top(corner.X + ((k & 1) + 0.5f) * Cell * 0.5f, corner.Y + ((k >> 1) + 0.5f) * Cell * 0.5f);

                depth[x + z * n] = _params.SeaLevel - 0.25f * sum;
            }
        }
    }

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
