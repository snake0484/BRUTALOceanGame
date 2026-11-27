using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Coast;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// GPU copies of a <see cref="CoastRegionSet"/>'s atlases (ocean_breaker.glsl): three array textures with a layer per
/// slot, R32F spatial phase (read unfiltered), RGBA16F fields and RG8 signed direction (both filtered). A slot is
/// uploaded once when its bake goes live, at most one a frame; the renderer draws only slots whose upload is current.
/// </summary>
public sealed unsafe class CoastAtlasGpu : IDisposable
{
    private const int N = CoastAtlas.N;
    private const int Layers = CoastRegionSet.Capacity;
    private const int PhaseBytes = N * N * sizeof(float);
    private const int FieldBytes = N * N * CoastAtlas.FieldChannels * 2;
    private const int DirectionBytes = N * N * 2;
    private const int SlotBytes = PhaseBytes + FieldBytes + DirectionBytes;

    private const VkPipelineStageFlags ShaderStages =
        VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _phase;
    private readonly VmaAllocation _phaseAllocation;
    private readonly VkImage _fields;
    private readonly VmaAllocation _fieldsAllocation;
    private readonly VkImage _direction;
    private readonly VmaAllocation _directionAllocation;
    private readonly GpuBuffer _staging;
    private readonly int[] _uploaded = new int[Layers];
    private bool _initialised;

    public CoastAtlasGpu(Renderer renderer)
    {
        _device = renderer.Context.Device;
        _allocator = renderer.Context.Allocator;
        (_phase, _phaseAllocation) = CreateImage(VkFormat.R32SFloat);
        (_fields, _fieldsAllocation) = CreateImage(VkFormat.R16G16B16A16SFloat);
        (_direction, _directionAllocation) = CreateImage(VkFormat.R8G8SNorm);
        PhaseView = CreateView(_phase, VkFormat.R32SFloat);
        FieldsView = CreateView(_fields, VkFormat.R16G16B16A16SFloat);
        DirectionView = CreateView(_direction, VkFormat.R8G8SNorm);
        NearestSampler = CreateSampler(VkFilter.Nearest);
        LinearSampler = CreateSampler(VkFilter.Linear);
        _staging = new GpuBuffer(renderer.Context, SlotBytes * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
    }

    public VkImageView PhaseView { get; }

    public VkImageView FieldsView { get; }

    public VkImageView DirectionView { get; }

    public VkSampler NearestSampler { get; }

    public VkSampler LinearSampler { get; }

    /// <summary>Uploads so far, and the last one's CPU copy into staging (ms).</summary>
    public int Uploads { get; private set; }

    public float LastUploadMs { get; private set; }

    public static int Bytes => Layers * SlotBytes;

    /// <summary>The slot's texture layer holds the region's current bake.</summary>
    public bool Ready(int slot, in CoastRegion region) => _uploaded[slot] == region.Version;

    /// <summary>Uploads one slot whose bake went live since its last upload. Record outside a render pass, before the ocean's passes.</summary>
    public void Record(CommandBuffer commands, int frameIndex, CoastRegionSet set)
    {
        var all = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = Layers };

        if (!_initialised)
        {
            Transition(commands, _phase, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags.TopOfPipeBit, ShaderStages, 0, VkAccessFlags.ShaderReadBit, all);
            Transition(commands, _fields, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags.TopOfPipeBit, ShaderStages, 0, VkAccessFlags.ShaderReadBit, all);
            Transition(commands, _direction, VkImageLayout.Undefined, VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags.TopOfPipeBit, ShaderStages, 0, VkAccessFlags.ShaderReadBit, all);
            _initialised = true;
        }

        var regions = set.Regions;

        for (var slot = 0; slot < regions.Length; slot++)
        {
            ref readonly var region = ref regions[slot];

            if (region.State != CoastRegionState.Live || _uploaded[slot] == region.Version)
                continue;

            Upload(commands, frameIndex, slot, set.Atlas(slot));
            _uploaded[slot] = region.Version;
            return;
        }
    }

    private void Upload(CommandBuffer commands, int frameIndex, int slot, CoastAtlas atlas)
    {
        var start = Stopwatch.GetTimestamp();
        var offset = frameIndex * SlotBytes;
        var staging = new Span<byte>((byte*)_staging.Mapped + offset, SlotBytes);
        MemoryMarshal.AsBytes<float>(atlas.Phase).CopyTo(staging);
        MemoryMarshal.AsBytes<Half>(atlas.Fields).CopyTo(staging[PhaseBytes..]);
        MemoryMarshal.AsBytes<sbyte>(atlas.Direction).CopyTo(staging[(PhaseBytes + FieldBytes)..]);
        LastUploadMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Uploads++;

        Copy(commands, _phase, slot, offset);
        Copy(commands, _fields, slot, offset + PhaseBytes);
        Copy(commands, _direction, slot, offset + PhaseBytes + FieldBytes);
    }

    private void Copy(CommandBuffer commands, VkImage image, int layer, int offset)
    {
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, BaseArrayLayer = layer, LayerCount = 1 };
        Transition(commands, image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, ShaderStages, VkPipelineStageFlags.TransferBit, VkAccessFlags.ShaderReadBit, VkAccessFlags.TransferWriteBit, range);
        commands.CopyBufferToImage(_staging.Handle, image, VkImageLayout.TransferDstOptimal,
        [
            new VkBufferImageCopy
            {
                BufferOffset = (ByteSize64)offset,
                BufferRowLength = N,
                BufferImageHeight = N,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, BaseArrayLayer = layer, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = N, Height = N, Depth = 1 },
            },
        ]);
        Transition(commands, image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkPipelineStageFlags.TransferBit, ShaderStages, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, range);
    }

    private static void Transition(CommandBuffer commands, VkImage image, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags after,
        VkPipelineStageFlags before, VkAccessFlags written, VkAccessFlags access, VkImageSubresourceRange range)
    {
        commands.PipelineBarrier(
            after,
            before,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = written,
                    DstAccessMask = access,
                    OldLayout = from,
                    NewLayout = to,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = range,
                },
            ]
        );
    }

    private (VkImage, VmaAllocation) CreateImage(VkFormat format)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = N, Height = N, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = Layers,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out var image,
            out var allocation,
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        return (image, allocation);
    }

    private VkImageView CreateView(VkImage image, VkFormat format) => _device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2DArray,
            Format = format,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = Layers },
        },
        null
    );

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

    public void Dispose()
    {
        _staging.Dispose();
        _device.DestroySampler(NearestSampler, null);
        _device.DestroySampler(LinearSampler, null);
        _device.DestroyImageView(PhaseView, null);
        _device.DestroyImageView(FieldsView, null);
        _device.DestroyImageView(DirectionView, null);
        _allocator.DestroyImage(_phase, _phaseAllocation);
        _allocator.DestroyImage(_fields, _fieldsAllocation);
        _allocator.DestroyImage(_direction, _directionAllocation);
    }
}
