using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Time;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// The water fog around the camera as a 3D texture the water and terrain shaders march through (water_fog.glsl,
/// docs/water-fog.md): one RGBA8 texel per metre, rgb the water's scatter tint and a its fog density (255 = solid, which
/// stops a march; 0 = air). A window of <see cref="BlocksX"/> × <see cref="BlocksY"/> × <see cref="BlocksZ"/> blocks of
/// <see cref="Block"/>³ cells, centred on the camera's block and stored toroidally (a cell's texel is its world cell mod
/// the texture size), so a block that enters the window is the only thing uploaded, and a block whose water changed is
/// uploaded again when its revision moves. The filler (BathymetryFog) asks <see cref="Stale"/> per block and
/// writes into <see cref="Reserve"/>'s span; <see cref="Record"/> copies this frame's blocks before the main pass.
/// </summary>
public sealed unsafe class WaterFogVolume : IDisposable
{
    public const int Block = 32;
    public const int BlocksX = 4;
    public const int BlocksY = 3;
    public const int BlocksZ = 4;
    public const int SizeX = BlocksX * Block;
    public const int SizeY = BlocksY * Block;
    public const int SizeZ = BlocksZ * Block;
    public const int BlockCells = Block * Block * Block;
    public const int BlockBytes = BlockCells * sizeof(uint);
    public const int Slots = BlocksX * BlocksY * BlocksZ;

    /// <summary>Blocks uploaded per frame at most: a stale block waits for a later frame (the window fills from nothing in 6 frames).</summary>
    public const int MaxUploadsPerFrame = 8;

    public const int UniformBytes = 5 * 16;
    private const int UniformAlignment = 256;
    private const int NoSlot = int.MinValue;

    /// <summary>Off: the water is shaded by its depth alone, as before the fog (A/B comparisons).</summary>
    public bool Enabled = true;

    /// <summary>Extinction (1/m) of water at fog density 255.</summary>
    public float Extinction = 4f;

    /// <summary>The hard limit: a march stops once this little light is left, and counts the rest as fog.</summary>
    public float StopTransmittance = 0.02f;

    /// <summary>March step (m).</summary>
    public float Step = 1f;

    public int MaxSteps = 96;

    /// <summary>Water nearer than FarStart (m) is marched; past FarEnd the closed-form sea fog by depth takes over.</summary>
    public float FarStart = 40f;
    public float FarEnd = 56f;

    /// <summary>The open sea's tint (rgb) and fog density (a), 0..1, for columns no block covers and beyond the window.</summary>
    public Vector4 SeaColour = new(10f / 255f, 61f / 255f, 56f / 255f, 64f / 255f);

    public float SeaLevel;

    /// <summary>The camera is under water, and the surface over it (world y): the terrain and surfaces are fogged from the camera.</summary>
    public bool CameraInWater;
    public float CameraSurface;

    /// <summary>Main-thread fill time (ms) of the frames that filled a block (after warm-up) and how many were uploaded, written by the filler.</summary>
    public readonly TimingRing FillMs = new(120);
    public int UploadsLastFrame;

    /// <summary>The longest fill of the run (ms) and how many blocks it filled.</summary>
    public float FillPeakMs;
    public int FillPeakUploads;
    public int Fills;

    /// <summary>The longest single block fill and the longest keying of the window's bathymetry blocks (ms), diagnostics.</summary>
    public float BlockPeakMs;
    public float KeysPeakMs;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _image;
    private readonly VmaAllocation _allocation;
    private readonly GpuBuffer _staging;
    private readonly GpuBuffer _uniforms;
    private readonly VkBufferImageCopy[] _copies = new VkBufferImageCopy[MaxUploadsPerFrame];
    private readonly (int X, int Y, int Z, int Revision)[] _held = new (int, int, int, int)[Slots];
    private int _frameIndex;
    private int _uploads;
    private bool _initialised;

    [StructLayout(LayoutKind.Sequential)]
    private struct Uniforms
    {
        public Vector4 Window;
        public Vector4 Size;
        public Vector4 Params;
        public Vector4 Sea;
        public Vector4 Camera;
    }

