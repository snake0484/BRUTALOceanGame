namespace BrutalGame.Terrain;

public enum NodeClass : byte
{
    Surface,
    Air,
    Solid,
}

/// <summary>
/// Per-planet height-bounds pyramid for the shell (planets.md "Shell"): conservative min/max of H per cube-sphere texel
/// at every mip, so selection classifies a node as uniform air or solid with no job and no storage. Each texel holds the
/// bounds over a cap of twice its level's largest half-diagonal around its centre, so the texel holding a query cap's
/// centre covers the whole cap; bounds come from <see cref="PlanetSurface.Bounds"/> (rigorous) rounded outward to 1 m.
/// Small nodes are refined with their own cap. Build once at system load (per face, on jobs); queries are read-only.
/// </summary>
public sealed class PlanetHeightBounds
{
    public const int DefaultResolution = 256;

    /// <summary>A node whose cap is under this share of the finest texel's gets its own bounds evaluated.</summary>
    private const double RefineShare = 0.25;

    public readonly PlanetGenerator Generator;
    public readonly int Resolution;
    public readonly int Levels;

    private readonly short[][] _lo;
    private readonly short[][] _hi;
    private readonly double[] _allowance;
    private readonly double _radius;

    public PlanetHeightBounds(PlanetGenerator generator, int resolution = DefaultResolution)
    {
        if (resolution < 1 || (resolution & (resolution - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(resolution), "Power of two");

        Generator = generator;
        Resolution = resolution;
        Levels = System.Numerics.BitOperations.Log2((uint)resolution) + 1;
        _radius = generator.Params.Radius;
        _lo = new short[Levels][];
        _hi = new short[Levels][];
        _allowance = new double[Levels];

        for (var level = 0; level < Levels; level++)
        {
            var n = resolution >> level;
            _lo[level] = new short[CubeLattice.Faces * n * n];
            _hi[level] = new short[CubeLattice.Faces * n * n];
            _allowance[level] = MaxHalfDiagonal(n);
        }
    }

    /// <summary>Lowest ground anywhere on the planet (the coarsest level's lowest texel): the horizon occluder's height.</summary>
    public float GlobalMin
    {
        get
        {
            var min = (float)Generator.Params.MaxHeight;

            foreach (var lo in _lo[Levels - 1])
                min = MathF.Min(min, lo);

            return min;
        }
    }

    /// <summary>Largest query cap (rad) a texel of this level answers.</summary>
    public double Allowance(int level) => _allowance[level];

    /// <summary>Fills every level of one face. Faces are independent: run them on six jobs.</summary>
    public void BuildFace(int face)
    {
        for (var level = 0; level < Levels; level++)
        {
            var n = Resolution >> level;
            var lo = _lo[level];
            var hi = _hi[level];

            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    TexelCentre(face, i, j, n, out var dx, out var dy, out var dz);
                    var cap = HalfDiagonal(face, i, j, n, dx, dy, dz) + _allowance[level];
                    Generator.Surface.Bounds(dx, dy, dz, cap, out var l, out var h);
                    var k = (face * n + j) * n + i;
                    lo[k] = (short)MathF.Floor(l);
                    hi[k] = (short)MathF.Ceiling(h);
                }
            }
        }
    }

    public void Build()
    {
        for (var face = 0; face < CubeLattice.Faces; face++)
            BuildFace(face);
    }

    /// <summary>Bounds of H over the cap of directions within <paramref name="angle"/> of unit direction d.</summary>
    public void Query(double dx, double dy, double dz, double angle, out float lo, out float hi)
    {
        if (angle < _allowance[0] * RefineShare)
        {
            Generator.Surface.Bounds(dx, dy, dz, angle, out lo, out hi);
            return;
        }

        for (var level = 0; level < Levels; level++)
        {
            if (angle > _allowance[level])
                continue;

            var n = Resolution >> level;
            var face = CubeLattice.Face(dx, dy, dz, out var gu, out var gv);
            var i = Math.Min((int)((CubeLattice.ToT(gu) + 1.0) * (n * 0.5)), n - 1);
            var j = Math.Min((int)((CubeLattice.ToT(gv) + 1.0) * (n * 0.5)), n - 1);
            var k = (face * n + j) * n + i;
            lo = _lo[level][k];
            hi = _hi[level][k];
            return;
        }

        lo = Generator.Params.MinHeight;
        hi = Generator.Params.MaxHeight;
    }

