using System.Diagnostics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;
using Rect = BrutalGame.Terrain.Runtime.ShoreDepthGrid.Rect;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// GPU copies of <see cref="ShoreDepthGrid"/>'s two maps: R16F signed depth. The near map is addressed toroidally
/// (repeat sampler), so a moving window uploads only the cells that changed; the far map is clamped. Only the grid's
/// dirty rectangles are uploaded; everything on the first record and after a frame swap.
/// </summary>
public sealed unsafe class ShoreDepthTexture : IDisposable
{
    private const int NearTexels = ShoreDepthGrid.Resolution * ShoreDepthGrid.Resolution;
    private const int FarTexels = ShoreDepthGrid.FarResolution * ShoreDepthGrid.FarResolution;
    private const int MaxUploads = 16;

    /// <summary>Stages that read the depth maps: the ocean's vertex and fragment shaders and ocean_surf_foam.comp.</summary>
    private const VkPipelineStageFlags ShaderStages =
        VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _near;
    private readonly VmaAllocation _nearAllocation;
    private readonly VkImage _far;
    private readonly VmaAllocation _farAllocation;
    private readonly GpuBuffer _staging;
    private readonly VkBufferImageCopy[] _copies = new VkBufferImageCopy[MaxUploads];
    private bool _initialised;

    public ShoreDepthTexture(Renderer renderer)
    {
        _device = renderer.Context.Device;
        _allocator = renderer.Context.Allocator;
        (_near, _nearAllocation) = CreateImage(ShoreDepthGrid.Resolution);
        (_far, _farAllocation) = CreateImage(ShoreDepthGrid.FarResolution);
        NearView = CreateView(_near);
        FarView = CreateView(_far);
        NearSampler = CreateSampler(VkSamplerAddressMode.Repeat);
        FarSampler = CreateSampler(VkSamplerAddressMode.ClampToEdge);
        _staging = new GpuBuffer(renderer.Context, (NearTexels + FarTexels) * sizeof(Half) * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
    }

    public VkImageView NearView { get; }

    public VkImageView FarView { get; }

    public VkSampler NearSampler { get; }

    public VkSampler FarSampler { get; }

    /// <summary>CPU time of the last upload's copy into staging (ms) and the cells it covered.</summary>
    public float LastUploadMs { get; private set; }

    public int LastUploadCells { get; private set; }

    public int Uploads { get; private set; }

    /// <summary>Uploads what changed. Record outside a render pass. The first call uploads both maps so the images are valid.</summary>
    public void Record(CommandBuffer commands, int frameIndex, ShoreDepthGrid grid)
    {
        var nearDirty = grid.NearDirty;
        var farDirty = grid.FarDirty;

        if (_initialised && nearDirty.IsEmpty && farDirty.IsEmpty)
            return;

        var start = Stopwatch.GetTimestamp();
        var offset = frameIndex * (NearTexels + FarTexels);
        Span<Rect> all = [new Rect(0, 0, ShoreDepthGrid.Resolution, ShoreDepthGrid.Resolution)];
        var cells = Upload(commands, _near, grid.Near, _initialised ? nearDirty : all, ShoreDepthGrid.Resolution, offset);

        all[0] = new Rect(0, 0, ShoreDepthGrid.FarResolution, ShoreDepthGrid.FarResolution);
        cells += Upload(commands, _far, grid.Far, _initialised ? farDirty : all, ShoreDepthGrid.FarResolution, offset + NearTexels);

        LastUploadMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastUploadCells = cells;
        Uploads++;
        grid.ClearDirty();
        _initialised = true;
    }

    private int Upload(CommandBuffer commands, VkImage image, ReadOnlySpan<Half> source, ReadOnlySpan<Rect> dirty, int resolution, int offset)
    {
        if (dirty.IsEmpty)
            return 0;

        var staging = new Span<Half>((Half*)_staging.Mapped + offset, resolution * resolution);
        var cells = 0;

        for (var i = 0; i < dirty.Length; i++)
        {
            var region = dirty[i];
            var width = region.X1 - region.X0;

            for (var z = region.Z0; z < region.Z1; z++)
                source.Slice(region.X0 + z * resolution, width).CopyTo(staging.Slice(region.X0 + z * resolution, width));

            _copies[i] = new VkBufferImageCopy
            {
                BufferOffset = (ByteSize64)((offset + region.X0 + region.Z0 * resolution) * sizeof(Half)),
                BufferRowLength = resolution,
                BufferImageHeight = resolution,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageOffset = new VkOffset3D { X = region.X0, Y = region.Z0 },
                ImageExtent = new VkExtent3D { Width = width, Height = region.Z1 - region.Z0, Depth = 1 },
            };
            cells += region.Area;
        }

        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };
        Transition(commands, image, _initialised ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
            _initialised ? ShaderStages : VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, _initialised ? VkAccessFlags.ShaderReadBit : 0, VkAccessFlags.TransferWriteBit, range);
        commands.CopyBufferToImage(_staging.Handle, image, VkImageLayout.TransferDstOptimal, _copies.AsSpan(0, dirty.Length));
        Transition(commands, image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags.TransferBit, ShaderStages, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit, range);
        return cells;
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

    private (VkImage, VmaAllocation) CreateImage(int resolution)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R16SFloat,
                Extent = new VkExtent3D { Width = resolution, Height = resolution, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
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

    private VkImageView CreateView(VkImage image) => _device.CreateImageView(
        new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = VkImageViewType._2D,
            Format = VkFormat.R16SFloat,
            SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
        },
        null
    );

    private VkSampler CreateSampler(VkSamplerAddressMode mode) => _device.CreateSampler(
        new VkSamplerCreateInfo
        {
            MagFilter = VkFilter.Linear,
            MinFilter = VkFilter.Linear,
            AddressModeU = mode,
            AddressModeV = mode,
            AddressModeW = mode,
        },
        null
    );

    public void Dispose()
    {
        _staging.Dispose();
        _device.DestroySampler(NearSampler, null);
        _device.DestroySampler(FarSampler, null);
        _device.DestroyImageView(NearView, null);
        _device.DestroyImageView(FarView, null);
        _allocator.DestroyImage(_near, _nearAllocation);
        _allocator.DestroyImage(_far, _farAllocation);
    }
}
