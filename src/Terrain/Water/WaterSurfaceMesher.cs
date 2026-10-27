using System.Numerics;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// Heightfield surface for one block (docs/voxel-water.md "Water surface rendering v1"): one quad per wet,
/// non-reservoir interior column, corners averaged over the wet columns around them (face-halo columns included,
/// so neighbouring blocks agree at seams). Positions are chunk-local metres. One instance per thread; no allocation.
/// </summary>
public sealed class WaterSurfaceMesher
{
    public const float WetThreshold = 0.02f;

    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int A = ChunkConstants.PaddedArea;
    private const int K = S + 1;
    private const byte SolidBit = (byte)WaterFlags.Solid;

    private readonly float[] _columnHeight = new float[P * P];
    private readonly float[] _columnThickness = new float[P * P];
    private readonly Vector2[] _columnFlow = new Vector2[P * P];
    private readonly float[] _columnFoam = new float[P * P];
    private readonly bool[] _columnWet = new bool[P * P];
    private readonly float[] _cornerHeight = new float[K * K];
    private readonly float[] _cornerThickness = new float[K * K];
    private readonly Vector2[] _cornerFlow = new Vector2[K * K];
    private readonly float[] _cornerFoam = new float[K * K];
    private readonly bool[] _cornerValid = new bool[K * K];
    private readonly int[] _cornerVertex = new int[K * K];

