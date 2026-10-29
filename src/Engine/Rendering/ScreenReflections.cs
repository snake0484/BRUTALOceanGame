using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Screen-space reflections on every water surface (water_ssr.glsl, docs/water-ssr.md). Between the opaque pass and the
/// water, <see cref="Record"/> copies the frame's colour and depth (the terrain, before any water) for the water's rays to
/// march through, and writes the ray's uniforms. Water pipelines bind <see cref="Set"/> at their WATER_SSR_SET with
/// <see cref="SetLayout"/>. The copies follow the swapchain's size; a resize waits for the device and rewrites the sets.
/// </summary>
public sealed unsafe class ScreenReflections : IDisposable
{
    private const int UniformAlignment = 256;

    /// <summary>Off: the water reflects the sky only, and nothing is copied (A/B comparisons).</summary>
    public bool Enabled = true;

    /// <summary>Longest reflected ray (m); it fades out over its last fifth.</summary>
    public float MaxDistance = 1000f;

    /// <summary>Most march steps along a ray, rounded up to a multiple of four (fetched and tested together).</summary>
    public int Steps = 24;

    /// <summary>Pixels between steps where the whole reflection shows; sparser as less of it does, up to <see cref="Steps"/>.</summary>
    public float Stride = 4f;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkFormat _colourFormat;
    private readonly GpuBuffer _uniforms;
    private readonly VkSampler _colourSampler;
    private readonly VkSampler _depthSampler;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private VkExtent2D _extent;
    private VkImage _colour;
    private VmaAllocation _colourAllocation;
    private VkImageView _colourView;
    private VkImage _depth;
    private VmaAllocation _depthAllocation;
    private VkImageView _depthView;

