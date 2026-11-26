using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// Fills planet chunks (planets.md "Generation"): cartesian voxels in the rotating planet frame, density = generated
/// height − radial altitude. Heights live on a cube-sphere lattice per LOD (cell ≤ 1 voxel), so every voxel's density is a
/// pure function of its planet-frame position and LOD: neighbours agree bit for bit on their shared aprons. The heights
/// come from the body's planet kind (<see cref="PlanetKindProgram"/>): octaves of 256 voxels and longer on every 8th
/// lattice point, interpolated; shorter ones down to 2 voxels per point; the radial altitude is exact (double sqrt per
/// voxel, review X1). Thread-safe; allocation-free with a warmed scratch.
/// </summary>
public sealed class PlanetGenerator
{
    /// <summary>Leaves go up to LOD 12 (4 km voxels).</summary>
    public const int MaxLod = 12;

    public const int CoarseStep = 8;
    public const float CoarseWavelengthVoxels = 256f;

    /// <summary>Density is vertical distance × max(1/√(1+slope²), InvMin): steeper than ~76° saturates a little early.</summary>
    public const float InvMin = 0.25f;

    /// <summary>Slope past 0.7 is bare rock.</summary>
    public const float SteepInv = 0.819f;

    /// <summary>Normalised distance (voxels) past which density is saturated (127/32 rounded up) and rock past the 4 m subsurface layer.</summary>
    public const float SaturationCore = 4.25f;

    /// <summary>Voxels further than this (× voxel size) vertically from the ground are saturated air or rock at any slope.</summary>
    public const float SaturationVoxels = SaturationCore / InvMin;

    /// <summary>Extra cap a chunk's heights can come from: the 8-point coarse cell plus a lattice cell (in cells).</summary>
    public const double LatticePadCells = 12.0;

    private const int P = ChunkConstants.Padded;
    private const int PP = ChunkConstants.PaddedArea;
    private const int LatticeMultiple = 1 << (MaxLod + 3);
    private const float SurfaceLayer = 1.5f;
    private const float SubSurfaceLayer = 4f;
    internal const float BeachHeight = 3f;
    internal const float PeakFraction = 0.55f;

    public readonly PlanetParams Params;
    public readonly PlanetSurface Surface;

    /// <summary>LOD-0 lattice cells per face side: a multiple of 2^15 so every leaf LOD has a coarse lattice, cell ≤ 1 m.</summary>
    public readonly int LatticeN0;

    private readonly double _radius;
    private readonly float _peak;

    public PlanetGenerator(in PlanetParams p)
    {
        Params = p;
        Surface = new PlanetSurface(p);
        _radius = p.Radius;
        _peak = p.MaxHeight * PeakFraction;
        LatticeN0 = LatticeMultiple * (int)Math.Ceiling(2.0 * p.Radius * CubeLattice.MaxArcPerT / LatticeMultiple);
    }

    public int LatticeN(int lod) => LatticeN0 >> lod;

    /// <summary>Largest lattice cell at this LOD (rad).</summary>
    public double CellAngle(int lod) => CubeLattice.MaxArcPerT * 2.0 / LatticeN(lod);

    /// <summary>Saturation distance in metres at this LOD.</summary>
    public static float Pad(int lod) => SaturationVoxels * (1 << lod);

    /// <summary>
    /// Fills the chunk's padded volume at its LOD. With <paramref name="withMask"/> (edits will replay) it also writes
    /// the protected voxels and a copy of the generated density into the scratch, and never takes a uniform early out.
    /// </summary>
    [SkipLocalsInit]
    public void Generate(VoxelChunk chunk, PlanetChunkScratch scratch, bool withMask = false)
    {
        var coord = chunk.Coord;
        var lod = coord.Lod;

        if ((uint)lod > MaxLod)
            throw new ArgumentOutOfRangeException(nameof(chunk), $"Planet leaves stop at LOD {MaxLod}");

        var s = coord.VoxelSize;
        var size = ChunkConstants.Size * s;
        var ox = coord.X * size;
        var oy = coord.Y * size;
        var oz = coord.Z * size;
        Span<double> box = [ox - s, oy - s, oz - s, ox + size, oy + size, oz + size];
        BoxRadii(box, out var rMin, out var rMax);
        var pad = Pad(lod);
        scratch.ProtectedCount = 0;

        if (!withMask && FillUniform(chunk, rMin - _radius, rMax - _radius, Params.MinHeight, Params.MaxHeight, pad))
            return;

        var n = LatticeN(lod);
        BuildWindows(scratch, box, n, ox, oy, oz, s);
        EvaluateWindows(scratch, lod, n, out var hMin, out var hMax, out var invLow);

        // The chunk's own gentlest normalisation saturates sooner than the worst case; same voxels either way.
        pad = MathF.Min(pad, SaturationCore * (float)s / invLow);

        if (!withMask && FillUniform(chunk, rMin - _radius, rMax - _radius, hMin, hMax, pad))
            return;

        Fill(chunk, scratch, n, ox, oy, oz, s, hMin - pad, hMax + pad, withMask);

        if (withMask)
            chunk.Density.AsSpan().CopyTo(scratch.Generated);
    }

