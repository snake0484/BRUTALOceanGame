using System.Diagnostics;
using BrutalGame.Terrain;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships.Aero;

/// <summary>
/// A rigid part's air load against the direction of the airflow: per direction (5 degree steps, poles on the part's X
/// axis), the force over dynamic pressure (m²) and the moment about <see cref="Reference"/> over dynamic pressure (m³),
/// both in the part's own frame. Multiply by q = ½ ρ |v|² for newtons and newton-metres. Read with
/// <see cref="Lookup"/> (bilinear), so a part's load per tick is a table read, whatever its shape.
/// </summary>
public sealed class AeroTable
{
    public const int Rows = 37;
    public const int Columns = 72;
    public const double StepRadians = Math.PI / 36;
    private const int Stride = 6;

    public readonly float[] Entries = new float[Rows * Columns * Stride];

    /// <summary>The point moments are taken about (the joint's pivot for a moving part, the ship's origin for its own).</summary>
    public Vector3d Reference;

    public double SurfaceArea;
    public double ExposedArea;
    public int Samples;
    public float BakeMs;

    /// <summary>The flow direction of table row <paramref name="row"/> (angle from +X) and column <paramref name="column"/> (angle about X from +Y towards +Z).</summary>
    public static Vector3d Direction(int row, int column)
    {
        double theta = row * StepRadians, psi = column * StepRadians, s = Math.Sin(theta);
        return new Vector3d(Math.Cos(theta), s * Math.Cos(psi), s * Math.Sin(psi));
    }

    /// <summary>The load for air flowing along unit <paramref name="flow"/> (the part's frame): force and moment over dynamic pressure.</summary>
    public void Lookup(Vector3d flow, out Vector3d force, out Vector3d moment)
    {
        var theta = Math.Acos(Math.Clamp(flow.X, -1, 1));
        var psi = Math.Atan2(flow.Z, flow.Y);

        if (psi < 0)
            psi += 2 * Math.PI;

        var fr = theta / StepRadians;
        var r0 = Math.Min((int)fr, Rows - 2);
        var tr = fr - r0;
        var fc = psi / StepRadians;
        var c0 = (int)fc % Columns;
        var tc = fc - Math.Floor(fc);
        var c1 = (c0 + 1) % Columns;

        Span<double> sum = stackalloc double[Stride];
        Accumulate(sum, r0, c0, (1 - tr) * (1 - tc));
        Accumulate(sum, r0, c1, (1 - tr) * tc);
        Accumulate(sum, r0 + 1, c0, tr * (1 - tc));
        Accumulate(sum, r0 + 1, c1, tr * tc);
        force = new Vector3d(sum[0], sum[1], sum[2]);
        moment = new Vector3d(sum[3], sum[4], sum[5]);
    }

    private void Accumulate(Span<double> sum, int row, int column, double weight)
    {
        var e = Entries.AsSpan((row * Columns + column) * Stride, Stride);

        for (var i = 0; i < Stride; i++)
            sum[i] += e[i] * weight;
    }

    internal void Set(int row, int column, Vector3d force, Vector3d moment)
    {
        var e = Entries.AsSpan((row * Columns + column) * Stride, Stride);
        (e[0], e[1], e[2]) = ((float)force.X, (float)force.Y, (float)force.Z);
        (e[3], e[4], e[5]) = ((float)moment.X, (float)moment.Y, (float)moment.Z);
    }
}

/// <summary>
/// Air loads from shape alone (D10, revised): every exposed face of the drawn surface feels the airflow by its angle to
/// it, so wings are just thin parts and a hull gives body lift. A face turned into the flow takes a pressure
/// (<see cref="WindwardSlope"/> · sin φ); a face turned away takes a suction that grows with the angle until the flow
/// separates at <see cref="StallDegrees"/>, then falls to the <see cref="BaseSuction"/> of a wake; every face drags by
/// skin friction along the flow. A flat plate so lifts about 4 per radian, stalls near 15 degrees with a lift
/// coefficient near 1.1, and broadside drags 1.15. A face shadowed by the part upstream takes no pressure. Only faces
/// that can be seen from outside count (rooms inside a hull do not). Each moving part is baked alone, in its own frame;
/// <see cref="Total"/> turns each by its pose.
/// </summary>
public static class HullAero
{
    public const double WindwardSlope = 0.8;
    public const double SuctionSlope = 3.2;
    public const double StallDegrees = 15;
    public const double StallFadeDegrees = 10;
    public const double BaseSuction = 0.35;
    public const double SkinFriction = 0.005;
    public const int TargetSamples = 4096;

