using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// High-frequency detail above the CPU cutoff: three FFT cascades evolved and transformed on the GPU each frame.
/// See docs/ocean-gpu-contract.md. Images are shared across frames in flight and kept in GENERAL layout.
/// </summary>
public sealed unsafe class OceanFft : IDisposable
{
    public const int Size = 256;
    public const int Cascades = 3;
    public const double LoopPeriod = 1000.0;
    private const int UniformAlignment = 256;
    private const int ImageCount = 7;

    public static readonly float[] PatchSizes = [48f, 12f, 3.2f];
    private static readonly float[] BandEdges = [4f, 16f];

    public float Choppiness = 1.2f;

    /// <summary>Whitecap foam decays by 1/e in 1 / this seconds: measured effective decay times are 1.4–4.8 s (Callaghan et al. 2012).</summary>
    public float FoamDecay = 0.6f;
    public float FoamStrength = 0.8f;

    /// <summary>
    /// Whitecap seeding: each frame the two larger cascades seed foam on the share of their surface with the lowest J
    /// that, once the foam lingers, covers <see cref="WhitecapCoverage"/>: the seeded share is that over this, since the
    /// foam outlives the breaking that makes it. Calibrated in game over deep water (foam debug view, weight past 0.5):
    /// 0.8, 4.6, 10.4, 24.8, 47.9% at 10, 16, 20, 25, 30 m/s against 1.0, 4.9, 10.5, 22.5, 41.8%.
    /// </summary>
    public float WhitecapLinger = 7f;

    /// <summary>Per frame, how far the whitecap thresholds ease toward this frame's quantile.</summary>
    public float WhitecapEase = 0.2f;
    public bool DebugView;
    public uint Seed = 1;

    private readonly Device _device;
    private readonly VmaAllocator _allocator;
    private readonly VkImage[] _images = new VkImage[ImageCount];
    private readonly VmaAllocation[] _allocations = new VmaAllocation[ImageCount];
    private readonly VkImageView[] _views = new VkImageView[ImageCount];
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _h0;
    private readonly VkPipeline _fft;
    private readonly VkPipeline _foamThreshold;

    /// <summary>
    /// Whitecap J histograms (two cascades of 128 bins, written by ocean_fft.comp's column pass) and thresholds (written
    /// by ocean_foam_threshold.comp): GPU only, so no readback.
    /// </summary>
    private const int FoamBins = 128;
    private const int FoamStatsBytes = (2 * FoamBins + 4) * sizeof(uint);
    private readonly GpuBuffer _foamStats;
    private bool _initialised;

    /// <summary>The uniforms the spectrum cache (ocean_h0.comp) was last built from; it is rebuilt when they change.</summary>
    private FftUniforms _cached;
    private bool _cacheValid;

    private const int SpecA = 0;
    private const int SpecB = 1;
    private const int Disp = 2;
    private const int Deriv = 3;
    private const int Foam = 4;
    private const int H0 = 5;
    private const int Omega = 6;

    [StructLayout(LayoutKind.Sequential)]
    private struct FftUniforms
    {
        public Vector4 CascadeL;
        public Vector4 CascadeKLo;
        public Vector4 CascadeKHi;
        public Vector4 Wind;
        public Vector4 Spectrum;
        public Vector4 Params;
        public Vector4 Foam;
        public uint Seed;
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
    }