    /// <summary>The lattice heights of a chunk's windows alone (what a generate evaluates before it fills voxels); returns the lattice points. For cost measurements.</summary>
    internal int EvaluateLattice(ChunkCoord coord, PlanetChunkScratch scratch)
    {
        var s = coord.VoxelSize;
        var size = ChunkConstants.Size * s;
        var ox = coord.X * size;
        var oy = coord.Y * size;
        var oz = coord.Z * size;
        Span<double> box = [ox - s, oy - s, oz - s, ox + size, oy + size, oz + size];
        var n = LatticeN(coord.Lod);
        BuildWindows(scratch, box, n, ox, oy, oz, s);
        EvaluateWindows(scratch, coord.Lod, n, out _, out _, out _);
        var points = 0;

        for (var w = 0; w < scratch.WindowCount; w++)
            points += scratch.Windows[w].HW * scratch.Windows[w].HH;

        return points;
    }

    /// <summary>
    /// True if a server can drop the op unlogged (D30): every voxel it reaches at LOD 0 is protected and generated as
    /// saturated rock, so neither removal (clamped) nor placing (already solid) changes anything. Conservative.
    /// </summary>
    public bool IsFullyProtected(in EditOp op)
    {
        op.Centre(out var cx, out var cy, out var cz);
        var r = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        var reach = op.ReachAt(1.0) + 1.0;

        if (r <= reach)
            return false;

        var angle = Math.Asin(Math.Min(1.0, reach / r)) + LatticePadCells * CellAngle(0);
        Surface.Bounds(cx / r, cy / r, cz / r, angle, 0, 0, out var lo, out var hi);
        var top = r + reach - _radius;
        var bottom = r - reach - _radius;

        if (top < lo - Params.DigDepth - Pad(0))
            return true;

        return hi <= Surface.ProtectHeight && top < lo - Pad(0) && bottom > hi - Params.DigDepth;
    }

    /// <summary>Distance range from the planet centre over an axis-aligned box (minX, minY, minZ, maxX, maxY, maxZ).</summary>
    public static void BoxRadii(ReadOnlySpan<double> box, out double min, out double max)
    {
        var near = 0.0;
        var far = 0.0;

        for (var a = 0; a < 3; a++)
        {
            var lo = box[a];
            var hi = box[a + 3];
            var c = lo > 0.0 ? lo : hi < 0.0 ? hi : 0.0;
            var f = Math.Max(Math.Abs(lo), Math.Abs(hi));
            near += c * c;
            far += f * f;
        }

        min = Math.Sqrt(near);
        max = Math.Sqrt(far);
    }

    private bool FillUniform(VoxelChunk chunk, double wMin, double wMax, float hMin, float hMax, float pad)
    {
        if (wMin > hMax + pad)
        {
            chunk.Fill(sbyte.MaxValue, VoxelMaterial.Air);
            return true;
        }

        if (wMax < hMin - pad)
        {
            chunk.Fill(-sbyte.MaxValue, Params.Materials.Rock);
            return true;
        }

        return false;
    }

