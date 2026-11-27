using System.Numerics;
using Brutal.VulkanApi;
using BrutalGame.Engine.Rendering;
using BrutalGame.Plans;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The paint finishes as two texture arrays, a layer per finish in catalog order, <see cref="Size"/> square, tiling:
/// <c>Albedo</c> (sRGB: the finish's colour, alpha = where it is scratched to bare metal) and <c>Surface</c> (linear:
/// normal X, Y, roughness, metalness). <see cref="Info"/> gives each layer's tile size, metalness scale and mean
/// luminance (a paint's colour replaces the finish's own, keeping its light and dark). Loaded once at start-up.
/// </summary>
public sealed class FinishTextures : IDisposable
{
    public const int Size = 1024;

    private static readonly (int, int)[] Colour = [(0, 0), (1, 1), (2, 2)];
    private static readonly (int, int)[] Normal = [(0, 0), (1, 1)];
    private static readonly (int, int)[] Red = [(0, 2)];
    private static readonly (int, int)[] Metal = [(0, 3)];
    private static readonly (int, int)[] Bare = [(0, 3)];

    private readonly GpuTextureArray _albedo;
    private readonly GpuTextureArray _surface;

    public FinishTextures(Renderer renderer, FinishCatalog catalog)
    {
        var count = catalog.All.Count;
        Info = new Vector4[Math.Max(count, 1)];
        _albedo = new GpuTextureArray(renderer, VkFormat.R8G8B8A8SRGB, Size, count, (layer, pixels) => Albedo(catalog, layer, pixels), alphaWeighted: false, repeat: true);
        _surface = new GpuTextureArray(renderer, VkFormat.R8G8B8A8UNorm, Size, count, (layer, pixels) => Surface(catalog, layer, pixels), alphaWeighted: false, repeat: true);

        for (var i = 0; i < count; i++)
        {
            Info[i].X = catalog.All[i].TileMetres;
            Info[i].Y = catalog.All[i].Metalness;
        }
    }

    /// <summary>Per layer: x tile metres, y metalness scale, z mean linear luminance of the colour, w unused.</summary>
    public Vector4[] Info { get; }

    public VkImageView AlbedoView => _albedo.View;

    public VkImageView SurfaceView => _surface.View;

    public VkSampler Sampler => _albedo.Sampler;

    public long Bytes => _albedo.Bytes + _surface.Bytes;

    private void Albedo(FinishCatalog catalog, int layer, byte[] pixels)
    {
        Map(catalog.MapPath(layer, "color"), pixels, Colour);
        Map(catalog.MapPath(layer, "bare_mask"), pixels, Bare);

        // Mean luminance of the paint (not the bare metal), linear: a paint colour is scaled by luminance / mean.
        double sum = 0, weight = 0;

        for (var i = 0; i < pixels.Length; i += 4 * 7)
        {
            var paint = 1.0 - pixels[i + 3] / 255.0;
            var l = 0.2126 * Linear(pixels[i]) + 0.7152 * Linear(pixels[i + 1]) + 0.0722 * Linear(pixels[i + 2]);
            sum += l * paint;
            weight += paint;
        }

        Info[layer].Z = weight > 0 ? (float)Math.Max(sum / weight, 1e-3) : 1f;
    }

    private static void Surface(FinishCatalog catalog, int layer, byte[] pixels)
    {
        // Flat, mid-rough and not metal where a map is missing.
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (128, 128, 160, 0);

        Map(catalog.MapPath(layer, "normal"), pixels, Normal);
        Map(catalog.MapPath(layer, "roughness"), pixels, Red);
        Map(catalog.MapPath(layer, "metalness"), pixels, Metal);
    }

    private static void Map(string file, byte[] pixels, ReadOnlySpan<(int, int)> channels)
    {
        if (!GpuTextureArray.TryLoad(file, out var image))
            return;

        try
        {
            GpuTextureArray.Resample(image, pixels, Size, 0, 0, Size, Size, channels);
        }
        finally
        {
            image.Dispose();
        }
    }

    private static double Linear(byte c) => Math.Pow(c / 255.0, 2.2);

    public void Dispose()
    {
        _albedo.Dispose();
        _surface.Dispose();
    }
}