    public OceanFft(Renderer renderer)
    {
        _device = renderer.Context.Device;
        _allocator = renderer.Context.Allocator;

        CreateImage(SpecA, VkFormat.R32G32B32A32SFloat, VkImageUsageFlags.StorageBit);
        CreateImage(SpecB, VkFormat.R32G32B32A32SFloat, VkImageUsageFlags.StorageBit);
        CreateImage(Disp, VkFormat.R16G16B16A16SFloat, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);
        CreateImage(Deriv, VkFormat.R16G16B16A16SFloat, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);
        CreateImage(Foam, VkFormat.R16G16B16A16SFloat, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit);
        CreateImage(H0, VkFormat.R32G32B32A32SFloat, VkImageUsageFlags.StorageBit);
        CreateImage(Omega, VkFormat.R32SFloat, VkImageUsageFlags.StorageBit);

        Sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.Repeat,
                AddressModeV = VkSamplerAddressMode.Repeat,
                AddressModeW = VkSamplerAddressMode.Repeat,
            },
            null
        );

        _uniforms = new GpuBuffer(renderer.Context, UniformAlignment * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _foamStats = new GpuBuffer(renderer.Context, FoamStatsBytes, VkBufferUsageFlags.StorageBufferBit, true);
        _foamStats.AsSpan<byte>().Clear();
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        AllocateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.ComputeBit, Size = (ByteSize32)sizeof(int) },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _h0 = CreatePipeline("ocean_h0.comp");
        _fft = CreatePipeline("ocean_fft.comp");
        _foamThreshold = CreatePipeline("ocean_foam_threshold.comp");
    }

    /// <summary>
    /// Share of the sea surface covered by whitecaps at 10 m wind speed U (m/s): 3.84e-6 U^3.41, Monahan and
    /// O'Muircheartaigh (1980), about 1% at 10 m/s, 5% at 16, 10% at 20, 40% at 30; capped at 0.6.
    /// </summary>
    public static float WhitecapCoverage(float windSpeed) => MathF.Min(3.84e-6f * MathF.Pow(MathF.Max(windSpeed, 0f), 3.41f), 0.6f);

    public VkSampler Sampler { get; }

    public VkImageView DisplacementView => _views[Disp];

    public VkImageView DerivativeView => _views[Deriv];

    public VkImageView FoamView => _views[Foam];

    public Vector4 CascadeSizes => new(PatchSizes[0], PatchSizes[1], PatchSizes[2], Cascades);

    /// <summary>Band low |k| per cascade; cascade 0 starts at the CPU cutoff.</summary>
    public static Vector4 BandLow(float minWavelength) => new(MathF.Tau / minWavelength, BandEdges[0], BandEdges[1], 0f);

    private void CreateImage(int index, VkFormat format, VkImageUsageFlags usage)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = Cascades,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out _images[index],
            out _allocations[index],
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        _views[index] = _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = _images[index],
                ViewType = VkImageViewType._2DArray,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = Cascades,
                },
            },
            null
        );
    }

    /// <summary>Binding 0 the uniforms, 1..ImageCount the images in index order, then the foam stats buffer.</summary>
    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[2 + ImageCount];
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.ComputeBit,
        };

        for (var i = 0; i < ImageCount; i++)
        {
            bindings[1 + i] = new VkDescriptorSetLayoutBinding
            {
                Binding = 1 + i,
                DescriptorType = VkDescriptorType.StorageImage,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.ComputeBit,
            };
        }

        bindings[1 + ImageCount] = new VkDescriptorSetLayoutBinding
        {
            Binding = 1 + ImageCount,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.ComputeBit,
        };

        return _device.CreateDescriptorSetLayout(
            new VkDescriptorSetLayoutCreateInfo { BindingCount = 2 + ImageCount, Bindings = bindings },
            null
        );
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageImage, DescriptorCount = Renderer.FramesInFlight * ImageCount },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 3, PoolSizes = sizes },
            null
        );
    }

    private void AllocateSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];
        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = _setLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _pool,
                DescriptorSetCount = Renderer.FramesInFlight,
                SetLayouts = layouts,
            },
            _sets
        );

        var imageInfos = stackalloc VkDescriptorImageInfo[ImageCount];
        for (var i = 0; i < ImageCount; i++)
            imageInfos[i] = new VkDescriptorImageInfo { ImageView = _views[i], ImageLayout = VkImageLayout.General };

        Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[2 + ImageCount];
        var statsInfo = new VkDescriptorBufferInfo { Buffer = _foamStats.Handle, Range = (ByteSize64)FoamStatsBytes };

        for (var frame = 0; frame < Renderer.FramesInFlight; frame++)
        {
            var bufferInfo = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(frame * UniformAlignment),
                Range = (ByteSize64)sizeof(FftUniforms),
            };

            writes[0] = new VkWriteDescriptorSet
            {
                DstSet = _sets[frame],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.UniformBuffer,
                BufferInfo = &bufferInfo,
            };

            for (var i = 0; i < ImageCount; i++)
            {
                writes[1 + i] = new VkWriteDescriptorSet
                {
                    DstSet = _sets[frame],
                    DstBinding = 1 + i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageImage,
                    ImageInfo = imageInfos + i,
                };
            }

            writes[1 + ImageCount] = new VkWriteDescriptorSet
            {
                DstSet = _sets[frame],
                DstBinding = 1 + ImageCount,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &statsInfo,
            };

            _device.UpdateDescriptorSets(writes, []);
        }
    }

    private VkPipeline CreatePipeline(string file)
    {
        var module = ShaderLoader.Load(_device, file, ShaderKind.ComputeShader);

        try
        {
            fixed (byte* name = "main"u8)
            {
                var pipeline = new VkPipeline();
                _device.CreateComputePipelines(
                    default,
                    [
                        new VkComputePipelineCreateInfo
                        {
                            Stage = new VkPipelineShaderStageCreateInfo
                            {
                                Stage = VkShaderStageFlags.ComputeBit,
                                Module = module,
                                Name = name,
                            },
                            Layout = _layout,
                        },
                    ],
                    null,
                    new ReadOnlySpan<VkPipeline>(&pipeline, 1)
                );
                return pipeline;
            }
        }
        finally
        {
            _device.DestroyShaderModule(module, null);
        }
    }

    /// <summary>
    /// Records the spectrum cache when it is stale, the row IFFT (which evolves the spectrum to this frame's time), the
    /// column IFFT (which resolves the maps and the foam) and the whitecap thresholds. Must be recorded outside a render
    /// pass, before the ocean draw.
    /// </summary>
    public void Record(CommandBuffer commands, int frameIndex, OceanSwell swell, double renderTime, float frameSeconds)
    {
        var rebuild = WriteUniforms(frameIndex, swell, renderTime, frameSeconds);

        if (!_initialised)
        {
            TransitionToGeneral(commands);
            _initialised = true;
        }
        else
        {
            // The previous frame's compute read specA/specB and the cache, and wrote the stats, which this frame's
            // compute overwrites. Compute only: the draw reads none of these, so the row pass overlaps the previous
            // frame's draw.
            ComputeBarrier(commands);
        }

        commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_sets[frameIndex]], []);

        if (rebuild)
        {
            commands.BindPipeline(VkPipelineBindPoint.Compute, _h0);
            commands.Dispatch(Size / 16, Size / 16, Cascades);
            ComputeBarrier(commands);
        }

        commands.BindPipeline(VkPipelineBindPoint.Compute, _fft);
        Span<int> direction = [0];
        commands.PushConstants(_layout, VkShaderStageFlags.ComputeBit, 0, MemoryMarshal.AsBytes(direction));
        commands.Dispatch(Size, 1, Cascades);

        // The rows must be complete, and the previous frame's draw must have finished sampling the maps the column pass
        // overwrites.
        Barrier(
            commands,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderWriteBit,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit
        );

        direction[0] = 1;
        commands.PushConstants(_layout, VkShaderStageFlags.ComputeBit, 0, MemoryMarshal.AsBytes(direction));
        commands.Dispatch(Size, 1, Cascades);
        ComputeBarrier(commands);

        // This frame's J histograms become the whitecap thresholds the column pass seeds foam below next frame.
        commands.BindPipeline(VkPipelineBindPoint.Compute, _foamThreshold);
        commands.Dispatch(1, 1, 1);

        Barrier(
            commands,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderWriteBit,
            VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderReadBit
        );
    }

    /// <summary>Writes this frame's uniforms. True when the spectrum cache must be rebuilt from them.</summary>
    private bool WriteUniforms(int frameIndex, OceanSwell swell, double renderTime, float frameSeconds)
    {
        var spectrum = swell.GpuSpectrum;
        var p = spectrum.Params;
        var depth = float.IsFinite(p.Depth) ? p.Depth : 1e6f;
        var u = (FftUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformAlignment);

        u->CascadeL = CascadeSizes with { W = Size };
        u->CascadeKLo = BandLow(p.MinWavelength);
        u->CascadeKHi = new Vector4(BandEdges[0], BandEdges[1], 1e9f, 0f);
        u->Wind = new Vector4(p.WindSpeed, p.WindDirection, Math.Max(p.Fetch, (float)Jonswap.MinFetch), depth);
        u->Spectrum = new Vector4(spectrum.Alpha, spectrum.PeakOmega, p.Gamma, p.Spread);
        u->Params = new Vector4(spectrum.SpreadNorm, Choppiness, (float)(renderTime % LoopPeriod), (float)LoopPeriod);
        u->Foam = new Vector4(MathF.Exp(-FoamDecay * frameSeconds), WhitecapCoverage(p.WindSpeed) / WhitecapLinger, frameSeconds, WhitecapEase);
        u->Seed = Seed;

        // The cache depends on everything but choppiness, time and the foam.
        var same = _cacheValid
            && u->CascadeL == _cached.CascadeL
            && u->CascadeKLo == _cached.CascadeKLo
            && u->CascadeKHi == _cached.CascadeKHi
            && u->Wind == _cached.Wind
            && u->Spectrum == _cached.Spectrum
            && u->Params.X == _cached.Params.X
            && u->Params.W == _cached.Params.W
            && u->Seed == _cached.Seed;

        if (same)
            return false;

        _cached = *u;
        _cacheValid = true;
        return true;
    }

    private void TransitionToGeneral(CommandBuffer commands)
    {
        Span<VkImageMemoryBarrier> barriers = stackalloc VkImageMemoryBarrier[ImageCount];
        for (var i = 0; i < ImageCount; i++)
        {
            barriers[i] = new VkImageMemoryBarrier
            {
                DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.General,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _images[i],
                SubresourceRange = new VkImageSubresourceRange
                {
                    AspectMask = VkImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = Cascades,
                },
            };
        }

        commands.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.ComputeShaderBit, VkDependencyFlags.None, [], [], barriers);
    }

    private static void ComputeBarrier(CommandBuffer commands) =>
        Barrier(
            commands,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderWriteBit,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit
        );

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess)
    {
        commands.PipelineBarrier(
            srcStage,
            dstStage,
            VkDependencyFlags.None,
            [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }],
            [],
            []
        );
    }

    public void Dispose()
    {
        _device.DestroyPipeline(_h0, null);
        _device.DestroyPipeline(_fft, null);
        _device.DestroyPipeline(_foamThreshold, null);
        _foamStats.Dispose();
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _uniforms.Dispose();
        _device.DestroySampler(Sampler, null);

        for (var i = 0; i < ImageCount; i++)
        {
            _device.DestroyImageView(_views[i], null);
            _allocator.DestroyImage(_images[i], _allocations[i]);
        }
    }
}