    public WaterFogVolume(VulkanContext context)
    {
        _device = context.Device;
        _allocator = context.Allocator;

        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._3D,
                Format = VkFormat.R8G8B8A8UNorm,
                Extent = new VkExtent3D { Width = SizeX, Height = SizeY, Depth = SizeZ },
                MipLevels = 1,
                ArrayLayers = 1,
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

        View = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = _image,
                ViewType = VkImageViewType._3D,
                Format = VkFormat.R8G8B8A8UNorm,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
            },
            null
        );

        // Nearest: a texel is one voxel's water, and 255 means solid, which filtering would blur into fog.
        Sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Nearest,
                MinFilter = VkFilter.Nearest,
                AddressModeU = VkSamplerAddressMode.Repeat,
                AddressModeV = VkSamplerAddressMode.Repeat,
                AddressModeW = VkSamplerAddressMode.Repeat,
            },
            null
        );

        _staging = new GpuBuffer(context, MaxUploadsPerFrame * BlockBytes * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _held.AsSpan().Fill((NoSlot, 0, 0, 0));
    }

    public VkImageView View { get; }

    public VkSampler Sampler { get; }

    public VkBuffer UniformBuffer => _uniforms.Handle;

    public int UniformOffset(int frameIndex) => frameIndex * UniformStride;

    /// <summary>The window's lowest block (x, y, z), in blocks; it spans BlocksX × BlocksY × BlocksZ from there.</summary>
    public (int X, int Y, int Z) WindowMin { get; private set; }

    public bool CanUpload => _uploads < MaxUploadsPerFrame;

    private static int UniformStride => (UniformBytes + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>Starts a frame: the window follows the camera's block, and this frame's staging region is free.</summary>
    public void Begin(int frameIndex, Vector3 camera)
    {
        _frameIndex = frameIndex;
        _uploads = 0;
        UploadsLastFrame = 0;
        var cx = (int)MathF.Floor(camera.X / Block);
        var cy = (int)MathF.Floor(camera.Y / Block);
        var cz = (int)MathF.Floor(camera.Z / Block);
        WindowMin = (cx - BlocksX / 2 + 1, cy - BlocksY / 2, cz - BlocksZ / 2 + 1);
    }

    /// <summary>The texture's copy of block (x, y, z) is missing or older than <paramref name="revision"/>.</summary>
    public bool Stale(int x, int y, int z, int revision)
    {
        ref var held = ref _held[Slot(x, y, z)];
        return held.X != x || held.Y != y || held.Z != z || held.Revision != revision;
    }

    /// <summary>
    /// This frame's staging cells for block (x, y, z), x fastest then y then z, to fill with packed RGBA8 fog; the block is
    /// copied into its toroidal slot by <see cref="Record"/> and counted as holding <paramref name="revision"/>.
    /// </summary>
    public Span<uint> Reserve(int x, int y, int z, int revision)
    {
        if (!CanUpload)
            throw new InvalidOperationException("The fog volume's uploads for this frame are spent; check CanUpload.");

        var index = _frameIndex * MaxUploadsPerFrame + _uploads;
        _copies[_uploads] = new VkBufferImageCopy
        {
            BufferOffset = (ByteSize64)((long)index * BlockBytes),
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageOffset = new VkOffset3D { X = Wrap(x, BlocksX) * Block, Y = Wrap(y, BlocksY) * Block, Z = Wrap(z, BlocksZ) * Block },
            ImageExtent = new VkExtent3D { Width = Block, Height = Block, Depth = Block },
        };
        _held[Slot(x, y, z)] = (x, y, z, revision);
        _uploads++;
        UploadsLastFrame = _uploads;
        return new Span<uint>((uint*)_staging.Mapped + (long)index * BlockCells, BlockCells);
    }

    /// <summary>Outside the render pass, after the fill: this frame's uniforms and the block copies, the image left readable.</summary>
    public void Record(CommandBuffer commands, int frameIndex)
    {
        WriteUniforms(frameIndex);
        var range = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 };

        if (!_initialised)
        {
            // Air everywhere until the blocks arrive, so the first frames march through nothing rather than garbage.
            Transition(commands, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.TopOfPipeBit, 0, range);
            var clear = new VkClearColorValue();
            commands.ClearColorImage(_image, VkImageLayout.TransferDstOptimal, &clear, [range]);
            _initialised = true;
        }
        else if (_uploads == 0)
        {
            return;
        }
        else
        {
            Transition(commands, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, VkPipelineStageFlags.FragmentShaderBit, VkAccessFlags.ShaderReadBit, range);
        }

        if (_uploads > 0)
            commands.CopyBufferToImage(_staging.Handle, _image, VkImageLayout.TransferDstOptimal, _copies.AsSpan(0, _uploads));

        commands.PipelineBarrier(
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.FragmentShaderBit,
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
    }

    private void WriteUniforms(int frameIndex)
    {
        var u = (Uniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        var (x, y, z) = WindowMin;
        u->Window = new Vector4(x * Block, y * Block, z * Block, Enabled ? 1f : 0f);
        u->Size = new Vector4(SizeX, SizeY, SizeZ, SeaLevel);
        u->Params = new Vector4(Extinction, StopTransmittance, MathF.Max(Step, 0.25f), MaxSteps);
        u->Sea = SeaColour;
        u->Camera = new Vector4(CameraInWater ? 1f : 0f, CameraSurface, FarStart, MathF.Max(FarEnd, FarStart + 1f));
    }

    private void Transition(CommandBuffer commands, VkImageLayout from, VkImageLayout to, VkPipelineStageFlags after, VkAccessFlags before, VkImageSubresourceRange range)
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
                    SrcAccessMask = before,
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = from,
                    NewLayout = to,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = _image,
                    SubresourceRange = range,
                },
            ]
        );
    }

    private static int Wrap(int v, int n) => (v % n + n) % n;

    private static int Slot(int x, int y, int z) => Wrap(x, BlocksX) + Wrap(y, BlocksY) * BlocksX + Wrap(z, BlocksZ) * BlocksX * BlocksY;

    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroySampler(Sampler, null);
        _device.DestroyImageView(View, null);
        _allocator.DestroyImage(_image, _allocation);
        _uniforms.Dispose();
        _staging.Dispose();
    }
}
