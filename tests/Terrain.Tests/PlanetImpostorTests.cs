using System.Numerics;
using BrutalGame.Terrain.Runtime.Rendering;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class PlanetImpostorTests(ITestOutputHelper output)
{
    private const int Size = 16;

    private static readonly PlanetGenerator Generator = TestPlanet.Generator;

    private static (ushort[] Heights, uint[] Albedo, ImpostorFaceStats[] Stats) Bake()
    {
        var heights = new ushort[PlanetImpostor.Texels(Size)];
        var albedo = new uint[heights.Length];
        var stats = new ImpostorFaceStats[PlanetImpostor.Faces];

        for (var face = 0; face < PlanetImpostor.Faces; face++)
            stats[face] = PlanetImpostor.BakeFace(Generator, Size, face, heights, albedo);

        return (heights, albedo, stats);
    }

    [Fact]
    public void LevelsAreLaidOutLevelMajor()
    {
        Assert.Equal(9, PlanetImpostor.Levels(256));
        Assert.Equal(0, PlanetImpostor.LevelOffset(256, 0));
        Assert.Equal(6L * 256 * 256, PlanetImpostor.LevelOffset(256, 1));
        Assert.Equal(6L * (256 * 256 + 128 * 128), PlanetImpostor.LevelOffset(256, 2));
        Assert.Equal(6L * (4 * 256 * 256 - 1) / 3, PlanetImpostor.Texels(256));
        Assert.Equal(6L, PlanetImpostor.Texels(1));
    }

    /// <summary>Faces follow the Vulkan cube map: +X, −X, +Y, −Y, +Z, −Z with its s and t axes.</summary>
    [Fact]
    public void FacesFollowTheCubeMapConvention()
    {
        (double X, double Y, double Z)[] centres = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

        for (var face = 0; face < PlanetImpostor.Faces; face++)
        {
            PlanetImpostor.Direction(face, 0.5, 0.5, out var x, out var y, out var z);
            Assert.Equal(centres[face].X, x, 12);
            Assert.Equal(centres[face].Y, y, 12);
            Assert.Equal(centres[face].Z, z, 12);
        }

        PlanetImpostor.Direction(0, 1.0, 0.5, out _, out var ys, out var zs);
        Assert.True(zs < 0 && Math.Abs(ys) < 1e-12, "+X: s runs along -z");
        PlanetImpostor.Direction(0, 0.5, 1.0, out _, out var yt, out var zt);
        Assert.True(yt < 0 && Math.Abs(zt) < 1e-12, "+X: t runs along -y");
        PlanetImpostor.Direction(2, 0.5, 1.0, out _, out _, out var zy);
        Assert.True(zy > 0, "+Y: t runs along +z");
    }

    [Fact]
    public void DirectionAndFaceRoundTrip()
    {
        var random = new Random(11);

        for (var n = 0; n < 2000; n++)
        {
            var face = random.Next(PlanetImpostor.Faces);
            var u = random.NextDouble();
            var v = random.NextDouble();
            PlanetImpostor.Direction(face, u, v, out var x, out var y, out var z);

            Assert.Equal(1.0, x * x + y * y + z * z, 12);
            Assert.Equal(face, PlanetImpostor.Face(x, y, z, out var u2, out var v2));
            Assert.Equal(u, u2, 12);
            Assert.Equal(v, v2, 12);
        }
    }

    [Fact]
    public void HeightCodecCoversTheRelief()
    {
        var p = TestPlanet.Params;
        var quantum = (p.MaxHeight - p.MinHeight) / 65535f;

        Assert.Equal(0, PlanetImpostor.EncodeHeight(p.MinHeight - 100f, p));
        Assert.Equal(65535, PlanetImpostor.EncodeHeight(p.MaxHeight + 100f, p));

        for (var h = p.MinHeight; h <= p.MaxHeight; h += 37.3f)
            Assert.InRange(PlanetImpostor.DecodeHeight(PlanetImpostor.EncodeHeight(h, p), p) - h, -0.51f * quantum - 0.01f, 0.51f * quantum + 0.01f);
    }

    /// <summary>Level 0 is the generator's visible height at each texel centre; alpha marks the sea; stats agree.</summary>
    [Fact]
    public void BakeMatchesTheGenerator()
    {
        var (heights, albedo, stats) = Bake();
        var p = TestPlanet.Params;
        var surface = Generator.Surface;
        var lod = PlanetImpostor.SampleLod(p, Size);
        var quantum = (p.MaxHeight - p.MinHeight) / 65535f;
        var water = 0;

        for (var face = 0; face < PlanetImpostor.Faces; face++)
        {
            var faceWater = 0;

            for (var j = 0; j < Size; j++)
            {
                for (var i = 0; i < Size; i++)
                {
                    PlanetImpostor.Direction(face, (i + 0.5) / Size, (j + 0.5) / Size, out var x, out var y, out var z);
                    var k = face * Size * Size + j * Size + i;
                    var ground = surface.Height(x, y, z, lod);
                    var expected = PlanetImpostor.VisibleHeight(surface, x, y, z, lod);

                    Assert.InRange(PlanetImpostor.DecodeHeight(heights[k], p) - expected, -quantum, quantum);
                    Assert.Equal(surface.HasSea && ground < surface.SeaHeight, albedo[k] >> 24 == 255);
                    Assert.True(albedo[k] >> 24 is 0 or 255, "level 0 water is all or nothing");
                    faceWater += albedo[k] >> 24 == 255 ? 1 : 0;
                }
            }

            Assert.Equal(Size * Size, stats[face].Texels);
            Assert.Equal(faceWater, (int)stats[face].WaterTexels);
            Assert.True(stats[face].MaxSlope >= 0f && float.IsFinite(stats[face].MaxSlope));
            water += faceWater;
        }

        output.WriteLine($"{Size}² per face at generator LOD {lod}: {water} of {6 * Size * Size} texels are sea");
        Assert.InRange(water, 1, 6 * Size * Size - 1);
    }

    /// <summary>Each mip texel is the box filter of the four under it, down to one texel per face.</summary>
    [Fact]
    public void MipsAreBoxFiltered()
    {
        var (heights, albedo, _) = Bake();

        for (var level = 1; level < PlanetImpostor.Levels(Size); level++)
        {
            var n = Size >> level;
            var w = 2 * n;

            for (var face = 0; face < PlanetImpostor.Faces; face++)
            {
                var fine = (int)PlanetImpostor.LevelOffset(Size, level - 1) + face * w * w;
                var coarse = (int)PlanetImpostor.LevelOffset(Size, level) + face * n * n;

                for (var j = 0; j < n; j++)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var k = fine + 2 * j * w + 2 * i;
                        var mean = 0.25 * (heights[k] + heights[k + 1] + heights[k + w] + heights[k + w + 1]);
                        var alpha = 0.25 * ((albedo[k] >> 24) + (albedo[k + 1] >> 24) + (albedo[k + w] >> 24) + (albedo[k + w + 1] >> 24));

                        // Levels are filtered in float from level 0, so each stored level carries its own rounding.
                        Assert.InRange(heights[coarse + j * n + i] - mean, -1.5, 1.5);
                        Assert.InRange((albedo[coarse + j * n + i] >> 24) - alpha, -1.5, 1.5);
                    }
                }
            }
        }
    }

    /// <summary>A coarse leaf on gentle land shows the surface material, as the impostor's albedo does (not the rock a few metres down).</summary>
    [Fact]
    public void CoarseLeavesShowTheSurfaceMaterial()
    {
        const int lod = 3;
        var materials = PlanetMaterials.Temperate;
        var generator = new PlanetGenerator(TestPlanet.Params with { Materials = materials });
        var surface = generator.Surface;
        var random = new Random(5);
        var scratch = new PlanetChunkScratch();
        var top = 0;
        var shown = 0;

        for (var n = 0; n < 4000 && top < 2000; n++)
        {
            // Near +Y, so "up" is the grid's y and the voxel under an air voxel is the top one.
            var x = (random.NextDouble() - 0.5) * 0.3;
            var z = (random.NextDouble() - 0.5) * 0.3;
            var l = Math.Sqrt(x * x + 1.0 + z * z);
            (x, var y, z) = (x / l, 1.0 / l, z / l);
            var h = surface.Height(x, y, z, lod);
            var step = 100.0 / TestPlanet.Radius;
            var slope = Math.Abs(surface.Height(x + step, y, z, lod) - h) + Math.Abs(surface.Height(x, y, z + step, lod) - h);

            if (h < surface.SeaHeight + 60f || h > 3000f || slope > 20.0)
                continue;

            var chunk = new VoxelChunk { Coord = TestPlanet.SurfaceChunk(x, y, z, lod) };
            generator.Generate(chunk, scratch, false);

            for (var cz = 0; cz < ChunkConstants.Size; cz++)
            {
                for (var cy = 0; cy < ChunkConstants.Size; cy++)
                {
                    for (var cx = 0; cx < ChunkConstants.Size; cx++)
                    {
                        var i = ChunkConstants.Index(cx, cy, cz);

                        if (chunk.Density[i] >= 0 || chunk.Density[ChunkConstants.Index(cx, cy + 1, cz)] < 0)
                            continue;

                        top++;
                        shown += chunk.Material[i] == (byte)materials.Surface ? 1 : 0;
                    }
                }
            }
        }

        output.WriteLine($"LOD {lod}: {shown} of {top} top voxels are {materials.Surface}");
        Assert.True(top > 500, "found gentle land");
        Assert.True(shown > 0.8 * top, $"{shown} of {top} top voxels show the surface material");
    }
}

