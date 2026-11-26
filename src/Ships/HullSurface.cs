using System.Numerics;

namespace BrutalGame.Ships;

/// <summary>
/// Ray queries against the hull's outer surface straight from the solids, in double: no bake or GPU mesh. Each solid
/// gives the ray's inside intervals (a box exactly, a wedge or cylinder by sphere tracing in from both ends, a mesh from
/// its crossings), combined in plan order (Add unions, Subtract removes, Intersect keeps the overlap). The first interval
/// that starts in front of the origin is the surface. Used to draw strokes and to stick them back onto the hull at every
/// bake, so every peer projects them the same way. Only skin solids count (rooms, components and panels are not the skin).
/// </summary>
public sealed class HullSurface
{
    private const int MaxSteps = 96;
    private const double HitEpsilon = 1e-4;
    private const float GradientStep = 1e-3f;

    private readonly Entry[] _entries;
    private readonly List<double> _crossings = new();
    private List<Span> _result = new();
    private List<Span> _scratch = new();
    private readonly List<Span> _solid = new();

    private sealed class Entry
    {
        public HullSolid Solid;
        public readonly double[] M = new double[9];
        public Vector3d Position;
        public Vector3 Half;
        public PreparedMesh? Mesh;
    }

    /// <summary>An inside interval along the ray; Start/End name the solid whose surface bounds it.</summary>
    private readonly record struct Span(double T0, int S0, double T1, int S1);

    public HullSurface(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes)
    {
        var entries = new List<Entry>(solids.Length);

        foreach (ref readonly var solid in solids)
        {
            if (solid.Op is not (SolidOp.Add or SolidOp.Subtract or SolidOp.Intersect) || SolidRoles.Of(solid) != SolidRole.Skin)
                continue;

            var mesh = solid.Shape == SolidShape.Mesh && solid.Param < meshes.Count ? MeshCache.Get(meshes[solid.Param]) : null;

            if ((!SolidGeometry.IsSupported(solid.Shape) && mesh is null) || !SolidRotation.IsValid(solid.Rotation))
                continue;

            var e = new Entry
            {
                Solid = solid,
                Position = new Vector3d(solid.PositionX, solid.PositionY, solid.PositionZ),
                Half = SolidGeometry.Half(solid),
                Mesh = mesh,
            };
            SolidRotation.Matrix(solid.Rotation, e.M);
            entries.Add(e);
        }

        _entries = entries.ToArray();
    }

    public bool IsEmpty => _entries.Length == 0;