    /// <summary>Closest sample spacing, m: a small part (a door leaf) needs no finer.</summary>
    public const double MinSpacing = 0.15;
    private const int MaxGrid = 256;
    private const double ExposureFacing = 0.05;

    private static readonly double StallRadians = StallDegrees * Math.PI / 180;
    private static readonly double FadeRadians = StallFadeDegrees * Math.PI / 180;

    /// <summary>
    /// Pressure on a face (positive pushes into the face) whose outward normal is turned <paramref name="sinPhi"/> into
    /// the flow (sin of the angle between the flow and the face's plane; negative turns away). A face in a wake (not
    /// <paramref name="lit"/>) takes none on its windward side.
    /// </summary>
    public static double Pressure(double sinPhi, bool lit)
    {
        if (sinPhi >= 0)
            return lit ? WindwardSlope * sinPhi : 0;

        var phi = Math.Asin(Math.Min(-sinPhi, 1));
        var stalled = SuctionSlope * Math.Sin(StallRadians);

        if (phi <= StallRadians)
            return -SuctionSlope * Math.Sin(phi);

        if (phi <= StallRadians + FadeRadians)
            return -(stalled + (BaseSuction - stalled) * (phi - StallRadians) / FadeRadians);

        return -BaseSuction;
    }

    /// <summary>Bakes the table of a part drawn as <paramref name="bricks"/> (its exact surface), moments about <paramref name="reference"/>.</summary>
    public static AeroTable Bake(IReadOnlyList<HullBrick> bricks, Vector3d reference)
    {
        var start = Stopwatch.GetTimestamp();
        var table = new AeroTable { Reference = reference };
        var samples = Sample(bricks, out var spacing, out table.SurfaceArea);
        table.Samples = samples.Count;

        if (samples.Count == 0)
            return table;

        var grid = new Grid(samples, spacing);
        var exposed = Exposed(samples, grid);
        var lit = new bool[exposed.Count];

        for (var row = 0; row < AeroTable.Rows; row++)
        {
            for (var column = 0; column < AeroTable.Columns; column++)
            {
                var u = AeroTable.Direction(row, column);
                grid.Shadows(exposed, u, lit);
                Vector3d force = default, moment = default;

                for (var i = 0; i < exposed.Count; i++)
                {
                    var s = exposed.Items[i];
                    var sinPhi = -Dot(s.N, u);
                    var p = Pressure(sinPhi, lit[i]);
                    var tangent = Sub(u, Scale(s.N, Dot(u, s.N)));
                    var f = Add(Scale(s.N, -p * s.Area), Scale(tangent, SkinFriction * s.Area));
                    force = Add(force, f);
                    moment = Add(moment, Cross(Sub(s.P, reference), f));
                }

                table.Set(row, column, force, moment);
            }
        }

        foreach (var s in exposed.Items.AsSpan(0, exposed.Count))
            table.ExposedArea += s.Area;

        table.BakeMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return table;
    }

    /// <summary>
    /// The whole ship's load for air moving at <paramref name="air"/> (m/s, ship frame; the reverse of the ship's own
    /// velocity through still air) of density <paramref name="density"/>: each part's table read in its own frame and
    /// turned by its pose (rotations row-major, 9 per part). Force in newtons; moment in newton-metres about the origin.
    /// </summary>
    public static void Total(ReadOnlySpan<AeroTable?> parts, ReadOnlySpan<double> rotations, ReadOnlySpan<Vector3d> translations, Vector3d air, double density,
        out Vector3d force, out Vector3d moment)
    {
        (force, moment) = (default, default);
        var speed = air.Length();

        if (speed < 1e-9)
            return;

        var q = 0.5 * density * speed * speed;
        var u = Scale(air, 1 / speed);

        for (var g = 0; g < parts.Length && g < translations.Length; g++)
        {
            if (parts[g] is not { } table)
                continue;

            var r = rotations.Slice(g * 9, 9);
            var local = new Vector3d(r[0] * u.X + r[3] * u.Y + r[6] * u.Z, r[1] * u.X + r[4] * u.Y + r[7] * u.Z, r[2] * u.X + r[5] * u.Y + r[8] * u.Z);
            table.Lookup(local, out var f, out var m);
            var fw = Scale(Turn(r, f), q);
            var at = Add(Turn(r, table.Reference), translations[g]);
            force = Add(force, fw);
            moment = Add(moment, Add(Scale(Turn(r, m), q), Cross(at, fw)));
        }
    }