public sealed class DetailLadderTests
{
    [Fact]
    public void StagesSwitchBySizeOnScreen()
    {
        const double radiansPerPixel = 0.001;

        Assert.Equal(2.0 * Math.Asin(0.5) / radiansPerPixel, DetailLadder.PixelDiameter(1.0, 2.0, radiansPerPixel), 2);
        Assert.Equal(Math.PI / radiansPerPixel, DetailLadder.PixelDiameter(1.0, 0.5, radiansPerPixel), 2);

        Assert.Equal(1f, DetailLadder.PointWeight(DetailLadder.PointFull));
        Assert.Equal(0f, DetailLadder.PointWeight(DetailLadder.DiscFull));
        Assert.InRange(DetailLadder.PointWeight(2f), 0.01f, 0.99f);
        Assert.Equal(0f, DetailLadder.Relief(DetailLadder.ReliefStart));
        Assert.Equal(1f, DetailLadder.Relief(DetailLadder.ReliefFull));
        Assert.InRange(DetailLadder.Relief(64f), 0.01f, 0.99f);

        for (var d = 0.5f; d < 200f; d *= 1.1f)
        {
            Assert.True(DetailLadder.PointWeight(d * 1.1f) <= DetailLadder.PointWeight(d));
            Assert.True(DetailLadder.Relief(d * 1.1f) >= DetailLadder.Relief(d));
        }
    }