    /// <summary>
    /// One lattice window per cube face the box reaches. A face whose half-space holds the whole box gets the bounding
    /// rectangle of its corners' projections (a central projection maps the box to the hull of its corners); a box that
    /// straddles a face's plane and could still reach its region falls back to projecting every voxel.
    /// </summary>
    private static void BuildWindows(PlanetChunkScratch scratch, ReadOnlySpan<double> box, int n, double ox, double oy, double oz, double s)
    {
        Span<double> range = stackalloc double[CubeLattice.Faces * 4];
        Span<bool> used = stackalloc bool[CubeLattice.Faces];
        var slow = false;

        for (var face = 0; face < CubeLattice.Faces; face++)
        {
            used[face] = false;
            var a = face >> 1;
            var b = (a + 1) % 3;
            var c = (a + 2) % 3;
            var sign = (face & 1) == 0 ? 1.0 : -1.0;
            var majorLo = Math.Min(sign * box[a], sign * box[a + 3]);
            var majorHi = Math.Max(sign * box[a], sign * box[a + 3]);

            if (majorHi <= 0.0)
                continue;

            if (majorLo <= 0.0)
            {
                if (majorHi >= Math.Max(MinAbs(box[b], box[b + 3]), MinAbs(box[c], box[c + 3])))
                    slow = true;

                continue;
            }

            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;

            for (var k = 0; k < 8; k++)
            {
                var major = sign * box[a + ((k & 1) != 0 ? 3 : 0)];
                var tu = CubeLattice.ToT(box[b + ((k & 2) != 0 ? 3 : 0)] / major);
                var tv = CubeLattice.ToT(box[c + ((k & 4) != 0 ? 3 : 0)] / major);
                uMin = Math.Min(uMin, tu);
                uMax = Math.Max(uMax, tu);
                vMin = Math.Min(vMin, tv);
                vMax = Math.Max(vMax, tv);
            }

            if (uMax < -1.0 || uMin > 1.0 || vMax < -1.0 || vMin > 1.0)
                continue;

            used[face] = true;
            range[face * 4] = uMin;
            range[face * 4 + 1] = uMax;
            range[face * 4 + 2] = vMin;
            range[face * 4 + 3] = vMax;
        }

        if (slow)
            ScanWindows(range, used, ox, oy, oz, s);

        scratch.WindowCount = 0;
        var heights = 0;
        var inv = 0;
        var axis = 0;

        for (var face = 0; face < CubeLattice.Faces; face++)
        {
            if (!used[face])
                continue;

            if (scratch.WindowCount == PlanetChunkScratch.MaxFaces)
                throw new InvalidOperationException("Chunk reaches more than three cube faces");

            ref var w = ref scratch.Windows[scratch.WindowCount++];
            w.Face = face;
            w.I0 = Math.Max(Cell(range[face * 4], n) - 1, 0);
            w.I1 = Math.Min(Cell(range[face * 4 + 1], n) + 2, n);
            w.J0 = Math.Max(Cell(range[face * 4 + 2], n) - 1, 0);
            w.J1 = Math.Min(Cell(range[face * 4 + 3], n) + 2, n);
            w.HI0 = Math.Max(w.I0 - 1, 0);
            w.HJ0 = Math.Max(w.J0 - 1, 0);
            w.HW = Math.Min(w.I1 + 1, n) - w.HI0 + 1;
            w.HH = Math.Min(w.J1 + 1, n) - w.HJ0 + 1;
            w.CI0 = w.HI0 / CoarseStep;
            w.CJ0 = w.HJ0 / CoarseStep;
            w.CW = (w.HI0 + w.HW - 1 + CoarseStep - 1) / CoarseStep - w.CI0 + 1;
            w.CH = (w.HJ0 + w.HH - 1 + CoarseStep - 1) / CoarseStep - w.CJ0 + 1;
            w.HBase = heights;
            w.InvBase = inv;
            w.AxisBase = axis;
            heights += w.HW * w.HH;
            inv += (w.I1 - w.I0 + 1) * (w.J1 - w.J0 + 1);
            axis += w.HW + w.HH;
        }

        PlanetChunkScratch.Ensure(ref scratch.Heights, heights);
        PlanetChunkScratch.Ensure(ref scratch.Inv, inv);
        PlanetChunkScratch.Ensure(ref scratch.AxisG, axis);
        PlanetChunkScratch.Ensure(ref scratch.AxisSlope, axis);
    }