    private static Vector3d Turn(ReadOnlySpan<double> r, Vector3d p) =>
        new(r[0] * p.X + r[1] * p.Y + r[2] * p.Z, r[3] * p.X + r[4] * p.Y + r[5] * p.Z, r[6] * p.X + r[7] * p.Y + r[8] * p.Z);

    private struct SurfaceSample
    {
        public Vector3d P, N;
        public double Area;
    }

    private sealed class SampleList
    {
        public SurfaceSample[] Items = new SurfaceSample[256];
        public int Count;

        public void Add(in SurfaceSample s)
        {
            if (Count == Items.Length)
                Array.Resize(ref Items, Count * 2);

            Items[Count++] = s;
        }
    }

    /// <summary>The surface as small patches about <paramref name="spacing"/> across (about <see cref="TargetSamples"/> of them; at least one a triangle).</summary>
    private static SampleList Sample(IReadOnlyList<HullBrick> bricks, out double spacing, out double area)
    {
        const float scale = HullBins.CellMetres;
        var triangles = new List<(Vector3d A, Vector3d B, Vector3d C, Vector3d N, double Area)>();
        area = 0;

        foreach (var brick in bricks)
        {
            var o = brick.Origin;
            var v = brick.VertexSpan;
            var idx = brick.IndexSpan;

            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var a = Add(o, ToD(v[(int)idx[i]].DecodePosition(scale)));
                var b = Add(o, ToD(v[(int)idx[i + 1]].DecodePosition(scale)));
                var c = Add(o, ToD(v[(int)idx[i + 2]].DecodePosition(scale)));
                var n = Cross(Sub(b, a), Sub(c, a));
                var len = n.Length();

                if (len < 1e-9)
                    continue;

                n = Scale(n, 1 / len);
                var shade = Add(Add(ToD(v[(int)idx[i]].DecodeNormal()), ToD(v[(int)idx[i + 1]].DecodeNormal())), ToD(v[(int)idx[i + 2]].DecodeNormal()));

                if (Dot(n, shade) < 0)
                    n = Scale(n, -1);

                triangles.Add((a, b, c, n, len / 2));
                area += len / 2;
            }
        }

        // Halving to edges no longer than the spacing leaves pieces of about a third of its square.
        spacing = Math.Max(MinSpacing, Math.Sqrt(3 * area / TargetSamples));
        var samples = new SampleList();

        // Each triangle halved across its longest edge until no edge is longer than the spacing, so no gap between
        // samples is wider than it (a long thin triangle is cut along its length only); one sample per piece.
        var stack = new Stack<(Vector3d A, Vector3d B, Vector3d C, double Area)>();
        var limit = spacing * spacing;

        foreach (var (a, b, c, n, triArea) in triangles)
        {
            stack.Push((a, b, c, triArea));

            while (stack.Count > 0)
            {
                var (p, q, r, pieceArea) = stack.Pop();
                double pq = Length2(Sub(q, p)), qr = Length2(Sub(r, q)), rp = Length2(Sub(p, r));
                var longest = Math.Max(pq, Math.Max(qr, rp));

                if (longest <= limit)
                {
                    samples.Add(new SurfaceSample { P = Scale(Add(Add(p, q), r), 1 / 3.0), N = n, Area = pieceArea });
                    continue;
                }

                if (longest == pq)
                    (p, q, r) = (p, q, r);
                else if (longest == qr)
                    (p, q, r) = (q, r, p);
                else
                    (p, q, r) = (r, p, q);

                var m = Scale(Add(p, q), 0.5);
                stack.Push((p, m, r, pieceArea / 2));
                stack.Push((m, q, r, pieceArea / 2));
            }
        }

