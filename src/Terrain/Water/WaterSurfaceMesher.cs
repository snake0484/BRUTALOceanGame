using System.Numerics;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// Heightfield surface for one block (docs/voxel-water.md "Water surface rendering v1"): one quad per wet,
/// non-reservoir interior column, corners averaged over the wet columns around them (face-halo columns included,
/// so neighbouring blocks agree at seams), and a skirt over the dry columns beside them whose ground stands at or over
/// the water (<see cref="Fringe"/>); where it ends against bare ground its edge sinks under its bed
/// (<see cref="WaterlineDip"/>), and where it runs onto open ground its edge is a line across the flow, as far as its water has come
/// (<see cref="FullDepth"/>). Positions are chunk-local metres. One instance per thread; no allocation once its
/// mesh has grown to fit.
/// </summary>
public sealed class WaterSurfaceMesher
{
    public const float WetThreshold = 0.02f;

    /// <summary>
    /// A dry column beside the water is skirted when its ground stands no more than this (m) under the water's surface.
    /// The voxels say only which cells are solid, and the terrain's smooth surface dips under the water inside a solid
    /// column at the shore: a surface that stopped at the last wet column's edge left a strip of dry-looking ground
    /// between the water and the bank. The skirt carries the surface flat over the bank's column and the terrain, drawn
    /// first, hides it wherever the ground is higher, so the waterline is where the surface meets the terrain. Where the
    /// ground falls away (a lip, a film's downhill side) there is no skirt, which would hang over it. Under water thinner
    /// than twice this, the ground must stand within half the water's depth: beside a film on level ground (its own
    /// depth under the surface) a skirt drew the film a column wider, and its front stepped a column at a time.
    /// </summary>
    public const float SkirtTolerance = 0.05f;

    /// <summary>
    /// Where the water ends against open ground with no water on it (a film's edge, a drop), the edge corners sink this far
    /// (m) under the bed, at most <see cref="MaxEdgeDrop"/> under the surface. The terrain then cuts the surface along
    /// where it meets the ground inside the edge column, chamfering the column steps; a surface that stopped flat at the
    /// last wet column's edge stood over the ground in a stair of column edges.
    /// </summary>
    public const float WaterlineDip = 0.05f;
    public const float MaxEdgeDrop = 1f;

    /// <summary>
    /// Fronts (<see cref="BuildReach"/>): the water in a column lies against the face it came in through, its edge a line
    /// across the flow that has reached its depth over this (m), or over the water upstream if deeper, of the way across.
    /// The open columns beside the water carry their own fill (under <see cref="WetThreshold"/>, or none) at their ground,
    /// so the front moves on as they fill; a column drawn whole once its fill passed the threshold made the front jump a
    /// voxel at a time.
    /// </summary>
    public const float FullDepth = 0.05f;

    /// <summary>m/s of flow a depth falling 1 m per metre counts as, in the direction a front faces: water spreading with no flux yet.</summary>
    public const float GradientSpeed = 10f;

    /// <summary>Under this (m/s) of flow and gradient a column's water has no direction and covers it evenly, by its fill.</summary>
    public const float StillSpeed = 0.1f;

    /// <summary>An open column beside the water takes its fill at its ground only where that lies no more than this (m) under the water's bed; under a bigger drop the edge sinks instead.</summary>
    public const float FrontDrop = 0.5f;

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

    /// <summary>A wet column whose surface stands in the halo layer over or under the block: the block there draws it (<see cref="BuildColumn"/>).</summary>
    private readonly bool[] _columnBeyond = new bool[P * P];
    private readonly bool[] _columnSkirt = new bool[P * P];
    private readonly bool[] _columnBare = new bool[P * P];
    private readonly bool[] _columnOpen = new bool[P * P];
    private readonly int[] _columnTop = new int[P * P];
    private readonly float[] _columnFill = new float[P * P];
    private readonly Vector2[] _columnFacing = new Vector2[P * P];
    private readonly Vector2[] _columnHeading = new Vector2[P * P];
    private readonly float[] _columnStill = new float[P * P];
    private readonly float[] _cornerHeight = new float[K * K];
    private readonly float[] _cornerDrop = new float[K * K];
    private readonly float[] _cornerReach = new float[K * K];
    private readonly float[] _cornerThickness = new float[K * K];
    private readonly Vector2[] _cornerFlow = new Vector2[K * K];
    private readonly float[] _cornerFoam = new float[K * K];
    private readonly bool[] _cornerValid = new bool[K * K];
    private readonly int[] _cornerVertex = new int[K * K];

