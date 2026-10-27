using System.Numerics;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// Heightfield surface for one block (docs/voxel-water.md "Water surface rendering v1"): one quad per wet,
/// non-reservoir interior column, corners averaged over the wet columns around them (face-halo columns included,
/// so neighbouring blocks agree at seams). Positions are chunk-local metres. One instance per thread; no allocation.
/// <para>
/// Each column also carries how far the wind reaches it (docs/voxel-water.md "Wind on voxel water"): 0 under a roof,
/// and per direction the wind blows toward, how far out of the lee of the walls upwind it stands and how much open water
/// lies upwind of it. The shader scales its wind ripples by it, so a sheltered pool is glass and an open lake has chop.
/// </para>
/// </summary>
public sealed class WaterSurfaceMesher
{
    public const float WetThreshold = 0.02f;

    /// <summary>The eight directions the wind can blow toward, 45° apart from +X: (dx, dz) steps; the odd ones are diagonals.</summary>
    public static readonly (int X, int Z)[] Directions = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];

    /// <summary>Cells walked upwind of a column to find the walls and the shore; past that the wind is taken as open.</summary>
    public const int ShelterReach = 16;

    /// <summary>A wall shelters this many times its height (m over the water) downwind, the wind coming back in over that run.</summary>
    public const float ShelterPerHeight = 6f;

    /// <summary>Wall rows counted over the water's surface, so a cliff shelters further than a bank.</summary>
    public const int MaxWallHeight = 6;

    /// <summary>Open water upwind (m) over which the wind raises its full ripples; less and they are smaller.</summary>
    public const float FullFetch = 6f;

    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int K = S + 1;
    private const byte SolidBit = (byte)WaterFlags.Solid;

    private readonly float[] _columnHeight = new float[P * P];
    private readonly float[] _columnThickness = new float[P * P];
    private readonly bool[] _columnWet = new bool[P * P];
    private readonly byte[] _columnTop = new byte[P * P];
    private readonly bool[] _columnCovered = new bool[P * P];
    private readonly byte[] _columnShelter = new byte[P * P * 8];
    private readonly float[] _cornerHeight = new float[K * K];
    private readonly float[] _cornerThickness = new float[K * K];
    private readonly bool[] _cornerValid = new bool[K * K];
    private readonly int[] _cornerVertex = new int[K * K];
    private readonly uint[] _cornerShelter = new uint[K * K * 2];

    public void Build(WaterBlock block, WaterMesh mesh)
    {
        mesh.Clear();
        BuildColumns(block);
        BuildShelter(block);
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

        // Under a roof (any solid over the surface within the block; the halo row above is solid wherever no block is
        // loaded there, which is the open sky over a lake, so it doesn't count) the wind never reaches the water.
        var covered = false;

        for (var y = top + 1; y < S; y++)
        {
            if ((flags[ChunkConstants.Index(x, y, z)] & SolidBit) == 0)
                continue;

            covered = true;
            break;
        }

        var c = Column(x, z);
        _columnWet[c] = true;
        _columnHeight[c] = surface;
        _columnTop[c] = (byte)top;
        _columnCovered[c] = covered;
        var bed = block.Bed[ChunkConstants.Index(x, bottom, z)];
        _columnThickness[c] = surface - (bottom - 0.5f + (bed == WaterBlock.NotBed ? 0f : bed * WaterBlock.BedUnit));
    }

    /// <summary>
    /// Per wet column and wind direction: a walk upwind over the air just above the surface. A wall there (solid at the
    /// surface's level) shelters the column by how near and how high it is; the first dry column ends the fetch. The walk
    /// stops at the padded block's edge, the wind taken as open beyond; the side halos count (a missing neighbour's is
    /// solid, a wall), the row over the block never does (it is solid over every lake block with sky above it). Halo
    /// columns get it too, clipped sooner, so the corners at a seam average nearly the same from both sides.
    /// </summary>
    private void BuildShelter(WaterBlock block)
    {
        var flags = block.Flags;
        var water = block.Water;

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                var c = Column(x, z);

                if (!_columnWet[c])
                    continue;

                var shelter = _columnShelter.AsSpan(c * 8, 8);

                if (_columnCovered[c])
                {
                    shelter.Clear();
                    continue;
                }

                var top = _columnTop[c];
                var air = top + 1;

                for (var k = 0; k < 8; k++)
                {
                    var (dx, dz) = Directions[k];
                    var step = (dx & dz) != 0 ? 1.4142135f : 1f;
                    var exposure = 1f;
                    var shore = ShelterReach * step;

                    for (var j = 1; j <= ShelterReach; j++)
                    {
                        var cx = x - j * dx;
                        var cz = z - j * dz;

                        if (cx < -1 || cx > S || cz < -1 || cz > S || ((cx < 0 || cx == S) && (cz < 0 || cz == S)))
                            break;

                        var distance = j * step;
                        var over = ChunkConstants.Index(cx, air, cz);

                        if (air < S && (flags[over] & SolidBit) != 0)
                        {
                            var height = 1;

                            for (var y = air + 1; y < air + MaxWallHeight && y < S; y++)
                                height += flags[over + (y - air) * P] & SolidBit;

                            exposure = MathF.Min(exposure, distance / (ShelterPerHeight * height));
                            shore = MathF.Min(shore, distance);

                            if (exposure <= 0f)
                                break;

                            continue;
                        }

                        var at = ChunkConstants.Index(cx, top, cz);

                        if ((flags[at] & SolidBit) != 0 || water[at] <= WetThreshold)
                            shore = MathF.Min(shore, distance);
                    }

                    exposure *= MathF.Min(shore / FullFetch, 1f);
                    shelter[k] = (byte)(exposure * 255f + 0.5f);
                }
            }
        }
    }

    private void BuildCorners()
    {
        Span<int> shelter = stackalloc int[8];

        for (var cz = 0; cz < K; cz++)
        {
            for (var cx = 0; cx < K; cx++)
            {
                var height = 0f;
                var thickness = 0f;
                var count = 0;
                shelter.Clear();

                for (var dz = -1; dz <= 0; dz++)
                {
                    for (var dx = -1; dx <= 0; dx++)
                    {
                        var c = Column(cx + dx, cz + dz);

                        if (!_columnWet[c])
                            continue;

                        height += _columnHeight[c];
                        thickness += _columnThickness[c];
                        count++;

                        for (var k = 0; k < 8; k++)
                            shelter[k] += _columnShelter[c * 8 + k];
                    }
                }

                var k0 = cx + cz * K;
                _cornerValid[k0] = count > 0;
                _cornerHeight[k0] = count > 0 ? height / count : 0f;
                _cornerThickness[k0] = count > 0 ? thickness / count : 0f;

                if (count == 0)
                    continue;

                uint low = 0;
                uint high = 0;

                for (var k = 0; k < 4; k++)
                {
                    low |= (uint)((shelter[k] + count / 2) / count) << (8 * k);
                    high |= (uint)((shelter[k + 4] + count / 2) / count) << (8 * k);
                }

                _cornerShelter[k0 * 2] = low;
                _cornerShelter[k0 * 2 + 1] = high;
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
            Shelter0 = _cornerShelter[k * 2],
            Shelter1 = _cornerShelter[k * 2 + 1],
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
