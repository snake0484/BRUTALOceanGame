using System.Diagnostics;
using System.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.Ships;

/// <summary>One baked 8 m brick: the packed mesh (brick-local, <see cref="PackedTerrainVertex"/> at 0.25 m cells) and occupancy sums.</summary>
public sealed class HullBrick
{
    public BrickKey Key;

    /// <summary>xxHash64 of the key, the brushes binned to it (in order) and their meshes' hashes: the cache key.</summary>
    public ulong InputHash;

    public PackedTerrainVertex[] Vertices = [];
    public int VertexCount;
    public uint[] Indices = [];
    public int IndexCount;
    public MaterialOccupancy[] Materials = new MaterialOccupancy[4];
    public int MaterialCount;
    public float BakeMs;

    public ReadOnlySpan<PackedTerrainVertex> VertexSpan => Vertices.AsSpan(0, VertexCount);

    public ReadOnlySpan<uint> IndexSpan => Indices.AsSpan(0, IndexCount);

    public ReadOnlySpan<MaterialOccupancy> MaterialSpan => Materials.AsSpan(0, MaterialCount);

    /// <summary>Brick origin in ship metres.</summary>
    public Vector3d Origin => new(Key.X * (double)HullBins.BrickMetres, Key.Y * (double)HullBins.BrickMetres, Key.Z * (double)HullBins.BrickMetres);
}

public enum HullMesher : byte { DualContouring, SurfaceNets }

/// <summary>
/// Bakes one brick: brushes in list order into a padded 34³ density (SDF × 128 per metre, i.e. 32 per 0.25 m cell, as
/// terrain's 32 per voxel), material and the winning brush per sample, then occupancy sums and a mesh. Primitives are
/// evaluated over their bounds plus band. Mesh brushes take their sign from ray parity along each brick row (rays start
/// outside the mesh) and their distance only near triangles (rasterised triangle bounds plus a 0.5 m band; further out
/// ±0.5 m), so a brick far from every triangle costs one ray per row. Dual contouring reads Hermite data from the
/// winning brushes: the exact crossing (root of the primitive's SDF, or the mesh triangle the edge pierces) and its
/// gradient. Sample positions are taken in double from the brick origin, so neighbouring bricks agree on shared samples.
/// Owns all scratch; one per worker.
/// </summary>
public sealed class HullBrickBaker : IHermiteField
{
    private const int P = ChunkConstants.Padded;
    private const int PP = ChunkConstants.PaddedArea;
    private const int N = ChunkConstants.PaddedVolume;
    private const float DensityPerMetre = ChunkConstants.DensityScale / HullBins.CellMetres;
    private const int HalfCell = (int)(ChunkConstants.DensityScale / 2f);
    private const int FullCell = (int)ChunkConstants.DensityScale;
    private const int MaterialSlots = 256;
    private const float MeshBand = 0.5f;
    private const int RootIterations = 16;
    private const float GradientStep = 1e-3f;

    private readonly VoxelChunk _chunk = new();
    private readonly ChunkMesh _mesh = new();
    private readonly SurfaceNetsMesher _surfaceNets = new();
    private readonly HullDualContour _dualContour = new();
    private readonly ExactSurface _exact = new();
    private readonly ushort[] _winner = new ushort[N];
    private readonly byte[] _near = new byte[N];
    private readonly List<int> _nearList = new();
    private readonly List<int> _triangles = new();
    private readonly List<double> _hits = new();
    private readonly MaterialOccupancy[] _occupancy = new MaterialOccupancy[MaterialSlots];
    private readonly bool[] _used = new bool[MaterialSlots];
    private readonly double[] _rotation = new double[9];
    private Brush[] _brushes = new Brush[16];
    private Vector3d _origin;

    /// <summary>A binned brush ready to evaluate: Rᵀ rows (local = Rᵀ · (p − position)), R columns, half, mesh.</summary>
    private struct Brush
    {
        public Vector3d Position;
        public Vector3 T0, T1, T2;
        public Vector3 R0, R1, R2;
        public Vector3 Half;
        public BrushShape Shape;
        public BrushOp Op;
        public byte Material;
        public PreparedMesh? Mesh;
        public HullBrush Source;
    }