    public void Build(WaterBlock block, WaterMesh mesh)
    {
        mesh.Clear();
        BuildColumns(block);
        BuildCorners();
        _cornerVertex.AsSpan().Fill(-1);

        var vertices = mesh.Vertices;
        var indices = mesh.Indices;
        var vertexCount = 0;
        var indexCount = 0;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                if (!_columnWet[Column(x, z)])
                    continue;

                var v00 = Vertex(x, z, vertices, ref vertexCount);
                var v10 = Vertex(x + 1, z, vertices, ref vertexCount);
                var v01 = Vertex(x, z + 1, vertices, ref vertexCount);
                var v11 = Vertex(x + 1, z + 1, vertices, ref vertexCount);

                indices[indexCount++] = v00;
                indices[indexCount++] = v01;
                indices[indexCount++] = v11;
                indices[indexCount++] = v00;
                indices[indexCount++] = v11;
                indices[indexCount++] = v10;
            }
        }

        mesh.VertexCount = vertexCount;
        mesh.IndexCount = indexCount;
    }

    private static int Column(int x, int z) => (x + 1) + (z + 1) * P;

    private void BuildColumns(WaterBlock block)
    {
        _columnWet.AsSpan().Clear();

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                var haloX = x < 0 || x == S;
                var haloZ = z < 0 || z == S;

                if (haloX && haloZ)
                    continue;

                BuildColumn(block, x, z);
            }
        }
    }

    private void BuildColumn(WaterBlock block, int x, int z)
    {
        var water = block.Water;
        var flags = block.Flags;
        var top = -1;

        for (var y = S - 1; y >= 0; y--)
        {
            var i = ChunkConstants.Index(x, y, z);

            if (water[i] <= WetThreshold)
                continue;

            var above = i + P;

            if ((flags[above] & SolidBit) != 0 || water[above] < WetThreshold)
            {
                top = y;
                break;
            }
        }

        if (top < 0)
            return;

        var topIndex = ChunkConstants.Index(x, top, z);

        // Open sea and simulated sea are drawn by the ocean renderer; every other body of water is drawn here.
        if ((flags[topIndex] & (byte)WaterFlags.SeaZone) != 0 || ((flags[topIndex] & (byte)WaterFlags.Reservoir) != 0 && block.Body[topIndex] == WaterBodies.Sea))
            return;

        // A film stands on the terrain's smooth surface, not on the voxel floor: drawn on the floor it sank under the
        // terrain wherever the slope rose above the floor, and a sheet running down a hill showed dry holes.
        var surface = top - 0.5f + MathF.Min(water[topIndex], 1f) + WaterBlock.BedRise(block.Bed[topIndex], water[topIndex]);
        var bottom = top;

        while (bottom > 0)
        {
            var below = ChunkConstants.Index(x, bottom - 1, z);

            if ((flags[below] & SolidBit) != 0 || water[below] <= WetThreshold)
                break;

            bottom--;
        }

        var c = Column(x, z);
        _columnWet[c] = true;
        _columnHeight[c] = surface;
        (_columnFlow[c], _columnFoam[c]) = FlowAndFoam(block, x, z, topIndex, water[topIndex]);
        var bed = block.Bed[ChunkConstants.Index(x, bottom, z)];
        _columnThickness[c] = surface - (bottom - 0.5f + (bed == WaterBlock.NotBed ? 0f : bed * WaterBlock.BedUnit));
    }

    /// <summary>
    /// The pipe model's surface flow at a column's top cell (its four side faces' fluxes over the water they move, m/s)
    /// and how churned the water is there: water landing on it from above (the cell over it falling), and fast flow over
    /// shallow water (its Froude number past 0.8). The automaton has neither.
    /// </summary>
    private static (Vector2 Flow, float Foam) FlowAndFoam(WaterBlock block, int x, int z, int i, float w)
    {
        if (block.FluxX is null)
            return (Vector2.Zero, 0f);

        // A halo column has only the face it shares with the interior; an interior column the two on each axis.
        var fx = block.FluxX;
        var fz = block.FluxZ!;
        var u = x < 0 ? fx[i] : x >= S ? fx[i - 1] : 0.5f * (fx[i - 1] + fx[i]);
        var v = z < 0 ? fz[i] : z >= S ? fz[i - A] : 0.5f * (fz[i - A] + fz[i]);
        var body = MathF.Max(MathF.Min(w, 1f), 0.1f);
        var flow = new Vector2(u, v) / body;
        var speed = flow.Length();
        var fall = -block.FallSpeed![i + P] * MathF.Min(block.Water[i + P], 1f);
        // Pours froth where they land; fast shallow flow froths past Froude 1.2, a thin sheet at its critical speed only a little.
        var froude = speed / MathF.Sqrt(9.81f * MathF.Max(MathF.Min(w, 1f), 0.05f));
        var foam = MathF.Max(MathF.Min(fall / 6f, 1f), MathF.Min(MathF.Max(froude - 1.2f, 0f) * 0.5f, 0.6f));
        return (flow, foam);
    }

    private void BuildCorners()
    {
        for (var cz = 0; cz < K; cz++)
        {
            for (var cx = 0; cx < K; cx++)
            {
                var height = 0f;
                var thickness = 0f;
                var flow = Vector2.Zero;
                var foam = 0f;
                var count = 0;

                for (var dz = -1; dz <= 0; dz++)
                {
                    for (var dx = -1; dx <= 0; dx++)
                    {
                        var c = Column(cx + dx, cz + dz);

                        if (!_columnWet[c])
                            continue;

                        height += _columnHeight[c];
                        thickness += _columnThickness[c];
                        flow += _columnFlow[c];
                        foam += _columnFoam[c];
                        count++;
                    }
                }

                var k = cx + cz * K;
                _cornerValid[k] = count > 0;
                _cornerHeight[k] = count > 0 ? height / count : 0f;
                _cornerThickness[k] = count > 0 ? thickness / count : 0f;
                _cornerFlow[k] = count > 0 ? flow / count : Vector2.Zero;
                _cornerFoam[k] = count > 0 ? foam / count : 0f;
            }
        }
    }

    private uint Vertex(int cx, int cz, WaterVertex[] vertices, ref int vertexCount)
    {
        var k = cx + cz * K;
        var existing = _cornerVertex[k];

        if (existing >= 0)
            return (uint)existing;

        var h = _cornerHeight[k];
        var dhdx = Slope(cx, cz, 1, 0, h);
        var dhdz = Slope(cx, cz, 0, 1, h);

        var index = vertexCount++;
        vertices[index] = new WaterVertex
        {
            Position = new Vector3(cx - 0.5f, h, cz - 0.5f) * ChunkConstants.VoxelSize,
            Normal = Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz)),
            Thickness = _cornerThickness[k],
            Flow = _cornerFlow[k],
            Foam = _cornerFoam[k],
        };
        _cornerVertex[k] = index;
        return (uint)index;
    }

    /// <summary>Corner-height gradient along one axis: central where both neighbours are valid, else one-sided, else flat.</summary>
    private float Slope(int cx, int cz, int dx, int dz, float h)
    {
        var hasNext = TryCorner(cx + dx, cz + dz, out var next);
        var hasPrev = TryCorner(cx - dx, cz - dz, out var prev);

        if (hasNext && hasPrev)
            return (next - prev) * 0.5f;

        if (hasNext)
            return next - h;

        return hasPrev ? h - prev : 0f;
    }

    private bool TryCorner(int cx, int cz, out float height)
    {
        if (cx < 0 || cz < 0 || cx >= K || cz >= K || !_cornerValid[cx + cz * K])
        {
            height = 0f;
            return false;
        }

        height = _cornerHeight[cx + cz * K];
        return true;
    }
}
