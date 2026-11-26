using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>Small kinds that between them use every slice-1 layer type, gate and combine.</summary>
internal static class TestKinds
{
    public const string Mountains = "t-mountains";
    public const string Mesa = "t-mesa";
    public const string Sea = "t-sea";
    public const string Stamps = "t-stamps";
    public const double Radius = 4.0e5;

    private const string MountainsJson = """
    {
      "id": "t-mountains", "schema": 1, "relief": { "minM": -4000, "maxM": 9000 }, "sea": { "present": false }, "materials": "barren",
      "layers": [
        { "name": "base", "type": "fbm", "lambdaR": 1.0, "octaves": 5, "normalize": true },
        { "name": "plains", "type": "curve", "in": "base", "out": "h", "ease": true, "knots": [[-1, -1500], [0, 0], [1, [1200, 1800]]] },
        { "name": "bend", "type": "warp", "lambdaM": 8000, "octaves": 3, "amplitudeM": 660, "gain": 0.5 },
        { "name": "beltNoise", "type": "fbm", "lambdaR": 0.4, "octaves": 3, "normalize": true },
        { "name": "ranges", "type": "ridged", "out": "h", "lambdaM": 20000, "octaves": 12, "amplitudeM": [2400, 3200], "gain": 0.45, "warp": "bend",
          "gate": { "channel": "beltNoise", "from": -0.05, "to": 0.2 } },
        { "name": "hills", "type": "billow", "out": "h", "lambdaM": 4000, "octaves": 8, "amplitudeM": 200, "gain": 0.5,
          "gate": { "not": { "channel": "beltNoise", "from": 0.0, "to": 0.3 } } },
        { "name": "gullies", "type": "eroded", "out": "h", "lambdaM": 3000, "octaves": 10, "amplitudeM": [150, 300], "damping": [0.6, 1.5],
          "gate": { "any": [ { "alt": [800, 400] }, { "lat": [0.5, 0.8] } ] } },
        { "name": "soil", "type": "fbm", "out": "h", "lambdaM": 32, "octaves": 5, "amplitudeM": 1.5, "gain": 0.5 }
      ]
    }
    """;

    private const string MesaJson = """
    {
      "id": "t-mesa", "schema": 1, "relief": { "minM": -3000, "maxM": 7000 }, "sea": { "present": false }, "materials": "desert",
      "layers": [
        { "name": "base", "type": "fbm", "lambdaR": 1.0, "octaves": 6, "normalize": true },
        { "name": "plateau", "type": "curve", "in": "base", "out": "h", "knots": [[-1, -1800], [-0.15, -400], [0.05, [900, 1400]], [0.12, [1300, 1900]], [1, [1800, 2400]]] },
        { "name": "mesa", "type": "fbm", "lambdaM": 90000, "octaves": 3, "normalize": true },
        { "name": "strata", "type": "fbm", "lambdaM": 24000, "octaves": 2, "amplitudeM": [20, 60] },
        { "name": "mesas", "type": "terrace", "levels": { "count": [5, 8], "fromM": 300, "toM": 2600, "jitter": 0.35 }, "riserBandM": 2, "benchSlope": 0.03,
          "warp": "strata", "strength": { "channel": "mesa", "from": -0.1, "to": 0.25 } },
        { "name": "steps", "type": "terrace", "levels": { "count": 4, "fromM": -1500, "toM": 200 }, "riserBandM": 8, "benchSlope": 0.1 },
        { "name": "meander", "type": "warp", "lambdaM": [6000, 9000], "octaves": 2, "amplitudeM": [400, 750] },
        { "name": "canyon", "type": "fbm", "lambdaM": [20000, 80000], "octaves": 3, "gain": 0.45, "warp": "meander", "precise": true, "gradient": true },
        { "name": "cfade", "type": "fbm", "lambdaM": 150000, "octaves": 2 },
        { "name": "canyons", "type": "canyon", "field": "canyon", "halfWidthM": [700, 1600], "depthM": [500, 1100], "floor": 0.3, "benches": [2, 3],
          "gate": { "all": [ { "alt": [600, 900] }, { "channel": "cfade", "from": -0.2, "to": 0.2 } ] } },
        { "name": "gravel", "type": "fbm", "out": "h", "lambdaM": 32, "octaves": 5, "amplitudeM": 1.0, "gain": 0.5 }
      ]
    }
    """;

    private const string SeaJson = """
    {
      "id": "t-sea", "schema": 1, "relief": { "minM": -6000, "maxM": 6000 }, "sea": { "present": true }, "continent": "continent", "materials": "temperate",
      "layers": [
        { "name": "continent", "type": "fbm", "lambdaR": 1.25, "octaves": 6, "normalize": true },
        { "name": "shape", "type": "curve", "in": "continent", "out": "h", "knots": [[-1, -5000], [-0.3, -3500], [-0.03, -150], [0, 0], [0.4, 900], [1, 2200]] },
        { "name": "inland", "type": "mask", "channel": "continent", "from": 0.0, "to": 0.15 },
        { "name": "hills", "type": "eroded", "out": "h", "lambdaM": 6000, "octaves": 11, "amplitudeM": 400, "damping": 1.0, "scale": { "channel": "inland" } },
        { "name": "soil", "type": "fbm", "out": "h", "lambdaM": 16, "octaves": 4, "amplitudeM": 0.6, "gain": 0.5, "scale": { "channel": "inland" } }
      ]
    }
    """;

