using Brutal.VulkanApi;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The decal images as one sRGB texture array, a layer each, <see cref="Size"/> square. An image that is not square is
/// fitted in, centred, the rest transparent (<see cref="Aspect"/> says its shape). A file that does not load is a
/// transparent layer. Loaded once at start-up.
/// </summary>
public sealed class DecalTextures : IDisposable
{
    public const int Size = 512;

    private static readonly (int, int)[] Rgba = [(0, 0), (1, 1), (2, 2), (3, 3)];

    private readonly GpuTextureArray _array;

    public DecalTextures(Renderer renderer, IReadOnlyList<string> files)
    {
        Aspect = new float[Math.Max(files.Count, 1)];
        Array.Fill(Aspect, 1f);
        _array = new GpuTextureArray(renderer, VkFormat.R8G8B8A8SRGB, Size, files.Count, (layer, pixels) => Aspect[layer] = Fit(files[layer], pixels),
            alphaWeighted: true, repeat: false);
    }

    /// <summary>Each layer's image width over height (1 when it did not load).</summary>
    public float[] Aspect { get; }

    public VkImageView View => _array.View;

    public VkSampler Sampler => _array.Sampler;

    private static float Fit(string file, byte[] pixels)
    {
        if (!GpuTextureArray.TryLoad(file, out var image))
            return 1f;

        try
        {
            var scale = Math.Max(image.Width, image.Height) / (double)Size;
            var fw = (int)Math.Round(image.Width / scale);
            var fh = (int)Math.Round(image.Height / scale);
            GpuTextureArray.Resample(image, pixels, Size, (Size - fw) / 2, (Size - fh) / 2, fw, fh, Rgba);
            return image.Width / (float)image.Height;
        }
        finally
        {
            image.Dispose();
        }
    }

    public void Dispose() => _array.Dispose();
}
