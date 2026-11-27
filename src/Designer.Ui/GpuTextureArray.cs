using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// A square RGBA8 texture array with a full mip chain built on the CPU, uploaded once through its own command buffer and a
/// wait (load time only, never in the frame loop). Layers come from callbacks giving each layer's top level.
/// </summary>
public sealed unsafe class GpuTextureArray : IDisposable
{
    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;

    /// <param name="layer">Fills a layer's top level (<paramref name="size"/>² RGBA bytes, zeroed).</param>
    /// <param name="alphaWeighted">Mips weight colour by alpha (images with transparent edges); otherwise a plain average.</param>
    public GpuTextureArray(Renderer renderer, VkFormat format, int size, int layers, Action<int, byte[]> layer, bool alphaWeighted, bool repeat)
    {
        var context = renderer.Context;
        _device = context.Device;
        _allocator = context.Allocator;
        Size = size;
        Layers = Math.Max(layers, 1);
        Levels = (int)Math.Log2(size) + 1;

        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = size, Height = size, Depth = 1 },
                MipLevels = Levels,
                ArrayLayers = Layers,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out _image,
            out _allocation,
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        Upload(context, layers, layer, alphaWeighted);
        View = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = _image,
                ViewType = VkImageViewType._2DArray,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = Levels, LayerCount = Layers },
            },
            null
        );
        var mode = repeat ? VkSamplerAddressMode.Repeat : VkSamplerAddressMode.ClampToEdge;
        Sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = mode,
                AddressModeV = mode,
                AddressModeW = mode,
                AnisotropyEnable = true,
                MaxAnisotropy = 8f,
                MaxLod = Levels,
            },
            null
        );
    }

    public int Size { get; }

    public int Layers { get; }

    public int Levels { get; }

    public VkImageView View { get; }

    public VkSampler Sampler { get; }

    /// <summary>GPU bytes with mips.</summary>
    public long Bytes => (long)Size * Size * 4 * Layers * 4 / 3;

    private void Upload(VulkanContext context, int layers, Action<int, byte[]> fill, bool alphaWeighted)
    {
        var levelBytes = new int[Levels];
        var total = 0;

        for (var l = 0; l < Levels; l++)
        {
            var side = Size >> l;
            levelBytes[l] = side * side * 4;
            total += levelBytes[l];
        }

        using var staging = new GpuBuffer(context, total * Layers, VkBufferUsageFlags.TransferSrcBit, true);
        var copies = new VkBufferImageCopy[Levels * Layers];
        var offset = 0;

        for (var layer = 0; layer < Layers; layer++)
        {
            var pixels = new byte[Size * Size * 4];

            if (layer < layers)
                fill(layer, pixels);

            for (var l = 0; l < Levels; l++)
            {
                var side = Size >> l;

                if (l > 0)
                    pixels = Halve(pixels, side * 2, alphaWeighted);

                pixels.AsSpan(0, levelBytes[l]).CopyTo(new Span<byte>((byte*)staging.Mapped + offset, levelBytes[l]));
                copies[layer * Levels + l] = new VkBufferImageCopy
                {
                    BufferOffset = (ByteSize64)offset,
                    ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = l, BaseArrayLayer = layer, LayerCount = 1 },
                    ImageExtent = new VkExtent3D { Width = side, Height = side, Depth = 1 },
                };
                offset += levelBytes[l];
            }
        }

        var pool = _device.CreateCommandPool(new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.TransientBit, QueueFamilyIndex = context.GraphicsQueue.Family }, null);

        try
        {
            var commands = _device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary });
            commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = Levels, LayerCount = Layers };
            Barrier(commands, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, 0, VkAccessFlags.TransferWriteBit, range);
            commands.CopyBufferToImage(staging.Handle, _image, VkImageLayout.TransferDstOptimal, copies);
            Barrier(commands, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit,
                VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, range);
            commands.End();
            context.GraphicsQueue.Submit([], [], [commands], [], default);
            _device.WaitIdle();
        }
        finally
        {
            _device.DestroyCommandPool(pool, null);
        }
    }

    private void Barrier(CommandBuffer commands, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags after, VkPipelineStageFlags before,
        VkAccessFlags written, VkAccessFlags access, VkImageSubresourceRange range) =>
        commands.PipelineBarrier(after, before, VkDependencyFlags.None, [], [],
        [
            new VkImageMemoryBarrier
            {
                SrcAccessMask = written,
                DstAccessMask = access,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _image,
                SubresourceRange = range,
            },
        ]);

    /// <summary>The next mip level: each 2×2 averaged (colour weighted by alpha when asked, so transparent texels do not darken edges).</summary>
    private static byte[] Halve(byte[] pixels, int side, bool alphaWeighted)
    {
        var half = side / 2;
        var result = new byte[half * half * 4];

        for (var y = 0; y < half; y++)
        {
            for (var x = 0; x < half; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;

                for (var k = 0; k < 4; k++)
                {
                    var o = ((y * 2 + (k >> 1)) * side + x * 2 + (k & 1)) * 4;
                    var weight = alphaWeighted ? pixels[o + 3] : 1.0;
                    r += pixels[o] * weight;
                    g += pixels[o + 1] * weight;
                    b += pixels[o + 2] * weight;
                    a += alphaWeighted ? pixels[o + 3] : pixels[o + 3] * 0.25;
                }

                var d = result.AsSpan((y * half + x) * 4, 4);
                var w = alphaWeighted ? a : 4.0;

                if (w > 0)
                    (d[0], d[1], d[2]) = ((byte)Math.Round(r / w), (byte)Math.Round(g / w), (byte)Math.Round(b / w));

                d[3] = (byte)Math.Round(alphaWeighted ? a / 4 : a);
            }
        }

        return result;
    }

    /// <summary>
    /// Loads <paramref name="file"/> as RGBA (null when it does not load or is not there). The caller disposes it.
    /// </summary>
    public static bool TryLoad(string file, out StbImage8 image)
    {
        image = default;

        if (!File.Exists(file))
            return false;

        try
        {
            image = Stb.Load(file, 4);
        }
        catch (Exception)
        {
            return false;
        }

        return image.Data != null && image.Width > 0 && image.Height > 0;
    }

    /// <summary>
    /// <paramref name="image"/> resampled (box filter when shrinking, bilinear otherwise) to <paramref name="width"/> ×
    /// <paramref name="height"/> into <paramref name="target"/> (row stride <paramref name="stride"/> texels) at
    /// (<paramref name="x0"/>, <paramref name="y0"/>), channels <paramref name="from"/> to the target's <paramref name="to"/>.
    /// </summary>
    public static void Resample(StbImage8 image, byte[] target, int stride, int x0, int y0, int width, int height, ReadOnlySpan<(int From, int To)> channels)
    {
        var source = image.AsSpan();
        int w = image.Width, h = image.Height;
        var sx = w / (double)width;
        var sy = h / (double)height;
        int bx = Math.Max(1, (int)Math.Floor(sx)), by = Math.Max(1, (int)Math.Floor(sy));
        Span<double> sum = stackalloc double[4];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                sum.Clear();

                // Box over the source texels this texel covers (one texel when enlarging).
                var ox = Math.Min((int)(x * sx), w - bx);
                var oy = Math.Min((int)(y * sy), h - by);

                for (var j = 0; j < by; j++)
                {
                    for (var i = 0; i < bx; i++)
                    {
                        var s = ((oy + j) * w + ox + i) * 4;

                        for (var c = 0; c < 4; c++)
                            sum[c] += source[s + c];
                    }
                }

                var o = ((y0 + y) * stride + x0 + x) * 4;

                foreach (var (from, to) in channels)
                    target[o + to] = (byte)Math.Round(sum[from] / (bx * by));
            }
        }
    }

    public void Dispose()
    {
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(_image, _allocation);
    }
}
