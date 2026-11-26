using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>What one face's bake measured: the steepest visible slope (rise over run) and the mean albedo.</summary>
public struct ImpostorFaceStats
{
    public float MaxSlope;
    public Vector3 AlbedoSum;
    public float WaterTexels;
    public int Texels;
}

/// <summary>
/// The baked cube-map impostor of a rocky body (planets.md "Detail ladder", stages 2-3): per texel the visible height
/// (ground, or the sea where the ground is below it) as R16 over [MinHeight, MaxHeight], and the albedo as sRGB RGBA8
/// with alpha = water, plus a box-filtered mip chain. Faces follow the Vulkan cube map convention so a samplerCube reads
/// it by direction with seamless filtering. A pure function of the params: it never sees edits. Octaves shorter than two
/// texels are skipped (the generator's band limit at the LOD whose voxel is a texel).
/// Layout is level-major: level m holds the six faces in order, each (size >> m)² texels, rows along v.
/// </summary>
public static class PlanetImpostor
{
    public const int Faces = 6;
    public const int BaseSize = 256;
    public const int FocusSize = 1024;

    private const float ShoreRamp = 25f;
    private const float SteepSlope = 0.70f;
    private const float SteepRamp = 0.3f;
    private const float PeakRamp = 500f;
    private const float ShallowDepth = 200f;
    private const int SrgbSteps = 4096;

    // Linear albedo by VoxelMaterial (Air, Rock, Sand, Grass, Dirt), as terrain.frag's palette.
    private static readonly Vector3[] Palette =
    [
        new(1.00f, 0.00f, 1.00f),
        new(0.28f, 0.25f, 0.22f),
        new(0.62f, 0.56f, 0.38f),
        new(0.14f, 0.28f, 0.07f),
        new(0.27f, 0.18f, 0.10f),
    ];

    private static readonly Vector3 Shallow = new(0.04f, 0.12f, 0.13f);
    private static readonly Vector3 Deep = new(0.010f, 0.030f, 0.060f);
    private static readonly byte[] SrgbLut = BuildSrgbLut();

    public static int Levels(int size) => BitOperations.Log2((uint)size) + 1;

    /// <summary>Texels before <paramref name="level"/> (all six faces of every finer level).</summary>
    public static long LevelOffset(int size, int level)
    {
        var offset = 0L;

        for (var k = 0; k < level; k++)
        {
            long n = size >> k;
            offset += Faces * n * n;
        }

        return offset;
    }

    public static long Texels(int size) => LevelOffset(size, Levels(size));

    /// <summary>Arc (rad) of a texel at a face's centre: 2 / size.</summary>
    public static double TexelAngle(int size) => 2.0 / size;

    /// <summary>The generator LOD whose band limit matches a texel: octaves shorter than two texels are skipped.</summary>
    public static int SampleLod(in PlanetParams p, int size) =>
        Math.Clamp((int)Math.Floor(Math.Log2(2.0 * p.Radius / size)), 0, PlanetGenerator.MaxLod);

    /// <summary>Visible height (m about Radius): the ground, or the sea over ground below it.</summary>
    public static float VisibleHeight(PlanetSurface surface, double x, double y, double z, int lod = 0)
    {
        var h = surface.Height(x, y, z, lod);
        return surface.HasSea ? MathF.Max(h, surface.SeaHeight) : h;
    }

    public static ushort EncodeHeight(float height, in PlanetParams p) =>
        (ushort)Math.Clamp(MathF.Round((height - p.MinHeight) / (p.MaxHeight - p.MinHeight) * 65535f), 0f, 65535f);

    public static float DecodeHeight(ushort value, in PlanetParams p) => p.MinHeight + value / 65535f * (p.MaxHeight - p.MinHeight);

    /// <summary>Unit direction of texture coordinates (u, v) in [0, 1] on a face (+X, −X, +Y, −Y, +Z, −Z).</summary>
    public static void Direction(int face, double u, double v, out double x, out double y, out double z)
    {
        var sc = 2.0 * u - 1.0;
        var tc = 2.0 * v - 1.0;

        (x, y, z) = face switch
        {
            0 => (1.0, -tc, -sc),
            1 => (-1.0, -tc, sc),
            2 => (sc, 1.0, tc),
            3 => (sc, -1.0, -tc),
            4 => (sc, -tc, 1.0),
            _ => (-sc, -tc, -1.0),
        };

        var inv = 1.0 / Math.Sqrt(x * x + y * y + z * z);
        x *= inv;
        y *= inv;
        z *= inv;
    }

