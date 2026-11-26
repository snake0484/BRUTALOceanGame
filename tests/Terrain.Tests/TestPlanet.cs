using Brutal.Numerics;

namespace BrutalGame.Terrain.Tests;

/// <summary>The test system's 1000 km planet (PlanetTests' seed), shared by tests that need generated planet chunks.</summary>
internal static class TestPlanet
{
    public const double Radius = 1.0e6;

    public static readonly PlanetParams Params = PlanetParams.Generate(PlanetParams.BodySeed(0x5EED_2026UL, 1), Radius);
    public static readonly PlanetGenerator Generator = new(Params);

    /// <summary>A point <paramref name="offset"/> m above inland ground, clear of the protected shore so digging is allowed.</summary>
    public static double3 Inland(double offset, int seed = 7)
    {
        var random = new Random(seed);

        while (true)
        {
            var d = double3.Normalize(new double3(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            var h = Generator.Surface.Height(d.X, d.Y, d.Z);

            if (h > Generator.Surface.SeaHeight + 300f && h < Generator.Surface.SeaHeight + 1500f)
                return d * (Radius + h + offset);
        }
    }

    /// <summary>The LOD chunk holding the point <paramref name="offset"/> m above the ground in unit direction d.</summary>
    public static ChunkCoord SurfaceChunk(double x, double y, double z, int lod, double offset = 0.0)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        (x, y, z) = (x / l, y / l, z / l);
        var r = Radius + Generator.Surface.Height(x, y, z, lod) + offset;
        return ChunkCoord.FromPlanet(x * r, y * r, z * r, lod);
    }
}