    public HullMesher Mesher = HullMesher.DualContouring;

    /// <param name="exact">
    /// Index into <paramref name="binned"/> of the only brush reaching this brick (an Add): its own polygons are the mesh,
    /// clipped to the brick; -1 meshes the grid. <paramref name="grow"/> bits 0-2 grow the clip box one cell down X, Y, Z
    /// (toward grid-meshed neighbours, so the two surfaces overlap rather than leave a crack).
    /// </param>
    public void Bake(BrickKey key, ReadOnlySpan<HullBrush> brushes, ReadOnlySpan<int> binned, IReadOnlyList<PreparedMesh?> meshes, HullBrick result, int exact = -1, int grow = 0)
    {
        var start = Stopwatch.GetTimestamp();
        _chunk.Density.AsSpan().Fill(sbyte.MaxValue);
        _chunk.Material.AsSpan().Fill(0);
        _winner.AsSpan().Clear();
        _origin = new Vector3d(key.X * (double)HullBins.BrickMetres, key.Y * (double)HullBins.BrickMetres, key.Z * (double)HullBins.BrickMetres);

        if (_brushes.Length < binned.Length)
            _brushes = new Brush[Math.Max(binned.Length, _brushes.Length * 2)];

        for (var k = 0; k < binned.Length; k++)
        {
            Prepare(ref _brushes[k], brushes[binned[k]], meshes);
            ref readonly var b = ref _brushes[k];

            if (b.Shape == BrushShape.Mesh)
            {
                if (b.Mesh is not null)
                    ApplyMesh(k);
            }
            else if (BrushGeometry.IsSupported(b.Shape))
            {
                ApplyPrimitive(k);
            }
        }

        result.Key = key;
        Occupancy(key, result);

        _chunk.RecomputeHasSurface();
        _mesh.Clear();

        if (exact >= 0)
        {
            ref readonly var b = ref _brushes[exact];
            const double cell = HullBins.CellMetres;
            var min = new Vector3d((grow & 1) != 0 ? -cell : 0.0, (grow & 2) != 0 ? -cell : 0.0, (grow & 4) != 0 ? -cell : 0.0);
            var max = new Vector3d(HullBins.BrickMetres, HullBins.BrickMetres, HullBins.BrickMetres);
            _exact.Append(b.Source, b.Mesh, _origin, min, max, _mesh);
            _mesh.Pack(HullBins.CellMetres);
        }
        else if (_chunk.HasSurface)
        {
            if (Mesher == HullMesher.DualContouring)
                _dualContour.Mesh(_chunk, _mesh, HullBins.CellMetres, this);
            else
                _surfaceNets.Mesh(_chunk, _mesh, skirts: false, HullBins.CellMetres);
        }

        CopyMesh(result);
        result.BakeMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void Prepare(ref Brush b, in HullBrush source, IReadOnlyList<PreparedMesh?> meshes)
    {
        BrushRotation.Matrix(source.Rotation, _rotation);
        var m = _rotation;
        b.Source = source;
        b.Position = new Vector3d(source.PositionX, source.PositionY, source.PositionZ);
        b.T0 = new Vector3((float)m[0], (float)m[3], (float)m[6]);
        b.T1 = new Vector3((float)m[1], (float)m[4], (float)m[7]);
        b.T2 = new Vector3((float)m[2], (float)m[5], (float)m[8]);
        b.R0 = new Vector3((float)m[0], (float)m[1], (float)m[2]);
        b.R1 = new Vector3((float)m[3], (float)m[4], (float)m[5]);
        b.R2 = new Vector3((float)m[6], (float)m[7], (float)m[8]);
        b.Half = BrushGeometry.Half(source);
        b.Shape = source.Shape;
        b.Op = source.Op;
        b.Material = source.Material;
        b.Mesh = source.Shape == BrushShape.Mesh && source.Param < meshes.Count ? meshes[source.Param] : null;
    }

    /// <summary>Sample (x, y, z) relative to the brush position, in double then float.</summary>
    private Vector3 Relative(in Brush b, int x, int y, int z) => new(
        (float)(_origin.X + x * (double)HullBins.CellMetres - b.Position.X),
        (float)(_origin.Y + y * (double)HullBins.CellMetres - b.Position.Y),
        (float)(_origin.Z + z * (double)HullBins.CellMetres - b.Position.Z));

    private static Vector3 ToLocal(in Brush b, Vector3 p) => new(Vector3.Dot(b.T0, p), Vector3.Dot(b.T1, p), Vector3.Dot(b.T2, p));

    private static Vector3 ToShip(in Brush b, Vector3 v) => new(Vector3.Dot(b.R0, v), Vector3.Dot(b.R1, v), Vector3.Dot(b.R2, v));

    private bool Range(in HullBrush source, double band, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
    {
        BrushGeometry.Bounds(source, out var min, out var max);
        const double cell = HullBins.CellMetres;
        x0 = Math.Max(-1, (int)Math.Floor((min.X - band - _origin.X) / cell));
        y0 = Math.Max(-1, (int)Math.Floor((min.Y - band - _origin.Y) / cell));
        z0 = Math.Max(-1, (int)Math.Floor((min.Z - band - _origin.Z) / cell));
        x1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.X + band - _origin.X) / cell));
        y1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.Y + band - _origin.Y) / cell));
        z1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.Z + band - _origin.Z) / cell));
        return x0 <= x1 && y0 <= y1 && z0 <= z1;
    }

    private void ApplyPrimitive(int k)
    {
        ref readonly var b = ref _brushes[k];

        if (!Range(b.Source, 127.0 / DensityPerMetre, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1))
            return;

        for (var z = z0; z <= z1; z++)
        {
            for (var y = y0; y <= y1; y++)
            {
                var i = ChunkConstants.Index(x0, y, z);

                for (var x = x0; x <= x1; x++, i++)
                {
                    var local = ToLocal(b, Relative(b, x, y, z));
                    Combine(i, k, Quantise(BrushGeometry.Distance(b.Shape, local, b.Half) * DensityPerMetre));
                }
            }
        }
    }

    private void ApplyMesh(int k)
    {
        ref readonly var b = ref _brushes[k];
        var mesh = b.Mesh!;

        if (!Range(b.Source, MeshBand + HullBins.CellMetres, out var x0, out var y0, out var z0, out var x1, out var y1, out var z1))
            return;

        MarkNearSamples(b, mesh, x0, y0, z0, x1, y1, z1);

        // Ray direction: the brick's +X in brush-local axes. Origins start past the mesh's bounding sphere.
        var dir = new Vector3d(b.T0.X, b.T1.X, b.T2.X);
        var far = Vector3.Max(Vector3.Abs(mesh.Min), Vector3.Abs(mesh.Max));
        var radius = far.Length() + 1.0;
        var qFar = Quantise(MeshBand * DensityPerMetre);

        for (var z = z0; z <= z1; z++)
        {
            for (var y = y0; y <= y1; y++)
            {
                var first = ToLocal(b, Relative(b, x0, y, z));
                var lastLocal = ToLocal(b, Relative(b, x1, y, z));

                if (!SegmentNearBox(first, lastLocal, mesh.Min, mesh.Max, MeshBand))
                    continue;

                var lead = new Vector3d(first.X, first.Y, first.Z).Length() + radius;
                var origin = new Vector3d(first.X - dir.X * lead, first.Y - dir.Y * lead, first.Z - dir.Z * lead);
                _hits.Clear();
                mesh.Crossings(origin, dir, _hits);
                _hits.Sort();
                var h = 0;
                var i = ChunkConstants.Index(x0, y, z);

                for (var x = x0; x <= x1; x++, i++)
                {
                    var t = lead + (x - x0) * (double)HullBins.CellMetres;

                    while (h < _hits.Count && _hits[h] < t)
                        h++;

                    var inside = (h & 1) != 0;
                    sbyte q;

                    if (_near[i] != 0)
                    {
                        var d = mesh.Distance(ToLocal(b, Relative(b, x, y, z)), MeshBand);
                        q = Quantise((inside ? -d : d) * DensityPerMetre);
                    }
                    else
                    {
                        q = inside ? (sbyte)-qFar : qFar;
                    }

                    Combine(i, k, q);
                }
            }
        }

        foreach (var i in _nearList)
            _near[i] = 0;

        _nearList.Clear();
    }

    /// <summary>Marks samples within the band of any triangle near the brick (triangle bounds in sample space, ± band).</summary>
    private void MarkNearSamples(in Brush b, PreparedMesh mesh, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);

        for (var c = 0; c < 8; c++)
        {
            var corner = ToLocal(b, Relative(b, (c & 1) != 0 ? x1 : x0, (c & 2) != 0 ? y1 : y0, (c & 4) != 0 ? z1 : z0));
            lo = Vector3.Min(lo, corner);
            hi = Vector3.Max(hi, corner);
        }

        _triangles.Clear();
        mesh.Overlapping(lo - new Vector3(MeshBand), hi + new Vector3(MeshBand), _triangles);
        const float band = MeshBand / HullBins.CellMetres;
        var offset = new Vector3((float)(b.Position.X - _origin.X), (float)(b.Position.Y - _origin.Y), (float)(b.Position.Z - _origin.Z)) / HullBins.CellMetres;

        foreach (var t in _triangles)
        {
            var pa = ToShip(b, mesh.A[t]) / HullBins.CellMetres + offset;
            var pb = ToShip(b, mesh.B[t]) / HullBins.CellMetres + offset;
            var pc = ToShip(b, mesh.C[t]) / HullBins.CellMetres + offset;
            var min = Vector3.Min(pa, Vector3.Min(pb, pc)) - new Vector3(band);
            var max = Vector3.Max(pa, Vector3.Max(pb, pc)) + new Vector3(band);
            var sx0 = Math.Max(x0, (int)MathF.Floor(min.X));
            var sy0 = Math.Max(y0, (int)MathF.Floor(min.Y));
            var sz0 = Math.Max(z0, (int)MathF.Floor(min.Z));
            var sx1 = Math.Min(x1, (int)MathF.Ceiling(max.X));
            var sy1 = Math.Min(y1, (int)MathF.Ceiling(max.Y));
            var sz1 = Math.Min(z1, (int)MathF.Ceiling(max.Z));

            for (var z = sz0; z <= sz1; z++)
                for (var y = sy0; y <= sy1; y++)
                {
                    var i = ChunkConstants.Index(sx0, y, z);

                    for (var x = sx0; x <= sx1; x++, i++)
                    {
                        if (_near[i] != 0)
                            continue;

                        _near[i] = 1;
                        _nearList.Add(i);
                    }
                }
        }
    }

    private static bool SegmentNearBox(Vector3 a, Vector3 b, Vector3 min, Vector3 max, float band)
    {
        var lo = Vector3.Min(a, b);
        var hi = Vector3.Max(a, b);
        return lo.X <= max.X + band && lo.Y <= max.Y + band && lo.Z <= max.Z + band && hi.X >= min.X - band && hi.Y >= min.Y - band && hi.Z >= min.Z - band;
    }

    /// <summary>Combines a brush's quantised value at sample <paramref name="i"/> by its op; the brush that sets the density wins it.</summary>
    private void Combine(int i, int k, sbyte q)
    {
        ref readonly var b = ref _brushes[k];
        var density = _chunk.Density;
        var old = density[i];

        switch (b.Op)
        {
            case BrushOp.Add:
                if (q < old)
                {
                    density[i] = q;
                    _chunk.Material[i] = b.Material;
                    _winner[i] = (ushort)(k + 1);
                }
                else if (q < 0)
                {
                    _chunk.Material[i] = b.Material;
                }

                break;
            case BrushOp.Subtract:
                if (-q > old)
                {
                    density[i] = (sbyte)-q;
                    _winner[i] = (ushort)(k + 1);
                }

                break;
            case BrushOp.Paint:
                if (q < 0)
                    _chunk.Material[i] = b.Material;

                break;
            case BrushOp.Intersect:
                if (q > old)
                {
                    density[i] = q;
                    _winner[i] = (ushort)(k + 1);
                }

                break;
        }
    }

    /// <summary>
    /// Hermite data for the edge from sample (x, y, z) one cell along <paramref name="axis"/>. Candidates are the brushes
    /// winning either end; each gives an exact crossing (primitive: root of its signed field by bracketed secant;
    /// mesh: the triangle the edge pierces) and a normal toward air. With two, the crossing nearer the quantised
    /// linear estimate wins. With none, the linear estimate and the density gradient along the edge.
    /// </summary>
    public void Edge(int x, int y, int z, int axis, int densityA, int densityB, out float t, out Vector3 normal, out Vector3 shade)
    {
        var i = ChunkConstants.Index(x, y, z);
        var j = i + (axis == 0 ? 1 : axis == 1 ? P : PP);
        var linear = densityA / (float)(densityA - densityB);
        t = linear;
        normal = DensityGradient(x, y, z, axis, linear);
        shade = normal;

        var best = float.MaxValue;
        Span<int> candidates = [_winner[i] - 1, _winner[j] - 1];

        for (var c = 0; c < 2; c++)
        {
            var k = candidates[c];

            if (k < 0 || (c == 1 && k == candidates[0]))
                continue;

            if (Crossing(k, x, y, z, axis, out var tk, out var nk, out var sk) && MathF.Abs(tk - linear) < best)
            {
                best = MathF.Abs(tk - linear);
                t = tk;
                normal = nk;
                shade = sk;
            }
        }
    }

    /// <summary>Fallback normal: central differences of the density at the crossing, toward air.</summary>
    private Vector3 DensityGradient(int x, int y, int z, int axis, float t)
    {
        var g0 = SampleGradient(x, y, z);
        var g1 = SampleGradient(x + (axis == 0 ? 1 : 0), y + (axis == 1 ? 1 : 0), z + (axis == 2 ? 1 : 0));
        var g = Vector3.Lerp(g0, g1, t);
        var length = g.Length();
        return length > 1e-6f ? g / length : axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
    }

    private Vector3 SampleGradient(int x, int y, int z)
    {
        var d = _chunk.Density;
        int D(int a, int b, int c) => d[ChunkConstants.Index(Math.Clamp(a, -1, HullBins.BrickCells), Math.Clamp(b, -1, HullBins.BrickCells), Math.Clamp(c, -1, HullBins.BrickCells))];
        return new Vector3(D(x + 1, y, z) - D(x - 1, y, z), D(x, y + 1, z) - D(x, y - 1, z), D(x, y, z + 1) - D(x, y, z - 1));
    }

    private bool Crossing(int k, int x, int y, int z, int axis, out float t, out Vector3 normal, out Vector3 shade)
    {
        ref readonly var b = ref _brushes[k];
        t = 0f;
        normal = default;
        shade = default;
        var step = axis == 0 ? new Vector3(HullBins.CellMetres, 0, 0) : axis == 1 ? new Vector3(0, HullBins.CellMetres, 0) : new Vector3(0, 0, HullBins.CellMetres);
        var pa = Relative(b, x, y, z);
        var la = ToLocal(b, pa);
        var lb = ToLocal(b, pa + step);
        var sign = b.Op == BrushOp.Subtract ? -1f : 1f;

        if (b.Shape == BrushShape.Mesh)
        {
            if (b.Mesh is null || !b.Mesh.FirstHit(new Vector3d(la.X, la.Y, la.Z), new Vector3d(lb.X - la.X, lb.Y - la.Y, lb.Z - la.Z), out var hit, out var triangle))
                return false;

            t = (float)hit;
            normal = Vector3.Normalize(ToShip(b, b.Mesh.Normals[triangle] * sign));
            shade = Vector3.Normalize(ToShip(b, b.Mesh.SmoothNormal(triangle, Vector3.Lerp(la, lb, t)) * sign));
            return true;
        }

        var ga = BrushGeometry.Distance(b.Shape, la, b.Half);
        var gb = BrushGeometry.Distance(b.Shape, lb, b.Half);

        if ((ga < 0f) == (gb < 0f))
            return false;

        // Bracketed secant (regula falsi, Illinois) on the brush's own exact field.
        float lo = 0f, hi = 1f, flo = ga, fhi = gb;
        var side = 0;

        for (var n = 0; n < RootIterations; n++)
        {
            var m = (lo * fhi - hi * flo) / (fhi - flo);
            var fm = BrushGeometry.Distance(b.Shape, Vector3.Lerp(la, lb, m), b.Half);

            if ((fm < 0f) == (flo < 0f))
            {
                lo = m;
                flo = fm;

                if (side == -1)
                    fhi *= 0.5f;

                side = -1;
            }
            else
            {
                hi = m;
                fhi = fm;

                if (side == 1)
                    flo *= 0.5f;

                side = 1;
            }

            if (MathF.Abs(fm) < 1e-6f)
                break;
        }

        t = (lo * MathF.Abs(fhi) + hi * MathF.Abs(flo)) / (MathF.Abs(flo) + MathF.Abs(fhi));
        var p = Vector3.Lerp(la, lb, t);
        var g = new Vector3(
            BrushGeometry.Distance(b.Shape, p + new Vector3(GradientStep, 0, 0), b.Half) - BrushGeometry.Distance(b.Shape, p - new Vector3(GradientStep, 0, 0), b.Half),
            BrushGeometry.Distance(b.Shape, p + new Vector3(0, GradientStep, 0), b.Half) - BrushGeometry.Distance(b.Shape, p - new Vector3(0, GradientStep, 0), b.Half),
            BrushGeometry.Distance(b.Shape, p + new Vector3(0, 0, GradientStep), b.Half) - BrushGeometry.Distance(b.Shape, p - new Vector3(0, 0, GradientStep), b.Half));
        var length = g.Length();

        if (length < 1e-9f)
            return false;

        normal = ToShip(b, g * (sign / length));
        shade = normal;
        return true;
    }

    /// <summary>Owned samples (0..31 per axis): occupancy 0..32 from the density's linear ramp over one cell, per material.</summary>
    private void Occupancy(BrickKey key, HullBrick result)
    {
        var density = _chunk.Density;
        var materials = _chunk.Material;
        var bx = key.X * HullBins.BrickCells;
        var by = key.Y * HullBins.BrickCells;
        var bz = key.Z * HullBins.BrickCells;

        for (var z = 0; z < HullBins.BrickCells; z++)
        {
            for (var y = 0; y < HullBins.BrickCells; y++)
            {
                var i = ChunkConstants.Index(0, y, z);

                for (var x = 0; x < HullBins.BrickCells; x++, i++)
                {
                    var occ = Math.Clamp(HalfCell - density[i], 0, FullCell);

                    if (occ == 0)
                        continue;

                    var mat = materials[i];
                    ref var slot = ref _occupancy[mat];

                    if (!_used[mat])
                    {
                        _used[mat] = true;
                        slot = new MaterialOccupancy { Material = mat };
                    }

                    slot.Add(occ, bx + x, by + y, bz + z);
                }
            }
        }

        result.MaterialCount = 0;

        for (var mat = 0; mat < MaterialSlots; mat++)
        {
            if (!_used[mat])
                continue;

            _used[mat] = false;

            if (result.MaterialCount == result.Materials.Length)
                Array.Resize(ref result.Materials, result.Materials.Length * 2);

            result.Materials[result.MaterialCount++] = _occupancy[mat];
        }
    }

    private void CopyMesh(HullBrick result)
    {
        var vertices = _mesh.VertexCount;
        var indices = _mesh.IndexCount;

        if (result.Vertices.Length < vertices)
            result.Vertices = new PackedTerrainVertex[Math.Max(vertices, result.Vertices.Length * 2)];

        if (result.Indices.Length < indices)
            result.Indices = new uint[Math.Max(indices, result.Indices.Length * 2)];

        _mesh.Packed.AsSpan(0, vertices).CopyTo(result.Vertices);
        _mesh.Indices.AsSpan(0, indices).CopyTo(result.Indices);
        result.VertexCount = vertices;
        result.IndexCount = indices;
    }

    private static sbyte Quantise(float value) => (sbyte)Math.Clamp(MathF.Round(value), -127f, 127f);
}
