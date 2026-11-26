using System.Diagnostics;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class PlanetTests(ITestOutputHelper output)
{
    private const ulong SystemSeed = 0x5EED_2026UL;
    private const double Radius = 1.0e6;

    private static readonly PlanetParams TestPlanet = PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 1), Radius);
    private static readonly PlanetGenerator Generator = new(TestPlanet);

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

    private static void Normalise(ref double x, ref double y, ref double z)
    {
        var l = Math.Sqrt(x * x + y * y + z * z);
        x /= l;
        y /= l;
        z /= l;
    }

    /// <summary>The LOD chunk holding the point <paramref name="offset"/> m above the ground in direction d.</summary>
    private static ChunkCoord SurfaceChunk(double x, double y, double z, int lod, double offset = 0.0)
    {
        var r = Radius + Generator.Surface.Height(x, y, z, lod) + offset;
        return ChunkCoord.FromPlanet(x * r, y * r, z * r, lod);
    }

    private static VoxelChunk Generate(ChunkCoord coord, PlanetGenerator generator, PlanetChunkScratch scratch, bool withMask = false)
    {
        var chunk = new VoxelChunk { Coord = coord };
        generator.Generate(chunk, scratch, withMask);
        return chunk;
    }

    [Fact]
    public void ParamsComeFromTheSystemSeed()
    {
        var again = PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 1), Radius);
        var moon = PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 2), Radius);

        Assert.Equal(TestPlanet, again);
        Assert.NotEqual(TestPlanet.Seed, moon.Seed);
        Assert.Equal(-8000f, TestPlanet.MinHeight);
        Assert.Equal(9000f, TestPlanet.MaxHeight);
        Assert.Equal(6f, TestPlanet.ShoreProtectHeight);
        Assert.True(TestPlanet.HasSea);
        Assert.InRange(TestPlanet.SeaRadius - Radius, -150.0, 150.0);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var other = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed, 1), Radius));
        var random = new Random(7);

        for (var n = 0; n < 6; n++)
        {
            RandomDirection(random, out var x, out var y, out var z);
            Assert.Equal(Generator.Surface.Height(x, y, z), other.Surface.Height(x, y, z));

            var coord = SurfaceChunk(x, y, z, n % 3);
            var a = Generate(coord, Generator, new PlanetChunkScratch());
            var b = Generate(coord, other, new PlanetChunkScratch());
            Assert.Equal(a.Density, b.Density);
            Assert.Equal(a.Material, b.Material);
        }

        var seeded = new PlanetGenerator(PlanetParams.Generate(PlanetParams.BodySeed(SystemSeed + 1, 1), Radius));
        RandomDirection(random, out var sx, out var sy, out var sz);
        Assert.NotEqual(Generator.Surface.Height(sx, sy, sz), seeded.Surface.Height(sx, sy, sz));
    }

    [Fact]
    public void SurfaceChunksHaveGround()
    {
        var random = new Random(11);
        var scratch = new PlanetChunkScratch();
        var surfaces = 0;

        for (var n = 0; n < 20; n++)
        {
            RandomDirection(random, out var x, out var y, out var z);

            for (var lod = 0; lod <= PlanetGenerator.MaxLod; lod += 3)
            {
                var chunk = Generate(SurfaceChunk(x, y, z, lod), Generator, scratch);
                surfaces += chunk.HasSurface ? 1 : 0;
            }
        }

        Assert.True(surfaces >= 90, $"{surfaces} of 100 surface chunks had ground");
    }

    /// <summary>Density is a pure function of position: neighbours agree on every shared apron voxel, across cube faces too.</summary>
    [Theory]
    [InlineData(0.3, 0.8, -0.52, 0)]
    [InlineData(0.3, 0.8, -0.52, 3)]
    [InlineData(1.0, 1.0, 0.3, 0)]
    [InlineData(1.0, 1.0, 1.0, 0)]
    [InlineData(-1.0, 0.2, 1.0, 2)]
    public void NeighboursAgreeOnAprons(double x, double y, double z, int lod)
    {
        Normalise(ref x, ref y, ref z);
        var scratch = new PlanetChunkScratch();
        var coord = SurfaceChunk(x, y, z, lod);
        var centre = Generate(coord, Generator, scratch);
        const int last = ChunkConstants.Size;

        for (var axis = 0; axis < 3; axis++)
        {
            var next = axis switch
            {
                0 => coord with { X = coord.X + 1 },
                1 => coord with { Y = coord.Y + 1 },
                _ => coord with { Z = coord.Z + 1 },
            };
            var neighbour = Generate(next, Generator, scratch);

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

    [Fact]
    public void CornerChunksSpanSeveralFaces()
    {
        var x = 1.0;
        var y = 1.0;
        var z = 1.0;
        Normalise(ref x, ref y, ref z);
        var scratch = new PlanetChunkScratch();
        Generate(SurfaceChunk(x, y, z, 0), Generator, scratch, withMask: true);

        Assert.Equal(3, scratch.WindowCount);
    }

    [Fact]
    public void OctavesAreBandLimitedPerLod()
    {
        var surface = Generator.Surface;
        Assert.Equal(PlanetSurface.Octaves, surface.OctaveCount(0));

        for (var lod = 0; lod <= PlanetGenerator.MaxLod; lod++)
        {
            var min = PlanetSurface.MinWavelength(lod);
            var expected = 0;
            var skipped = 0f;

            for (var i = 0; i < PlanetSurface.Octaves; i++)
            {
                if (surface.Wavelengths[i] >= min)
                    expected++;
                else
                    skipped += surface.Amplitudes[i];
            }

            Assert.Equal(expected, surface.OctaveCount(lod));

            // Each skipped detail octave moves H by at most its amplitude (scale ≤ 1, clamps are 1-Lipschitz).
            var random = new Random(lod);

            for (var n = 0; n < 50; n++)
            {
                RandomDirection(random, out var x, out var y, out var z);
                var full = surface.Height(x, y, z);
                var limited = surface.Height(x, y, z, lod);
                Assert.True(MathF.Abs(full - limited) <= skipped + 1e-3f, $"LOD {lod}: {full} vs {limited}, skipped {skipped}");
            }
        }

        Assert.Equal(2f, surface.Wavelengths[PlanetSurface.Octaves - 1]);
        Assert.Equal(PlanetSurface.Octaves - 1, surface.OctaveCount(1));
    }

    [Fact]
    public void BoundsHoldEverySample()
    {
        var surface = Generator.Surface;
        var random = new Random(3);

        foreach (var angle in new[] { 1e-5, 1e-4, 1e-3, 1e-2 })
        {
            for (var n = 0; n < 40; n++)
            {
                RandomDirection(random, out var x, out var y, out var z);
                surface.Bounds(x, y, z, angle, out var lo, out var hi);

                for (var k = 0; k < 40; k++)
                {
                    RandomDirection(random, out var ox, out var oy, out var oz);
                    var t = angle * random.NextDouble() * 0.99;
                    var px = x + ox * t;
                    var py = y + oy * t;
                    var pz = z + oz * t;
                    Normalise(ref px, ref py, ref pz);
                    var h = surface.Height(px, py, pz);
                    Assert.InRange(h, lo, hi);
                }
            }
        }
    }

    /// <summary>Every node the pyramid calls uniform generates saturated, with the uniform material, voxel by voxel.</summary>
    [Fact]
    public void ShellClassificationMatchesBruteForce()
    {
        var bounds = new PlanetHeightBounds(Generator, 64);
        bounds.Build();
        var scratch = new PlanetChunkScratch();
        var random = new Random(5);
        var rock = (byte)TestPlanet.Materials.Rock;
        int uniform = 0, offSurface = 0;

        for (var n = 0; n < 12; n++)
        {
            RandomDirection(random, out var x, out var y, out var z);

            foreach (var lod in new[] { 0, 1, 2, 4, 6, 9, 12 })
            {
                var size = ChunkConstants.Size * (double)(1 << lod);

                for (var k = -4; k <= 4; k++)
                {
                    var coord = SurfaceChunk(x, y, z, lod, k * size * 0.75);
                    var cls = bounds.Classify(coord);
                    var full = Generate(coord, Generator, scratch, withMask: true);
                    var fast = Generate(coord, Generator, scratch);

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

                    for (var i = 0; i < ChunkConstants.PaddedVolume; i++)
                    {
                        Assert.Equal(air ? sbyte.MaxValue : -sbyte.MaxValue, full.Density[i]);
                        Assert.Equal(air ? (byte)VoxelMaterial.Air : rock, full.Material[i]);
                    }
                }
            }
        }

        output.WriteLine($"{uniform} of {offSurface} nodes 2.25+ chunk sizes off the ground classified uniform");
        Assert.True(uniform * 2 >= offSurface, $"Only {uniform} of {offSurface} off-surface nodes classified uniform");
    }

    [Fact]
    public void FarNodesAreUniformWithoutEvaluation()
    {
        var bounds = new PlanetHeightBounds(Generator, 16);
        bounds.Build();

        Assert.Equal(NodeClass.Air, bounds.Classify(ChunkCoord.FromPlanet(0, Radius + 20000, 0, 4)));
        Assert.Equal(NodeClass.Solid, bounds.Classify(ChunkCoord.FromPlanet(0, Radius - 20000, 0, 4)));
        Assert.Equal(NodeClass.Solid, bounds.Classify(ChunkCoord.FromPlanet(0, 0, Radius * 0.5, 8)));
    }

    [Fact]
    public void EditVisibilityRule()
    {
        Assert.True(EditStore.Applies(0.5f, 0, 1e9, EditStore.DefaultEditRadius));
        Assert.False(EditStore.Applies(3f, 1, 0, EditStore.DefaultEditRadius));
        Assert.True(EditStore.Applies(4f, 1, 0, EditStore.DefaultEditRadius));
        Assert.True(EditStore.Applies(10f, 2, 100, EditStore.DefaultEditRadius));
        Assert.False(EditStore.Applies(10f, 3, 100, EditStore.DefaultEditRadius));
        Assert.False(EditStore.Applies(10f, 1, 8001, EditStore.DefaultEditRadius));
        Assert.True(EditStore.Applies(40f, 3, 50000, EditStore.DefaultEditRadius));
        Assert.False(EditStore.Applies(40f, 5, 50000, EditStore.DefaultEditRadius));

        var store = new EditStore();
        var buffer = new EditOp[4];
        var p = new ChunkCoord(1000, 20, -7).Origin + new Brutal.Numerics.double3(16.0);
        var small = store.Add(EditOp.At(p.X, p.Y, p.Z, 1.5f, BrushMode.Dig, VoxelMaterial.Air));
        var medium = store.Add(EditOp.At(p.X, p.Y, p.Z, 10f, BrushMode.Dig, VoxelMaterial.Air));
        var huge = store.Add(EditOp.At(p.X, p.Y, p.Z, 40f, BrushMode.Dig, VoxelMaterial.Air));

        Assert.Equal(new[] { small, medium, huge }, Gathered(store, p, 0, 0, ref buffer));
        Assert.Equal(new[] { medium, huge }, Gathered(store, p, 1, 0, ref buffer));
        Assert.Equal(new[] { medium, huge }, Gathered(store, p, 2, 0, ref buffer));
        Assert.Equal(new[] { huge }, Gathered(store, p, 3, 0, ref buffer));
        Assert.Equal(new[] { huge }, Gathered(store, p, 1, 9000, ref buffer));
        Assert.Equal(new[] { huge }, Gathered(store, p, 4, 9000, ref buffer));
        Assert.Empty(Gathered(store, p, 5, 0, ref buffer));
        Assert.True(store.Reaches(ChunkCoord.FromPlanet(p.X, p.Y, p.Z, 4), 9000));
        Assert.False(store.Reaches(ChunkCoord.FromPlanet(p.X, p.Y, p.Z, 5), 0));
    }

    /// <summary>Every op is under each octree node over it, whatever its size, and nodes away from every op hold none.</summary>
    [Fact]
    public void EveryOpIsUnderItsAncestors()
    {
        var store = new EditStore();
        var random = new Random(5);
        var centre = new Brutal.Numerics.double3(Radius * 0.6, -Radius * 0.8, 0.0);

        for (var i = 0; i < 200; i++)
        {
            var radius = (float)(i % 20 == 0 ? 32.0 + random.NextDouble() * 2000.0 : 0.5 + random.NextDouble() * 31.0);
            store.Add(EditOp.At(
                centre.X + (random.NextDouble() - 0.5) * 30000.0, centre.Y + (random.NextDouble() - 0.5) * 30000.0, centre.Z + (random.NextDouble() - 0.5) * 30000.0,
                radius, i % 3 == 0 ? BrushMode.Place : BrushMode.Dig, VoxelMaterial.Rock));
        }

        foreach (var op in store.Ops)
        {
            op.Centre(out var x, out var y, out var z);

            for (var lod = 0; lod <= EditStore.RootLod; lod++)
                Assert.True(store.Under(ChunkCoord.FromPlanet(x, y, z, lod)), $"radius {op.Radius} at LOD {lod}");
        }

        for (var lod = 0; lod <= 12; lod++)
            Assert.False(store.Under(ChunkCoord.FromPlanet(-centre.X, -centre.Y, centre.Z + 100000.0, lod)), $"LOD {lod}");
    }

    private static EditOp[] Gathered(EditStore store, Brutal.Numerics.double3 p, int lod, double distance, ref EditOp[] buffer)
    {
        var n = store.Gather(ChunkCoord.FromPlanet(p.X, p.Y, p.Z, lod), distance, ref buffer);
        return buffer.AsSpan(0, n).ToArray();
    }

    /// <summary>The tiered index finds exactly the ops a scan of every op would, in Seq order, at every LOD.</summary>
    [Fact]
    public void EditIndexMatchesFullScan()
    {
        var store = new EditStore();
        var random = new Random(9);
        var centre = new Brutal.Numerics.double3(Radius * 0.6, Radius * 0.8, 0.0);

        for (var i = 0; i < 3000; i++)
        {
            var spread = i % 10 == 0 ? 40000.0 : 3000.0;
            var radius = (float)(i % 50 == 0 ? 30.0 + random.NextDouble() * 3000.0 : 0.5 + random.NextDouble() * random.NextDouble() * 40.0);
            store.Add(EditOp.At(
                centre.X + (random.NextDouble() - 0.5) * spread, centre.Y + (random.NextDouble() - 0.5) * spread, centre.Z + (random.NextDouble() - 0.5) * spread,
                radius, i % 3 == 0 ? BrushMode.Place : BrushMode.Dig, VoxelMaterial.Rock));
        }

        var buffer = new EditOp[8];
        var expected = new List<EditOp>();
        var nonEmpty = 0;

        for (var n = 0; n < 400; n++)
        {
            var lod = n % 13;
            var spread = n % 4 == 0 ? 40000.0 : 3000.0;
            var coord = ChunkCoord.FromPlanet(
                centre.X + (random.NextDouble() - 0.5) * spread, centre.Y + (random.NextDouble() - 0.5) * spread, centre.Z + (random.NextDouble() - 0.5) * spread, lod);
            var distance = random.NextDouble() * 12000.0;
            expected.Clear();

            foreach (var op in store.Ops)
            {
                if (EditStore.Applies(op.Radius, lod, distance, store.EditRadius) && PlanetEditor.Intersects(op, coord))
                    expected.Add(op);
            }

            var count = store.Gather(coord, distance, ref buffer);
            Assert.Equal(expected, buffer.AsSpan(0, count).ToArray());
            Assert.Equal(expected.Count > 0, store.Reaches(coord, distance));
            nonEmpty += count > 0 ? 1 : 0;
        }

        Assert.True(nonEmpty > 50, $"{nonEmpty} nodes had ops");
    }

    [Fact]
    public void EditSeqIsStampedAndOrdered()
    {
        var store = new EditStore();
        var a = store.Add(EditOp.At(0, Radius, 0, 1f, BrushMode.Dig, VoxelMaterial.Air));
        var b = store.Add(EditOp.At(0, Radius, 0, 1f, BrushMode.Dig, VoxelMaterial.Air) with { Seq = 5 });

        Assert.Equal(1u, a.Seq);
        Assert.Equal(5u, b.Seq);
        Assert.Throws<ArgumentException>(() => store.Add(a with { Seq = 4 }));
    }

    [Fact]
    public void PositionsRoundTripAsInt64()
    {
        var random = new Random(13);

        for (var n = 0; n < 1000; n++)
        {
            var x = (random.NextDouble() - 0.5) * 4.0e6;
            var y = (random.NextDouble() - 0.5) * 4.0e6;
            var z = (random.NextDouble() - 0.5) * 4.0e6;
            var op = EditOp.At(x, y, z, 2.5f, BrushMode.Place, VoxelMaterial.Sand, 77) with { Seq = (uint)n + 1 };
            op.Centre(out var cx, out var cy, out var cz);

            Assert.InRange(Math.Abs(cx - x), 0.0, 0.5 / 1024.0 + 1e-9);
            Assert.InRange(Math.Abs(cy - y), 0.0, 0.5 / 1024.0 + 1e-9);
            Assert.InRange(Math.Abs(cz - z), 0.0, 0.5 / 1024.0 + 1e-9);
            Assert.Equal(op, EditOp.At(cx, cy, cz, op.Radius, op.Mode, op.Material, op.Author) with { Seq = op.Seq });

            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                op.Write(writer);

            Assert.Equal(EditOp.StoredSize, stream.Length);
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            Assert.Equal(op, EditOp.Read(reader));
        }
    }

    [Fact]
    public void ChunkKeysAreUnique()
    {
        var keys = new HashSet<ulong>();
        int[] values = [-131072, -31600, -1, 0, 1, 31600, 131071];

        for (var lod = 0; lod < 16; lod++)
        foreach (var z in values)
        foreach (var y in values)
        foreach (var x in values)
            Assert.True(keys.Add(new ChunkCoord(x, y, z, lod).Key));
    }

    /// <summary>A direction whose ground lies 50 m or more under the protected band.</summary>
    private static void FindSeabed(out double x, out double y, out double z)
    {
        var random = new Random(17);

        do
            RandomDirection(random, out x, out y, out z);
        while (Generator.Surface.Height(x, y, z) > Generator.Surface.ProtectHeight - 50f);
    }

    /// <summary>Bisects between a sea and a land direction to the edge of the protected band.</summary>
    private static void FindShore(out double x, out double y, out double z)
    {
        var surface = Generator.Surface;
        var random = new Random(17);
        double sx, sy, sz, lx, ly, lz;

        do
            RandomDirection(random, out sx, out sy, out sz);
        while (surface.Height(sx, sy, sz) > surface.ProtectHeight - 50f);

        do
            RandomDirection(random, out lx, out ly, out lz);
        while (surface.Height(lx, ly, lz) < surface.ProtectHeight + 50f);

        for (var i = 0; i < 80; i++)
        {
            var mx = sx + lx;
            var my = sy + ly;
            var mz = sz + lz;
            Normalise(ref mx, ref my, ref mz);

            if (surface.Height(mx, my, mz) <= surface.ProtectHeight)
                (sx, sy, sz) = (mx, my, mz);
            else
                (lx, ly, lz) = (mx, my, mz);
        }

        (x, y, z) = (sx, sy, sz);
    }

    /// <summary>D30: a dig across the band digs its inland part and stops at the protected face; placed material can go again.</summary>
    [Fact]
    public void ProtectedBandClampsRemoval()
    {
        FindShore(out var dx, out var dy, out var dz);
        var scratch = new PlanetChunkScratch();
        var coord = SurfaceChunk(dx, dy, dz, 0);
        var chunk = Generate(coord, Generator, scratch, withMask: true);
        var generated = (sbyte[])chunk.Density.Clone();

        Assert.InRange(scratch.ProtectedCount, 1, ChunkConstants.PaddedVolume - 1);

        var ground = Radius + Generator.Surface.Height(dx, dy, dz);
        var dig = EditOp.At(dx * ground, dy * ground, dz * ground, 10f, BrushMode.Dig, VoxelMaterial.Air) with { Seq = 1 };
        dig.Centre(out var cx, out var cy, out var cz);
        var local = new Brutal.Numerics.double3(cx, cy, cz) - coord.Origin;
        var free = new VoxelChunk { Coord = coord };
        generated.CopyTo(free.Density, 0);
        PlanetEditor.Apply(free, [dig]);
        PlanetEditor.Apply(chunk, [dig], scratch);
        int dug = 0, held = 0;

        for (var i = 0; i < ChunkConstants.PaddedVolume; i++)
        {
            var wanted = free.Density[i] != generated[i];

            if (scratch.IsProtected(i))
            {
                Assert.Equal(generated[i], chunk.Density[i]);
                held += wanted ? 1 : 0;
            }
            else
            {
                Assert.Equal(free.Density[i], chunk.Density[i]);
                dug += wanted ? 1 : 0;
            }
        }

        Assert.True(dug > 0 && held > 0, $"dug {dug}, held {held}");

        // Placed on the protected ground, then dug again: never past the generated ground, and inside the brush back to it.
        chunk = Generate(coord, Generator, scratch, withMask: true);
        var place = dig with { Mode = BrushMode.Place, Material = VoxelMaterial.Rock, Seq = 1 };
        PlanetEditor.Apply(chunk, [place, dig with { Seq = 2 }], scratch);
        var restored = 0;

        for (var z = -1; z <= ChunkConstants.Size; z++)
        for (var y = -1; y <= ChunkConstants.Size; y++)
        for (var x = -1; x <= ChunkConstants.Size; x++)
        {
            var i = ChunkConstants.Index(x, y, z);

            if (!scratch.IsProtected(i))
                continue;

            Assert.True(chunk.Density[i] <= generated[i]);

            var ex = x - local.X;
            var ey = y - local.Y;
            var ez = z - local.Z;

            if (ex * ex + ey * ey + ez * ez < 5.5 * 5.5)
            {
                Assert.Equal(generated[i], chunk.Density[i]);
                restored += generated[i] > 0 ? 1 : 0;
            }
        }

        Assert.True(restored > 0);
    }

    [Fact]
    public void FullyProtectedOpsCanBeDropped()
    {
        FindSeabed(out var x, out var y, out var z);
        var surface = Generator.Surface;
        var deep = Radius + surface.Height(x, y, z) - 300.0;
        var underBeach = EditOp.At(x * deep, y * deep, z * deep, 4f, BrushMode.Dig, VoxelMaterial.Air);
        var r = Radius + 2000.0;
        var high = EditOp.At(0, r, 0, 4f, BrushMode.Dig, VoxelMaterial.Air);
        var floor = Radius + surface.Height(0, 1, 0) - TestPlanet.DigDepth - 500.0;
        var belowFloor = EditOp.At(0, floor, 0, 4f, BrushMode.Dig, VoxelMaterial.Air);

        // Under the seabed the whole column is protected down to the dig floor, and below the floor nothing can be dug.
        Assert.True(Generator.IsFullyProtected(underBeach));
        Assert.False(Generator.IsFullyProtected(high));
        Assert.True(Generator.IsFullyProtected(belowFloor));
    }

    [Fact]
    public void NoSeaMeansNoBand()
    {
        var dry = new PlanetGenerator(TestPlanet with { SeaRadius = double.NaN });
        var scratch = new PlanetChunkScratch();
        var random = new Random(19);

        for (var n = 0; n < 4; n++)
        {
            RandomDirection(random, out var x, out var y, out var z);
            var h = dry.Surface.Height(x, y, z);
            var r = Radius + h;
            var chunk = new VoxelChunk { Coord = ChunkCoord.FromPlanet(x * r, y * r, z * r, 0) };
            dry.Generate(chunk, scratch, withMask: true);

            Assert.Equal(0, scratch.ProtectedCount);
        }
    }

    [Fact]
    public void DeepHeatFollowsTheTable()
    {
        Assert.Equal(300f, DeepHeat.RockTemperature(TestPlanet, 500f), 0.5f);
        Assert.Equal(418f, DeepHeat.RockTemperature(TestPlanet, 1200f), 0.5f);
        Assert.Equal(1238f, DeepHeat.RockTemperature(TestPlanet, 2000f), 0.5f);
    }

    [Fact]
    public void SamplingAndGenerationAreAllocationFree()
    {
        var bounds = new PlanetHeightBounds(Generator, 16);
        bounds.Build();
        var scratch = new PlanetChunkScratch();
        var store = new EditStore();
        var buffer = new EditOp[64];
        var random = new Random(23);
        RandomDirection(random, out var x, out var y, out var z);
        var coord = SurfaceChunk(x, y, z, 0);
        var origin = coord.Origin;
        store.Add(EditOp.At(origin.X + 5, origin.Y + 5, origin.Z + 5, 3f, BrushMode.Dig, VoxelMaterial.Air));
        var chunk = new VoxelChunk { Coord = coord };

        void Run()
        {
            Generator.Surface.Height(x, y, z);
            Generator.Surface.Bounds(x, y, z, 1e-4, out _, out _);
            bounds.Classify(coord);
            Generator.Generate(chunk, scratch, withMask: true);
            var n = store.Gather(coord, 0, ref buffer);
            PlanetEditor.Apply(chunk, buffer.AsSpan(0, n), scratch);
            Generator.Generate(chunk, scratch);
        }

        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 5; i++)
            Run();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void GenerationCost()
    {
        var scratch = new PlanetChunkScratch();
        var random = new Random(29);
        var coords = new List<ChunkCoord>();

        while (coords.Count < 64)
        {
            RandomDirection(random, out var x, out var y, out var z);
            var coord = SurfaceChunk(x, y, z, 0);
            var chunk = new VoxelChunk { Coord = coord };
            Generator.Generate(chunk, scratch);

            if (chunk.HasSurface)
                coords.Add(coord);
        }

        var target = new VoxelChunk();

        foreach (var lod in new[] { 0, 4, 8, 12 })
        {
            var sw = Stopwatch.StartNew();
            var rounds = 0;

            while (sw.Elapsed.TotalMilliseconds < 500)
            {
                foreach (var c in coords)
                {
                    target.Coord = lod == 0 ? c : ChunkCoord.FromPlanet(c.Origin.X, c.Origin.Y, c.Origin.Z, lod);
                    Generator.Generate(target, scratch);
                }

                rounds++;
            }

            output.WriteLine($"LOD {lod}: {sw.Elapsed.TotalMilliseconds / (rounds * coords.Count):F3} ms per surface leaf");
        }

        var samples = 0;
        var watch = Stopwatch.StartNew();

        while (watch.Elapsed.TotalMilliseconds < 200)
        {
            RandomDirection(random, out var x, out var y, out var z);
            Generator.Surface.Height(x, y, z);
            samples++;
        }

        output.WriteLine($"H(dir) at LOD 0: {watch.Elapsed.TotalMilliseconds * 1000.0 / samples:F3} us");

        var build = Stopwatch.StartNew();
        var bounds = new PlanetHeightBounds(Generator);
        bounds.Build();
        output.WriteLine($"Height bounds 6 x 256^2 + mips: {build.Elapsed.TotalMilliseconds:F0} ms single-threaded");

        var classify = Stopwatch.StartNew();
        var classified = 0;

        while (classify.Elapsed.TotalMilliseconds < 200)
        {
            foreach (var c in coords)
                bounds.Classify(c with { Y = c.Y + 3 });

            classified += coords.Count;
        }

        output.WriteLine($"Classify LOD-0 node: {classify.Elapsed.TotalMilliseconds * 1000.0 / classified:F3} us");
    }
}