    /// <summary>Highest ground within <paramref name="angle"/> of a direction, for horizon pruning.</summary>
    public float MaxHeightAround(double dx, double dy, double dz, double angle)
    {
        Query(dx, dy, dz, angle, out _, out var hi);
        return hi;
    }

    /// <summary>
    /// Uniform air or solid if every voxel of the node's padded volume is saturated in the generator; else Surface.
    /// Edits are the caller's: a uniform node an op reaches (<see cref="EditStore.Reaches"/>) must still be generated.
    /// </summary>
    public NodeClass Classify(in ChunkCoord node) => Classify(node, out _);

    /// <summary>As <see cref="Classify(in ChunkCoord)"/>; <paramref name="hi"/> is the highest ground the node's cap can hold (MaxHeight if not queried).</summary>
    public NodeClass Classify(in ChunkCoord node, out float hi)
    {
        hi = Generator.Params.MaxHeight;
        var lod = node.Lod;
        var s = node.VoxelSize;
        var size = ChunkConstants.Size * s;
        var ox = node.X * size;
        var oy = node.Y * size;
        var oz = node.Z * size;
        Span<double> box = [ox - s, oy - s, oz - s, ox + size, oy + size, oz + size];
        PlanetGenerator.BoxRadii(box, out var rMin, out var rMax);
        var pad = PlanetGenerator.Pad(lod);
        var p = Generator.Params;
        var wMin = rMin - _radius;
        var wMax = rMax - _radius;

        if (wMin > p.MaxHeight + pad)
            return NodeClass.Air;

        if (wMax < p.MinHeight - pad)
            return NodeClass.Solid;

        var cx = ox + 0.5 * (size - s);
        var cy = oy + 0.5 * (size - s);
        var cz = oz + 0.5 * (size - s);
        var dc = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        var half = 0.5 * (size + s) * Math.Sqrt(3.0);

        if (half >= dc || lod > PlanetGenerator.MaxLod)
            return NodeClass.Surface;

        var angle = Math.Asin(half / dc) + PlanetGenerator.LatticePadCells * Generator.CellAngle(lod);
        Query(cx / dc, cy / dc, cz / dc, angle, out var lo, out hi);

        if (wMin > hi + pad)
            return NodeClass.Air;

        if (wMax < lo - pad)
            return NodeClass.Solid;

        return NodeClass.Surface;
    }

    private void TexelCentre(int face, int i, int j, int n, out double dx, out double dy, out double dz) =>
        CubeLattice.Direction(face, CubeLattice.FromT(-1.0 + (2.0 * i + 1.0) / n), CubeLattice.FromT(-1.0 + (2.0 * j + 1.0) / n), out dx, out dy, out dz);

    /// <summary>Largest angle from a texel's centre to its corners: the farthest point of a great-circle quad is a corner.</summary>
    private static double HalfDiagonal(int face, int i, int j, int n, double dx, double dy, double dz)
    {
        var max = 0.0;

        for (var k = 0; k < 4; k++)
        {
            CubeLattice.LatticeDirection(face, i + (k & 1), j + (k >> 1), n, out var x, out var y, out var z);
            var ex = x - dx;
            var ey = y - dy;
            var ez = z - dz;
            max = Math.Max(max, 2.0 * Math.Asin(Math.Min(1.0, 0.5 * Math.Sqrt(ex * ex + ey * ey + ez * ez))));
        }

        return max * 1.0001;
    }

    private double MaxHalfDiagonal(int n)
    {
        var max = 0.0;

        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                TexelCentre(0, i, j, n, out var dx, out var dy, out var dz);
                max = Math.Max(max, HalfDiagonal(0, i, j, n, dx, dy, dz));
            }
        }

        return max;
    }
}
