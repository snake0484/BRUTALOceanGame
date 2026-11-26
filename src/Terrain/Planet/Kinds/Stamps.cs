using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>One seeded stamp that can reach a chunk: centre on the datum sphere (planet frame, m) and its resolved shape.</summary>
internal struct Stamp
{
    public double Cx, Cy, Cz;
    public double Ax, Ay, Az;
    public double Reach2;
    public float Radius, Depth, Rim, ReachX, Benches, Wavelength, Height;
    public int Class;
    public bool Complex;
}

/// <summary>
/// Seeded stamps (planet-kinds.md §1.4): crater size classes and ergs. Each class owns a cube-sphere grid in one of 16
/// fixed rotations with one jittered candidate per cell; a stamp's whole reach stays inside its cell, so a point meets
/// at most one stamp per class and same-class stamps never overlap. Distances are chords between datum points in
/// double, no trig; a stamp is exactly 0 from its reach outward. Classes apply large to small and add up, so a crater on
/// a basin floor digs deeper; the sum then passes a knee that leaves any single crater alone and eases nested ones into
/// 1.5 × the deepest class (and rims likewise), which bounds the layer by that, not by the sum of every class. The bowls'
/// coverage is exported as the <c>fresh</c> mask (1 on old ground, 0 on a crater floor).
/// </summary>
internal static class Stamps
{
    public const int Rotations = 16;
    public const float CellRadii = 8f;
    public const float CraterReach = 2f;
    public const float SmallReach = 1.5f;
    public const float SimpleDepth = 0.4f;
    public const float RimShare = 0.27f;
    public const float WindwardShare = 0.786f;

    private const float FloorShare = 0.55f;
    private const float PeakShare = 0.2f;
    private const float PeakHeight = 0.3f;
    private const float ErgCore = 0.6f;
    private const int BoundsBuffer = 48;

    /// <summary>Smallest arc (rad) per unit of lattice t over a face: a class cell is never narrower than this × 2 / N.</summary>
    public static readonly double MinArcPerT = MinArcRate();

    private static readonly double[] Rotation = BuildRotations();