    /// <summary>Face and texture coordinates of a non-zero direction, as a samplerCube selects them.</summary>
    public static int Face(double x, double y, double z, out double u, out double v)
    {
        var ax = Math.Abs(x);
        var ay = Math.Abs(y);
        var az = Math.Abs(z);
        int face;
        double sc, tc, ma;

        if (ax >= ay && ax >= az)
            (face, sc, tc, ma) = x >= 0 ? (0, -z, -y, ax) : (1, z, -y, ax);
        else if (ay >= az)
            (face, sc, tc, ma) = y >= 0 ? (2, x, z, ay) : (3, x, -z, ay);
        else
            (face, sc, tc, ma) = z >= 0 ? (4, x, -y, az) : (5, -x, -y, az);

        u = 0.5 * (sc / ma + 1.0);
        v = 0.5 * (tc / ma + 1.0);
        return face;
    }

    /// <summary>
    /// Bakes one face at every level into <paramref name="heights"/> and <paramref name="albedo"/> (each the whole
    /// level-major chain, <see cref="Texels"/> long). Faces write disjoint texels: run the six on jobs. Scratch is pooled.
    /// </summary>
    public static ImpostorFaceStats BakeFace(PlanetGenerator generator, int size, int face, Span<ushort> heights, Span<uint> albedo)
    {
        var n = size;
        var texels = n * n;
        var height = ArrayPool<float>.Shared.Rent(texels);
        var colour = ArrayPool<float>.Shared.Rent(texels * 4);

        try
        {
            SampleHeights(generator, n, face, height);
            var stats = Shade(generator, n, height, colour);
            Write(generator.Params, n, face, 0, height, colour, heights, albedo);

            for (var level = 1; level < Levels(size); level++)
            {
                n >>= 1;
                Downsample(n, height, colour);
                Write(generator.Params, n, face, level, height, colour, heights, albedo);
            }

            return stats;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(height);
            ArrayPool<float>.Shared.Return(colour);
        }
    }

