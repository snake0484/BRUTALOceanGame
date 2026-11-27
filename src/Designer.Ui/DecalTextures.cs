using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The decal images as one sRGB texture array, a layer each, <see cref="Size"/> square with a mip chain built on the CPU.
/// An image that is not square is fitted in, centred, the rest transparent (<see cref="Aspect"/> says its shape). Loaded
/// and uploaded once, through its own command buffer and a wait: at start-up, never in the frame loop. A file that does
/// not load is a transparent layer.
/// </summary>
public sealed unsafe class DecalTextures : IDisposable
{
    public const int Size = 512;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;
    private readonly int _levels;

    public DecalTextures(Renderer renderer, IReadOnlyList<string> files)
    {
        var context = renderer.Context;
        _device = context.Device;
        _allocator = context.Allocator;
        Layers = Math.Max(files.Count, 1);
        _levels = (int)Math.Log2(Size) + 1;
        Aspect = new float[Layers];

        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R8G8B8A8SRGB,
                Extent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
                MipLevels = _levels,
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

        Upload(context, files);
        View = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = _image,
                ViewType = VkImageViewType._2DArray,
                Format = VkFormat.R8G8B8A8SRGB,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = _levels, LayerCount = Layers },
            },
            null
        );
        Sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxLod = _levels,
            },
            null
        );
    }

    public int Layers { get; }

    /// <summary>Each layer's image width over height (1 when it did not load).</summary>
    public float[] Aspect { get; }

    public VkImageView View { get; }

    public VkSampler Sampler { get; }

    private void Upload(VulkanContext context, IReadOnlyList<string> files)
    {
        var levelBytes = new int[_levels];
        var total = 0;

        for (var l = 0; l < _levels; l++)
        {
            var side = Size >> l;
            levelBytes[l] = side * side * 4;
            total += levelBytes[l];
        }

        using var staging = new GpuBuffer(context, total * Layers, VkBufferUsageFlags.TransferSrcBit, true);
        var copies = new VkBufferImageCopy[_levels * Layers];
        var offset = 0;

        for (var layer = 0; layer < Layers; layer++)
        {
            var pixels = new byte[Size * Size * 4];
            Aspect[layer] = layer < files.Count ? Fit(files[layer], pixels) : 1f;

            for (var l = 0; l < _levels; l++)
            {
                var side = Size >> l;

                if (l > 0)
                    pixels = Halve(pixels, side * 2);

                pixels.AsSpan(0, levelBytes[l]).CopyTo(new Span<byte>((byte*)staging.Mapped + offset, levelBytes[l]));
                copies[layer * _levels + l] = new VkBufferImageCopy
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
            var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = _levels, LayerCount = Layers };
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

    /// <summary>The file fitted into the square (bilinear, centred, the rest transparent); returns its aspect, or 1 when it does not load.</summary>
    private static float Fit(string file, byte[] pixels)
    {
        StbImage8 image;

        try
        {
            image = Stb.Load(file, 4);
        }
        catch (Exception)
        {
            return 1f;
        }

        if (image.Data == null || image.Width <= 0 || image.Height <= 0)
            return 1f;

        try
        {
            var source = image.AsSpan();
            int w = image.Width, h = image.Height;
            var scale = Math.Max(w, h) / (double)Size;
            var fw = (int)Math.Round(w / scale);
            var fh = (int)Math.Round(h / scale);
            int x0 = (Size - fw) / 2, y0 = (Size - fh) / 2;

            for (var y = 0; y < fh; y++)
            {
                var sy = Math.Clamp((y + 0.5) * scale - 0.5, 0, h - 1);
                int ya = (int)sy, yb = Math.Min(ya + 1, h - 1);
                var fy = sy - ya;

                for (var x = 0; x < fw; x++)
                {
                    var sx = Math.Clamp((x + 0.5) * scale - 0.5, 0, w - 1);
                    int xa = (int)sx, xb = Math.Min(xa + 1, w - 1);
                    var fx = sx - xa;
                    var o = ((y0 + y) * Size + x0 + x) * 4;

                    for (var c = 0; c < 4; c++)
                    {
                        var top = source[(ya * w + xa) * 4 + c] * (1 - fx) + source[(ya * w + xb) * 4 + c] * fx;
                        var bottom = source[(yb * w + xa) * 4 + c] * (1 - fx) + source[(yb * w + xb) * 4 + c] * fx;
                        pixels[o + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                    }
                }
            }

            return w / (float)h;
        }
        finally
        {
            image.Dispose();
        }
    }

    /// <summary>The next mip level: each 2×2 averaged, colour weighted by alpha so transparent texels do not darken edges.</summary>
    private static byte[] Halve(byte[] pixels, int side)
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
                    var alpha = pixels[o + 3];
                    r += pixels[o] * alpha;
                    g += pixels[o + 1] * alpha;
                    b += pixels[o + 2] * alpha;
                    a += alpha;
                }

                var d = result.AsSpan(((y * half) + x) * 4, 4);

                if (a > 0)
                    (d[0], d[1], d[2]) = ((byte)Math.Round(r / a), (byte)Math.Round(g / a), (byte)Math.Round(b / a));

                d[3] = (byte)Math.Round(a / 4);
            }
        }

        return result;
    }

    public void Dispose()
    {
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(_image, _allocation);
    }
}
