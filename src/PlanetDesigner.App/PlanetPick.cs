using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.PlanetDesigner.App;

/// <summary>Ray against the analytic surface (generated + authored): the sphere of the relief top, then march and bisect.</summary>
public static class PlanetPick
{
    private const int MaxSteps = 4000;
    private const int Bisections = 40;

    public static bool Ray(PlanetSurface surface, double3 origin, double3 direction, out double3 hit)
    {
        hit = default;
        var radius = surface.Radius;
        var top = radius + surface.Params.MaxHeight + 10.0;
        var b = double3.Dot(origin, direction);
        var c = double3.Dot(origin, origin) - top * top;
        var disc = b * b - c;

        if (disc < 0.0)
            return false;

        var root = Math.Sqrt(disc);
        var t = Math.Max(0.0, -b - root);
        var far = -b + root;
        var last = t;

        for (var step = 0; step < MaxSteps && t < far; step++)
        {
            var above = Above(surface, origin + direction * t);

            if (above <= 0.0)
            {
                double lo = last, hi = t;

                for (var k = 0; k < Bisections; k++)
                {
                    var mid = 0.5 * (lo + hi);

                    if (Above(surface, origin + direction * mid) > 0.0)
                        lo = mid;
                    else
                        hi = mid;
                }

                hit = origin + direction * hi;
                return true;
            }

            last = t;
            t += Math.Max(0.5, 0.3 * above);
        }

        return false;
    }

    private static double Above(PlanetSurface surface, double3 p)
    {
        var r = p.Length();
        var d = p / r;
        return r - surface.Radius - surface.Height(d.X, d.Y, d.Z);
    }
}