    /// <summary>Raw generated heights (not sea-clamped) at texel centres.</summary>
    private static void SampleHeights(PlanetGenerator generator, int n, int face, float[] height)
    {
        var surface = generator.Surface;
        var lod = SampleLod(generator.Params, n);
        var points = ArrayPool<double>.Shared.Rent(3 * n);
        var rows = ArrayPool<float>.Shared.Rent(surface.Program.Rows * n);

        try
        {
            Span<double> px = points.AsSpan(0, n), py = points.AsSpan(n, n), pz = points.AsSpan(2 * n, n);

            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    Direction(face, (i + 0.5) / n, (j + 0.5) / n, out var x, out var y, out var z);
                    (px[i], py[i], pz[i]) = (x * surface.Radius, y * surface.Radius, z * surface.Radius);
                }

                surface.Heights(px, py, pz, lod, height.AsSpan(j * n, n), rows);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(points);
            ArrayPool<float>.Shared.Return(rows);
        }
    }

    /// <summary>Linear albedo + water into <paramref name="colour"/> (4 per texel); heights become visible heights.</summary>
    private static ImpostorFaceStats Shade(PlanetGenerator generator, int n, float[] height, float[] colour)
    {
        var p = generator.Params;
        var surface = generator.Surface;
        var sea = surface.SeaHeight;
        var hasSea = surface.HasSea;
        var peak = p.MaxHeight * PlanetGenerator.PeakFraction;
        var step = 2.0 / n;
        var stats = new ImpostorFaceStats { Texels = n * n };

        for (var j = 0; j < n; j++)
        {
            var b = -1.0 + (j + 0.5) * step;

            for (var i = 0; i < n; i++)
            {
                var a = -1.0 + (i + 0.5) * step;
                var k = j * n + i;
                var h = height[k];

                // Texel spacing on the ground (m) along u and v: gnomonic step times arc per unit, times the radius.
                var q = 1.0 + a * a + b * b;
                var runU = (float)(2.0 * step * Math.Sqrt(1.0 + b * b) / q * p.Radius);
                var runV = (float)(2.0 * step * Math.Sqrt(1.0 + a * a) / q * p.Radius);
                var du = (Visible(height, n, i + 1, j, sea, hasSea) - Visible(height, n, i - 1, j, sea, hasSea)) / runU;
                var dv = (Visible(height, n, i, j + 1, sea, hasSea) - Visible(height, n, i, j - 1, sea, hasSea)) / runV;
                var slope = MathF.Sqrt(du * du + dv * dv);
                stats.MaxSlope = MathF.Max(stats.MaxSlope, slope);

                var water = hasSea && h < sea;
                var c = water ? Vector3.Lerp(Shallow, Deep, Smooth01((sea - h) / ShallowDepth)) : Land(p.Materials, h, slope, sea, hasSea, peak);
                colour[4 * k] = c.X;
                colour[4 * k + 1] = c.Y;
                colour[4 * k + 2] = c.Z;
                colour[4 * k + 3] = water ? 1f : 0f;
                stats.AlbedoSum += c;
                stats.WaterTexels += water ? 1f : 0f;
            }
        }

        for (var k = 0; k < n * n; k++)
            height[k] = hasSea ? MathF.Max(height[k], sea) : height[k];

        return stats;
    }

    /// <summary>Visible height at a texel, clamped to the face (one-sided differences at the edges).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Visible(float[] height, int n, int i, int j, float sea, bool hasSea)
    {
        var h = height[Math.Clamp(j, 0, n - 1) * n + Math.Clamp(i, 0, n - 1)];
        return hasSea ? MathF.Max(h, sea) : h;
    }

    /// <summary>The generator's material rules (PickMaterial) blended over a texel: surface, beach, steep or high rock.</summary>
    private static Vector3 Land(in PlanetMaterials m, float h, float slope, float sea, bool hasSea, float peak)
    {
        var c = Palette[(int)m.Surface];

        if (hasSea)
            c = Vector3.Lerp(c, Palette[(int)m.Shore], 1f - Smooth01((h - sea - PlanetGenerator.BeachHeight) / ShoreRamp));

        var rock = MathF.Max(Smooth01((slope - SteepSlope) / SteepRamp + 0.5f), Smooth01((h - peak) / PeakRamp + 0.5f));
        return Vector3.Lerp(c, Palette[(int)m.Rock], rock);
    }

    /// <summary>2×2 box filter in place: level n reads the previous level (2n wide) from the same arrays.</summary>
    private static void Downsample(int n, float[] height, float[] colour)
    {
        var w = 2 * n;

        for (var j = 0; j < n; j++)
        {
            for (var i = 0; i < n; i++)
            {
                var k00 = 2 * j * w + 2 * i;
                var k10 = k00 + 1;
                var k01 = k00 + w;
                var k11 = k01 + 1;
                var k = j * n + i;
                height[k] = 0.25f * (height[k00] + height[k10] + height[k01] + height[k11]);

                for (var c = 0; c < 4; c++)
                    colour[4 * k + c] = 0.25f * (colour[4 * k00 + c] + colour[4 * k10 + c] + colour[4 * k01 + c] + colour[4 * k11 + c]);
            }
        }
    }

    private static void Write(in PlanetParams p, int n, int face, int level, float[] height, float[] colour, Span<ushort> heights, Span<uint> albedo)
    {
        var size = n << level;
        var start = (int)(LevelOffset(size, level) + (long)face * n * n);
        var h = heights.Slice(start, n * n);
        var a = albedo.Slice(start, n * n);

        for (var k = 0; k < n * n; k++)
        {
            h[k] = EncodeHeight(height[k], p);
            a[k] = Srgb(colour[4 * k]) | (uint)Srgb(colour[4 * k + 1]) << 8 | (uint)Srgb(colour[4 * k + 2]) << 16
                | (uint)(Math.Clamp(colour[4 * k + 3], 0f, 1f) * 255f + 0.5f) << 24;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Srgb(float linear) => SrgbLut[(int)(Math.Clamp(linear, 0f, 1f) * (SrgbSteps - 1) + 0.5f)];

    private static byte[] BuildSrgbLut()
    {
        var lut = new byte[SrgbSteps];

        for (var i = 0; i < SrgbSteps; i++)
        {
            var c = i / (double)(SrgbSteps - 1);
            var s = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
            lut[i] = (byte)Math.Round(s * 255.0);
        }

        return lut;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Smooth01(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v * v * (3f - 2f * v);
    }
}
