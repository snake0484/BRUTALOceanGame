using System.Diagnostics;
using System.Numerics;
using Brutal;
using Brutal.Concurrency.Jobs;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// One body's baked impostor on the GPU (<see cref="PlanetImpostor"/>): an R16 height cube and an sRGB albedo cube
/// (alpha = water), full mip chains. The six faces bake on jobs straight into a persistently mapped staging buffer, then
/// <see cref="RecordUpload"/> copies every level at once and the staging is released. Main thread except the bake jobs.
/// </summary>
public sealed unsafe class ImpostorTexture : IDisposable
{
    public readonly int Size;
    public readonly int Levels;
    public readonly PlanetParams Params;

    private readonly VulkanContext _context;
    private readonly PlanetGenerator? _generator;
    private readonly VkImage _height;
    private readonly VmaAllocation _heightAllocation;
    private readonly VkImage _albedo;
    private readonly VmaAllocation _albedoAllocation;
    private readonly long _texels;
    private readonly long _albedoOffset;
    private readonly ImpostorFaceStats[] _faces = new ImpostorFaceStats[PlanetImpostor.Faces];
    private GpuBuffer? _staging;
    private int _facesDone;
    private long _bakeStart;

    private sealed class FaceJob(ImpostorTexture texture, int face) : IJob
    {
        public void Run() => texture.BakeFace(face);
    }

    /// <summary>A texture for <paramref name="generator"/>'s body at <paramref name="size"/>² per face; null bakes a 1×1 grey placeholder.</summary>
    public ImpostorTexture(VulkanContext context, PlanetGenerator? generator, int size)
    {
        if (size < 1 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Power of two");

        _context = context;
        _generator = generator;
        Params = generator?.Params ?? default;
        Size = size;
        Levels = PlanetImpostor.Levels(size);
        _texels = PlanetImpostor.Texels(size);
        _albedoOffset = (_texels * sizeof(ushort) + 3) & ~3L;
        _staging = new GpuBuffer(context, (int)(_albedoOffset + _texels * sizeof(uint)), VkBufferUsageFlags.TransferSrcBit, true);
        (_height, _heightAllocation) = CreateImage(VkFormat.R16UNorm);
        (_albedo, _albedoAllocation) = CreateImage(VkFormat.R8G8B8A8SRGB);
        HeightView = CreateView(_height, VkFormat.R16UNorm);
        AlbedoView = CreateView(_albedo, VkFormat.R8G8B8A8SRGB);

        if (generator is null)
        {
            Heights.Fill(0);
            Albedo.Fill(0x00808080u);
            _facesDone = PlanetImpostor.Faces;
        }
    }

    public VkImageView HeightView { get; }

    public VkImageView AlbedoView { get; }

    /// <summary>Every face baked: <see cref="RecordUpload"/> may run.</summary>
    public bool Baked => Volatile.Read(ref _facesDone) == PlanetImpostor.Faces;

    /// <summary>Copied to the GPU: draws may sample it.</summary>
    public bool Uploaded { get; private set; }

    /// <summary>Wall time from <see cref="QueueBake"/> to the last face.</summary>
    public float BakeMs { get; private set; }

    /// <summary>Steepest visible slope (rise over run) at the finest level: the heightfield march's step bound.</summary>
    public float MaxSlope { get; private set; }

    /// <summary>Mean linear albedo (water included): the point of light's colour.</summary>
    public Vector3 MeanAlbedo { get; private set; } = new(0.3f);

    /// <summary>Share of texels that are sea.</summary>
    public float WaterShare { get; private set; }

    /// <summary>GPU bytes of both cubes, every level.</summary>
    public long Bytes => _texels * (sizeof(ushort) + sizeof(uint));

    private Span<ushort> Heights => new((byte*)_staging!.Mapped, (int)_texels);

    private Span<uint> Albedo => new((byte*)_staging!.Mapped + _albedoOffset, (int)_texels);

    /// <summary>Queues the six face bakes on <paramref name="scheduler"/> (the caller executes it).</summary>
    public void QueueBake(JobScheduler scheduler)
    {
        if (_generator is null)
            return;

        _bakeStart = Stopwatch.GetTimestamp();

        for (var face = 0; face < PlanetImpostor.Faces; face++)
            scheduler.Queue(new FaceJob(this, face), Size / 64 + 1);
    }

    private void BakeFace(int face)
    {
        _faces[face] = PlanetImpostor.BakeFace(_generator!, Size, face, Heights, Albedo);

        if (Interlocked.Increment(ref _facesDone) == PlanetImpostor.Faces)
            BakeMs = (float)Stopwatch.GetElapsedTime(_bakeStart).TotalMilliseconds;
    }

    /// <summary>Outside a render pass, once <see cref="Baked"/>: copies every level and leaves both cubes readable by fragment shaders.</summary>
    public void RecordUpload(CommandBuffer commands)
    {
        if (Uploaded || !Baked)
            return;

        Summarise();
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = Levels, LayerCount = PlanetImpostor.Faces };
        Barrier(commands, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, 0, VkAccessFlags.TransferWriteBit,
            VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, range);

        Span<VkBufferImageCopy> copies = stackalloc VkBufferImageCopy[Levels];
        Fill(copies, 0, sizeof(ushort));
        commands.CopyBufferToImage(_staging!.Handle, _height, VkImageLayout.TransferDstOptimal, copies);
        Fill(copies, _albedoOffset, sizeof(uint));
        commands.CopyBufferToImage(_staging.Handle, _albedo, VkImageLayout.TransferDstOptimal, copies);

        Barrier(commands, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, VkAccessFlags.ShaderReadBit,
            VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.VertexShaderBit, range);
        Uploaded = true;
    }