    private const string StampsJson = """
    {
      "id": "t-stamps", "schema": 1, "relief": { "minM": -6000, "maxM": 6000 }, "sea": { "present": false }, "materials": "barren",
      "layers": [
        { "name": "base", "type": "fbm", "out": "h", "lambdaR": 1.0, "octaves": 6, "amplitudeM": 800 },
        { "name": "impacts", "type": "craters", "radiusM": [128, 16000], "coverage": 0.12, "age": [0.0, 1.0], "complexDiameterM": 8000, "freshOut": "fresh" },
        { "name": "dunes", "type": "ergs", "cellKm": 120, "presence": 0.6, "radiusKm": [20, 45], "lambdaM": [90, 260], "heightRatio": [0.06, 0.10],
          "gate": { "alt": [200, -200] } },
        { "name": "regolith", "type": "fbm", "out": "h", "lambdaM": 32, "octaves": 5, "amplitudeM": 1.2, "scale": { "channel": "fresh" } }
      ]
    }
    """;

    static TestKinds()
    {
        PlanetKinds.Register(PlanetKind.Parse(StampsJson));
        PlanetKinds.Register(PlanetKind.Parse(MountainsJson));
        PlanetKinds.Register(PlanetKind.Parse(MesaJson));
        PlanetKinds.Register(PlanetKind.Parse(SeaJson));
    }

    private static readonly Dictionary<string, PlanetGenerator> Generators = new();

    public static PlanetGenerator Generator(string kind)
    {
        lock (Generators)
        {
            if (!Generators.TryGetValue(kind, out var generator))
                Generators[kind] = generator = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(0x5EED_2026UL, 3), RadiusOf(kind), 5.0, kind: kind));

            return generator;
        }
    }

    public const string Temperate = "temperate-continental";
    public const string Barren = "barren-cratered";
    public const string Desert = "desert-canyon";

    /// <summary>The shipped planet kinds run at the test system's planet radius, the moon's kind and the test kinds at the moon's.</summary>
    public static double RadiusOf(string kind) => kind is Temperate or Desert ? 1.0e6 : Radius;

    public static void RandomDirection(Random random, out double x, out double y, out double z)
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

    public static ChunkCoord SurfaceChunk(PlanetGenerator generator, double x, double y, double z, int lod, double offset = 0.0)
    {
        var r = generator.Params.Radius + generator.Surface.Height(x, y, z, lod) + offset;
        return ChunkCoord.FromPlanet(x * r, y * r, z * r, lod);
    }
}

public sealed class PlanetLayerTests(ITestOutputHelper output)
{
    private static VoxelChunk Generate(ChunkCoord coord, PlanetGenerator generator, PlanetChunkScratch scratch, bool withMask = false)
    {
        var chunk = new VoxelChunk { Coord = coord };
        generator.Generate(chunk, scratch, withMask);
        return chunk;
    }

    [Theory]
    [InlineData(TestKinds.Mountains)]
    [InlineData(TestKinds.Mesa)]
    [InlineData(TestKinds.Sea)]
    [InlineData(TestKinds.Stamps)]
    [InlineData(TestKinds.Temperate)]
    [InlineData(TestKinds.Barren)]
    [InlineData(TestKinds.Desert)]
    public void ProgramsAreDeterministic(string kind)
    {
        var p = TestKinds.Generator(kind).Params;
        var again = PlanetParams.Generate(p.Seed, p.Radius, p.SurfaceGravity, kind: kind);
        var other = PlanetParams.Generate(p.Seed + 1, p.Radius, p.SurfaceGravity, kind: kind);
        var reseeded = PlanetParams.Generate(p.Seed, p.Radius, p.SurfaceGravity, kind: kind, kindSeed: 99);

        Assert.Equal(p, again);
        Assert.NotEqual(0UL, p.ProgramHash);
        Assert.NotEqual(p.ProgramHash, other.ProgramHash);
        Assert.NotEqual(p.ProgramHash, reseeded.ProgramHash);
        Assert.True(TestKinds.Generator(kind).Surface.Program.ChannelCount <= PlanetKindProgram.MaxChannels);

        var a = new PlanetGenerator(p);
        var b = new PlanetGenerator(again);
        Assert.Equal(PlanetKindTests.GoldenHashes(a), PlanetKindTests.GoldenHashes(b));
    }

