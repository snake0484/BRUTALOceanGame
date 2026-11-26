using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Ships;

/// <summary>
/// Primitive signed distances in brush-local metres (negative inside) and ship-space bounds. Scalar float, fixed
/// operation order, no trig. Wedge: the box below the plane through its bottom front edge (y = -hy, z = +hz) and top
/// back edge (y = +hy, z = -hz), so its full face is the bottom and its vertical face the back (-Z). Cylinder: along
/// local Y, radius = half X, half height = half Y.
/// </summary>
public static class BrushGeometry
{
    public static bool IsSupported(BrushShape shape) => shape is BrushShape.Box or BrushShape.Wedge or BrushShape.Cylinder;

    /// <summary>Half extents in metres as the SDF uses them (a cylinder's Z mirrors its radius).</summary>
    public static Vector3 Half(in HullBrush brush)
    {
        var x = (float)(brush.HalfX * HullBrush.MetresPerHalfUnit);
        var y = (float)(brush.HalfY * HullBrush.MetresPerHalfUnit);
        var z = brush.Shape == BrushShape.Cylinder ? x : (float)(brush.HalfZ * HullBrush.MetresPerHalfUnit);
        return new Vector3(x, y, z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(BrushShape shape, Vector3 p, Vector3 half) => shape switch
    {
        BrushShape.Wedge => Wedge(p, half),
        BrushShape.Cylinder => Cylinder(p, half),
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

    /// <summary>Ship-space AABB in metres of the brush's local box (a cylinder's bounding box).</summary>
    public static void Bounds(in HullBrush brush, out Vector3d min, out Vector3d max)
    {
        Span<double> m = stackalloc double[9];
        BrushRotation.Matrix(brush.Rotation, m);
        var h = Half(brush);
        var ex = Math.Abs(m[0]) * h.X + Math.Abs(m[1]) * h.Y + Math.Abs(m[2]) * h.Z;
        var ey = Math.Abs(m[3]) * h.X + Math.Abs(m[4]) * h.Y + Math.Abs(m[5]) * h.Z;
        var ez = Math.Abs(m[6]) * h.X + Math.Abs(m[7]) * h.Y + Math.Abs(m[8]) * h.Z;
        var c = new Vector3d(brush.PositionX, brush.PositionY, brush.PositionZ);
        min = new Vector3d(c.X - ex, c.Y - ey, c.Z - ez);
        max = new Vector3d(c.X + ex, c.Y + ey, c.Z + ez);
    }

    /// <summary>Exact solid volume in m³ of one primitive.</summary>
    public static double Volume(in HullBrush brush)
    {
        double hx = brush.HalfX * HullBrush.MetresPerHalfUnit, hy = brush.HalfY * HullBrush.MetresPerHalfUnit, hz = brush.HalfZ * HullBrush.MetresPerHalfUnit;

        return brush.Shape switch
        {
            BrushShape.Wedge => 4.0 * hx * hy * hz,
            BrushShape.Cylinder => Math.PI * hx * hx * 2.0 * hy,
            _ => 8.0 * hx * hy * hz,
        };
    }
}

/// <summary>Double-precision point or extent in ship metres.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
}
