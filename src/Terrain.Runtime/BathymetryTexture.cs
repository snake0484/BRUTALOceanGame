using System.Diagnostics;
using System.Numerics;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;
using Region = BrutalGame.Terrain.Runtime.BathymetryGrid.Region;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// GPU copy of <see cref="BathymetryGrid"/>: RGBA16F (signed depth, zone level, zone flag, whitewater) in two layers,
/// 0 = the latest sim tick and 1 = the tick before, so the ocean blends sea-zone levels between ticks by the clock's alpha.
/// Only what changed is uploaded: the regions the terrain re-merged, and once per tick the zone cells that moved. When the
/// grid re-centres its data moves, and so does the texture's, on the GPU (via a scratch image); only the strips that came
/// in are filled. The whole map goes up only on the first upload. Layer 1 only differs from layer 0 on zone cells, so
/// everything else is filled once and copied into both layers.
/// </summary>
public sealed unsafe class BathymetryTexture : IDisposable
{
    private const int Texels = BathymetryGrid.Resolution * BathymetryGrid.Resolution;

    /// <summary>
    /// Depth marking pits cut off from the sea (ground below sea level, no sea water); the ocean never draws there
    /// (the shader discards below -500). Dry ground instead stores its signed depth, sea level − ground (negative).
    /// </summary>
    public const float Pit = -1000f;

    /// <summary>Lowest signed depth stored for dry ground; anything higher above the sea never meets the surface.</summary>
    private const float MinLandDepth = -20f;

    /// <summary>
    /// Depth for dry sea-zone cells. Strongly negative so the interpolated thickness fades to 0 close to the wet cell:
    /// the surface between a wet rim and a dry hole floor slants through open air and must not be drawn.
    /// </summary>
    public const float ZoneDry = -4f;

    private const int Channels = 4;
    private const int Layers = 2;
    private const int LayerHalves = Texels * Channels;

    /// <summary>
    /// Cells (2 m) a below-sea-level column may lie from open sea, through other below-sea-level columns, and still count
    /// as sea. The voxel sea flag reads the sea-level voxel layer, which is mostly solid where the ground sits just under
    /// the surface, so the shoreline band reads "dry"; without this it was discarded as a pit and left a hole at the beach.
    /// </summary>
    private const int ShoreReach = 4;

    /// <summary>Upload rectangles per record: the grid's changed regions plus the zone-level/blend one.</summary>
    private const int MaxUploads = 42;

    private readonly byte[] _reach = new byte[Texels];
    private readonly byte[] _reachScratch = new byte[Texels];
    private readonly int[] _queue = new int[Texels];

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;
    private readonly VkImage _scratch;
    private readonly VmaAllocation _scratchAllocation;
    private readonly GpuBuffer _staging;
    private bool _initialised;
    private long _uploadedTick = -1;

    /// <summary>Layers 0 and 1 as last uploaded. Layer 0 becomes layer 1 for cells that change on the next tick.</summary>
    private readonly Half[] _shown = new Half[LayerHalves];
    private readonly Half[] _shownPrevious = new Half[LayerHalves];

    /// <summary>Cells where layer 1 differs from layer 0 (zone cells that moved last tick).</summary>
    private Region _blend;