    /// <summary>Heights stay inside the relief range (the knee), and inside the bounds of every cap, at every LOD from minLod.</summary>
    [Theory]
    [InlineData(TestKinds.Mountains, 0)]
    [InlineData(TestKinds.Mountains, 3)]
    [InlineData(TestKinds.Mesa, 0)]
    [InlineData(TestKinds.Mesa, 5)]
    [InlineData(TestKinds.Sea, 0)]
    [InlineData(TestKinds.Sea, 3)]
    [InlineData(TestKinds.Stamps, 0)]
    [InlineData(TestKinds.Stamps, 4)]
    [InlineData(TestKinds.Stamps, 8)]
    [InlineData(TestKinds.Temperate, 0)]
    [InlineData(TestKinds.Temperate, 3)]
    [InlineData(TestKinds.Barren, 0)]
    [InlineData(TestKinds.Barren, 3)]
    [InlineData(TestKinds.Desert, 0)]
    [InlineData(TestKinds.Desert, 3)]
    public void BoundsHoldEverySample(string kind, int minLod)
    {
        var surface = TestKinds.Generator(kind).Surface;
        var random = new Random(3 + minLod);
        var p = surface.Params;
        var samples = 0;

        foreach (var angle in new[] { 3e-6, 1e-5, 1e-4, 1e-3, 1e-2, 0.1 })
        {
            for (var n = 0; n < 50; n++)
            {
                TestKinds.RandomDirection(random, out var x, out var y, out var z);
                surface.Bounds(x, y, z, angle, minLod, PlanetGenerator.MaxLod, out var lo, out var hi);

                for (var k = 0; k < 36; k++)
                {
                    TestKinds.RandomDirection(random, out var ox, out var oy, out var oz);
                    var t = angle * random.NextDouble() * 0.99;
                    var px = x + ox * t;
                    var py = y + oy * t;
                    var pz = z + oz * t;
                    var l = Math.Sqrt(px * px + py * py + pz * pz);
                    var lod = minLod + k % (PlanetGenerator.MaxLod - minLod + 1);
                    var h = surface.Height(px / l, py / l, pz / l, lod);
                    Assert.True(h >= lo && h <= hi, $"{kind} LOD {lod} angle {angle}: {h} outside {lo} .. {hi}");
                    Assert.InRange(h, p.MinHeight, p.MaxHeight);
                    samples++;
                }
            }
        }

        Assert.True(samples >= 10_000);
    }

    [Theory]
    [InlineData(TestKinds.Mountains, 0)]
    [InlineData(TestKinds.Mountains, 2)]
    [InlineData(TestKinds.Mesa, 0)]
    [InlineData(TestKinds.Mesa, 3)]
    [InlineData(TestKinds.Sea, 0)]
    [InlineData(TestKinds.Sea, 4)]
    [InlineData(TestKinds.Stamps, 0)]
    [InlineData(TestKinds.Stamps, 2)]
    [InlineData(TestKinds.Stamps, 5)]
    [InlineData(TestKinds.Temperate, 0)]
    [InlineData(TestKinds.Temperate, 3)]
    [InlineData(TestKinds.Barren, 0)]
    [InlineData(TestKinds.Barren, 4)]
    [InlineData(TestKinds.Desert, 0)]
    [InlineData(TestKinds.Desert, 2)]
    public void NeighboursAgreeOnAprons(string kind, int lod)
    {
        var generator = TestKinds.Generator(kind);
        var scratch = new PlanetChunkScratch();
        var other = new PlanetChunkScratch();
        var random = new Random(17 + lod);
        const int last = ChunkConstants.Size;
        Span<(double, double, double)> spots = [(1.0, 1.0, 1.0), (1.0, 1.0, 0.3), (0, 0, 0), (0, 0, 0), (0, 0, 0)];

        foreach (var (sx, sy, sz) in spots)
        {
            double x = sx, y = sy, z = sz;

            if (x == 0.0)
                TestKinds.RandomDirection(random, out x, out y, out z);

            var l = Math.Sqrt(x * x + y * y + z * z);
            var coord = TestKinds.SurfaceChunk(generator, x / l, y / l, z / l, lod);
            var centre = Generate(coord, generator, scratch);

            for (var axis = 0; axis < 3; axis++)
            {
                var next = axis switch
                {
                    0 => coord with { X = coord.X + 1 },
                    1 => coord with { Y = coord.Y + 1 },
                    _ => coord with { Z = coord.Z + 1 },
                };
                var neighbour = Generate(next, generator, other);

                for (var b = -1; b <= last; b++)
                for (var a = -1; a <= last; a++)
                for (var k = 0; k < 2; k++)
                {
                    var (i0, i1) = axis switch
                    {
                        0 => (ChunkConstants.Index(last - 1 + k, a, b), ChunkConstants.Index(k - 1, a, b)),
                        1 => (ChunkConstants.Index(a, last - 1 + k, b), ChunkConstants.Index(a, k - 1, b)),
                        _ => (ChunkConstants.Index(a, b, last - 1 + k), ChunkConstants.Index(a, b, k - 1)),
                    };

                    Assert.Equal(centre.Density[i0], neighbour.Density[i1]);
                    Assert.Equal(centre.Material[i0], neighbour.Material[i1]);
                }
            }
        }
    }

