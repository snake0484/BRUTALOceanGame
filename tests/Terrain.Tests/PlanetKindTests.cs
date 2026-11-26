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