    /// <summary>After the upload's frame has finished on the GPU: frees the staging buffer.</summary>
    public void ReleaseStaging()
    {
        _staging?.Dispose();
        _staging = null;
    }

    private void Summarise()
    {
        if (_generator is null)
            return;

        var sum = Vector3.Zero;
        var water = 0f;
        var texels = 0;
        var slope = 0f;

        foreach (var face in _faces)
        {
            sum += face.AlbedoSum;
            water += face.WaterTexels;
            texels += face.Texels;
            slope = MathF.Max(slope, face.MaxSlope);
        }

        MeanAlbedo = sum / texels;
        WaterShare = water / texels;
        MaxSlope = slope;
    }

    private void Fill(Span<VkBufferImageCopy> copies, long baseOffset, int texelBytes)
    {
        for (var level = 0; level < Levels; level++)
        {
            var n = Size >> level;
            copies[level] = new VkBufferImageCopy
            {
                BufferOffset = (ByteSize64)(baseOffset + PlanetImpostor.LevelOffset(Size, level) * texelBytes),
                ImageSubresource = new VkImageSubresourceLayers
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    MipLevel = level,
                    LayerCount = PlanetImpostor.Faces,
                },
                ImageExtent = new VkExtent3D { Width = n, Height = n, Depth = 1 },
            };
        }
    }

    private void Barrier(CommandBuffer commands, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst,
        VkPipelineStageFlags after, VkPipelineStageFlags before, VkImageSubresourceRange range)
    {
        commands.PipelineBarrier(after, before, VkDependencyFlags.None, [], [],
        [
            new VkImageMemoryBarrier
            {
                SrcAccessMask = src,
                DstAccessMask = dst,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _height,
                SubresourceRange = range,
            },
            new VkImageMemoryBarrier
            {
                SrcAccessMask = src,
                DstAccessMask = dst,
                OldLayout = from,
                NewLayout = to,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _albedo,
                SubresourceRange = range,
            },
        ]);
    }

    private (VkImage, VmaAllocation) CreateImage(VkFormat format)
    {
        var result = _context.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                Flags = VkImageCreateFlags.CubeCompatibleBit,
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
                MipLevels = Levels,
                ArrayLayers = PlanetImpostor.Faces,
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

    private VkImageView CreateView(VkImage image, VkFormat format) =>
        _context.Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType.Cube,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    LevelCount = Levels,
                    LayerCount = PlanetImpostor.Faces,
                },
            },
            null
        );

    /// <summary>The GPU must be done with it (deferred by the owner past the frames in flight).</summary>
    public void Dispose()
    {
        ReleaseStaging();
        _context.Device.DestroyImageView(HeightView, null);
        _context.Device.DestroyImageView(AlbedoView, null);
        _context.Allocator.DestroyImage(_height, _heightAllocation);
        _context.Allocator.DestroyImage(_albedo, _albedoAllocation);
    }
}
