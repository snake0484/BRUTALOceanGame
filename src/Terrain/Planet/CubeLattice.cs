using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// Cube-sphere parameterisation of directions. Face <c>f = 2a + (negative ? 1 : 0)</c> has major axis a; its coordinates
/// are the other two components over |major| (b = a+1, c = a+2 mod 3), so a shared edge reads the same coordinate on both
/// faces. Each gnomonic coordinate g is warped to t = (1+k)g / (1+k|g|) (k = √2−1), which evens out the cell arc from
/// face centre to edge. A face of N cells per side has lattice points t = −1 + 2i/N, i in [0, N].
/// </summary>
public static class CubeLattice
{
    public const int Faces = 6;
    public const double Warp = 0.41421356237309515;
    private const double WarpScale = 1.0 + Warp;

    /// <summary>Largest arc (rad) per unit of t over a face in either lattice direction (measured, see <see cref="MaxArcRate"/>), plus 1 %.</summary>
    public static readonly double MaxArcPerT = MaxArcRate() * 1.01;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ToT(double g) => WarpScale * g / (1.0 + Warp * Math.Abs(g));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double FromT(double t) => t / (WarpScale - Warp * Math.Abs(t));

    /// <summary>dg/dt at t.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Slope(double t)
    {
        var d = WarpScale - Warp * Math.Abs(t);
        return WarpScale / (d * d);
    }

    /// <summary>Face of a non-zero vector (ties go to the lower axis) and its gnomonic coordinates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Face(double x, double y, double z, out double gu, out double gv)
    {
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var az = Math.Abs(z);

        if (ax >= ay && ax >= az)
        {
            var inv = 1.0 / ax;
            gu = y * inv;
            gv = z * inv;
            return x < 0 ? 1 : 0;
        }

        if (ay >= az)
        {
            var inv = 1.0 / ay;
            gu = z * inv;
            gv = x * inv;
            return y < 0 ? 3 : 2;
        }

        var invZ = 1.0 / az;
        gu = x * invZ;
        gv = y * invZ;
        return z < 0 ? 5 : 4;
    }

    /// <summary>Unit direction of face coordinates (gnomonic, unwarped).</summary>
    public static void Direction(int face, double gu, double gv, out double x, out double y, out double z)
    {
        var major = (face & 1) == 0 ? 1.0 : -1.0;
        // gu² + gv² commutes bit for bit, so a point on a shared edge gets the same direction from both faces.
        var inv = 1.0 / Math.Sqrt(1.0 + (gu * gu + gv * gv));

        switch (face >> 1)
        {
            case 0:
                x = major * inv;
                y = gu * inv;
                z = gv * inv;
                break;
            case 1:
                y = major * inv;
                z = gu * inv;
                x = gv * inv;
                break;
            default:
                z = major * inv;
                x = gu * inv;
                y = gv * inv;
                break;
        }
    }

    /// <summary>Unit direction of lattice point (i, j) on a face of n cells per side.</summary>
    public static void LatticeDirection(int face, int i, int j, int n, out double x, out double y, out double z) =>
        Direction(face, FromT(-1.0 + 2.0 * i / n), FromT(-1.0 + 2.0 * j / n), out x, out y, out z);

    /// <summary>Arc per unit of gnomonic g_u at (g_u, g_v): |∂dir/∂g_u| = √(1+g_v²) / (1+g_u²+g_v²).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ArcPerG(double gu, double gv) => Math.Sqrt(1.0 + gv * gv) / (1.0 + gu * gu + gv * gv);

    private static double MaxArcRate()
    {
        const int steps = 256;
        var max = 0.0;

        for (var j = 0; j <= steps; j++)
        {
            var gv = FromT(-1.0 + 2.0 * j / steps);

            for (var i = 0; i <= steps; i++)
            {
                var t = -1.0 + 2.0 * i / steps;
                max = Math.Max(max, ArcPerG(FromT(t), gv) * Slope(t));
            }
        }

        return max;
    }
}