    /// <summary>
    /// Every node classified uniform for leaves from wantLod up generates saturated at its own LOD and at wantLod (a
    /// descendant chunk inside it), voxel by voxel.
    /// </summary>
    [Theory]
    [InlineData(TestKinds.Mountains)]
    [InlineData(TestKinds.Mesa)]
    [InlineData(TestKinds.Sea)]
    [InlineData(TestKinds.Stamps)]
    [InlineData(TestKinds.Temperate)]
    [InlineData(TestKinds.Barren)]
    [InlineData(TestKinds.Desert)]
    public void ShellClassificationMatchesBruteForce(string kind)
    {
        var generator = TestKinds.Generator(kind);
        var bounds = new PlanetHeightBounds(generator, 32);
        bounds.Build();
        var scratch = new PlanetChunkScratch();
        var random = new Random(5);
        var rock = (byte)generator.Params.Materials.Rock;
        int uniform = 0, offSurface = 0;

        for (var n = 0; n < 10; n++)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);

            foreach (var lod in new[] { 0, 1, 2, 4, 6, 9, 10 })
            {
                var size = ChunkConstants.Size * (double)(1 << lod);
                var wantLod = Math.Max(0, lod - 1 - n % 3);

                for (var k = -4; k <= 4; k++)
                {
                    var coord = TestKinds.SurfaceChunk(generator, x, y, z, lod, k * size * 0.75);
                    var cls = bounds.Classify(coord, wantLod);
                    var full = Generate(coord, generator, scratch, withMask: true);
                    var fast = Generate(coord, generator, scratch);

                    Assert.Equal(full.Density, fast.Density);
                    Assert.Equal(full.Material, fast.Material);

                    if (Math.Abs(k) >= 3)
                    {
                        offSurface++;
                        uniform += cls == NodeClass.Surface ? 0 : 1;
                    }

                    if (cls == NodeClass.Surface)
                        continue;

                    var air = cls == NodeClass.Air;
                    var origin = coord.Origin;
                    var inside = ChunkCoord.FromPlanet(origin.X + random.NextDouble() * size, origin.Y + random.NextDouble() * size, origin.Z + random.NextDouble() * size, wantLod);
                    var fine = Generate(inside, generator, scratch, withMask: true);

                    for (var i = 0; i < ChunkConstants.PaddedVolume; i++)
                    {
                        Assert.Equal(air ? sbyte.MaxValue : -sbyte.MaxValue, full.Density[i]);
                        Assert.Equal(air ? (byte)VoxelMaterial.Air : rock, full.Material[i]);
                        Assert.Equal(air ? sbyte.MaxValue : -sbyte.MaxValue, fine.Density[i]);
                    }
                }
            }
        }

        output.WriteLine($"{kind}: {uniform} of {offSurface} nodes 2.25+ chunk sizes off the ground classified uniform");
        Assert.True(uniform * 4 >= offSurface, $"Only {uniform} of {offSurface} off-surface nodes classified uniform");
    }

    /// <summary>
    /// A lattice window and a point query see the same stamps: heights from the row evaluator over a window (culled stamp
    /// list, footprint rectangles) equal the per-point lookup wherever no octave sits on the coarse grid.
    /// </summary>
    [Fact]
    public void StampWindowsMatchPointQueries()
    {
        var generator = TestKinds.Generator(TestKinds.Stamps);
        var program = generator.Surface.Program;
        var scratch = new PlanetChunkScratch();
        var random = new Random(47);
        int craters = 0, dunes = 0;

        foreach (var lod in new[] { 0, 2, 5, 8 })
        {
            var n = generator.LatticeN(lod);

            for (var trial = 0; trial < 40; trial++)
            {
                const int w = 24, h = 20;
                var face = random.Next(6);
                int i0 = random.Next(n - w), j0 = random.Next(n - h);
                double[] x = new double[w * h], y = new double[w * h], z = new double[w * h];
                var rows = new float[program.Rows * w * h];

                for (var j = 0; j < h; j++)
                for (var i = 0; i < w; i++)
                {
                    CubeLattice.LatticeDirection(face, i0 + i, j0 + j, n, out var dx, out var dy, out var dz);
                    (x[i + j * w], y[i + j * w], z[i + j * w]) = (dx * generator.Params.Radius, dy * generator.Params.Radius, dz * generator.Params.Radius);
                }

                var set = new RowSet { X = x, Y = y, Z = z, Ch = rows, Count = w * h, Stride = w * h, Channels = program.ChannelCount, W = w, H = h, I0 = i0, J0 = j0, Face = face, N = n, Scratch = scratch, Direct = true };
                var direct = set with { Scratch = null, Ch = new float[rows.Length] };

                for (var layer = 0; layer < program.LayerCount; layer++)
                {
                    if (program.Op[layer] is not (LayerOp.Craters or LayerOp.Ergs))
                        continue;

                    set.Direct = false;
                    Stamps.Evaluate(program, layer, lod, ref set);
                    Stamps.Evaluate(program, layer, lod, ref direct);
                    Assert.True(set.Row(program.Dst[layer]).SequenceEqual(direct.Row(program.Dst[layer])), $"LOD {lod} layer {layer}");

                    foreach (var v in set.Row(program.Dst[layer]))
                    {
                        craters += v < -1f ? 1 : 0;
                        dunes += program.Op[layer] == LayerOp.Ergs && v > 1f ? 1 : 0;
                    }
                }
            }
        }

        output.WriteLine($"{craters} lattice points in craters, {dunes} on dunes");
        Assert.True(craters > 1000 && dunes > 100);
    }

    /// <summary>The Lipschitz constant the bounds rely on holds for the analytic gradient at 2 × 10⁶ points.</summary>
    [Fact]
    public void NoiseGradientStaysUnderTheLipschitzBound()
    {
        var random = new Random(43);
        var steepest = 0f;

        for (var n = 0; n < 2_000_000; n++)
        {
            var v = Noise.Gradient3(random.NextDouble() * 4096.0, random.NextDouble() * 4096.0, random.NextDouble() * 4096.0, (uint)n >> 12, out var gx, out var gy, out var gz);
            Assert.InRange(v, -1.5f, 1.5f);
            steepest = MathF.Max(steepest, MathF.Sqrt(gx * gx + gy * gy + gz * gz));
        }

        output.WriteLine($"Steepest noise gradient {steepest:F3}, bound {Noise.GradientLipschitz}");
        Assert.True(steepest <= Noise.GradientLipschitz);
    }

    /// <summary>The eight-lane lattice rows equal the scalar ones bit for bit: heights and chunk bytes of every kind, either way.</summary>
    [Theory]
    [InlineData("legacy-rolling")]
    [InlineData(TestKinds.Temperate)]
    [InlineData(TestKinds.Barren)]
    [InlineData(TestKinds.Desert)]
    [InlineData(TestKinds.Mountains)]
    public void SimdRowsMatchScalar(string kind)
    {
        var generator = kind == "legacy-rolling" ? TestPlanet.Generator : TestKinds.Generator(kind);
        var scratch = new PlanetChunkScratch();
        var random = new Random(59);
        var surfaces = 0;

        for (var n = 0; n < 12; n++)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);
            var coord = TestKinds.SurfaceChunk(generator, x, y, z, n % 4 * 2);
            var vector = Generate(coord, generator, scratch, withMask: n % 2 == 0);
            var lattice = scratch.Heights.AsSpan(0, 1024).ToArray();

            try
            {
                LayerOps.SimdEnabled = false;
                var scalar = Generate(coord, generator, scratch, withMask: n % 2 == 0);
                Assert.Equal(scalar.Density, vector.Density);
                Assert.Equal(scalar.Material, vector.Material);
                Assert.True(scratch.Heights.AsSpan(0, 1024).SequenceEqual(lattice));
                surfaces += scalar.HasSurface ? 1 : 0;
            }
            finally
            {
                LayerOps.SimdEnabled = true;
            }
        }

        Assert.True(surfaces >= 8);
    }

    [Fact]
    public void WarpThatFoldsIsRejected()
    {
        const string template = """
        { "id": "t-fold", "schema": 1, "relief": { "minM": -1000, "maxM": 1000 }, "sea": { "present": false },
          "layers": [
            { "name": "w", "type": "warp", "lambdaM": 8000, "octaves": 3, "amplitudeM": AMP, "gain": 0.5 },
            { "name": "f", "type": "fbm", "out": "h", "lambdaM": 16000, "octaves": 4, "amplitudeM": 200, "warp": "w" } ] }
        """;
        var p = TestKinds.Generator(TestKinds.Mountains).Params;
        var atLimit = PlanetKindCompiler.Compile(PlanetKind.Parse(template.Replace("AMP", "666")), p);
        Assert.Equal(3 * 3 + 4, atLimit.Wavelength.Length);
        Assert.Throws<InvalidDataException>(() => PlanetKindCompiler.Compile(PlanetKind.Parse(template.Replace("AMP", "680")), p));
    }

    [Fact]
    public void ReliefOverTwiceTheRangeIsRejected()
    {
        const string template = """
        { "id": "t-tall", "schema": 1, "relief": { "minM": -1000, "maxM": 1000 }, "sea": { "present": false },
          "layers": [ { "name": "f", "type": "fbm", "out": "h", "lambdaM": 16000, "octaves": 1, "amplitudeM": AMP } ] }
        """;
        var p = TestKinds.Generator(TestKinds.Mountains).Params with { MinHeight = -1000f, MaxHeight = 1000f };
        var program = PlanetKindCompiler.Compile(PlanetKind.Parse(template.Replace("AMP", "1900")), p);
        Assert.Equal(LayerOp.Curve, program.Op[^1]);
        Assert.Throws<InvalidDataException>(() => PlanetKindCompiler.Compile(PlanetKind.Parse(template.Replace("AMP", "2100")), p));
        Assert.Throws<InvalidDataException>(() => PlanetKind.Parse("""{ "id": "t-wet", "schema": 1, "relief": { "minM": -1, "maxM": 1 }, "sea": { "present": true }, "layers": [] }"""));
    }

    private static PlanetKindProgram CompileLayers(string layers) =>
        PlanetKindCompiler.Compile(PlanetKind.Parse($$"""{ "id": "t-bad", "schema": 1, "relief": { "minM": -4000, "maxM": 4000 }, "sea": { "present": false }, "layers": [ {{layers}} ] }"""), TestKinds.Generator(TestKinds.Mountains).Params);

    private const string Field = """{ "name": "n", "type": "fbm", "lambdaR": 0.5, "octaves": 3, "normalize": true, "gradient": true }, """;

    /// <summary>Bad data fails at compile with the kind and the layer in the message, never as a hang, a NaN or a loose bound.</summary>
    [Theory]
    [InlineData("""{ "name": "g", "type": "fbm", "out": "h", "lambdaM": 900, "octaves": 3, "amplitudeM": 50, "scale": { "channel": "n" } }""", "'n' is not a mask")]
    [InlineData("""{ "name": "g", "type": "terrace", "levels": { "count": 4, "fromM": 0, "toM": 900 }, "strength": { "channel": "n" } }""", "'n' is not a mask")]
    [InlineData("""{ "name": "g", "type": "canyon", "field": "n", "halfWidthM": 500, "depthM": 100, "gate": { "channel": "n" } }""", "'n' is not a mask")]
    [InlineData("""{ "name": "g", "type": "canyon", "field": "n", "halfWidthM": 0, "depthM": 100 }""", "halfWidthM > 0")]
    [InlineData("""{ "name": "g", "type": "canyon", "field": "n", "halfWidthM": 500, "depthM": -100 }""", "depthM")]
    [InlineData("""{ "name": "g", "type": "craters", "radiusM": [0, 5000] }""", "radiusM 0 < min < max")]
    [InlineData("""{ "name": "g", "type": "craters", "radiusM": [100, 5000], "age": [0.8, 0.2] }""", "age")]
    [InlineData("""{ "name": "g", "type": "ergs", "cellKm": 0, "radiusKm": [20, 45], "lambdaM": [90, 260], "heightRatio": [0.06, 0.1] }""", "cellKm > 0")]
    [InlineData("""{ "name": "g", "type": "fbm", "out": "h", "lambdaM": 900, "octaves": 3, "amplitudM": 50 }""", "unknown key 'amplitudM'")]
    [InlineData("""{ "name": "g", "type": "fbm", "out": "h", "lambdaM": 900, "octaves": "three" }""", "not a number")]
    [InlineData("""{ "name": "g", "type": "fbm", "out": "h", "lambdaM": 900, "octaves": 3, "gate": { "channel": "n", "from": 0.1, "too": 0.3 } }""", "unknown key 'too'")]
    [InlineData("""{ "name": "g", "type": "terrace", "riserBandM": 2 }""", "missing 'levels'")]
    [InlineData("""{ "name": "g", "type": "curve", "in": "n", "knots": [[0, 1], [1]] }""", "a knot is [in, out]")]
    [InlineData("""{ "name": "g", "type": "curve", "in": "n", "knots": [[0, "low"], [1, 2]] }""", "not a number")]
    [InlineData("""{ "name": "n", "type": "mask", "lat": [0.1, 0.9] }""", "channel 'n' already exists")]
    public void BadLayersAreRejectedByName(string layer, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => CompileLayers(Field + layer));
        Assert.Contains("t-bad", e.Message);
        Assert.Contains(layer.Contains("\"name\": \"n\"") ? "'n'" : "'g'", e.Message);
        Assert.Contains(message, e.Message);
    }

    [Theory]
    [InlineData("""{ "id": "t-top", "schema": 1, "relif": { "minM": -1, "maxM": 1 }, "layers": [] }""", "unknown key 'relif'")]
    [InlineData("""{ "id": "t-top", "schema": 1, "layers": [] }""", "missing 'relief'")]
    [InlineData("""{ "id": "t-top", "schema": 1, "relief": { "minM": "low", "maxM": 1 }, "layers": [] }""", "malformed")]
    [InlineData("""{ "id": "t-top", "schema": 1, "relief": { "minM": -1, "maxM": 1 }, "layers": {} }""", "layers must be an array")]
    [InlineData("""{ "id": "t-top", "schema": 1, "relief": { "minM": -1, "maxM": 1 }, "materials": "lava", "layers": [] }""", "unknown materials")]
    public void BadKindsAreRejectedByName(string json, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => PlanetKind.Parse(json));
        Assert.Contains("t-top", e.Message);
        Assert.Contains(message, e.Message);
    }

    /// <summary>A gate's seeded range draws on its layer and its place in it, so adding a layer elsewhere leaves it alone.</summary>
    [Fact]
    public void GateRangesDoNotMoveWithOtherLayers()
    {
        const string gated = """{ "name": "g", "type": "fbm", "out": "h", "lambdaM": 900, "octaves": 3, "amplitudeM": 50, "gate": { "all": [ { "lat": [0.1, 0.9] }, { "channel": "n", "from": [-0.3, 0.3], "to": [0.4, 0.9] } ] } }""";
        const string extra = """{ "name": "x", "type": "fbm", "out": "h", "lambdaM": 5000, "octaves": 2, "amplitudeM": 10, "gate": { "lat": [0.2, 0.4] } }, """;

        static (float From, float Inverse) Range(PlanetKindProgram p)
        {
            for (var layer = p.LayerCount - 1; layer >= 0; layer--)
            {
                if (p.Op[layer] == LayerOp.Mask && p.Mode[layer] == LayerMode.MaskChannel && p.LayerNames[layer] == "g")
                    return (p.Params[p.ParamBase[layer]], p.Params[p.ParamBase[layer] + 1]);
            }

            throw new InvalidOperationException("No channel mask");
        }

        var range = Range(CompileLayers(Field + gated));
        Assert.InRange(range.From, -0.3f, 0.3f);
        Assert.Equal(range, Range(CompileLayers(Field + extra + gated)));
    }

    /// <summary>The knee is the identity below 80 % of the range, strictly increasing above it, and never leaves the range.</summary>
    [Fact]
    public void KneeIsMonotoneAndBounded()
    {
        ReadOnlySpan<float> knee = [800f, 2500f, 1000f, (2500f - 800f) / (1000f - 800f), -800f, -1800f, -1000f, (1800f - 800f) / (1000f - 800f)];
        var last = float.NegativeInfinity;

        for (var v = -1800f; v <= 2500f; v += 0.5f)
        {
            var k = LayerOps.KneeAt(knee, v);
            Assert.True(k > last, $"{v}: {k} after {last}");
            Assert.InRange(k, -1000f, 1000f);

            if (v is >= -800f and <= 800f)
                Assert.Equal(v, k);

            last = k;
        }
    }

    /// <summary>Terraces are monotone in the input, zero-mean over a step, and the identity once the riser band spans the step.</summary>
    [Fact]
    public void TerraceIsMonotoneAndZeroMean()
    {
        ReadOnlySpan<float> levels = [100f, 400f, 550f, 900f];

        foreach (var band in new[] { 2f, 40f, 149f })
        {
            var last = float.NegativeInfinity;
            var sum = 0.0;

            for (var u = 100f; u <= 400f; u += 0.25f)
            {
                var t = u + LayerOps.TerraceAt(levels, 0.05f, band, u);
                Assert.True(t >= last);
                Assert.InRange(t, 100f, 400f);
                sum += t - u;
                last = t;
            }

            Assert.InRange(sum / 1200.0, -0.5, 0.5);
        }

        Assert.Equal(0f, LayerOps.TerraceAt(levels, 0.05f, 400f, 317f), 3);
        Assert.Equal(0f, LayerOps.TerraceAt(levels, 0.05f, 2f, 99f));
        Assert.Equal(0f, LayerOps.CanyonProfile(1f, 0.3f, 3f, 0.5f));
        Assert.Equal(1f, LayerOps.CanyonProfile(0.2f, 0.3f, 3f, 0.5f));
    }

    /// <summary>Each LOD's heights against LOD 0 box-filtered over that LOD's voxel: the band limit drops detail, not mean height.</summary>
    [Theory]
    [InlineData(TestKinds.Mountains)]
    [InlineData(TestKinds.Mesa)]
    [InlineData(TestKinds.Sea)]
    [InlineData(TestKinds.Stamps)]
    [InlineData(TestKinds.Temperate)]
    [InlineData(TestKinds.Barren)]
    [InlineData(TestKinds.Desert)]
    public void LodsFollowTheFilteredSurface(string kind) => BandLimit(TestKinds.Generator(kind).Surface, output, 0.35, 1.5);

    internal static void BandLimit(PlanetSurface surface, ITestOutputHelper output, double rmsLimit, double maxLimit)
    {
        var random = new Random(31);
        const int taps = 6;

        foreach (var lod in new[] { 1, 2, 3, 4, 5, 6 })
        {
            var s = (double)(1 << lod);
            double square = 0.0, worst = 0.0;
            const int points = 300;

            for (var n = 0; n < points; n++)
            {
                TestKinds.RandomDirection(random, out var x, out var y, out var z);
                TestKinds.RandomDirection(random, out var ux, out var uy, out var uz);
                var dot = ux * x + uy * y + uz * z;
                (ux, uy, uz) = (ux - dot * x, uy - dot * y, uz - dot * z);
                var ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
                (ux, uy, uz) = (ux / ul, uy / ul, uz / ul);
                var (vx, vy, vz) = (y * uz - z * uy, z * ux - x * uz, x * uy - y * ux);
                var mean = 0.0;

                for (var j = 0; j < taps; j++)
                for (var i = 0; i < taps; i++)
                {
                    var a = ((i + 0.5) / taps - 0.5) * s / surface.Radius;
                    var b = ((j + 0.5) / taps - 0.5) * s / surface.Radius;
                    var (px, py, pz) = (x + ux * a + vx * b, y + uy * a + vy * b, z + uz * a + vz * b);
                    var l = Math.Sqrt(px * px + py * py + pz * pz);
                    mean += surface.Height(px / l, py / l, pz / l);
                }

                var error = (surface.Height(x, y, z, lod) - mean / (taps * taps)) / s;
                square += error * error;
                worst = Math.Max(worst, Math.Abs(error));
            }

            var rms = Math.Sqrt(square / points);
            output.WriteLine($"{surface.Params.KindId} LOD {lod}: rms {rms:F3} max {worst:F3} voxels (targets 0.35 / 1.5)");
            Assert.True(rms <= rmsLimit && worst <= maxLimit, $"{surface.Params.KindId} LOD {lod}: rms {rms:F3} max {worst:F3} voxels");
        }
    }

    [Theory]
    [InlineData(TestKinds.Mountains)]
    [InlineData(TestKinds.Mesa)]
    [InlineData(TestKinds.Sea)]
    [InlineData(TestKinds.Stamps)]
    [InlineData(TestKinds.Temperate)]
    [InlineData(TestKinds.Barren)]
    [InlineData(TestKinds.Desert)]
    public void SamplingAndGenerationAreAllocationFree(string kind)
    {
        var generator = TestKinds.Generator(kind);
        var bounds = new PlanetHeightBounds(generator, 16);
        bounds.Build();
        var scratch = new PlanetChunkScratch();
        var random = new Random(23);
        TestKinds.RandomDirection(random, out var x, out var y, out var z);
        var coord = TestKinds.SurfaceChunk(generator, x, y, z, 0);
        var coarse = TestKinds.SurfaceChunk(generator, x, y, z, 6);
        var chunk = new VoxelChunk { Coord = coord };

        void Run()
        {
            generator.Surface.Height(x, y, z);
            generator.Surface.Bounds(x, y, z, 1e-4, out _, out _);
            bounds.Classify(coord, 0);
            chunk.Coord = coord;
            generator.Generate(chunk, scratch, withMask: true);
            generator.Generate(chunk, scratch);
            chunk.Coord = coarse;
            generator.Generate(chunk, scratch);
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 5; i++)
            Run();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>D30 on a sea kind: past the coastal ramp no ground stands at or below the protected band, and the voxel mask is the base-height test.</summary>
    [Theory]
    [InlineData(TestKinds.Sea)]
    [InlineData(TestKinds.Temperate)]
    public void InlandGroundClearsTheProtectedBand(string kind) => ShoreBand(TestKinds.Generator(kind), output);

    /// <summary>The shipped sea kind's carving layers gate in where its sea clamp starts: land in the coastal ramp never dips under the sea.</summary>
    [Fact]
    public void CoastalRampHoldsNoPits()
    {
        var surface = TestKinds.Generator(TestKinds.Temperate).Surface;
        var program = surface.Program;
        var random = new Random(71);
        Span<float> rows = stackalloc float[program.Rows];
        var ramp = 0;

        for (var n = 0; n < 200_000; n++)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);
            double qx = x * surface.Radius, qy = y * surface.Radius, qz = z * surface.Radius;
            rows.Clear();
            var set = new RowSet { X = new ReadOnlySpan<double>(in qx), Y = new ReadOnlySpan<double>(in qy), Z = new ReadOnlySpan<double>(in qz), Ch = rows, Count = 1, Stride = 1, Channels = program.ChannelCount, W = 1, H = 1, Direct = true };
            LayerOps.Evaluate(program, 0, ref set);
            var continent = rows[program.ContinentChannel];

            if (continent < 0.001f || continent >= program.InlandStart)
                continue;

            ramp++;
            Assert.True(rows[PlanetKindProgram.Height] >= surface.SeaHeight - 0.5f, $"Ground at {rows[PlanetKindProgram.Height] - surface.SeaHeight} m about the sea where the continent is {continent}");
        }

        Assert.True(ramp > 1_000, $"{ramp} ramp samples");
    }

    internal static void ShoreBand(PlanetGenerator generator, ITestOutputHelper output)
    {
        var surface = generator.Surface;
        var program = surface.Program;
        var random = new Random(37);
        Span<float> rows = stackalloc float[program.Rows];
        int inland = 0, sea = 0;

        for (var n = 0; n < 100_000; n++)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);
            double qx = x * surface.Radius, qy = y * surface.Radius, qz = z * surface.Radius;
            rows.Clear();
            var set = new RowSet { X = new ReadOnlySpan<double>(in qx), Y = new ReadOnlySpan<double>(in qy), Z = new ReadOnlySpan<double>(in qz), Ch = rows, Count = 1, Stride = 1, Channels = program.ChannelCount, W = 1, H = 1, Direct = true };
            LayerOps.Evaluate(program, 0, ref set);
            var h = rows[PlanetKindProgram.Height];
            Assert.Equal(h, surface.Height(x, y, z));
            sea += h < surface.SeaHeight ? 1 : 0;

            if (rows[program.ContinentChannel] < program.InlandStart)
                continue;

            inland++;
            Assert.True(h > surface.ProtectHeight, $"Inland ground at {h} m, protected band tops at {surface.ProtectHeight} m");
        }

        output.WriteLine($"{generator.Params.KindId}: {inland} inland, {sea} sea of 100000");
        Assert.True(inland > 10_000 && sea > 10_000);

        var scratch = new PlanetChunkScratch();
        var chunk = new VoxelChunk();
        var masked = 0;

        for (var n = 0; n < 400 && masked < 4; n++)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);
            var h = surface.Height(x, y, z);

            if (h > surface.SeaHeight + 2f || h < surface.SeaHeight - 12f)
                continue;

            chunk.Coord = TestKinds.SurfaceChunk(generator, x, y, z, 0);
            generator.Generate(chunk, scratch, withMask: true);
            masked++;
            Assert.True(scratch.ProtectedCount > 0);
        }

        Assert.True(masked > 0, "No shore chunk found");
    }
}