    private static void ScanWindows(Span<double> range, Span<bool> used, double ox, double oy, double oz, double s)
    {
        for (var face = 0; face < CubeLattice.Faces; face++)
        {
            used[face] = false;
            range[face * 4] = range[face * 4 + 2] = double.MaxValue;
            range[face * 4 + 1] = range[face * 4 + 3] = double.MinValue;
        }

        for (var z = 0; z < P; z++)
        for (var y = 0; y < P; y++)
        for (var x = 0; x < P; x++)
        {
            var face = CubeLattice.Face(ox + (x - 1) * s, oy + (y - 1) * s, oz + (z - 1) * s, out var gu, out var gv);
            var tu = CubeLattice.ToT(gu);
            var tv = CubeLattice.ToT(gv);
            used[face] = true;
            range[face * 4] = Math.Min(range[face * 4], tu);
            range[face * 4 + 1] = Math.Max(range[face * 4 + 1], tu);
            range[face * 4 + 2] = Math.Min(range[face * 4 + 2], tv);
            range[face * 4 + 3] = Math.Max(range[face * 4 + 3], tv);
        }
    }

    private static double MinAbs(double lo, double hi) => lo > 0.0 ? lo : hi < 0.0 ? -hi : 0.0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cell(double t, int n) => Math.Min((int)((Math.Clamp(t, -1.0, 1.0) + 1.0) * (n * 0.5)), n - 1);

    /// <summary>Lattice t of point i on a face of n cells.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double LatticeT(int i, int n) => -1.0 + 2.0 * i / n;

