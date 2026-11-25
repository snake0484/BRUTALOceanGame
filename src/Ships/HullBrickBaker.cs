using System.Diagnostics;
using System.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.Ships;

/// <summary>One baked 8 m brick: the packed mesh (brick-local, <see cref="PackedTerrainVertex"/> at 0.25 m cells) and occupancy sums.</summary>
public sealed class HullBrick
{
    public BrickKey Key;

    /// <summary>xxHash64 of the key and the bytes of the brushes binned to it, in order: the cache key.</summary>
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

/// <summary>
/// Bakes one brick: brushes in list order into a padded 34³ density (SDF × 128 per metre, i.e. 32 per 0.25 m cell, as
/// terrain's 32 per voxel) and material, then occupancy sums and a surface nets mesh. Each brush is evaluated only over
/// its bounds plus band. Owns all scratch; one per worker.
/// </summary>
public sealed class HullBrickBaker
{
    private const int P = ChunkConstants.Padded;
    private const float DensityPerMetre = ChunkConstants.DensityScale / HullBins.CellMetres;
    private const int HalfCell = (int)(ChunkConstants.DensityScale / 2f);
    private const int FullCell = (int)ChunkConstants.DensityScale;
    private const int MaterialSlots = 256;

    private readonly VoxelChunk _chunk = new();
    private readonly ChunkMesh _mesh = new();
    private readonly SurfaceNetsMesher _mesher = new();
    private readonly MaterialOccupancy[] _occupancy = new MaterialOccupancy[MaterialSlots];
    private readonly bool[] _used = new bool[MaterialSlots];
    private readonly double[] _rotation = new double[9];

    public void Bake(BrickKey key, ReadOnlySpan<HullBrush> brushes, ReadOnlySpan<int> binned, HullBrick result)
    {
        var start = Stopwatch.GetTimestamp();
        _chunk.Density.AsSpan().Fill(sbyte.MaxValue);
        _chunk.Material.AsSpan().Fill(0);

        var origin = new Vector3d(key.X * (double)HullBins.BrickMetres, key.Y * (double)HullBins.BrickMetres, key.Z * (double)HullBins.BrickMetres);

        foreach (var index in binned)
            Apply(brushes[index], origin);

        result.Key = key;
        Occupancy(key, result);

        _chunk.RecomputeHasSurface();
        _mesh.Clear();

        if (_chunk.HasSurface)
            _mesher.Mesh(_chunk, _mesh, skirts: false, HullBins.CellMetres);

        CopyMesh(result);
        result.BakeMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void Apply(in HullBrush brush, Vector3d origin)
    {
        BrushGeometry.Bounds(brush, out var min, out var max);
        const double band = 127.0 / DensityPerMetre;
        const double cell = HullBins.CellMetres;

        var x0 = Math.Max(-1, (int)Math.Floor((min.X - band - origin.X) / cell));
        var y0 = Math.Max(-1, (int)Math.Floor((min.Y - band - origin.Y) / cell));
        var z0 = Math.Max(-1, (int)Math.Floor((min.Z - band - origin.Z) / cell));
        var x1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.X + band - origin.X) / cell));
        var y1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.Y + band - origin.Y) / cell));
        var z1 = Math.Min(HullBins.BrickCells, (int)Math.Ceiling((max.Z + band - origin.Z) / cell));

        if (x0 > x1 || y0 > y1 || z0 > z1)
            return;

        BrushRotation.Matrix(brush.Rotation, _rotation);
        var m = _rotation;

        // Local = Rᵀ · (sample − position); the brick-to-brush offset is taken in double once, the steps are exact floats.
        var o = new Vector3((float)(origin.X - brush.PositionX), (float)(origin.Y - brush.PositionY), (float)(origin.Z - brush.PositionZ));
        var c0 = new Vector3((float)m[0], (float)m[3], (float)m[6]);
        var c1 = new Vector3((float)m[1], (float)m[4], (float)m[7]);
        var c2 = new Vector3((float)m[2], (float)m[5], (float)m[8]);
        var half = BrushGeometry.Half(brush);
        var shape = brush.Shape;
        var op = brush.Op;
        var material = brush.Material;
        var density = _chunk.Density;
        var materials = _chunk.Material;

        for (var z = z0; z <= z1; z++)
        {
            for (var y = y0; y <= y1; y++)
            {
                var i = ChunkConstants.Index(x0, y, z);

                for (var x = x0; x <= x1; x++, i++)
                {
                    var p = o + new Vector3(x, y, z) * HullBins.CellMetres;
                    var local = new Vector3(Vector3.Dot(c0, p), Vector3.Dot(c1, p), Vector3.Dot(c2, p));
                    var q = Quantise(BrushGeometry.Distance(shape, local, half) * DensityPerMetre);
                    var old = density[i];

                    switch (op)
                    {
                        case BrushOp.Add:
                            if (q < old)
                            {
                                density[i] = q;
                                materials[i] = material;
                            }
                            else if (q < 0)
                            {
                                materials[i] = material;
                            }

                            break;
                        case BrushOp.Subtract:
                            if (-q > old)
                                density[i] = (sbyte)-q;

                            break;
                        case BrushOp.Paint:
                            if (q < 0)
                                materials[i] = material;

                            break;
                        case BrushOp.Intersect:
                            if (q > old)
                                density[i] = q;

                            break;
                    }
                }
            }
        }
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
