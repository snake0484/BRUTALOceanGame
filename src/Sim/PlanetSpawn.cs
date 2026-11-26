using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame.Sim;

/// <summary>
/// A deterministic spawn on a planet's coast: scanning eastward along latitude rows from 30° outwards (alternating north
/// and south) for the first sea-to-land step whose land rises gently (a beach, not a cliff or a marsh), then refined to
/// the waterline at full detail, with open water just offshore (not a tidal flat). With no sea, a point on the ground at
/// 30° north.
/// </summary>
public static class PlanetSpawn
{
    private const double StartLatitude = 30.0;
    private const double MaxLatitudeOffset = 25.0;
    private const double RowStep = 0.5;

    /// <summary>Scan spacing along a row (m) and the LOD whose octaves the scan evaluates (64 m voxels: features of 128 m and up).</summary>
    private const double ScanStep = 2000.0;
    private const int ScanLod = 6;

    /// <summary>Land this far inland (m) must stand between these heights above sea.</summary>
    private const double InlandProbe = 400.0;
    private const float MinRise = 4f;
    private const float MaxRise = 60f;

    /// <summary>The sea this far out (m) must be at least this deep.</summary>
    private const double OffshoreProbe = 300.0;
    private const float MinOffshoreDepth = 5f;

    /// <param name="coast">Unit direction of the waterline.</param>
    /// <param name="seaward">Unit tangent at <paramref name="coast"/> pointing out to sea.</param>
    public static void FindCoast(PlanetSurface surface, double3 spinAxis, out double3 coast, out double3 seaward)
    {
        var (east0, north0) = Basis(spinAxis);

        if (!surface.HasSea)
        {
            coast = Direction(spinAxis, east0, north0, StartLatitude, 0.0);
            seaward = Tangent(east0, coast);
            return;
        }

        var sea = surface.SeaHeight;
        var radius = surface.Radius;

        for (var offset = 0.0; offset <= MaxLatitudeOffset; offset += RowStep)
        {
            for (var side = 0; side < 2; side++)
            {
                if (offset == 0.0 && side == 1)
                    continue;

                var latitude = StartLatitude + (side == 0 ? offset : -offset);
                var rowRadius = radius * Math.Cos(latitude * Math.PI / 180.0);
                var steps = (int)(2.0 * Math.PI * rowRadius / ScanStep);
                var lonStep = 360.0 / steps;
                var previous = Height(surface, Direction(spinAxis, east0, north0, latitude, 0.0), ScanLod);

                for (var i = 1; i <= steps; i++)
                {
                    var d = Direction(spinAxis, east0, north0, latitude, i * lonStep);
                    var h = Height(surface, d, ScanLod);
                    var wasSea = previous < sea;
                    previous = h;

                    if (!wasSea || h < sea)
                        continue;

                    var west = Direction(spinAxis, east0, north0, latitude, (i - 1) * lonStep);
                    var shore = Refine(surface, west, d, sea);
                    var inland = double3.Normalize(d - west);
                    var probe = double3.Normalize(shore + inland * (InlandProbe / radius));
                    var rise = Height(surface, probe, 0) - sea;
                    var offshore = double3.Normalize(shore - inland * (OffshoreProbe / radius));

                    if (rise < MinRise || rise > MaxRise || sea - Height(surface, offshore, 0) < MinOffshoreDepth)
                        continue;

                    coast = shore;
                    seaward = Tangent(-inland, shore);
                    return;
                }
            }
        }

        coast = Direction(spinAxis, east0, north0, StartLatitude, 0.0);
        seaward = Tangent(east0, coast);
    }

    private static float Height(PlanetSurface surface, double3 d, int lod) => surface.Height(d.X, d.Y, d.Z, lod);

    /// <summary>Bisects the great-circle arc from sea to land down to 0.25 m at full detail.</summary>
    private static double3 Refine(PlanetSurface surface, double3 sea, double3 land, float level)
    {
        var arc = Math.Acos(Math.Clamp(double3.Dot(sea, land), -1.0, 1.0)) * surface.Radius;

        while (arc > 0.25)
        {
            var mid = double3.Normalize(sea + land);

            if (Height(surface, mid, 0) < level)
                sea = mid;
            else
                land = mid;

            arc *= 0.5;
        }

        return double3.Normalize(sea + land);
    }

    private static (double3 East, double3 North) Basis(double3 spinAxis)
    {
        var reference = Math.Abs(spinAxis.X) < 0.9 ? double3.UnitX : double3.UnitZ;
        var east = double3.Normalize(double3.Cross(spinAxis, reference));
        var north = double3.Cross(east, spinAxis);
        return (east, north);
    }

    private static double3 Direction(double3 axis, double3 east, double3 north, double latitude, double longitude)
    {
        var lat = latitude * Math.PI / 180.0;
        var lon = longitude * Math.PI / 180.0;
        var equator = north * Math.Cos(lon) + east * Math.Sin(lon);
        return double3.Normalize(equator * Math.Cos(lat) + axis * Math.Sin(lat));
    }

    private static double3 Tangent(double3 v, double3 up) => double3.Normalize(v - up * double3.Dot(v, up));
}