    /// <summary>The vertex where the waterline crosses each corner-to-corner edge: x edges first, then z edges.</summary>
    private readonly int[] _edgeVertex = new int[2 * K * K];

    /// <summary>The waterline's vertex at a corner it runs through (the water reaching it exactly), shared by every edge into it.</summary>
    private readonly int[] _sunkVertex = new int[K * K];

    private readonly uint[] _polygon = new uint[6];

    /// <summary>A column's corners counter-clockwise from above: (x, z), (x, z + 1), (x + 1, z + 1), (x + 1, z).</summary>
    private static readonly int[] LoopX = [0, 0, 1, 1];
    private static readonly int[] LoopZ = [0, 1, 1, 0];

    public void Build(WaterBlock block, WaterMesh mesh)
    {
        mesh.Clear();
        BuildColumns(block);
        BuildReach();
        BuildCorners();
        _cornerVertex.AsSpan().Fill(-1);
        _edgeVertex.AsSpan().Fill(-1);
        _sunkVertex.AsSpan().Fill(-1);

        var vertexCount = 0;
        var indexCount = 0;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var column = Column(x, z);
                if ((!_columnWet[column] && !_columnSkirt[column] && !_columnOpen[column]) || _columnBeyond[column])
                    continue;

                // A clipped quad is at most a hexagon: four corners and four crossings to add, four triangles.
                mesh.EnsureCapacity(vertexCount + 8, indexCount + 12);
                var vertices = mesh.Vertices;
                var indices = mesh.Indices;
                var count = 0;

                for (var e = 0; e < 4; e++)
                {
                    var ax = x + LoopX[e];
                    var az = z + LoopZ[e];
                    var bx = x + LoopX[(e + 1) & 3];
                    var bz = z + LoopZ[(e + 1) & 3];
                    var aInside = _cornerReach[ax + az * K] > 0f;
                    var bInside = _cornerReach[bx + bz * K] > 0f;

                    if (aInside)
                        _polygon[count++] = Vertex(ax, az, vertices, ref vertexCount);

                    if (aInside == bInside)
                        continue;

                    // A waterline through a corner reaches it along both edges: one vertex, no empty triangle.
                    var crossing = aInside ? Crossing(ax, az, bx, bz, vertices, ref vertexCount) : Crossing(bx, bz, ax, az, vertices, ref vertexCount);
                    if (count == 0 || _polygon[count - 1] != crossing)
                        _polygon[count++] = crossing;
                }

                if (count > 1 && _polygon[count - 1] == _polygon[0])
                    count--;

                for (var i = 2; i < count; i++)
                {
                    indices[indexCount++] = _polygon[0];
                    indices[indexCount++] = _polygon[i - 1];
                    indices[indexCount++] = _polygon[i];
                }
            }
        }

        mesh.VertexCount = vertexCount;
        mesh.IndexCount = indexCount;
    }

    private static int Column(int x, int z) => (x + 1) + (z + 1) * P;

    private void BuildColumns(WaterBlock block)
    {
        _columnWet.AsSpan().Clear();
        _columnBeyond.AsSpan().Clear();
        _columnBare.AsSpan().Clear();

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

        _columnSkirt.AsSpan().Clear();
        _columnOpen.AsSpan().Clear();

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                if ((x < 0 || x == S) && (z < 0 || z == S))
                    continue;

                Fringe(block, x, z);
            }
        }
    }

    /// <summary>
    /// A dry column beside wet ones (sides and corners) whose ground stands at or over their surface (<see cref="SkirtTolerance"/>)
    /// is a skirt: it takes their mean surface height and no thickness, flow or foam. A bare one whose ground lies under
    /// their surface, by no more than <see cref="FrontDrop"/> under their bed, is open ground the water can run onto: it
    /// takes its own fill (under <see cref="WetThreshold"/>, or none) on that ground, so the waterline can be drawn
    /// through it (<see cref="FullDepth"/>). Halo columns are judged from the neighbours this block holds, so a seam
    /// corner can differ from the next block's by a neighbour two columns out.
    /// </summary>
    private void Fringe(WaterBlock block, int x, int z)
    {
        var c = Column(x, z);
        if (_columnWet[c])
            return;

        var height = 0f;
        var count = 0;
        var openGround = float.NegativeInfinity;
        var openCell = -1;

        // Diagonal neighbours too, or a convex bend in the shore keeps a notch at its corner column.
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var nx = x + dx;
                var nz = z + dz;
                if ((dx == 0 && dz == 0) || nx < -1 || nz < -1 || nx > S || nz > S)
                    continue;

                var n = Column(nx, nz);
                if (!_columnWet[n] || _columnBeyond[n] || block.Water[ChunkConstants.Index(x, _columnTop[n], z)] > WetThreshold)
                    continue;

                var ground = Ground(block, x, z, _columnTop[n], out var cell);

                if (ground >= _columnHeight[n] - MathF.Min(SkirtTolerance, 0.5f * _columnThickness[n]))
                {
                    height += _columnHeight[n];
                    count++;
                    continue;
                }

                if (ground > openGround && ground >= _columnHeight[n] - _columnThickness[n] - FrontDrop)
                    (openGround, openCell) = (ground, cell);
            }
        }

        if (count > 0)
        {
            _columnSkirt[c] = true;
            _columnBare[c] = false;
            _columnHeight[c] = height / count;
            _columnThickness[c] = 0f;
            _columnFlow[c] = Vector2.Zero;
            _columnFoam[c] = 0f;
            return;
        }

        if (openCell < 0 || !_columnBare[c])
            return;

        // As a wet column's surface (BuildColumn), so nothing jumps when its fill passes the threshold.
        var bed = block.Bed[openCell];
        var fill = Math.Clamp(block.Water[openCell], 0f, 1f);
        var surface = openGround - BedHeight(bed) + fill + WaterBlock.BedRise(bed, fill);
        _columnOpen[c] = true;
        _columnBare[c] = false;
        _columnHeight[c] = surface;
        _columnThickness[c] = surface - openGround;
        _columnFlow[c] = FlowAndFoam(block, x, z, openCell, fill).Flow;
        _columnFoam[c] = 0f;
    }

    /// <summary>
    /// Where each wet or open column's water reaches (<see cref="FullDepth"/>): its facing, the way its front moves (its
    /// flow, plus the way its depth falls at <see cref="GradientSpeed"/>), and its fill, its depth over the deeper of
    /// <see cref="FullDepth"/> and the water it comes from (a neighbour counts as far as the facing points away from it).
    /// Slower than <see cref="StillSpeed"/> it has no facing and fills the column evenly. The facing is smoothed over the
    /// water around (a 3 × 3 tent): columns along one front each facing their own way cut it into short runs and spurs.
    /// All of it is continuous in the water, so the edges drawn from it are too. Halo columns see only the neighbours this
    /// block holds, so an edge across a seam can differ a little from the next block's.
    /// </summary>
    private void BuildReach()
    {
        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                var c = Column(x, z);
                if (!_columnWet[c] && !_columnOpen[c])
                    continue;

                // The surface, not the depth: still water over a bumpy bed has no front.
                var h = _columnHeight[c];
                var slope = 0.5f * new Vector2(Surface(x - 1, z, h) - Surface(x + 1, z, h), Surface(x, z - 1, h) - Surface(x, z + 1, h));
                _columnHeading[c] = _columnFlow[c] + GradientSpeed * slope;
            }
        }

        for (var z = -1; z <= S; z++)
        {
            for (var x = -1; x <= S; x++)
            {
                var c = Column(x, z);
                if (!_columnWet[c] && !_columnOpen[c])
                    continue;

                var v = Vector2.Zero;
                var weight = 0f;

                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (!Held(x + dx, z + dz))
                            continue;

                        var n = Column(x + dx, z + dz);
                        if (!_columnWet[n] && !_columnOpen[n])
                            continue;

                        var w = (2 - Math.Abs(dx)) * (2 - Math.Abs(dz));
                        v += w * _columnHeading[n];
                        weight += w;
                    }
                }

                v /= weight;
                var speed = v.Length();
                var t = Math.Clamp(speed / StillSpeed, 0f, 1f);
                var still = 1f - t * t * (3f - 2f * t);
                var facing = speed > 1e-6f ? v / speed : Vector2.UnitX;

                var d = _columnThickness[c];
                var upstream = d;
                upstream = MathF.Max(upstream, Depth(x - 1, z) * Upstream(facing.X, still));
                upstream = MathF.Max(upstream, Depth(x + 1, z) * Upstream(-facing.X, still));
                upstream = MathF.Max(upstream, Depth(x, z - 1) * Upstream(facing.Y, still));
                upstream = MathF.Max(upstream, Depth(x, z + 1) * Upstream(-facing.Y, still));

                _columnFill[c] = Math.Clamp(d / MathF.Max(upstream, FullDepth), 0f, 1f);
                _columnFacing[c] = facing;
                _columnStill[c] = still;
            }
        }
    }

    /// <summary>How much a neighbour's depth counts as the water a column comes from: as far as the facing points away from it (<paramref name="away"/>), none when still.</summary>
    private static float Upstream(float away, float still) => (1f - still) * Math.Clamp(away, 0f, 1f);

    private static bool Held(int x, int z) => x >= -1 && z >= -1 && x <= S && z <= S && !((x < 0 || x == S) && (z < 0 || z == S));

    /// <summary>A neighbour column's water depth: its wet or open depth, else none.</summary>
    private float Depth(int x, int z)
    {
        if (!Held(x, z))
            return 0f;

        var c = Column(x, z);
        return _columnWet[c] || _columnOpen[c] ? _columnThickness[c] : 0f;
    }

    /// <summary>A neighbour column's surface: its wet or open surface, <paramref name="unknown"/> for a bank, a drop or past the halo.</summary>
    private float Surface(int x, int z, float unknown)
    {
        if (!Held(x, z))
            return unknown;

        var c = Column(x, z);
        return _columnWet[c] || _columnOpen[c] ? _columnHeight[c] : unknown;
    }

    /// <summary>
    /// How far (in columns) a column's water reaches past its corner at (rx, rz) from its centre, negative short of it: a
    /// line across its facing that has come its fill of the way over the column from the face behind it, or the fill
    /// spread evenly when still. Never under −1, as for no water at all. Only behind the line's middle does it reach past
    /// a corner: a full column facing along its diagonal reached 0.7 past its side corners and drew a spike from each.
    /// </summary>
    private float Reach(int c, float rx, float rz)
    {
        var fill = _columnFill[c];
        if (fill <= 0f)
            return -1f;

        var n = _columnFacing[c];
        var half = 0.5f * (MathF.Abs(n.X) + MathF.Abs(n.Y));
        var along = rx * n.X + rz * n.Y;
        var line = MathF.Max(MathF.Min((2f * fill - 1f) * half - along, MathF.Max(-2f * along, 0f)), -1f);
        return float.Lerp(line, 2f * fill - 1f, _columnStill[c]);
    }

    /// <summary>
    /// The ground's height (chunk-local m) in column (x, z) about the level of cell y: the smooth surface (the bed) of the
    /// open cell (<paramref name="cell"/>) standing on solid at y − 1, y or y + 1, the lowest a voxel step down a slope;
    /// infinite where solid stands over y (a bank or a wall), minus infinity where the ground lies lower (a drop).
    /// </summary>
    private static float Ground(WaterBlock block, int x, int z, int y, out int cell)
    {
        var flags = block.Flags;
        var i = ChunkConstants.Index(x, y, z);
        cell = -1;

        if ((flags[i + P] & SolidBit) != 0)
            return float.PositiveInfinity;

        if ((flags[i] & SolidBit) != 0)
        {
            cell = i + P;
            return y + 0.5f + BedHeight(block.Bed[cell]);
        }

        if ((flags[i - P] & SolidBit) != 0)
        {
            cell = i;
            return y - 0.5f + BedHeight(block.Bed[cell]);
        }

        if (y > 0 && (flags[i - 2 * P] & SolidBit) != 0)
        {
            cell = i - P;
            return y - 1.5f + BedHeight(block.Bed[cell]);
        }

        return float.NegativeInfinity;
    }

    private static float BedHeight(sbyte bed) => bed == WaterBlock.NotBed ? 0f : bed * WaterBlock.BedUnit;

    private void BuildColumn(WaterBlock block, int x, int z)
    {
        var water = block.Water;
        var flags = block.Flags;
        var top = -1;
        var any = false;

        for (var y = S - 1; y >= 0; y--)
        {
            var i = ChunkConstants.Index(x, y, z);

            if (water[i] <= WetThreshold)
                continue;

            any = true;
            var above = i + P;

            if ((flags[above] & SolidBit) != 0 || water[above] < WetThreshold)
            {
                top = y;
                break;
            }
        }

        // A stream crossing a block's top or bottom: a column full to the block's top has its surface in the block above,
        // and a dry one may hold water in the block below. Either is still water here, at its height there, for the
        // corners, fronts and waterlines of the columns beside it, but the other block draws it. Taken as no water, each
        // block clipped its waterline toward the other's and both stopped short: a dry zig-zag across a river where its
        // surface crossed a block's height.
        var beyond = false;

        if (top < 0)
        {
            var over = ChunkConstants.Index(x, S, z);
            var under = ChunkConstants.Index(x, -1, z);

            // Over: standing on this block's top cell, full of water or solid. Under: the block's bottom cell is dry.
            if ((any || (flags[over - P] & SolidBit) != 0) && water[over] > WetThreshold && (flags[over] & SolidBit) == 0)
                top = S;
            else if (!any && water[under] > WetThreshold && (flags[under] & SolidBit) == 0)
                top = -1;
            else
            {
                _columnBare[Column(x, z)] = !any;
                return;
            }

            beyond = true;
        }

        var topIndex = ChunkConstants.Index(x, top, z);

        // Open sea and simulated sea are drawn by the ocean renderer; every other body of water is drawn here.
        if ((flags[topIndex] & (byte)WaterFlags.SeaZone) != 0 || ((flags[topIndex] & (byte)WaterFlags.Reservoir) != 0 && block.Body[topIndex] == WaterBodies.Sea))
            return;

        // A film stands on the terrain's smooth surface, not on the voxel floor: drawn on the floor it sank under the
        // terrain wherever the slope rose above the floor, and a sheet running down a hill showed dry holes. A held cell's
        // fill is its body's level over the floor, bed or not: lifted by its bed, the lake's shore ring stood up to a
        // quarter metre over the lake and ended in a stair of column edges over the terrain under it.
        var held = (flags[topIndex] & (byte)WaterFlags.Reservoir) != 0;
        var surface = top - 0.5f + MathF.Min(water[topIndex], 1f) + (held ? 0f : WaterBlock.BedRise(block.Bed[topIndex], water[topIndex]));
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
        _columnBeyond[c] = beyond;
        _columnTop[c] = top;
        _columnHeight[c] = surface;

        // The halo layers hold no faces of this block's: the flow there is the other block's to draw.
        (_columnFlow[c], _columnFoam[c]) = beyond ? (Vector2.Zero, 0f) : FlowAndFoam(block, x, z, topIndex, water[topIndex]);
        var bed = block.Bed[ChunkConstants.Index(x, bottom, z)];
        _columnThickness[c] = surface - (bottom - 0.5f + (bed == WaterBlock.NotBed ? 0f : bed * WaterBlock.BedUnit));
    }

    /// <summary>
    /// The pipe model's surface flow at a column's top cell (its four side faces' fluxes over the water they move, m/s)
    /// and how churned the water is there: its froth (<see cref="WaterBlock.Froth"/>: aerated by falling, carried on from
    /// where a pour lands), and fast flow over shallow water (its Froude number past 1.2). The automaton has neither.
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
        // Fast shallow flow froths past Froude 1.2, a thin sheet at its critical speed only a little.
        var froude = speed / MathF.Sqrt(9.81f * MathF.Max(MathF.Min(w, 1f), 0.05f));
        var foam = MathF.Max(block.Froth![i], MathF.Min(MathF.Max(froude - 1.2f, 0f) * 0.5f, 0.6f));
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
                var wet = false;
                var bare = false;
                var reach = -1f;

                for (var dz = -1; dz <= 0; dz++)
                {
                    for (var dx = -1; dx <= 0; dx++)
                    {
                        var c = Column(cx + dx, cz + dz);

                        if (!_columnWet[c] && !_columnOpen[c])
                        {
                            bare |= _columnBare[c];

                            // A bank's skirt is drawn whole.
                            if (_columnSkirt[c])
                                reach = MathF.Max(reach, 1f);

                            continue;
                        }

                        // The corner from the column's centre: half a column back along each axis it lies behind.
                        reach = MathF.Max(reach, Reach(c, -dx - 0.5f, -dz - 0.5f));
                        height += _columnHeight[c];
                        thickness += _columnThickness[c];
                        flow += _columnFlow[c];
                        foam += _columnFoam[c];
                        wet |= _columnWet[c];
                        count++;
                    }
                }

                // The farthest any column's water reaches past the corner: inside where it is past it.
                var k = cx + cz * K;
                _cornerReach[k] = reach;

                // The water's edge against bare ground sinks to just under its bed, so the terrain draws the waterline.
                _cornerDrop[k] = bare && wet ? MathF.Min(thickness / count + WaterlineDip, MaxEdgeDrop) : 0f;

                if (_cornerDrop[k] > 0f)
                    thickness = 0f;

                // A corner no wet column touches is a skirt's outer corner: the skirts' height, no water under it.
                if (count == 0)
                {
                    for (var dz = -1; dz <= 0; dz++)
                    {
                        for (var dx = -1; dx <= 0; dx++)
                        {
                            var c = Column(cx + dx, cz + dz);

                            if (!_columnSkirt[c])
                                continue;

                            height += _columnHeight[c];
                            count++;
                        }
                    }
                }

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

        var index = vertexCount++;
        vertices[index] = Corner(cx, cz);
        _cornerVertex[k] = index;
        return (uint)index;
    }

    private WaterVertex Corner(int cx, int cz)
    {
        var k = cx + cz * K;
        var h = _cornerHeight[k];
        var dhdx = Slope(cx, cz, 1, 0, h);
        var dhdz = Slope(cx, cz, 0, 1, h);

        return new WaterVertex
        {
            // The normal keeps the surface's slope; only the position sinks at the waterline.
            Position = new Vector3(cx - 0.5f, h - _cornerDrop[k], cz - 0.5f) * ChunkConstants.VoxelSize,
            Normal = Vector3.Normalize(new Vector3(-dhdx, 1f, -dhdz)),
            Thickness = _cornerThickness[k],
            Flow = _cornerFlow[k],
            Foam = _cornerFoam[k],
        };
    }

    /// <summary>
    /// The waterline's vertex on the edge from corner a (inside, the water reaching past it) to its neighbour b (outside),
    /// where the reach interpolated between them comes to nothing, shared by the two quads on that edge. It sinks
    /// <see cref="WaterlineDip"/> with no thickness, so the terrain draws the waterline as at a bare edge, unless b has
    /// sunk already.
    /// </summary>
    private uint Crossing(int ax, int az, int bx, int bz, WaterVertex[] vertices, ref int vertexCount)
    {
        var edge = az == bz ? Math.Min(ax, bx) + az * K : K * K + ax + Math.Min(az, bz) * K;
        var existing = _edgeVertex[edge];

        if (existing >= 0)
            return (uint)existing;

        var kb = bx + bz * K;
        var ra = _cornerReach[ax + az * K];
        var rb = _cornerReach[kb];
        var t = ra / (ra - rb);

        if (t > 0.9999f && _sunkVertex[kb] >= 0)
        {
            _edgeVertex[edge] = _sunkVertex[kb];
            return (uint)_sunkVertex[kb];
        }

        var a = Corner(ax, az);
        var b = Corner(bx, bz);
        var dip = _cornerDrop[kb] > 0f ? 0f : WaterlineDip;

        var index = vertexCount++;
        if (t > 0.9999f)
        {
            t = 1f;
            _sunkVertex[kb] = index;
        }

        vertices[index] = new WaterVertex
        {
            Position = Vector3.Lerp(a.Position, b.Position, t) - new Vector3(0f, dip * ChunkConstants.VoxelSize, 0f),
            Normal = Vector3.Normalize(Vector3.Lerp(a.Normal, b.Normal, t)),
            Thickness = 0f,
            Flow = Vector2.Lerp(a.Flow, b.Flow, t),
            Foam = float.Lerp(a.Foam, b.Foam, t),
        };
        _edgeVertex[edge] = index;
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
