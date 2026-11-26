using Brutal.Numerics;
using BrutalGame.Terrain;

namespace BrutalGame;

/// <summary>
/// --steep: where the scripted terrain tests fly instead of the spawn's coastal plain. The land point of most relief
/// among seeded candidates (heights on an 8 km ring round each), heading for the ring's highest ground.
/// </summary>
public static class SteepGround
{
    private const int Candidates = 4000;
    private const int RingPoints = 8;
    private const double RingRadius = 4000.0;
    private const int SampleLod = 4;
    private const double Diagonal = 0.7071067811865476;

    private static readonly double[] Compass = [1.0, 0.0, Diagonal, Diagonal, 0.0, 1.0, -Diagonal, Diagonal, -1.0, 0.0, -Diagonal, -Diagonal, 0.0, -1.0, Diagonal, -Diagonal];

    public static void Find(PlanetSurface surface, out double3 start, out double3 heading)
    {
        var random = new Random(7);
        var sea = surface.HasSea ? surface.SeaHeight + 100f : float.NegativeInfinity;
        var best = -1f;
        start = double3.UnitY;
        heading = double3.UnitX;

        for (var n = 0; n < Candidates; n++)
        {
            var d = double3.Normalize(new double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            var h = surface.Height(d.X, d.Y, d.Z, SampleLod);

            if (h < sea)
                continue;

            var east = double3.Normalize(double3.Cross(Math.Abs(d.Y) < 0.9 ? double3.UnitY : double3.UnitX, d));
            var north = double3.Cross(d, east);
            float lo = h, hi = h;
            var highest = east;

            for (var k = 0; k < RingPoints; k++)
            {
                var tangent = east * Compass[2 * k] + north * Compass[2 * k + 1];
                var p = double3.Normalize(d + tangent * (RingRadius / surface.Radius));
                var ring = surface.Height(p.X, p.Y, p.Z, SampleLod);

                if (ring > hi)
                    highest = tangent;

                lo = MathF.Min(lo, ring);
                hi = MathF.Max(hi, ring);
            }

            if (hi - lo <= best)
                continue;

            (best, start, heading) = (hi - lo, d, highest);
        }
    }
}