    private readonly Region[] _uploads = new Region[MaxUploads];
    private readonly VkBufferImageCopy[] _copies = new VkBufferImageCopy[MaxUploads * Layers];

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
                ViewType = VkImageViewType._2DArray,
                Format = VkFormat.R16G16B16A16SFloat,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = Layers },
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

        _staging = new GpuBuffer(renderer.Context, LayerHalves * Layers * sizeof(Half) * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
    }

    private (VkImage, VmaAllocation) CreateImage(VkImageUsageFlags usage)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R16G16B16A16SFloat,
                Extent = new VkExtent3D { Width = BathymetryGrid.Resolution, Height = BathymetryGrid.Resolution, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = Layers,
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

    /// <summary>
    /// Uploads what changed. Record outside a render pass. The first call always uploads so the image is valid.
    /// Zone levels go up once per sim <paramref name="tick"/>: the cells that changed get their previous value in layer 1,
    /// and a tick later, once they stop changing, layer 1 is set equal to layer 0 so the blend settles.
    /// </summary>
    public void Record(CommandBuffer commands, int frameIndex, BathymetryGrid grid, long tick)
    {
        var full = !_initialised || grid.ChangedAll;
        var changed = grid.Dirty;
        var newTick = tick != _uploadedTick;
        var due = newTick && (grid.LevelsDirty || !_blend.Empty);
        if (!full && !changed && !due)
        {
            _uploadedTick = tick;
            return;
        }

        var levels = newTick ? LevelRegion(grid.TakeLevelRect()) : default;
        var moved = _initialised && grid.Shift != default;
        var shift = moved && !full ? grid.Shift : default;

        // On a tick the cells that changed in it blend from their last value; between ticks (a bathymetry change) the
        // running blend is kept. Everything else is shown as it is now, on both layers. Moved data restarts the blend.
        var blend = !_initialised || moved ? default : newTick ? levels : _blend;
        var stale = _blend;
        _blend = blend;
        _uploadedTick = tick;

        var start = Stopwatch.GetTimestamp();
        if (shift != default)
        {
            BathymetryGrid.ShiftCells(_shown, shift.X, shift.Z, default, Channels);
            BathymetryGrid.ShiftCells(_shownPrevious, shift.X, shift.Z, default, Channels);
        }

        UpdateShoreReach(grid);
        LastReachMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        // Terrain regions show the same on both layers; the zone region (cells that moved this tick, and those that
        // stop blending) gets its own layer 1. It is filled and copied last, so its layer 1 wins where they overlap.
        var count = 0;
        if (full)
            _uploads[count++] = Region.All;
        else
        {
            foreach (var region in grid.ChangedRegions)
                _uploads[count++] = region;

            count += BathymetryGrid.Exposed(shift.X, shift.Z, _uploads.AsSpan(count, 2));
        }

        var zone = full ? default : Join(levels, stale);
        var offset = frameIndex * LayerHalves * Layers;
        var cells = zone.Area;
        for (var i = 0; i < count; i++)
        {
            FillRows(grid, _uploads[i], default, null, offset);
            cells += _uploads[i].Area;
        }

        if (!zone.Empty)
            FillRows(grid, zone, blend, newTick ? _shown : _shownPrevious, offset);

        LastUploadMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastUploadCells = cells;
        Uploads++;

        for (var i = 0; i < count; i++)
        {
            _copies[2 * i] = Copy(offset, _uploads[i], 0);
            _copies[2 * i + 1] = Copy(offset, _uploads[i], 1);
        }

        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = Layers };

        if (shift != default)
            RecordShift(commands, shift, range);
        else
            Transition(commands, _image, _initialised ? VkImageLayout.ShaderReadOnlyOptimal : VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal,
                VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit, 0, range);

        if (count > 0)
            commands.CopyBufferToImage(_staging.Handle, _image, VkImageLayout.TransferDstOptimal, _copies.AsSpan(0, 2 * count));

        if (!zone.Empty)
        {
            if (count > 0)
                commands.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None,
                    [new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.TransferWriteBit }], [], []);

            commands.CopyBufferToImage(_staging.Handle, _image, VkImageLayout.TransferDstOptimal,
                [Copy(offset, zone, 0), Copy(offset + LayerHalves, zone, 1)]);
        }

        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit,
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

    private static Region Join(Region a, Region b) => a.Empty ? b : b.Empty ? a : a.Union(b);

    /// <summary>
    /// Moves the texture's contents with the grid: all of it into the scratch image, then back offset by the shift (an
    /// image can't copy onto itself where the regions overlap). Leaves the texture in TRANSFER_DST for the uploads.
    /// </summary>
    private void RecordShift(CommandBuffer commands, (int X, int Z) shift, VkImageSubresourceRange range)
    {
        const int n = BathymetryGrid.Resolution;
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = Layers };

        Transition(commands, _image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.TransferReadBit, range);
        Transition(commands, _scratch, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TopOfPipeBit, 0, range);
        commands.CopyImage(_image, VkImageLayout.TransferSrcOptimal, _scratch, VkImageLayout.TransferDstOptimal,
        [
            new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = new VkExtent3D { Width = n, Height = n, Depth = 1 } },
        ]);

        Transition(commands, _image, VkImageLayout.TransferSrcOptimal, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, range);
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

    /// <summary>The grid's inclusive zone-level rectangle as a region (empty when Max &lt; Min).</summary>
    private static Region LevelRegion((int MinX, int MinZ, int MaxX, int MaxZ) rect) =>
        rect.MaxX < rect.MinX ? default : new Region(rect.MinX, rect.MinZ, rect.MaxX + 1, rect.MaxZ + 1);

    private static VkBufferImageCopy Copy(int layerOffset, Region region, int layer) => new()
    {
        BufferOffset = (ByteSize64)((layerOffset + (region.X0 + region.Z0 * BathymetryGrid.Resolution) * Channels) * sizeof(Half)),
        BufferRowLength = BathymetryGrid.Resolution,
        BufferImageHeight = BathymetryGrid.Resolution,
        ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, BaseArrayLayer = layer, LayerCount = 1 },
        ImageOffset = new VkOffset3D { X = region.X0, Y = region.Z0 },
        ImageExtent = new VkExtent3D { Width = region.X1 - region.X0, Height = region.Z1 - region.Z0, Depth = 1 },
    };

    /// <summary>
    /// Writes <paramref name="region"/> into this frame's staging at <paramref name="offset"/> halves: layer 0, and when
    /// <paramref name="before"/> is given layer 1 as well, which is <paramref name="before"/> on zone cells inside
    /// <paramref name="blend"/> and the new value elsewhere. The mirrors only track zone cells, the only ones that blend.
    /// </summary>
    private void FillRows(BathymetryGrid grid, Region region, Region blend, Half[]? before, int offset)
    {
        const int n = BathymetryGrid.Resolution;
        var current = (Half*)_staging.Mapped + offset;
        var previous = current + LayerHalves;
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var seaLods = grid.SeaLods;
        var simulated = grid.Simulated;
        var zoneWet = grid.ZoneWet;
        var zoneOffset = grid.ZoneOffset;
        var zoneDepth = grid.ZoneDepth;
        var zoneWhite = grid.ZoneWhite;
        var zoneWeight = grid.ZoneWeight;

        // R = signed depth, sea level − ground (negative on dry land; Pit where the ocean must not draw), G = sim level
        // offset, B = sea-zone flag, A = flow whitewater. The shader fades the surface by its height over the ground,
        // so the water's edge is wherever the moving surface meets the beach.
        for (var z = region.Z0; z < region.Z1; z++)
        {
            var rowBlends = z >= blend.Z0 && z < blend.Z1;

            for (var x = region.X0; x < region.X1; x++)
            {
                var i = x + z * n;
                var ground = BathymetryGrid.Ground(tops[i], beds[i], sea[i]);
                float depth;
                var level = 0f;
                var zone = 0f;

                if (float.IsNaN(ground))
                    depth = BathymetryGrid.UnknownDepth;
                else if (sea[i] == BathymetryGrid.SeaState.Dry && ground < BathymetryGrid.SeaLevel - SeaVoxel(seaLods[i]) && _reach[i] == 0)
                    depth = Pit;
                else
                    depth = MathF.Max(MinLandDepth, BathymetryGrid.SeaLevel - ground);

                // A zone cell's depth is the sim's, blended in by its zone weight as the shader blends the surface, so a
                // disc fading in or out changes the depth with the surface, not at once (a pit has no open-sea depth).
                if (simulated[i])
                {
                    var simDepth = zoneWet[i] ? zoneDepth[i] : ZoneDry;
                    level = zoneOffset[i];
                    zone = zoneWeight[i];
                    depth = depth == Pit ? simDepth : float.Lerp(depth, simDepth, zone);
                }

                var t = i * Channels;
                current[t] = (Half)depth;
                current[t + 1] = (Half)level;
                current[t + 2] = (Half)zone;
                current[t + 3] = (Half)zoneWhite[i];

                if (!simulated[i])
                {
                    if (before is not null)
                        new Span<Half>(current + t, Channels).CopyTo(new Span<Half>(previous + t, Channels));

                    continue;
                }

                var blends = before is not null && rowBlends && x >= blend.X0 && x < blend.X1;
                for (var c = 0; c < Channels; c++)
                {
                    var old = blends ? before![t + c] : current[t + c];
                    _shown[t + c] = current[t + c];
                    _shownPrevious[t + c] = old;

                    if (before is not null)
                        previous[t + c] = old;
                }
            }
        }
    }

    /// <summary>
    /// Height of the voxel layer the sea flag was read from (2^lod m). Ground inside it can't hold a sea voxel, so a
    /// column only counts as a cut-off pit when its ground lies deeper than that.
    /// </summary>
    private static float SeaVoxel(byte lod) => lod == byte.MaxValue ? 1f : 1 << Math.Min((int)lod, 8);

    /// <summary>
    /// Open-sea cells (1) and the below-sea-level dry cells within <see cref="ShoreReach"/> steps of them (2..), found
    /// breadth-first from the sea's edge. The first upload floods the whole grid; after that the reach moves with the
    /// grid's data and is recomputed only around the regions that changed.
    /// </summary>
    private void UpdateShoreReach(BathymetryGrid grid)
    {
        if (!_initialised)
        {
            Flood(grid, Region.All, Region.All);
            return;
        }

        if (grid.Shift != default)
            BathymetryGrid.ShiftCells(_reach, grid.Shift.X, grid.Shift.Z, (byte)0);

        // A cell's reach depends only on cells within ShoreReach steps, so flooding the region grown by twice that and
        // keeping the result grown by once that is exact.
        foreach (var region in grid.ChangedRegions)
            Flood(grid, region.Expand(ShoreReach), region.Expand(2 * ShoreReach));
    }

    /// <summary>Breadth-first reach within <paramref name="area"/>; the result is kept inside <paramref name="keep"/>.</summary>
    private void Flood(BathymetryGrid grid, Region keep, Region area)
    {
        const int n = BathymetryGrid.Resolution;
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var head = 0;
        var tail = 0;

        for (var z = area.Z0; z < area.Z1; z++)
        {
            var row = z * n;
            _reachScratch.AsSpan(row + area.X0, area.X1 - area.X0).Clear();

            for (var x = area.X0; x < area.X1; x++)
            {
                if (sea[row + x] == BathymetryGrid.SeaState.Dry)
                    continue;

                _reachScratch[row + x] = 1;
                _queue[tail++] = row + x;
            }
        }

        while (head < tail)
        {
            var i = _queue[head++];
            var steps = _reachScratch[i];
            if (steps > ShoreReach)
                continue;

            var x = i % n;
            var z = i / n;
            for (var k = 0; k < 4; k++)
            {
                var (nx, nz) = k switch
                {
                    0 => (x - 1, z),
                    1 => (x + 1, z),
                    2 => (x, z - 1),
                    _ => (x, z + 1),
                };

                if (nx < area.X0 || nx >= area.X1 || nz < area.Z0 || nz >= area.Z1)
                    continue;

                var j = nx + nz * n;
                if (_reachScratch[j] != 0 || sea[j] != BathymetryGrid.SeaState.Dry || !(BathymetryGrid.Ground(tops[j], beds[j], sea[j]) < BathymetryGrid.SeaLevel))
                    continue;

                _reachScratch[j] = (byte)(steps + 1);
                _queue[tail++] = j;
            }
        }

        for (var z = keep.Z0; z < keep.Z1; z++)
            _reachScratch.AsSpan(z * n + keep.X0, keep.X1 - keep.X0).CopyTo(_reach.AsSpan(z * n + keep.X0));
    }

    public void Dispose()
    {
        _staging.Dispose();
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(_image, _allocation);
        _allocator.DestroyImage(_scratch, _scratchAllocation);
    }
}
