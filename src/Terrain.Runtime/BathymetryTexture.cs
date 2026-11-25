using System.Diagnostics;
using System.Numerics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;
using Region = BrutalGame.Terrain.Runtime.BathymetryGrid.Region;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// GPU copy of <see cref="BathymetryGrid"/>: R16F signed depth. Only what changed is uploaded: the regions the terrain
/// re-merged. When the grid re-centres its data moves, and so does the texture's, on the GPU (via a scratch image); only
/// the strips that came in are filled. The whole map goes up only on the first upload.
/// </summary>
public sealed unsafe class BathymetryTexture : IDisposable
{
    private const int Texels = BathymetryGrid.Resolution * BathymetryGrid.Resolution;

    /// <summary>Upload rectangles per record: at most the grid's changed regions.</summary>
    private const int MaxUploads = 32;

    /// <summary>Stages that read the depth map: the ocean's vertex and fragment shaders and ocean_surf_foam.comp.</summary>
    private const VkPipelineStageFlags ShaderStages =
        VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ComputeShaderBit;

    private readonly ShoreDepthMap _depths = new();

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;
    private readonly VkImage _scratch;
    private readonly VmaAllocation _scratchAllocation;
    private readonly GpuBuffer _staging;
    private bool _initialised;

    private readonly Region[] _uploads = new Region[MaxUploads];
    private readonly VkBufferImageCopy[] _copies = new VkBufferImageCopy[MaxUploads];