    /// <summary>
    /// Runs the kind's program over each window's lattice points: the coarse grid (every 8th point) first, then every
    /// point, a row of channels at a time; then the slope normalisation from central differences of the heights.
    /// </summary>
    private void EvaluateWindows(PlanetChunkScratch scratch, int lod, int n, out float hMin, out float hMax, out float invLow)
    {
        var program = Surface.Program;
        var metric = _radius * 2.0 / n;
        var heights = scratch.Heights;
        var invs = scratch.Inv;
        var axisG = scratch.AxisG;
        var axisSlope = scratch.AxisSlope;
        hMin = float.MaxValue;
        hMax = float.MinValue;
        invLow = 1f;

        for (var wi = 0; wi < scratch.WindowCount; wi++)
        {
            ref readonly var w = ref scratch.Windows[wi];
            var points = w.HW * w.HH;
            var coarsePoints = w.CW * w.CH;
            scratch.EnsureRows(program, points, coarsePoints);

            for (var i = 0; i < w.HW; i++)
            {
                var t = LatticeT(w.HI0 + i, n);
                axisG[w.AxisBase + i] = CubeLattice.FromT(t);
                axisSlope[w.AxisBase + i] = CubeLattice.Slope(t);
            }

            for (var j = 0; j < w.HH; j++)
            {
                var t = LatticeT(w.HJ0 + j, n);
                axisG[w.AxisBase + w.HW + j] = CubeLattice.FromT(t);
                axisSlope[w.AxisBase + w.HW + j] = CubeLattice.Slope(t);
            }

            for (var cj = 0; cj < w.CH; cj++)
            {
                var gv = CubeLattice.FromT(LatticeT((w.CJ0 + cj) * CoarseStep, n));

                for (var ci = 0; ci < w.CW; ci++)
                {
                    var gu = CubeLattice.FromT(LatticeT((w.CI0 + ci) * CoarseStep, n));
                    CubeLattice.Direction(w.Face, gu, gv, out var dx, out var dy, out var dz);
                    var k = ci + cj * w.CW;
                    scratch.CoarseX[k] = dx * _radius;
                    scratch.CoarseY[k] = dy * _radius;
                    scratch.CoarseZ[k] = dz * _radius;
                }
            }

            var coarse = new RowSet
            {
                X = scratch.CoarseX.AsSpan(0, coarsePoints),
                Y = scratch.CoarseY.AsSpan(0, coarsePoints),
                Z = scratch.CoarseZ.AsSpan(0, coarsePoints),
                Ch = scratch.CoarseRows,
                Slots = scratch.Slots,
                Count = coarsePoints,
                Stride = coarsePoints,
                Channels = program.ChannelCount,
                W = w.CW,
                H = w.CH,
                SlotStride = coarsePoints,
                Direct = true,
            };

            LayerOps.Coarse(program, lod, ref coarse);

            for (var j = 0; j < w.HH; j++)
            {
                var gv = axisG[w.AxisBase + w.HW + j];

                for (var i = 0; i < w.HW; i++)
                {
                    CubeLattice.Direction(w.Face, axisG[w.AxisBase + i], gv, out var dx, out var dy, out var dz);
                    var k = i + j * w.HW;
                    scratch.PointX[k] = dx * _radius;
                    scratch.PointY[k] = dy * _radius;
                    scratch.PointZ[k] = dz * _radius;
                }
            }

            for (var k = 0; k < w.HW + w.HH; k++)
            {
                var column = k < w.HW;
                var lattice = column ? w.HI0 + k : w.HJ0 + k - w.HW;
                var cell = lattice / CoarseStep - (column ? w.CI0 : w.CJ0);
                scratch.CoarseA[k] = cell;
                scratch.CoarseB[k] = Math.Min(cell + 1, (column ? w.CW : w.CH) - 1);
                scratch.CoarseF[k] = (lattice % CoarseStep) * (1f / CoarseStep);
            }

            var rows = new RowSet
            {
                X = scratch.PointX.AsSpan(0, points),
                Y = scratch.PointY.AsSpan(0, points),
                Z = scratch.PointZ.AsSpan(0, points),
                Ch = scratch.Rows,
                Slots = scratch.Slots,
                Count = points,
                Stride = points,
                Channels = program.ChannelCount,
                W = w.HW,
                H = w.HH,
                I0 = w.HI0,
                J0 = w.HJ0,
                CW = w.CW,
                CH = w.CH,
                CI0 = w.CI0,
                CJ0 = w.CJ0,
                SlotStride = coarsePoints,
                ColA = scratch.CoarseA.AsSpan(0, w.HW),
                ColB = scratch.CoarseB.AsSpan(0, w.HW),
                ColF = scratch.CoarseF.AsSpan(0, w.HW),
                RowA = scratch.CoarseA.AsSpan(w.HW, w.HH),
                RowB = scratch.CoarseB.AsSpan(w.HW, w.HH),
                RowF = scratch.CoarseF.AsSpan(w.HW, w.HH),
                Scratch = scratch,
                Face = w.Face,
                N = n,
            };

            rows.Row(PlanetKindProgram.Height).Clear();
            LayerOps.Evaluate(program, lod, ref rows);
            rows.Row(PlanetKindProgram.Height).CopyTo(heights.AsSpan(w.HBase, points));

            var vw = w.I1 - w.I0 + 1;

            for (var j = w.J0; j <= w.J1; j++)
            {
                var hj = j - w.HJ0;
                var gv = axisG[w.AxisBase + w.HW + hj];
                var slopeV = axisSlope[w.AxisBase + w.HW + hj];
                var jm = j > 0 ? hj - 1 : hj;
                var jp = j < n ? hj + 1 : hj;

                for (var i = w.I0; i <= w.I1; i++)
                {
                    var hi = i - w.HI0;
                    var gu = axisG[w.AxisBase + hi];
                    var im = i > 0 ? hi - 1 : hi;
                    var ip = i < n ? hi + 1 : hi;
                    var row = w.HBase + hj * w.HW;
                    var h = heights[row + hi];
                    var du = (heights[row + ip] - heights[row + im]) / ((ip - im) * metric * CubeLattice.ArcPerG(gu, gv) * axisSlope[w.AxisBase + hi]);
                    var dv = (heights[w.HBase + jp * w.HW + hi] - heights[w.HBase + jm * w.HW + hi]) / ((jp - jm) * metric * CubeLattice.ArcPerG(gv, gu) * slopeV);
                    var inv = MathF.Max((float)(1.0 / Math.Sqrt(1.0 + du * du + dv * dv)), InvMin);
                    invs[w.InvBase + (i - w.I0) + (j - w.J0) * vw] = inv;
                    invLow = MathF.Min(invLow, inv);
                    hMin = MathF.Min(hMin, h);
                    hMax = MathF.Max(hMax, h);
                }
            }
        }
    }