        return samples;
    }

    private static double Length2(Vector3d v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;

    private static Vector3d ToD(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The samples that some outside direction sees first (every other table direction is enough).</summary>
    private static SampleList Exposed(SampleList all, Grid grid)
    {
        var seen = new bool[all.Count];
        var lit = new bool[all.Count];

        for (var row = 0; row < AeroTable.Rows; row += 2)
        {
            for (var column = 0; column < AeroTable.Columns; column += 2)
            {
                var u = AeroTable.Direction(row, column);
                grid.Shadows(all, u, lit);

                for (var i = 0; i < all.Count; i++)
                    seen[i] |= lit[i] && Dot(all.Items[i].N, u) < -ExposureFacing;
            }
        }

        var exposed = new SampleList();

        for (var i = 0; i < all.Count; i++)
        {
            if (seen[i])
                exposed.Add(all.Items[i]);
        }

        return exposed;
    }

    /// <summary>
    /// An orthographic depth grid across the flow: each cell keeps its most upstream sample, and a windward sample is lit
    /// unless that sample's face, carried across to it, lies upstream of it (so a face nearly along the flow, which
    /// spans much depth in one cell, never shadows itself, and a wall in front of a room's wall always does).
    /// </summary>
    private sealed class Grid
    {
        private readonly Vector3d _centre;
        private readonly double _radius, _cell;
        private readonly int _size;
        private readonly double[] _min;
        private readonly int[] _first;
        private readonly int[] _stamp;
        private int[] _cells = [];
        private int _pass;

        public Grid(SampleList samples, double spacing)
        {
            Vector3d sum = default;
            double area = 0;

            for (var i = 0; i < samples.Count; i++)
            {
                sum = Add(sum, Scale(samples.Items[i].P, samples.Items[i].Area));
                area += samples.Items[i].Area;
            }

            _centre = Scale(sum, 1 / Math.Max(area, 1e-12));

            for (var i = 0; i < samples.Count; i++)
                _radius = Math.Max(_radius, Sub(samples.Items[i].P, _centre).Length());

            _cell = Math.Max(1.5 * spacing, 2 * _radius / (MaxGrid - 1));
            _size = (int)Math.Ceiling(2 * _radius / _cell) + 2;
            _min = new double[_size * _size];
            _first = new int[_size * _size];
            _stamp = new int[_size * _size];
        }

        public void Shadows(SampleList samples, Vector3d u, bool[] lit)
        {
            _pass++;
            var e1 = Normalise(Cross(u, Math.Abs(u.X) < 0.9 ? new Vector3d(1, 0, 0) : new Vector3d(0, 1, 0)), new Vector3d(0, 0, 1));
            var e2 = Cross(u, e1);

            if (_cells.Length < samples.Count)
                _cells = new int[samples.Items.Length];

            for (var i = 0; i < samples.Count; i++)
            {
                var d = Sub(samples.Items[i].P, _centre);
                var x = Math.Clamp((int)((Dot(d, e1) + _radius) / _cell), 0, _size - 1);
                var y = Math.Clamp((int)((Dot(d, e2) + _radius) / _cell), 0, _size - 1);
                var cell = x + y * _size;
                var depth = Dot(d, u);
                _cells[i] = cell;

                if (_stamp[cell] != _pass || depth < _min[cell])
                    (_stamp[cell], _min[cell], _first[cell]) = (_pass, depth, i);
            }

            for (var i = 0; i < samples.Count; i++)
            {
                var sinPhi = -Dot(samples.Items[i].N, u);

                if (sinPhi <= 0)
                {
                    lit[i] = false;
                    continue;
                }

                var j = _first[_cells[i]];

                if (j == i)
                {
                    lit[i] = true;
                    continue;
                }

                // How far upstream of this sample the front sample's face is, along the flow (a little slack for a
                // curved face's neighbours).
                var tolerance = 0.25 * _cell;
                var front = samples.Items[j];
                var facing = Dot(front.N, u);
                var ahead = facing < -1e-6
                    ? Dot(front.N, Sub(samples.Items[i].P, front.P)) / facing
                    : Dot(Sub(samples.Items[i].P, front.P), u);
                lit[i] = ahead <= tolerance;
            }
        }
    }
}