    public static void Evaluate(PlanetKindProgram p, int layer, int lod, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        int first = (int)p.Params[pb], count = (int)p.Params[pb + 1];
        var acc = r.Row(p.Dst[layer]);
        var fresh = p.SrcB[layer] == PlanetKindProgram.None ? default : r.Row(p.SrcB[layer]);
        var weights = p.ClassWeight.AsSpan(lod * p.Classes.Length * 2);
        acc.Clear();
        fresh.Fill(1f);

        if (r.Direct || r.Scratch is null)
        {
            var one = 1f;

            for (var i = 0; i < acc.Length; i++)
            {
                ref var f = ref fresh.IsEmpty ? ref one : ref fresh[i];

                for (var k = first; k < first + count; k++)
                {
                    if (weights[2 * k] == 0f && p.Classes[k].Kind == LayerOp.Craters)
                        continue;

                    if (Own(p, k, r.X[i], r.Y[i], r.Z[i], out var stamp))
                        Apply(p, in stamp, r.X[i], r.Y[i], r.Z[i], weights, ref acc[i], ref f);
                }
            }

            Knee(p, layer, acc);
            return;
        }

        var scratch = r.Scratch;
        Cap(ref r, out var cx, out var cy, out var cz, out var rho);
        scratch.StampCount = 0;

        for (var k = first; k < first + count; k++)
        {
            if (weights[2 * k] == 0f && p.Classes[k].Kind == LayerOp.Craters)
                continue;

            int n;

            while ((n = Collect(p, k, cx, cy, cz, rho, scratch.Stamps.AsSpan(scratch.StampCount))) < 0)
                Array.Resize(ref scratch.Stamps, scratch.Stamps.Length * 2);

            scratch.StampCount += n;
        }

        var cellsPerMetre = r.N * 0.5 / (MinArcPerT * p.Radius);
        var one2 = 1f;

        for (var s = 0; s < scratch.StampCount; s++)
        {
            ref readonly var stamp = ref scratch.Stamps[s];
            int i0 = 0, i1 = r.W - 1, j0 = 0, j1 = r.H - 1;
            var face = CubeLattice.Face(stamp.Cx, stamp.Cy, stamp.Cz, out var gu, out var gv);

            // A stamp centred on the window's own face reaches no further than its radius over the narrowest cell.
            if (face == r.Face)
            {
                var extent = Math.Sqrt(stamp.Reach2) * cellsPerMetre + 2.0;
                var iu = (CubeLattice.ToT(gu) + 1.0) * (r.N * 0.5) - r.I0;
                var iv = (CubeLattice.ToT(gv) + 1.0) * (r.N * 0.5) - r.J0;
                i0 = (int)Math.Max(iu - extent, 0.0);
                i1 = (int)Math.Min(iu + extent, r.W - 1.0);
                j0 = (int)Math.Max(iv - extent, 0.0);
                j1 = (int)Math.Min(iv + extent, r.H - 1.0);
            }

            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    var k = i + j * r.W;
                    ref var f = ref fresh.IsEmpty ? ref one2 : ref fresh[k];
                    Apply(p, in stamp, r.X[k], r.Y[k], r.Z[k], weights, ref acc[k], ref f);
                }
            }
        }

        Knee(p, layer, acc);
    }

    /// <summary>Crater layer params: first class, class count, then the knee (8).</summary>
    public const int CraterParams = 10;

    private static void Knee(PlanetKindProgram p, int layer, Span<float> acc)
    {
        if (p.Op[layer] != LayerOp.Craters)
            return;

        ReadOnlySpan<float> knee = p.Params.AsSpan(p.ParamBase[layer] + 2, 8);

        for (var i = 0; i < acc.Length; i++)
            acc[i] = LayerOps.KneeAt(knee, acc[i]);
    }

    /// <summary>
    /// Bounds over the cap for every LOD in range: each class active in it adds [−deepest, +highest rim] (ergs
    /// [0, tallest]) of the stamps in reach, or of the class when the cap spans many cells; nothing where none can reach.
    /// The crater knee is monotone, so it maps the summed interval.
    /// </summary>
    public static void Bounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double rho, int minLod, int maxLod, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        int first = (int)p.Params[pb], count = (int)p.Params[pb + 1];
        var dst = p.Dst[layer];
        var weights = p.ClassWeight.AsSpan(minLod * p.Classes.Length * 2);
        float accLo = 0f, accHi = 0f;
        Span<Stamp> buffer = stackalloc Stamp[BoundsBuffer];
        var inv = 1.0 / Math.Sqrt(qx * qx + qy * qy + qz * qz);
        double cx = qx * inv, cy = qy * inv, cz = qz * inv;

        for (var k = first; k < first + count; k++)
        {
            ref readonly var cls = ref p.Classes[k];
            var crater = cls.Kind == LayerOp.Craters;

            if (crater && weights[2 * k] == 0f)
                continue;

            float deepest = cls.DepthMax, highest = cls.RimMax;
            var cell = MinArcPerT * 2.0 / cls.CellsPerSide * p.Radius;
            var n = rho <= 0.5 * cell ? Collect(p, k, cx, cy, cz, rho, buffer) : -1;

            if (n >= 0)
            {
                deepest = highest = 0f;
                var any = false;

                for (var s = 0; s < n; s++)
                {
                    ref readonly var stamp = ref buffer[s];
                    double dx = qx - stamp.Cx, dy = qy - stamp.Cy, dz = qz - stamp.Cz;
                    var reach = Math.Sqrt(stamp.Reach2) + rho;

                    if (dx * dx + dy * dy + dz * dz >= reach * reach)
                        continue;

                    any = true;
                    deepest = MathF.Max(deepest, stamp.Depth);
                    highest = MathF.Max(highest, crater ? stamp.Rim : stamp.Height);
                }

                if (!any)
                    continue;
            }

            accLo -= deepest;
            accHi += highest;
        }

        if (p.Op[layer] == LayerOp.Craters)
        {
            ReadOnlySpan<float> knee = p.Params.AsSpan(pb + 2, 8);
            accLo = LayerOps.KneeAt(knee, accLo);
            accHi = LayerOps.KneeAt(knee, accHi);
        }

        lo[dst] = accLo;
        hi[dst] = accHi;

        if (p.SrcB[layer] != PlanetKindProgram.None)
        {
            lo[p.SrcB[layer]] = 0f;
            hi[p.SrcB[layer]] = 1f;
        }
    }

    /// <summary>The window's bounding cap: unit centre and chord radius (m) over its four corners, the farthest points of a lattice rectangle.</summary>
    private static void Cap(ref RowSet r, out double cx, out double cy, out double cz, out double rho)
    {
        Span<int> corners = [0, r.W - 1, (r.H - 1) * r.W, r.H * r.W - 1];
        double sx = 0.0, sy = 0.0, sz = 0.0;

        foreach (var k in corners)
        {
            sx += r.X[k];
            sy += r.Y[k];
            sz += r.Z[k];
        }

        var inv = 1.0 / Math.Sqrt(sx * sx + sy * sy + sz * sz);
        (cx, cy, cz) = (sx * inv, sy * inv, sz * inv);
        var radius = Math.Sqrt(r.X[0] * r.X[0] + r.Y[0] * r.Y[0] + r.Z[0] * r.Z[0]);
        var far = 0.0;

        foreach (var k in corners)
        {
            double dx = r.X[k] - cx * radius, dy = r.Y[k] - cy * radius, dz = r.Z[k] - cz * radius;
            far = Math.Max(far, dx * dx + dy * dy + dz * dz);
        }

        rho = Math.Sqrt(far) * 1.001 + 1e-3;
    }

    /// <summary>
    /// The class's present stamps whose cells the ball of chord radius <paramref name="rho"/> (m) about unit centre c
    /// can touch, into <paramref name="list"/>; −1 if it does not fit. Conservative: the ball's box in the class's
    /// rotated frame, centrally projected onto each face of its cube.
    /// </summary>
    private static int Collect(PlanetKindProgram p, int k, double cx, double cy, double cz, double rho, Span<Stamp> list)
    {
        ref readonly var cls = ref p.Classes[k];
        ReadOnlySpan<double> m = Rotation.AsSpan(cls.Rotation * 9, 9);
        Span<double> c = [m[0] * cx + m[1] * cy + m[2] * cz, m[3] * cx + m[4] * cy + m[5] * cz, m[6] * cx + m[7] * cy + m[8] * cz];
        var reach = rho / p.Radius;
        var n = cls.CellsPerSide;
        var count = 0;

        // A cap whose reach in lattice t stays inside the centre's face touches only that face's cells around it.
        var own = CubeLattice.Face(c[0], c[1], c[2], out var cu, out var cv);
        var span = reach / MinArcPerT * 1.05 + 1e-9;
        double tu0 = CubeLattice.ToT(cu), tv0 = CubeLattice.ToT(cv);

        if (tu0 - span > -1.0 && tu0 + span < 1.0 && tv0 - span > -1.0 && tv0 + span < 1.0)
        {
            int a0 = Cell(tu0 - span, n), a1 = Cell(tu0 + span, n), b0 = Cell(tv0 - span, n), b1 = Cell(tv0 + span, n);

            for (var j = b0; j <= b1; j++)
            {
                for (var i = a0; i <= a1; i++)
                {
                    if (!Candidate(p, k, own, i, j, out var stamp))
                        continue;

                    if (count == list.Length)
                        return -1;

                    list[count++] = stamp;
                }
            }

            return count;
        }

        for (var face = 0; face < CubeLattice.Faces; face++)
        {
            var a = face >> 1;
            var b = (a + 1) % 3;
            var d = (a + 2) % 3;
            var sign = (face & 1) == 0 ? 1.0 : -1.0;
            var majorLo = sign * c[a] - reach;
            var majorHi = sign * c[a] + reach;

            // A face's directions have a major component of at least 1/√3.
            if (majorHi < 0.57)
                continue;

            int i0 = 0, i1 = n - 1, j0 = 0, j1 = n - 1;

            if (majorLo > 1e-6)
            {
                double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;

                for (var q = 0; q < 8; q++)
                {
                    var major = (q & 1) != 0 ? majorHi : majorLo;
                    var tu = CubeLattice.ToT((c[b] + ((q & 2) != 0 ? reach : -reach)) / major);
                    var tv = CubeLattice.ToT((c[d] + ((q & 4) != 0 ? reach : -reach)) / major);
                    uMin = Math.Min(uMin, tu);
                    uMax = Math.Max(uMax, tu);
                    vMin = Math.Min(vMin, tv);
                    vMax = Math.Max(vMax, tv);
                }

                if (uMax < -1.0 || uMin > 1.0 || vMax < -1.0 || vMin > 1.0)
                    continue;

                i0 = Math.Max(Cell(uMin, n) - 1, 0);
                i1 = Math.Min(Cell(uMax, n) + 1, n - 1);
                j0 = Math.Max(Cell(vMin, n) - 1, 0);
                j1 = Math.Min(Cell(vMax, n) + 1, n - 1);
            }

            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++)
                {
                    if (!Candidate(p, k, face, i, j, out var stamp))
                        continue;

                    if (count == list.Length)
                        return -1;

                    list[count++] = stamp;
                }
            }
        }

        return count;
    }

    /// <summary>The class's stamp in the cell holding datum point q, if it has one.</summary>
    private static bool Own(PlanetKindProgram p, int k, double qx, double qy, double qz, out Stamp stamp)
    {
        ref readonly var cls = ref p.Classes[k];
        ReadOnlySpan<double> m = Rotation.AsSpan(cls.Rotation * 9, 9);
        var face = CubeLattice.Face(m[0] * qx + m[1] * qy + m[2] * qz, m[3] * qx + m[4] * qy + m[5] * qz, m[6] * qx + m[7] * qy + m[8] * qz, out var gu, out var gv);
        return Candidate(p, k, face, Cell(CubeLattice.ToT(gu), cls.CellsPerSide), Cell(CubeLattice.ToT(gv), cls.CellsPerSide), out stamp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Cell(double t, int n) => Math.Min((int)((Math.Clamp(t, -1.0, 1.0) + 1.0) * (n * 0.5)), n - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Unit(uint h) => (h >> 8) * (1f / (1 << 24));

    /// <summary>The candidate of one class cell: present by the class's probability, centre jittered so its reach stays in the cell.</summary>
    private static bool Candidate(PlanetKindProgram p, int k, int face, int i, int j, out Stamp stamp)
    {
        ref readonly var cls = ref p.Classes[k];
        var h = Noise.Mix(cls.Seed ^ ((uint)face * 0x9E3779B9u) ^ ((uint)i * 0x8DA6B343u) ^ ((uint)j * 0xD8163841u));
        stamp = default;

        if (Unit(h) >= cls.Presence)
            return false;

        var h1 = Noise.Mix(h ^ 0x68E31DA4u);
        var h2 = Noise.Mix(h ^ 0xB5297A4Du);
        var h3 = Noise.Mix(h ^ 0x1B56C4E9u);
        var h4 = Noise.Mix(h ^ 0x7F4A7C15u);
        var n = cls.CellsPerSide;
        var tu = -1.0 + 2.0 * (i + 0.5 + (Unit(h1) * 2f - 1f) * cls.Jitter) / n;
        var tv = -1.0 + 2.0 * (j + 0.5 + (Unit(h2) * 2f - 1f) * cls.Jitter) / n;
        CubeLattice.Direction(face, CubeLattice.FromT(tu), CubeLattice.FromT(tv), out var x, out var y, out var z);
        ReadOnlySpan<double> m = Rotation.AsSpan(cls.Rotation * 9, 9);
        double ux = m[0] * x + m[3] * y + m[6] * z, uy = m[1] * x + m[4] * y + m[7] * z, uz = m[2] * x + m[5] * y + m[8] * z;
        var radius = cls.RadiusMin + (cls.RadiusMax - cls.RadiusMin) * Unit(h3);
        var reach = (double)cls.Reach * radius;
        stamp.Cx = ux * p.Radius;
        stamp.Cy = uy * p.Radius;
        stamp.Cz = uz * p.Radius;
        stamp.Radius = radius;
        stamp.ReachX = cls.Reach;
        stamp.Reach2 = reach * reach;
        stamp.Class = k;

        if (cls.Kind == LayerOp.Craters)
        {
            var age = cls.AgeMin + (cls.AgeMax - cls.AgeMin) * Unit(h4);
            var fresh = CraterDepth(radius, cls.ComplexDiameter);
            stamp.Complex = 2f * radius > cls.ComplexDiameter;
            stamp.Depth = fresh * (1f - 0.7f * age);
            stamp.Rim = RimShare * fresh * (1f - 0.9f * age);
            stamp.Benches = 2f + (h4 & 1);
            return true;
        }

        // Wind axis: the zonal direction at the erg's latitude, swung up to ± 30° (tan 30° of the meridional one).
        double zx = p.AxisY * uz - p.AxisZ * uy, zy = p.AxisZ * ux - p.AxisX * uz, zz = p.AxisX * uy - p.AxisY * ux;
        var zl = Math.Sqrt(zx * zx + zy * zy + zz * zz);

        if (zl < 1e-6)
            (zx, zy, zz, zl) = (uy, -ux, 0.0, Math.Sqrt(ux * ux + uy * uy) + 1e-9);

        (zx, zy, zz) = (zx / zl, zy / zl, zz / zl);
        double nx = uy * zz - uz * zy, ny = uz * zx - ux * zz, nz = ux * zy - uy * zx;
        var swing = (Unit(h4) * 2.0 - 1.0) * 0.577;
        double ax = zx + swing * nx, ay = zy + swing * ny, az = zz + swing * nz;
        var al = Math.Sqrt(ax * ax + ay * ay + az * az);
        stamp.Ax = ax / al;
        stamp.Ay = ay / al;
        stamp.Az = az / al;
        stamp.Wavelength = cls.LambdaMin + (cls.LambdaMax - cls.LambdaMin) * Unit(Noise.Mix(h ^ 0x51ED270Bu));
        stamp.Height = stamp.Wavelength * (cls.RatioMin + (cls.RatioMax - cls.RatioMin) * Unit(Noise.Mix(h ^ 0x2545F491u)));
        return true;
    }

    /// <summary>Fresh depth: 0.4 r for a simple bowl; past the complex diameter it grows as the fourth root of the diameter.</summary>
    public static float CraterDepth(float radius, float complexDiameter)
    {
        var diameter = 2f * radius;
        return diameter > complexDiameter ? 0.5f * SimpleDepth * complexDiameter * MathF.Sqrt(MathF.Sqrt(diameter / complexDiameter)) : SimpleDepth * radius;
    }

    private static void Apply(PlanetKindProgram p, in Stamp s, double qx, double qy, double qz, ReadOnlySpan<float> weights, ref float acc, ref float fresh)
    {
        double dx = qx - s.Cx, dy = qy - s.Cy, dz = qz - s.Cz;
        var d2 = dx * dx + dy * dy + dz * dz;

        if (d2 >= s.Reach2)
            return;

        var x = (float)Math.Sqrt(d2) / s.Radius;
        var all = weights[2 * s.Class];

        if (p.Classes[s.Class].Kind == LayerOp.Ergs)
        {
            var t = (dx * s.Ax + dy * s.Ay + dz * s.Az) / s.Wavelength;
            var phase = (float)(t - Math.Floor(t));
            var dune = phase < WindwardShare ? phase / WindwardShare : (1f - phase) / (1f - WindwardShare);
            acc += s.Height * (1f - LayerOps.Smooth01((x - ErgCore) / (1f - ErgCore))) * (all * dune + (1f - all) * 0.5f);
            return;
        }

        var rim = s.Rim * weights[2 * s.Class + 1];
        float profile;

        if (x > 1f)
        {
            profile = rim / (x * x * x) * (1f - LayerOps.Smooth01((x - 1f) / (s.ReachX - 1f)));
        }
        else if (!s.Complex)
        {
            profile = (rim + s.Depth) * x * x - s.Depth;
        }
        else if (x < FloorShare)
        {
            var peak = MathF.Max(1f - x * x / (PeakShare * PeakShare), 0f);
            profile = -s.Depth + PeakHeight * s.Depth * peak * peak;
        }
        else
        {
            profile = -s.Depth + (s.Depth + rim) * (1f - LayerOps.CanyonProfile((x - FloorShare) / (1f - FloorShare), 0f, s.Benches, 0.85f));
        }

        acc += profile * all;
        fresh *= 1f - (1f - LayerOps.Smooth01((x - 0.7f) / 0.5f)) * all;
    }

    /// <summary>16 fixed rotations of the class cubes: orthonormal rows from small integer triples by Gram–Schmidt (sqrt and divide only).</summary>
    private static double[] BuildRotations()
    {
        var table = new double[Rotations * 9];
        table[0] = table[4] = table[8] = 1.0;
        var state = 0x9E3779B97F4A7C15UL;

        for (var r = 1; r < Rotations; r++)
        {
            Span<double> a = stackalloc double[3], b = stackalloc double[3];

            while (true)
            {
                for (var i = 0; i < 3; i++)
                {
                    state = PlanetKindCompiler.SplitMix(state);
                    a[i] = (long)(state % 15) - 7;
                    state = PlanetKindCompiler.SplitMix(state);
                    b[i] = (long)(state % 15) - 7;
                }

                var al = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);

                if (al < 1.0)
                    continue;

                for (var i = 0; i < 3; i++)
                    a[i] /= al;

                var dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

                for (var i = 0; i < 3; i++)
                    b[i] -= dot * a[i];

                var bl = Math.Sqrt(b[0] * b[0] + b[1] * b[1] + b[2] * b[2]);

                if (bl < 0.5)
                    continue;

                for (var i = 0; i < 3; i++)
                    b[i] /= bl;

                break;
            }

            var m = table.AsSpan(r * 9, 9);
            a.CopyTo(m);
            b.CopyTo(m[3..]);
            m[6] = a[1] * b[2] - a[2] * b[1];
            m[7] = a[2] * b[0] - a[0] * b[2];
            m[8] = a[0] * b[1] - a[1] * b[0];
        }

        return table;
    }

    private static double MinArcRate()
    {
        const int steps = 256;
        var min = double.MaxValue;

        for (var j = 0; j <= steps; j++)
        {
            var gv = CubeLattice.FromT(-1.0 + 2.0 * j / steps);

            for (var i = 0; i <= steps; i++)
            {
                var t = -1.0 + 2.0 * i / steps;
                min = Math.Min(min, CubeLattice.ArcPerG(CubeLattice.FromT(t), gv) * CubeLattice.Slope(t));
            }
        }

        return min * 0.99;
    }
}
