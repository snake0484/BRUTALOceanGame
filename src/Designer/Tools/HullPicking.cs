using System.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tools;

/// <summary>
/// Headless tool maths: ray picking against solids (slab test on the solid box, then sphere tracing its SDF), and
/// closest points between a view ray and a gizmo axis. Rays are in ship metres, double.
/// </summary>
public static class HullPicking
{
    private const int MaxSteps = 64;
    private const float HitEpsilon = 1e-3f;

    /// <summary>Nearest solid the ray hits (any op), or 0. <paramref name="distance"/> is along the unit ray.</summary>
    public static uint Pick(PlanDocument doc, Vector3d origin, Vector3d direction, out double distance)
    {
        distance = double.MaxValue;
        uint hit = 0;
        Span<double> m = stackalloc double[9];

        foreach (ref readonly var solid in doc.Solids.AsSpan())
        {
            var mesh = solid.Shape == SolidShape.Mesh && solid.Param < doc.Meshes.Count ? doc.Meshes[solid.Param] : null;

            if ((!SolidGeometry.IsSupported(solid.Shape) && mesh is null) || !SolidRotation.IsValid(solid.Rotation))
                continue;

            SolidRotation.Matrix(solid.Rotation, m);
            var o = ToLocal(m, new Vector3d(origin.X - solid.PositionX, origin.Y - solid.PositionY, origin.Z - solid.PositionZ));
            var d = ToLocal(m, direction);
            var hitSolid = mesh is not null ? TraceMesh(MeshCache.Get(mesh), o, d, out var t) : Trace(solid.Shape, SolidGeometry.Half(solid), o, d, out t);

            if (hitSolid && t < distance)
            {
                distance = t;
                hit = solid.Id;
            }
        }

        return hit;
    }

    /// <summary>
    /// Parameter along the axis (from <paramref name="axisOrigin"/>, unit <paramref name="axis"/>) of the point closest
    /// to the ray. False when the ray runs (nearly) parallel to the axis.
    /// </summary>
    public static bool ClosestOnAxis(Vector3d rayOrigin, Vector3d rayDirection, Vector3d axisOrigin, Vector3d axis, out double along)
    {
        var w = new Vector3d(axisOrigin.X - rayOrigin.X, axisOrigin.Y - rayOrigin.Y, axisOrigin.Z - rayOrigin.Z);
        var a = Dot(axis, axis);
        var b = Dot(axis, rayDirection);
        var c = Dot(rayDirection, rayDirection);
        var d = Dot(axis, w);
        var e = Dot(rayDirection, w);
        var denom = a * c - b * b;
        along = 0.0;

        if (Math.Abs(denom) < 1e-9)
            return false;

        along = (b * e - c * d) / denom;
        return true;
    }

    /// <summary>Nearest triangle along the ray within <see cref="MaxRayMetres"/>.</summary>
    public static bool TraceMesh(PreparedMesh mesh, Vector3d o, Vector3d d, out double t)
    {
        var delta = new Vector3d(d.X * MaxRayMetres, d.Y * MaxRayMetres, d.Z * MaxRayMetres);
        var hit = mesh.FirstHit(o, delta, out t, out _);
        t *= MaxRayMetres;
        return hit;
    }

    public const double MaxRayMetres = 4000.0;

    private static bool Trace(SolidShape shape, Vector3 half, Vector3d o, Vector3d d, out double t)
    {
        t = 0.0;

        if (!Slab(half, o, d, out var t0, out var t1))
            return false;

        t = Math.Max(t0, 0.0);

        for (var i = 0; i < MaxSteps && t <= t1 + HitEpsilon; i++)
        {
            var p = new Vector3((float)(o.X + d.X * t), (float)(o.Y + d.Y * t), (float)(o.Z + d.Z * t));
            var dist = SolidGeometry.Distance(shape, p, half);

            if (dist < HitEpsilon)
                return true;

            t += dist;
        }

        return false;
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

    private static Vector3d ToLocal(ReadOnlySpan<double> m, Vector3d v) =>
        new(m[0] * v.X + m[3] * v.Y + m[6] * v.Z, m[1] * v.X + m[4] * v.Y + m[7] * v.Z, m[2] * v.X + m[5] * v.Y + m[8] * v.Z);

    private static double Dot(Vector3d a, Vector3d b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