    [SkipLocalsInit]
    private unsafe void Fill(VoxelChunk chunk, PlanetChunkScratch scratch, int n, double ox, double oy, double oz, double s, float solidBelow, float airAbove, bool withMask)
    {
        Span<int> faceSlot = stackalloc int[CubeLattice.Faces];
        faceSlot.Fill(-1);

        for (var wi = 0; wi < scratch.WindowCount; wi++)
            faceSlot[scratch.Windows[wi].Face] = wi;

        var heights = scratch.Heights;
        var invs = scratch.Inv;
        var windows = scratch.Windows;
        var mask = scratch.Protected;
        var seaColumns = scratch.SeaColumns;
        var materials = Params.Materials;
        var rock = (byte)materials.Rock;
        var quant = ChunkConstants.DensityScale / (float)s;
        var perVoxel = 1f / (float)s;
        var halfN = n * 0.5;
        var hasSea = Surface.HasSea;
        var protect = Surface.ProtectHeight;
        var sea = Surface.SeaHeight;
        var shore = sea + BeachHeight;
        var dig = Params.DigDepth;
        var protectedCount = 0;
        var flags = 0;

        if (withMask)
            mask.AsSpan().Clear();

        seaColumns.AsSpan().Clear();

        fixed (sbyte* density = chunk.Density)
        fixed (byte* material = chunk.Material)
        {
            for (var z = 0; z < P; z++)
            {
                var pz = oz + (z - 1) * s;

                for (var y = 0; y < P; y++)
                {
                    var py = oy + (y - 1) * s;
                    var vi = z * PP + y * P;

                    for (var x = 0; x < P; x++, vi++)
                    {
                        var px = ox + (x - 1) * s;
                        var w = (float)(Math.Sqrt(px * px + py * py + pz * pz) - _radius);

                        if (!withMask)
                        {
                            if (w > airAbove)
                            {
                                density[vi] = sbyte.MaxValue;
                                material[vi] = (byte)VoxelMaterial.Air;
                                flags |= 2;
                                continue;
                            }

                            if (w < solidBelow)
                            {
                                density[vi] = -sbyte.MaxValue;
                                material[vi] = rock;
                                flags |= 1;
                                continue;
                            }
                        }

                        var face = CubeLattice.Face(px, py, pz, out var gu, out var gv);
                        ref readonly var win = ref windows[faceSlot[face]];
                        var giu = (CubeLattice.ToT(gu) + 1.0) * halfN;
                        var giv = (CubeLattice.ToT(gv) + 1.0) * halfN;
                        var iu = Math.Min((int)giu, n - 1);
                        var iv = Math.Min((int)giv, n - 1);
                        var fu = (float)(giu - iu);
                        var fv = (float)(giv - iv);
                        var hb = win.HBase + (iu - win.HI0) + (iv - win.HJ0) * win.HW;
                        var h = Bilinear(heights[hb], heights[hb + 1], heights[hb + win.HW], heights[hb + win.HW + 1], fu, fv);
                        var vw = win.I1 - win.I0 + 1;
                        var ib = win.InvBase + (iu - win.I0) + (iv - win.J0) * vw;
                        var inv = Bilinear(invs[ib], invs[ib + 1], invs[ib + vw], invs[ib + vw + 1], fu, fv);

                        if (hasSea && h < sea)
                            seaColumns[vi >> 6] |= 1UL << vi;

                        if (withMask && ((hasSea && h <= protect) || w < h - dig))
                        {
                            mask[vi >> 6] |= 1UL << vi;
                            protectedCount++;
                        }

                        var dist = (w - h) * inv;
                        var q = Math.Clamp((int)MathF.Round(dist * quant), -sbyte.MaxValue, sbyte.MaxValue);
                        density[vi] = (sbyte)q;

                        if (q >= 0)
                        {
                            material[vi] = (byte)VoxelMaterial.Air;
                            flags |= 2;
                            continue;
                        }

                        flags |= 1;
                        material[vi] = (byte)PickMaterial(-dist * perVoxel, h, inv < SteepInv, hasSea && h <= shore, materials);
                    }
                }
            }
        }

        scratch.ProtectedCount = protectedCount;
        chunk.HasSurface = flags == 3;
        chunk.Version++;
    }

    /// <param name="depth">Below the surface in voxels: the layers are metres at LOD 0 and stay as many voxels thick at coarser LODs, so a coarse leaf shows the surface material, as the impostor does.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private VoxelMaterial PickMaterial(float depth, float height, bool steep, bool shore, in PlanetMaterials m)
    {
        if (depth >= SubSurfaceLayer)
            return m.Rock;

        if (shore)
            return m.Shore;

        if (steep || height > _peak)
            return m.Rock;

        return depth < SurfaceLayer ? m.Surface : m.Subsurface;
    }

    /// <summary>a(1−f) + b·f form: commutes bit for bit when the corners are read in the other order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Bilinear(float h00, float h10, float h01, float h11, float fu, float fv)
    {
        var gu = 1f - fu;
        return (h00 * gu + h10 * fu) * (1f - fv) + (h01 * gu + h11 * fu) * fv;
    }
}
