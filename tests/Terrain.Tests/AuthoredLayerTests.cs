using Brutal.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class AuthoredLayerTests(ITestOutputHelper output)
{
    private const string Kind = "temperate-continental";
    private const double Radius = 1.0e6;

    private static readonly PlanetParams Base = PlanetParams.Generate(PlanetParams.BodySeed(1, 1), Radius, kind: Kind);
    private static readonly Lazy<(double3 Site, double3 Bump)> Spots = new(FindSpots);

    /// <summary>Two land spots 20 km apart, well above the sea: one for the site, one for the painted bump.</summary>
    private static (double3, double3) FindSpots()
    {
        var surface = new PlanetSurface(Base);
        var random = new Random(5);

        while (true)
        {
            TestKinds.RandomDirection(random, out var x, out var y, out var z);
            var d = new double3(x, y, z);
            var t = double3.Normalize(double3.Cross(d, double3.UnitY));
            var e = double3.Normalize(d + t * (20_000.0 / Radius));
            float h0 = surface.Height(d.X, d.Y, d.Z), h1 = surface.Height(e.X, e.Y, e.Z);

            if (h0 > surface.SeaHeight + 100f && h1 > surface.SeaHeight + 100f && h0 < 2500f && h1 < 2500f)
                return (d, e);
        }
    }

    private static AuthoredLayer Layer(string id, bool bump, bool site)
    {
        var map = new AuthoredMap("shape", 512, 0.05f);
        var layer = new AuthoredLayer { Id = id, Seed = Base.Seed, Kind = Base.KindId, KindSeed = Base.KindSeed, Version = 1 };
        layer.Maps.Add(new AuthoredMapBinding(map, AuthoredMapMode.Height));
        var (s, b) = Spots.Value;

        if (bump)
            map.Paint(b.X, b.Y, b.Z, 12_000.0 / Radius, 400f, AuthoredBrush.Raise);

        if (site)
        {
            var surface = new PlanetSurface(Base);
            var h = MathF.Round(surface.Height(s.X, s.Y, s.Z) + 35f);
            layer.Sites.Add(AuthoredSite.At("pad", s * (Radius + h), 300f, 250f, h, 1));
        }

        return layer;
    }

    private static PlanetGenerator Register(AuthoredLayer layer)
    {
        AuthoredWorld.Register(layer.Snapshot());
        return new PlanetGenerator(Base.WithBody(layer.Id));
    }

    [Fact]
    public void EmptyLayerChangesNothing()
    {
        var plain = new PlanetGenerator(Base);
        var authored = Register(Layer("test-empty", bump: false, site: false));
        Assert.Equal(plain.Params.ProgramHash, authored.Params.ProgramHash);
        Assert.Equal(plain.Surface.Program.ContentHash, authored.Surface.Program.ContentHash);
        Assert.Equal(PlanetKindTests.GoldenHashes(plain), PlanetKindTests.GoldenHashes(authored));
    }

    [Fact]
    public void LayerAppliesOnlyToItsBody()
    {
        var layer = Layer("test-other", bump: true, site: true);
        AuthoredWorld.Register(layer.Snapshot());
        var elsewhere = PlanetParams.Generate(PlanetParams.BodySeed(1, 2), Radius, kind: Kind).WithBody(layer.Id);
        Assert.Null(AuthoredWorld.For(elsewhere));
        Assert.Equal(PlanetParams.Generate(elsewhere.Seed, Radius, kind: Kind).ProgramHash, elsewhere.ProgramHash);
    }

    [Fact]
    public void SiteAndMapShapeTheGround()
    {
        var plain = new PlanetSurface(Base);
        var generator = Register(Layer("test-shape", bump: true, site: true));
        var surface = generator.Surface;
        var (s, b) = Spots.Value;
        var site = AuthoredWorld.Get("test-shape")!.Sites[0];
        Assert.NotEqual(plain.Params.ProgramHash, generator.Params.ProgramHash);

        foreach (var lod in (ReadOnlySpan<int>)[0, 3, 7, 12])
        {
            Assert.Equal(site.HeightM, surface.Height(s.X, s.Y, s.Z, lod));

            // 250 m inside the core edge: still exactly flat.
            var t = double3.Normalize(double3.Cross(s, double3.UnitZ));
            var inside = double3.Normalize(s + t * (50.0 / Radius));
            Assert.Equal(site.HeightM, surface.Height(inside.X, inside.Y, inside.Z, lod));
        }

        var bumped = surface.Height(b.X, b.Y, b.Z) - plain.Height(b.X, b.Y, b.Z);
        output.WriteLine($"bump +{bumped:F1} m at its centre");
        Assert.InRange(bumped, 300f, 400.1f);

        // Outside every reach the ground is the generated one, bit for bit.
        var far = double3.Normalize(-s);
        Assert.Equal(plain.Height(far.X, far.Y, far.Z), surface.Height(far.X, far.Y, far.Z));
    }

    [Fact]
    public void BoundsHoldEverySample()
    {
        var surface = Register(Layer("test-bounds", bump: true, site: true)).Surface;
        var (s, b) = Spots.Value;
        var random = new Random(3);
        var checks = 0;

        foreach (var angle in (ReadOnlySpan<double>)[2e-5, 2e-4, 2e-3, 2e-2])
        {
            for (var n = 0; n < 60; n++)
            {
                var c = n % 2 == 0 ? s : b;
                TestKinds.RandomDirection(random, out var rx, out var ry, out var rz);
                var q = double3.Normalize(c + new double3(rx, ry, rz) * (angle * random.NextDouble() * 2.0));

                foreach (var minLod in (ReadOnlySpan<int>)[0, 4])
                {
                    surface.Bounds(q.X, q.Y, q.Z, angle, minLod, PlanetGenerator.MaxLod, out var lo, out var hi);

                    for (var k = 0; k < 40; k++)
                    {
                        TestKinds.RandomDirection(random, out var px, out var py, out var pz);
                        var p = double3.Normalize(q + new double3(px, py, pz) * (angle * random.NextDouble() * 0.95));

                        foreach (var lod in (ReadOnlySpan<int>)[minLod, minLod + 3, 12])
                        {
                            var h = surface.Height(p.X, p.Y, p.Z, lod);
                            Assert.True(h >= lo && h <= hi, $"H {h} outside [{lo}, {hi}] at angle {angle}, LOD {lod}");
                            checks++;
                        }
                    }
                }
            }
        }

        output.WriteLine($"{checks} samples inside their bounds");
    }

    [Fact]
    public void ChunksAgreeScalarSimdAndAcrossNeighbours()
    {
        var generator = Register(Layer("test-chunks", bump: true, site: true));
        var site = AuthoredWorld.Get("test-chunks")!.Sites[0];
        var (s, b) = Spots.Value;
        var scratch = new PlanetChunkScratch();
        var t = double3.Normalize(double3.Cross(s, double3.UnitY));

        // On the blend ring, where the site's weight changes fastest, and on the bump.
        Span<double3> spots = [double3.Normalize(s + t * ((site.CoreM + 0.5 * site.BlendM) / Radius)), b];

        foreach (var spot in spots)
        {
            foreach (var lod in (ReadOnlySpan<int>)[0, 2, 5])
            {
                var coord = TestKinds.SurfaceChunk(generator, spot.X, spot.Y, spot.Z, lod);
                var vector = new VoxelChunk { Coord = coord };
                var scalar = new VoxelChunk { Coord = coord };
                generator.Generate(vector, scratch);
                LayerOps.SimdEnabled = false;

                try
                {
                    generator.Generate(scalar, scratch);
                }
                finally
                {
                    LayerOps.SimdEnabled = true;
                }

                Assert.True(vector.Density.AsSpan().SequenceEqual(scalar.Density), $"scalar and SIMD differ at LOD {lod}");

                var next = new VoxelChunk { Coord = coord with { X = coord.X + 1 } };
                generator.Generate(next, new PlanetChunkScratch());
                const int last = ChunkConstants.Size;

                for (var bb = -1; bb <= last; bb++)
                for (var a = -1; a <= last; a++)
                for (var k = 0; k < 2; k++)
                    Assert.Equal(vector.Density[ChunkConstants.Index(last - 1 + k, a, bb)], next.Density[ChunkConstants.Index(k - 1, a, bb)]);
            }
        }
    }

    [Fact]
    public void MapIsContinuousAcrossCubeEdges()
    {
        var map = new AuthoredMap("edge", 64, 0.1f);
        var d = double3.Normalize(new double3(1.0, 1.0, 0.3));
        map.Paint(d.X, d.Y, d.Z, 0.2, 100f, AuthoredBrush.Raise);
        map.Freeze();

        for (var k = -20; k <= 20; k++)
        {
            var z = k * 0.03;
            var a = double3.Normalize(new double3(1.0, 1.0 - 1e-9, z));
            var c = double3.Normalize(new double3(1.0 - 1e-9, 1.0, z));
            Assert.InRange(map.Sample(a.X, a.Y, a.Z) - map.Sample(c.X, c.Y, c.Z), -0.01f, 0.01f);
        }

        for (var face = 0; face < AuthoredMap.Faces; face++)
        {
            for (var i = 0; i <= map.Res; i += map.Res)
            for (var j = 0; j <= map.Res; j++)
            {
                map.VertexDirection(face, i, j, out var x, out var y, out var z);
                var l = Math.Sqrt(x * x + y * y + z * z);
                Assert.InRange(map.Value(face, i, j) - map.Sample(x / l * 0.999999, y / l * 0.999999, z / l * 0.999999), -0.11f, 0.11f);
            }
        }
    }

    [Fact]
    public void SaveLoadRoundTrips()
    {
        var layer = Layer("test-save", bump: true, site: true);
        layer.Markers.Add(new AuthoredMarker("newport", "city", "Newport", "pad"));
        var folder = Path.Combine(Path.GetTempPath(), "brutal-authored-" + Guid.NewGuid().ToString("N"));

        try
        {
            layer.Save(folder);
            var again = AuthoredLayer.Load(folder);
            Assert.Equal(layer.ToJson(), again.ToJson());
            Assert.Equal(layer.Hash(), again.Hash());
            Assert.Equal(layer.Maps[0].Map.Write(), again.Maps[0].Map.Write());
            Assert.Equal(layer.Sites[0], again.Sites[0]);
            Assert.Equal(layer.Markers[0], again.Markers[0]);
            again.Save(folder);
            Assert.Equal(layer.Hash(), AuthoredLayer.Load(folder).Hash());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>D85: a player's edits inside the core of a site newer than their save are dropped; everything else replays.</summary>
    [Fact]
    public void UpgradeDropsPlayerEditsOnlyInNewerSiteCores()
    {
        var layer = Layer("test-upgrade", bump: false, site: true);
        var site = layer.Sites[0] with { Since = 3 };
        layer.Sites[0] = site;
        var c = site.Position;
        var t = double3.Normalize(double3.Cross(c, double3.UnitY));
        EditOp[] ops =
        [
            EditOp.At(c.X, c.Y, c.Z, 2f, BrushMode.Dig, VoxelMaterial.Air),
            EditOp.At(c.X + t.X * 280.0, c.Y + t.Y * 280.0, c.Z + t.Z * 280.0, 2f, BrushMode.Dig, VoxelMaterial.Air),
            EditOp.At(c.X + t.X * 400.0, c.Y + t.Y * 400.0, c.Z + t.Z * 400.0, 2f, BrushMode.Dig, VoxelMaterial.Air),
        ];
        var kept = new EditOp[ops.Length];

        Assert.Equal(1, layer.KeepPlayerEdits(ops, savedVersion: 2, kept));
        Assert.Equal(ops[2], kept[0]);
        Assert.Equal(3, layer.KeepPlayerEdits(ops, savedVersion: 3, kept));
    }

    [Fact]
    public void ShippedSystemNamesItsBodies()
    {
        Assert.Equal(1UL, AuthoredWorld.SystemSeed("test"));
        Assert.Equal("home", AuthoredWorld.BodyId(1, 1));
        Assert.Equal("home-moon", AuthoredWorld.BodyId(1, 2));
        Assert.Null(AuthoredWorld.BodyId(2, 1));
    }

    /// <summary>Lattice ns per point at chunks on the site's blend ring and on the bump, with and without the layer.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void AuthoredCost()
    {
        var plain = new PlanetGenerator(Base);
        var authored = Register(Layer("test-cost", bump: true, site: true));
        var site = AuthoredWorld.Get("test-cost")!.Sites[0];
        var (s, b) = Spots.Value;
        var t = double3.Normalize(double3.Cross(s, double3.UnitY));
        var ring = double3.Normalize(s + t * ((site.CoreM + 0.5 * site.BlendM) / Radius));
        var scratch = new PlanetChunkScratch();
        var sw = new System.Diagnostics.Stopwatch();

        foreach (var lod in new[] { 0, 2, 4, 6, 8, 12 })
        {
            double Cost(PlanetGenerator g, double3 at)
            {
                var coord = TestKinds.SurfaceChunk(g, at.X, at.Y, at.Z, lod);
                long points = 0;

                for (var warm = 0; warm < 20; warm++)
                    g.EvaluateLattice(coord, scratch);

                sw.Restart();

                while (sw.Elapsed.TotalMilliseconds < 150)
                    points += g.EvaluateLattice(coord, scratch);

                return sw.Elapsed.TotalMilliseconds * 1e6 / points;
            }

            output.WriteLine($"LOD {lod}: site ring {Cost(plain, ring):F0} -> {Cost(authored, ring):F0} ns/point, bump {Cost(plain, b):F0} -> {Cost(authored, b):F0} ns/point");
        }
    }
}
