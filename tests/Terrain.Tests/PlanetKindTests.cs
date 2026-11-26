using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class PlanetKindTests(ITestOutputHelper output)
{
    private const ulong SystemSeed = 0x5EED_2026UL;

    private static void RandomDirection(Random random, out double x, out double y, out double z)
    {
        while (true)
        {
            x = random.NextDouble() * 2.0 - 1.0;
            y = random.NextDouble() * 2.0 - 1.0;
            z = random.NextDouble() * 2.0 - 1.0;
            var l = Math.Sqrt(x * x + y * y + z * z);

            if (l is < 0.1 or > 1.0)
                continue;

            x /= l;
            y /= l;
            z /= l;
            return;
        }
    }

    private static ulong Fnv(ulong hash, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            hash = (hash ^ b) * 0x100000001B3UL;

        return hash;
    }

    /// <summary>Hash of heights at 3 LODs over 512 directions, then of 24 chunks' bytes (LOD 0, 2, 5, 9; every third masked).</summary>
    internal static (ulong Heights, ulong Chunks) GoldenHashes(PlanetGenerator generator)
    {
        var random = new Random(41);
        var heights = 0xCBF29CE484222325UL;
        var chunks = 0xCBF29CE484222325UL;
        var scratch = new PlanetChunkScratch();
        var chunk = new VoxelChunk();
        Span<int> lods = [0, 2, 5, 9];

        for (var n = 0; n < 512; n++)
        {
            RandomDirection(random, out var x, out var y, out var z);

            foreach (var lod in (ReadOnlySpan<int>)[0, 3, 7])
                heights = Fnv(heights, BitConverter.GetBytes(generator.Surface.Height(x, y, z, lod)));

            if (n >= 24)
                continue;

            var l = lods[n % 4];
            var r = generator.Params.Radius + generator.Surface.Height(x, y, z, l);
            chunk.Coord = ChunkCoord.FromPlanet(x * r, y * r, z * r, l);
            generator.Generate(chunk, scratch, withMask: n % 3 == 0);
            chunks = Fnv(chunks, System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Density.AsSpan()));
            chunks = Fnv(chunks, chunk.Material);
            chunks = Fnv(chunks, BitConverter.GetBytes(scratch.ProtectedCount));
        }

        return (heights, chunks);
    }

    /// <summary>
    /// Slope and relief of a kind over land (planet-kinds.md §1.5), from 10⁵ slope samples and 2,000 relief windows of
    /// 10 km. Roughness is the RMS of the 1 m slope less the 100 m slope it rides on, over ground under 20° (off the risers).
    /// </summary>
    private const double FlatSlope = 0.364;

    internal readonly record struct KindStats(double Relief50, double Relief95, double Slope50, double Slope90, double Slope99, double Over30, double Over45, double Rough1m);

    internal static KindStats Measure(PlanetSurface surface, int samples = 100_000, int windows = 2_000)
    {
        var random = new Random(53);
        var slopes = new double[samples];
        var relief = new double[windows];
        var rough = 0.0;
        var flats = 0;
        var sea = surface.HasSea ? surface.SeaHeight : float.NegativeInfinity;
        var radius = surface.Radius;

        static void Tangents(Random random, double x, double y, double z, out (double X, double Y, double Z) u, out (double X, double Y, double Z) v)
        {
            RandomDirection(random, out var ux, out var uy, out var uz);
            var dot = ux * x + uy * y + uz * z;
            (ux, uy, uz) = (ux - dot * x, uy - dot * y, uz - dot * z);
            var l = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            u = (ux / l, uy / l, uz / l);
            v = (y * u.Z - z * u.Y, z * u.X - x * u.Z, x * u.Y - y * u.X);
        }

        float At(double x, double y, double z, (double X, double Y, double Z) u, (double X, double Y, double Z) v, double a, double b, int lod = 0)
        {
            var (px, py, pz) = (x + (u.X * a + v.X * b) / radius, y + (u.Y * a + v.Y * b) / radius, z + (u.Z * a + v.Z * b) / radius);
            var l = Math.Sqrt(px * px + py * py + pz * pz);
            return surface.Height(px / l, py / l, pz / l, lod);
        }

        for (var n = 0; n < samples;)
        {
            RandomDirection(random, out var x, out var y, out var z);
            var h = surface.Height(x, y, z);

            if (h < sea)
                continue;

            Tangents(random, x, y, z, out var u, out var v);
            double du = (At(x, y, z, u, v, 100.0, 0.0) - h) / 100.0, dv = (At(x, y, z, u, v, 0.0, 100.0) - h) / 100.0;
            double fu = At(x, y, z, u, v, 1.0, 0.0) - h, fv = At(x, y, z, u, v, 0.0, 1.0) - h;
            slopes[n++] = Math.Atan(Math.Sqrt(du * du + dv * dv)) * (180.0 / Math.PI);

            if (du * du + dv * dv > FlatSlope * FlatSlope)
                continue;

            rough += (fu - du) * (fu - du) + (fv - dv) * (fv - dv);
            flats++;
        }

        for (var n = 0; n < windows;)
        {
            RandomDirection(random, out var x, out var y, out var z);

            if (surface.Height(x, y, z, 5) < sea)
                continue;

            Tangents(random, x, y, z, out var u, out var v);
            float lo = float.MaxValue, hi = float.MinValue;

            for (var j = 0; j < 9; j++)
            for (var i = 0; i < 9; i++)
            {
                var h = MathF.Max(At(x, y, z, u, v, (i - 4) * 1250.0, (j - 4) * 1250.0, 5), sea);
                lo = MathF.Min(lo, h);
                hi = MathF.Max(hi, h);
            }

            relief[n++] = hi - lo;
        }

        Array.Sort(slopes);
        Array.Sort(relief);

        double Share(double degrees)
        {
            var k = Array.BinarySearch(slopes, degrees);
            return (samples - (k < 0 ? ~k : k)) / (double)samples;
        }

        return new KindStats(relief[windows / 2], relief[windows * 95 / 100], slopes[samples / 2], slopes[samples * 9 / 10], slopes[samples * 99 / 100],
            Share(30.0), Share(45.0), Math.Sqrt(rough / (2.0 * flats)));
    }

    /// <summary>The brief's target table (§1): relief p50/p95 (m), 100 m slope p50/p90/p99 (deg), share over 30° and 45°, 1 m roughness.</summary>
    [Theory]
    [InlineData("legacy-rolling", 1.0e6, 350, 900, 3, 7, 13, 0.0, 0.0, 0.19)]
    [InlineData("temperate-continental", 1.0e6, 700, 3000, 9, 28, 42, 0.08, 0.008, 0.08)]
    [InlineData("barren-cratered", 4.0e5, 800, 4000, 7, 25, 40, 0.07, 0.003, 0.10)]
    [InlineData("desert-canyon", 1.0e6, 500, 1800, 3, 25, 70, 0.08, 0.04, 0.05)]
    public void KindStatistics(string kind, double radius, double relief50, double relief95, double slope50, double slope90, double slope99, double over30, double over45, double rough)
    {
        var generator = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, kind == "barren-cratered" ? 2 : 1), radius, kind: kind));
        var s = Measure(generator.Surface);
        output.WriteLine($"{kind}: target relief {relief50}/{relief95} m, slope {slope50}/{slope90}/{slope99} deg, over 30/45 {over30:P1}/{over45:P1}, rough {rough:P0}");
        output.WriteLine($"{kind}: actual relief {s.Relief50:F0}/{s.Relief95:F0} m, slope {s.Slope50:F1}/{s.Slope90:F1}/{s.Slope99:F1} deg, over 30/45 {s.Over30:P1}/{s.Over45:P2}, rough {s.Rough1m:P1}");

        if (kind == PlanetKinds.Legacy)
            return;

        Assert.InRange(s.Relief50, relief50 * 0.5, relief50 * 2.0);
        Assert.InRange(s.Slope50, slope50 * 0.5, slope50 * 2.0);
        Assert.InRange(s.Slope90, slope90 * 0.5, slope90 * 2.0);
    }

    /// <summary>The v1 generator's heights and chunk bytes, recorded before the kinds landed: legacy-rolling reproduces them bit for bit.</summary>
    [Fact]
    public void LegacyKindMatchesV1()
    {
        var planet = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 1), 1.0e6));
        var moon = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 2), 2.5e5, 1.6, ocean: false));

        Assert.Equal(PlanetKinds.Legacy, planet.Params.KindId);
        Assert.Equal(2u, planet.Params.GeneratorVersion);
        Assert.Equal((0xA4D7B2D93D3A4FE0UL, 0x15C843DCA11D66E1UL), GoldenHashes(planet));
        Assert.Equal((0xDE677C6163CEBECDUL, 0x12313C773C693313UL), GoldenHashes(moon));
    }
}