    [Fact]
    public void ImpostorHandsOverToTheOctreeAcrossTheSwitchBand()
    {
        Assert.Equal(1f, DetailLadder.ImpostorFade(DetailLadder.SwitchHigh));
        Assert.Equal(0f, DetailLadder.ImpostorFade(DetailLadder.SwitchLow));
        Assert.InRange(DetailLadder.ImpostorFade(800_000.0), 0.4f, 0.6f);
        Assert.True(DetailLadder.OctreeTop > DetailLadder.SwitchHigh && DetailLadder.OctreeRelease > DetailLadder.OctreeTop);
        Assert.Equal(0f, DetailLadder.SpaceLook(DetailLadder.SpaceLow));
        Assert.Equal(1f, DetailLadder.SpaceLook(DetailLadder.SpaceHigh));

        // Above the band, and in it until the octree has loaded: the impostor alone, opaque in front.
        Assert.Equal(new FocusBlend(1f, 0f, 1f, DetailLadder.OverlayDepthScale), DetailLadder.Blend(950_000.0, 1f));
        Assert.Equal(new FocusBlend(1f, 0f, 1f, DetailLadder.OverlayDepthScale), DetailLadder.Blend(800_000.0, 0f));
        Assert.Equal(new FocusBlend(1f, 0f, 1f, DetailLadder.OverlayDepthScale), DetailLadder.Blend(300_000.0, 0f));

        // In the band with the octree loaded: fading in front, opaque behind.
        var band = DetailLadder.Blend(800_000.0, 1f);
        Assert.InRange(band.Overlay, 0.4f, 0.6f);
        Assert.Equal(1f, band.Backdrop);
        Assert.True(band.Covers);

        // Below it: only behind, with the rim, down to where the ground's own sky takes over.
        var below = DetailLadder.Blend(300_000.0, 1f);
        Assert.Equal((0f, 1f, 1f), (below.Overlay, below.Backdrop, below.Rim));
        Assert.True(below.Covers);

        var low = DetailLadder.Blend(60_000.0, 1f);
        Assert.Equal(0f, low.Overlay);
        Assert.InRange(low.Backdrop, 0.01f, 0.99f);
        Assert.False(low.Covers);

        // On the ground the impostor never draws, loaded or not.
        Assert.Equal((0f, 0f, 0f), (DetailLadder.Blend(30.0, 1f).Overlay, DetailLadder.Blend(30.0, 1f).Backdrop, DetailLadder.Blend(30.0, 1f).Rim));
        Assert.Equal((0f, 0f, 0f), (DetailLadder.Blend(30.0, 0f).Overlay, DetailLadder.Blend(30.0, 0f).Backdrop, DetailLadder.Blend(30.0, 0f).Rim));

        for (var cover = 0f; cover < 1f; cover += 0.125f)
            Assert.True(DetailLadder.Blend(800_000.0, cover + 0.125f).Overlay <= DetailLadder.Blend(800_000.0, cover).Overlay);
    }

    [Fact]
    public void PointsOfLightFollowPhaseAndSize()
    {
        Assert.Equal(1f, DetailLadder.LambertPhase(1f), 5);
        Assert.Equal(1f / MathF.PI, DetailLadder.LambertPhase(0f), 5);
        Assert.Equal(0f, DetailLadder.LambertPhase(-1f), 5);

        Assert.Equal(Vector3.Zero, DetailLadder.PointColour(Vector3.Zero, 1f));

        var dim = DetailLadder.PointColour(new Vector3(0.2f), 0.5f);
        var bright = DetailLadder.PointColour(new Vector3(0.2f), 1.5f);
        Assert.True(bright.X > dim.X && dim.X > 0f);
        Assert.InRange(bright.X, 0f, 1f);

        // A faint body keeps a visible floor; one a millionth of a lit pixel is gone.
        Assert.True(DetailLadder.PointColour(new Vector3(0.3f), 0.2f).X > 0.2f);
        Assert.True(DetailLadder.PointColour(new Vector3(1e-7f), 0.01f).X < 0.01f);
    }
}