    public BathymetryTexture(Renderer renderer)
    {
        _device = renderer.Context.Device;
        _allocator = renderer.Context.Allocator;

        (_image, _allocation) = CreateImage(VkImageUsageFlags.SampledBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit);
        (_scratch, _scratchAllocation) = CreateImage(VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.TransferSrcBit);

        View = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = _image,
                ViewType = VkImageViewType._2D,
                Format = VkFormat.R16SFloat,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            },
            null
        );

        Sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
            },
            null
        );

        _staging = new GpuBuffer(renderer.Context, Texels * sizeof(Half) * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
    }

    private (VkImage, VmaAllocation) CreateImage(VkImageUsageFlags usage)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R16SFloat,
                Extent = new VkExtent3D { Width = BathymetryGrid.Resolution, Height = BathymetryGrid.Resolution, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
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

    public VkImageView View { get; }

    public VkSampler Sampler { get; }

    /// <summary>(origin x, origin z, world size, unknown depth) for shaders.</summary>
    public Vector4 Rect { get; private set; }

    /// <summary>CPU time of the last upload's fill (ms) and the cells it covered.</summary>
    public float LastUploadMs { get; private set; }

    public int LastUploadCells { get; private set; }

    /// <summary>CPU time of the last upload's shoreline reach update (ms), part of <see cref="LastUploadMs"/>.</summary>
    public float LastReachMs { get; private set; }

    /// <summary>Uploads recorded so far.</summary>
    public int Uploads { get; private set; }

    /// <summary>Uploads what changed. Record outside a render pass. The first call always uploads so the image is valid.</summary>
    public void Record(CommandBuffer commands, int frameIndex, BathymetryGrid grid)
    {
        var full = !_initialised || grid.ChangedAll;
        if (!full && !grid.Dirty)
            return;

        var shift = _initialised && !full ? grid.Shift : default;
        var start = Stopwatch.GetTimestamp();
        _depths.Update(grid, !_initialised);
        LastReachMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // The strips a shift brings in are cleared on the GPU (RecordShift); regions the grid rebuilt there are uploaded over them.
        var count = 0;
        if (full)
            _uploads[count++] = Region.All;
        else
        {
            foreach (var region in grid.ChangedRegions)
                _uploads[count++] = region;
        }

        var offset = frameIndex * Texels;
        var cells = 0;
        for (var i = 0; i < count; i++)
        {
            FillRows(grid, _uploads[i], offset);
            _copies[i] = Copy(offset, _uploads[i]);
            cells += _uploads[i].Area;
        }

        LastUploadMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastUploadCells = cells;
        Uploads++;

        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

        if (shift != default)
            RecordShift(commands, shift, range);
        else
            Transition(commands, _image, _initialised ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                ShaderStages, 0, range);

        if (count > 0)
            commands.CopyBufferToImage(_staging.Handle, _image, VkImageLayout.TransferDstOptimal, _copies.AsSpan(0, count));

        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            ShaderStages,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = VkAccessFlags.TransferWriteBit,
                    DstAccessMask = VkAccessFlags.ShaderReadBit,
                    OldLayout = VkImageLayout.TransferDstOptimal,
                    NewLayout = VkImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = _image,
                    SubresourceRange = range,
                },
            ]
        );

        Rect = new Vector4(grid.Origin.X, grid.Origin.Y, grid.WorldSize, BathymetryGrid.UnknownDepth);
        grid.ClearChanged();
        _initialised = true;
    }

    /// <summary>
    /// Moves the texture's contents with the grid: all of it into the scratch image, then back offset by the shift (an
    /// image can't copy onto itself where the regions overlap). Leaves the texture in TRANSFER_DST for the uploads.
    /// </summary>
    private void RecordShift(CommandBuffer commands, (int X, int Z) shift, VkImageSubresourceRange range)
    {
        const int n = BathymetryGrid.Resolution;
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };

        Transition(commands, _image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
            ShaderStages, VkAccessFlags.TransferReadBit, range);
        Transition(commands, _scratch, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TopOfPipeBit, 0, range);
        commands.CopyImage(_image, VkImageLayout.TransferSrcOptimal, _scratch, VkImageLayout.TransferDstOptimal,
        [
            new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = new VkExtent3D { Width = n, Height = n, Depth = 1 } },
        ]);

        Transition(commands, _image, VkImageLayout.TransferSrcOptimal, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, range);

        // The strips that came in hold what the grid's do: unknown ground. Clearing it all and copying the kept
        // part back over it fills them on the GPU instead of the CPU writing and uploading every exposed cell.
        var unknown = new VkClearColorValue { Float32 = new Brutal.Numerics.float4(BathymetryGrid.UnknownDepth, 0f, 0f, 0f) };
        commands.ClearColorImage(_image, VkImageLayout.TransferDstOptimal, &unknown, [range]);
        commands.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None,
            [new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.TransferWriteBit }], [], []);

        Transition(commands, _scratch, VkImageLayout.TransferDstOptimal, VkImageLayout.TransferSrcOptimal, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferReadBit, range,
            VkAccessFlags.TransferWriteBit);
        commands.CopyImage(_scratch, VkImageLayout.TransferSrcOptimal, _image, VkImageLayout.TransferDstOptimal,
        [
            new VkImageCopy
            {
                SrcSubresource = layers,
                SrcOffset = new VkOffset3D { X = Math.Max(shift.X, 0), Y = Math.Max(shift.Z, 0) },
                DstSubresource = layers,
                DstOffset = new VkOffset3D { X = Math.Max(-shift.X, 0), Y = Math.Max(-shift.Z, 0) },
                Extent = new VkExtent3D { Width = n - Math.Abs(shift.X), Height = n - Math.Abs(shift.Z), Depth = 1 },
            },
        ]);
    }

    private static void Transition(CommandBuffer commands, VkImage image, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags after,
        VkAccessFlags access, VkImageSubresourceRange range, VkAccessFlags written = 0)
    {
        commands.PipelineBarrier(
            after,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    SrcAccessMask = written,
                    DstAccessMask = access == 0 ? VkAccessFlags.TransferWriteBit : access,
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

    private static VkBufferImageCopy Copy(int offset, Region region) => new()
    {
        BufferOffset = (ByteSize64)((offset + region.X0 + region.Z0 * BathymetryGrid.Resolution) * sizeof(Half)),
        BufferRowLength = BathymetryGrid.Resolution,
        BufferImageHeight = BathymetryGrid.Resolution,
        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
        ImageOffset = new VkOffset3D { X = region.X0, Y = region.Z0 },
        ImageExtent = new VkExtent3D { Width = region.X1 - region.X0, Height = region.Z1 - region.Z0, Depth = 1 },
    };

    /// <summary>
    /// Writes <paramref name="region"/> into this frame's staging at <paramref name="offset"/> halves: signed depth
    /// (<see cref="ShoreDepthMap"/>). The shader fades the surface by its height over the ground, so the water's edge is
    /// wherever the moving surface meets the beach.
    /// </summary>
    private void FillRows(BathymetryGrid grid, Region region, int offset) =>
        _depths.Fill(grid, region, new Span<Half>((Half*)_staging.Mapped + offset, Texels));

    public void Dispose()
    {
        _staging.Dispose();
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(_image, _allocation);
        _allocator.DestroyImage(_scratch, _scratchAllocation);
    }
}