    /// <summary>First point where the ray (unit <paramref name="direction"/>) enters the hull within <paramref name="maxT"/>, with the outward normal there.</summary>
    public bool Raycast(Vector3d origin, Vector3d direction, double maxT, out double t, out Vector3d normal)
    {
        t = 0.0;
        normal = default;
        _result.Clear();
        var first = true;

        for (var i = 0; i < _entries.Length; i++)
        {
            var e = _entries[i];
            _solid.Clear();
            Intervals(e, i, origin, direction, maxT, _solid);

            if (first)
            {
                if (e.Solid.Op == SolidOp.Add)
                {
                    _result.AddRange(_solid);
                    first = false;
                }

                continue;
            }

            _scratch.Clear();
            Combine(_result, _solid, e.Solid.Op, _scratch);
            (_result, _scratch) = (_scratch, _result);
        }

        foreach (var span in _result)
        {
            // An interval holding the origin is not a surface in front of it.
            if (span.T0 <= HitEpsilon || span.S0 < 0)
                continue;

            if (span.T0 > maxT)
                break;

            t = span.T0;
            normal = Normal(_entries[span.S0], Add(origin, Scale(direction, t)), direction);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sticks a point back onto the surface: casts from <paramref name="reach"/> metres out along the normal back through
    /// the point. False (point unchanged) when the hull is not within reach there.
    /// </summary>
    public bool Project(Vector3d point, Vector3d normal, double reach, out Vector3d onSurface, out Vector3d surfaceNormal)
    {
        var origin = Add(point, Scale(normal, reach));

        if (Raycast(origin, Scale(normal, -1.0), reach * 2.0, out var t, out surfaceNormal))
        {
            onSurface = Add(origin, Scale(normal, -t));
            return true;
        }

        onSurface = point;
        surfaceNormal = normal;
        return false;
    }

    private void Intervals(Entry e, int index, Vector3d origin, Vector3d direction, double maxT, List<Span> output)
    {
        var o = ToLocal(e.M, Sub(origin, e.Position));
        var d = ToLocal(e.M, direction);

        if (e.Mesh is not null)
        {
            _crossings.Clear();
            e.Mesh.Crossings(o, d, _crossings);
            _crossings.Sort();
            var inside = (_crossings.Count & 1) != 0;
            var start = inside ? 0.0 : double.NaN;
            var startSolid = inside ? -1 : index;

            foreach (var c in _crossings)
            {
                if (double.IsNaN(start))
                {
                    start = c;
                    startSolid = index;
                }
                else
                {
                    output.Add(new Span(start, startSolid, c, index));
                    start = double.NaN;
                }
            }

            return;
        }

        if (!Slab(e.Half, o, d, out var t0, out var t1))
            return;

        if (e.Solid.Shape == SolidShape.Box)
        {
            output.Add(t0 <= 0.0 ? new Span(0.0, -1, t1, index) : new Span(t0, index, t1, index));
            return;
        }

        // Convex: march in from both slab ends.
        var entry = Math.Max(t0, 0.0);
        var originInside = t0 <= 0.0 && Distance(e, o, d, 0.0) <= 0f;

        if (!originInside)
        {
            if (!March(e, o, d, ref entry, t1, 1.0))
                return;
        }

        var exit = t1;

        if (!March(e, o, d, ref exit, entry, -1.0))
            exit = entry;

        output.Add(originInside ? new Span(0.0, -1, exit, index) : new Span(entry, index, exit, index));
    }

    private static bool March(Entry e, Vector3d o, Vector3d d, ref double t, double limit, double sign)
    {
        for (var i = 0; i < MaxSteps; i++)
        {
            var dist = Distance(e, o, d, t);

            if (dist < HitEpsilon)
                return true;

            t += sign * dist;

            if (sign > 0 ? t > limit + HitEpsilon : t < limit - HitEpsilon)
                return false;
        }

        return false;
    }

    private static float Distance(Entry e, Vector3d o, Vector3d d, double t) =>
        SolidGeometry.Distance(e.Solid.Shape, new Vector3((float)(o.X + d.X * t), (float)(o.Y + d.Y * t), (float)(o.Z + d.Z * t)), e.Half);

    /// <summary>Plan-order CSG on sorted, disjoint interval lists.</summary>
    private static void Combine(List<Span> a, List<Span> b, SolidOp op, List<Span> output)
    {
        // Sweep every boundary in order, tracking whether we are inside a and inside b.
        int i = 0, j = 0;
        bool inA = false, inB = false, was = false;
        double start = 0.0;
        var startSolid = -1;

        while (i < a.Count * 2 || j < b.Count * 2)
        {
            var ta = i < a.Count * 2 ? ((i & 1) == 0 ? a[i >> 1].T0 : a[i >> 1].T1) : double.PositiveInfinity;
            var tb = j < b.Count * 2 ? ((j & 1) == 0 ? b[j >> 1].T0 : b[j >> 1].T1) : double.PositiveInfinity;
            double t;
            int solid;

            if (ta <= tb)
            {
                t = ta;
                solid = (i & 1) == 0 ? a[i >> 1].S0 : a[i >> 1].S1;
                inA = (i & 1) == 0;
                i++;
            }
            else
            {
                t = tb;
                solid = (j & 1) == 0 ? b[j >> 1].S0 : b[j >> 1].S1;
                inB = (j & 1) == 0;
                j++;
            }

            var now = op switch
            {
                SolidOp.Add => inA || inB,
                SolidOp.Subtract => inA && !inB,
                _ => inA && inB,
            };

            if (now && !was)
            {
                start = t;
                startSolid = solid;
            }
            else if (!now && was && t > start)
            {
                output.Add(new Span(start, startSolid, t, solid));
            }

            was = now;
        }
    }

    /// <summary>Outward surface normal of a solid at a ship point, turned to face the ray.</summary>
    private static Vector3d Normal(Entry e, Vector3d point, Vector3d direction)
    {
        var p = ToLocal(e.M, Sub(point, e.Position));
        Vector3d local;

        if (e.Mesh is not null)
        {
            var d = ToLocal(e.M, direction);
            var from = new Vector3d(p.X - d.X * 1e-3, p.Y - d.Y * 1e-3, p.Z - d.Z * 1e-3);

            if (e.Mesh.FirstHit(from, new Vector3d(d.X * 2e-3, d.Y * 2e-3, d.Z * 2e-3), out _, out var triangle))
            {
                var n = e.Mesh.Normals[triangle];
                local = new Vector3d(n.X, n.Y, n.Z);
            }
            else
            {
                local = new Vector3d(-d.X, -d.Y, -d.Z);
            }
        }
        else
        {
            var q = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            var h = GradientStep;
            local = new Vector3d(
                SolidGeometry.Distance(e.Solid.Shape, q + new Vector3(h, 0, 0), e.Half) - SolidGeometry.Distance(e.Solid.Shape, q - new Vector3(h, 0, 0), e.Half),
                SolidGeometry.Distance(e.Solid.Shape, q + new Vector3(0, h, 0), e.Half) - SolidGeometry.Distance(e.Solid.Shape, q - new Vector3(0, h, 0), e.Half),
                SolidGeometry.Distance(e.Solid.Shape, q + new Vector3(0, 0, h), e.Half) - SolidGeometry.Distance(e.Solid.Shape, q - new Vector3(0, 0, h), e.Half));
        }

        var m = e.M;
        var ship = new Vector3d(
            m[0] * local.X + m[1] * local.Y + m[2] * local.Z,
            m[3] * local.X + m[4] * local.Y + m[5] * local.Z,
            m[6] * local.X + m[7] * local.Y + m[8] * local.Z);
        ship = Normalise(ship, Scale(direction, -1.0));
        return Dot(ship, direction) > 0.0 ? Scale(ship, -1.0) : ship;
    }

    private static bool Slab(Vector3 half, Vector3d o, Vector3d d, out double t0, out double t1)
    {
        t0 = double.NegativeInfinity;
        t1 = double.PositiveInfinity;
        return Axis(half.X, o.X, d.X, ref t0, ref t1) && Axis(half.Y, o.Y, d.Y, ref t0, ref t1) && Axis(half.Z, o.Z, d.Z, ref t0, ref t1) && t1 >= 0.0;
    }

    private static bool Axis(double half, double o, double d, ref double t0, ref double t1)
    {
        if (Math.Abs(d) < 1e-12)
            return Math.Abs(o) <= half;

        var a = (-half - o) / d;
        var b = (half - o) / d;

        if (a > b)
            (a, b) = (b, a);

        t0 = Math.Max(t0, a);
        t1 = Math.Min(t1, b);
        return t0 <= t1;
    }

    private static Vector3d ToLocal(double[] m, Vector3d v) =>
        new(m[0] * v.X + m[3] * v.Y + m[6] * v.Z, m[1] * v.X + m[4] * v.Y + m[7] * v.Z, m[2] * v.X + m[5] * v.Y + m[8] * v.Z);

    // ---- small double vector helpers (Vector3d is a plain record) ----

    public static Vector3d Add(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3d Sub(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3d Scale(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    public static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static Vector3d Normalise(Vector3d a, Vector3d fallback)
    {
        var length = a.Length();
        return length > 1e-12 ? Scale(a, 1.0 / length) : fallback;
    }

    public static Vector3d Lerp(Vector3d a, Vector3d b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
}