    /// <summary>The copies hold nothing yet (created or resized): their first copy runs even when off, so their layout is readable.</summary>
    private bool _fresh;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Screen;
        public Vector4 Params;
    }

    public ScreenReflections(Renderer renderer)
    {
        var context = renderer.Context;
        _device = context.Device;
        _allocator = context.Allocator;
        _colourFormat = renderer.Swapchain.Format.Format;
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _colourSampler = CreateSampler(VkFilter.Linear);

        // Nearest: depth filtered across a silhouette is a surface that isn't there.
        _depthSampler = CreateSampler(VkFilter.Nearest);

        SetLayout = CreateSetLayout();
        _pool = CreatePool();
        AllocateSets();
    }

    public VkDescriptorSetLayout SetLayout { get; }

    public VkDescriptorSet Set(int frameIndex) => _sets[frameIndex];

    private static int UniformStride => (sizeof(Uniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>
    /// Between Renderer.EndOpaquePass and BeginWaterPass: this frame's uniforms, and the frame's colour and depth (both left
    /// in TRANSFER_SRC by the opaque pass) copied for the water to sample.
    /// </summary>
    /// <param name="near">The camera's near plane (m): reverse-Z infinite, so view depth = near / depth.</param>
    /// <param name="depthWanted">The water reads the scene's depth for something else (the fog's bed along the eye's ray), so
    /// the copies are made with the reflections off too.</param>
    public void Record(CommandBuffer commands, int frameIndex, VkImage colour, VkImage depth, VkExtent2D extent, in Matrix4x4 viewProj, float near, bool depthWanted)
    {
        if (extent.Width != _extent.Width || extent.Height != _extent.Height)
            Resize(extent);

        bool copy = Enabled || depthWanted;
        var u = (Uniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        u->ViewProj = viewProj;
        u->Screen = new Vector4(extent.Width, extent.Height, MathF.Max(Stride, 0.5f), copy ? 1f : 0f);
        u->Params = new Vector4(Enabled ? 1f : 0f, MathF.Max(MaxDistance, 1f), Math.Max(Steps, 1), near);

        if (!copy && !_fresh)
            return;

        var colourRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        var depthRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 1 };
        var from = _fresh ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal;

        // The previous frame's water read both copies; nothing of theirs needs keeping.
        commands.PipelineBarrier(
            VkPipelineStageFlags.FragmentShaderBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                ImageBarrier(_colour, colourRange, from, VkImageLayout.TransferDstOptimal, 0, VkAccessFlags.TransferWriteBit),
                ImageBarrier(_depth, depthRange, from, VkImageLayout.TransferDstOptimal, 0, VkAccessFlags.TransferWriteBit),
            ]
        );

        var size = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 };
        var colourLayers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        var depthLayers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.DepthBit, LayerCount = 1 };
        commands.CopyImage(colour, VkImageLayout.TransferSrcOptimal, _colour, VkImageLayout.TransferDstOptimal,
            [new VkImageCopy { SrcSubresource = colourLayers, DstSubresource = colourLayers, Extent = size }]);
        commands.CopyImage(depth, VkImageLayout.TransferSrcOptimal, _depth, VkImageLayout.TransferDstOptimal,
            [new VkImageCopy { SrcSubresource = depthLayers, DstSubresource = depthLayers, Extent = size }]);

        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                ImageBarrier(_colour, colourRange, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit),
                ImageBarrier(_depth, depthRange, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit),
            ]
        );
        _fresh = false;
    }

    private static VkImageMemoryBarrier ImageBarrier(VkImage image, VkImageSubresourceRange range, VkImageLayout from, VkImageLayout to, VkAccessFlags srcAccess, VkAccessFlags dstAccess) => new()
    {
        SrcAccessMask = srcAccess,
        DstAccessMask = dstAccess,
        OldLayout = from,
        NewLayout = to,
        SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
        Image = image,
        SubresourceRange = range,
    };

    /// <summary>New copies at the swapchain's size. Rare, so it waits for the device rather than tracking frames.</summary>
    private void Resize(VkExtent2D extent)
    {
        _device.WaitIdle();
        DestroyImages();
        _extent = extent;
        _colourView = CreateImage(_colourFormat, VkImageAspectFlags.ColorBit, out _colour, out _colourAllocation);
        _depthView = CreateImage(Swapchain.DepthFormat, VkImageAspectFlags.DepthBit, out _depth, out _depthAllocation);
        WriteSets();
        _fresh = true;
    }

    private VkImageView CreateImage(VkFormat format, VkImageAspectFlags aspect, out VkImage image, out VmaAllocation allocation)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out image,
            out allocation,
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        return _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
            null
        );
    }

    private VkSampler CreateSampler(VkFilter filter) => _device.CreateSampler(
        new VkSamplerCreateInfo
        {
            MagFilter = filter,
            MinFilter = filter,
            AddressModeU = VkSamplerAddressMode.ClampToEdge,
            AddressModeV = VkSamplerAddressMode.ClampToEdge,
            AddressModeW = VkSamplerAddressMode.ClampToEdge,
        },
        null
    );

    private void DestroyImages()
    {
        if (_colour.IsNull())
            return;

        _device.DestroyImageView(_colourView, null);
        _allocator.DestroyImage(_colour, _colourAllocation);
        _device.DestroyImageView(_depthView, null);
        _allocator.DestroyImage(_depth, _depthAllocation);
        _colour = default;
        _depth = default;
    }

    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 3, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 2, PoolSizes = sizes },
            null
        );
    }

    private void AllocateSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = SetLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = Renderer.FramesInFlight, SetLayouts = layouts },
            _sets
        );
    }

    private void WriteSets()
    {
        var colour = new VkDescriptorImageInfo { Sampler = _colourSampler, ImageView = _colourView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var depth = new VkDescriptorImageInfo { Sampler = _depthSampler, ImageView = _depthView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(i * UniformStride),
                Range = (ByteSize64)sizeof(Uniforms),
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &info },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &colour },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &depth },
                ],
                []
            );
        }
    }

    public void Dispose()
    {
        _device.WaitIdle();
        DestroyImages();
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(SetLayout, null);
        _device.DestroySampler(_depthSampler, null);
        _device.DestroySampler(_colourSampler, null);
        _uniforms.Dispose();
    }
}
