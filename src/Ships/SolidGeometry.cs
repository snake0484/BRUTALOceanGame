using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Ships;

/// <summary>
/// Primitive signed distances in solid-local metres (negative inside) and ship-space bounds. Scalar float, fixed
/// operation order, no trig. Wedge: the box below the plane through its bottom front edge (y = -hy, z = +hz) and top
/// back edge (y = +hy, z = -hz), so its full face is the bottom and its vertical face the back (-Z). Cylinder: along
/// local Y, radius = half X, half height = half Y.
/// </summary>
public static class SolidGeometry
{
    public static bool IsSupported(SolidShape shape) => shape is SolidShape.Box or SolidShape.Wedge or SolidShape.Cylinder;

    /// <summary>Half extents in metres as the SDF uses them (a cylinder's Z mirrors its radius).</summary>
    public static Vector3 Half(in HullSolid solid)
    {
        var x = (float)(solid.HalfX * HullSolid.MetresPerHalfUnit);
        var y = (float)(solid.HalfY * HullSolid.MetresPerHalfUnit);
        var z = solid.Shape == SolidShape.Cylinder ? x : (float)(solid.HalfZ * HullSolid.MetresPerHalfUnit);
        return new Vector3(x, y, z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(SolidShape shape, Vector3 p, Vector3 half) => shape switch
    {
        SolidShape.Wedge => Wedge(p, half),
        SolidShape.Cylinder => Cylinder(p, half),
        _ => Box(p, half),
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Box(Vector3 p, Vector3 half)
    {
        var qx = MathF.Abs(p.X) - half.X;
        var qy = MathF.Abs(p.Y) - half.Y;
        var qz = MathF.Abs(p.Z) - half.Z;
        var ox = MathF.Max(qx, 0f);
        var oy = MathF.Max(qy, 0f);
        var oz = MathF.Max(qz, 0f);
        return MathF.Sqrt(ox * ox + oy * oy + oz * oz) + MathF.Min(MathF.Max(qx, MathF.Max(qy, qz)), 0f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Wedge(Vector3 p, Vector3 half)
    {
        var plane = (half.Z * p.Y + half.Y * p.Z) / MathF.Sqrt(half.Z * half.Z + half.Y * half.Y);
        return MathF.Max(Box(p, half), plane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cylinder(Vector3 p, Vector3 half)
    {
        var dr = MathF.Sqrt(p.X * p.X + p.Z * p.Z) - half.X;
        var dy = MathF.Abs(p.Y) - half.Y;
        var ox = MathF.Max(dr, 0f);
        var oy = MathF.Max(dy, 0f);
        return MathF.Min(MathF.Max(dr, dy), 0f) + MathF.Sqrt(ox * ox + oy * oy);
    }

    /// <summary>Ship-space AABB in metres of the solid's local box (a cylinder's bounding box).</summary>
    public static void Bounds(in HullSolid solid, out Vector3d min, out Vector3d max)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(solid.Rotation, m);
        var h = Half(solid);
        var ex = Math.Abs(m[0]) * h.X + Math.Abs(m[1]) * h.Y + Math.Abs(m[2]) * h.Z;
        var ey = Math.Abs(m[3]) * h.X + Math.Abs(m[4]) * h.Y + Math.Abs(m[5]) * h.Z;
        var ez = Math.Abs(m[6]) * h.X + Math.Abs(m[7]) * h.Y + Math.Abs(m[8]) * h.Z;
        var c = new Vector3d(solid.PositionX, solid.PositionY, solid.PositionZ);
        min = new Vector3d(c.X - ex, c.Y - ey, c.Z - ez);
        max = new Vector3d(c.X + ex, c.Y + ey, c.Z + ez);
    }

    /// <summary>Exact solid volume in m³ of one primitive.</summary>
    public static double Volume(in HullSolid solid)
    {
        double hx = solid.HalfX * HullSolid.MetresPerHalfUnit, hy = solid.HalfY * HullSolid.MetresPerHalfUnit, hz = solid.HalfZ * HullSolid.MetresPerHalfUnit;

        return solid.Shape switch
        {
            SolidShape.Wedge => 4.0 * hx * hy * hz,
            SolidShape.Cylinder => Math.PI * hx * hx * 2.0 * hy,
            _ => 8.0 * hx * hy * hz,
        };
    }
}

/// <summary>Double-precision point or extent in ship metres.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);

    public static implicit operator Brutal.Numerics.double3(Vector3d v) => new(v.X, v.Y, v.Z);

    public static implicit operator Vector3d(Brutal.Numerics.double3 v) => new(v.X, v.Y, v.Z);
}
