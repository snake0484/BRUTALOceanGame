using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// The wind sea on voxel water (docs/voxel-water.md "Wind on voxel water"): ten octave bands of wave slope, 16 m down to
/// 1.6 cm, each of unit mean square slope and spread about the wind, evolved and transformed on the GPU each frame. Two
/// bands share a layer and its patch (128 m, then a quarter each layer), so voxel water reads both with one fetch.
/// Voxel water weights them per vertex by the fetch-limited spectrum its own wind and fetch raise, so a lake, a pond and
/// a flooded pit each show their own peak wavelength. Images are kept in GENERAL layout.
/// </summary>
public sealed unsafe class WindSeaBands : IDisposable
{
    public const int Size = 128;
    public const int Bands = 10;
    public const int Layers = Bands / 2;
    public const double LoopPeriod = 1000.0;
    private const int UniformAlignment = 256;
    private const int ImageCount = 4;

    /// <summary>Radians the wind may turn before the spectrum cache is rebuilt.</summary>
    private const float RebuildTurn = 0.5f * MathF.PI / 180f;

    private const int H0 = 0;
    private const int Omega = 1;
    private const int Spec = 2;
    private const int Slopes = 3;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
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

    private bool _initialised;
    private bool _cacheValid;
    private float _cachedDirection;
    private float _cachedSpread;

    [StructLayout(LayoutKind.Sequential)]
    private struct BandUniforms
    {
        public Vector4 Wind;
        public Vector4 Params;
    }

    public WindSeaBands(Renderer renderer)
    {
        _device = renderer.Context.Device;
        _pipelines = renderer.Context.Pipelines;
        _allocator = renderer.Context.Allocator;

        CreateImage(H0, VkFormat.R32G32B32A32SFloat, VkImageUsageFlags.StorageBit, Bands);
        CreateImage(Omega, VkFormat.R32SFloat, VkImageUsageFlags.StorageBit, Bands);
        CreateImage(Spec, VkFormat.R32G32B32A32SFloat, VkImageUsageFlags.StorageBit, Layers);
        CreateImage(Slopes, VkFormat.R16G16B16A16SFloat, VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit, Layers);

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
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        AllocateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.ComputeBit, Size = (ByteSize32)sizeof(int) },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _h0 = CreatePipeline("wind_sea_h0.comp");
        _fft = CreatePipeline("wind_sea_fft.comp");
    }

    public VkSampler Sampler { get; }

    /// <summary>Per layer: (hx, hz) of band 2i, (hx, hz) of band 2i + 1.</summary>
    public VkImageView SlopeView => _views[Slopes];

    /// <summary>True once recorded: the images are laid out and hold a transform.</summary>
    public bool Ready => _initialised;

    private void CreateImage(int index, VkFormat format, VkImageUsageFlags usage, int layers)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = Size, Height = Size, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = layers,
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
                    LayerCount = layers,
                },
            },
            null
        );
    }

    /// <summary>Binding 0 the uniforms, 1..ImageCount the images in index order.</summary>
    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[1 + ImageCount];
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

        return _device.CreateDescriptorSetLayout(
            new VkDescriptorSetLayoutCreateInfo { BindingCount = 1 + ImageCount, Bindings = bindings },
            null
        );
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageImage, DescriptorCount = Renderer.FramesInFlight * ImageCount },
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

        Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[1 + ImageCount];

        for (var frame = 0; frame < Renderer.FramesInFlight; frame++)
        {
            var bufferInfo = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(frame * UniformAlignment),
                Range = (ByteSize64)sizeof(BandUniforms),
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
                return _pipelines.CreateComputePipeline(
                    new VkComputePipelineCreateInfo
                    {
                        Stage = new VkPipelineShaderStageCreateInfo
                        {
                            Stage = VkShaderStageFlags.ComputeBit,
                            Module = module,
                            Name = name,
                        },
                        Layout = _layout,
                    }
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(module, null);
        }
    }

    /// <summary>
    /// Records the spectrum cache when the wind has turned or its spread changed, then the row and column IFFTs. Must be
    /// recorded outside a render pass, before the voxel water draw.
    /// </summary>
    /// <param name="wind">The 10 m wind (m/s, the way it blows toward), as voxel water gets it.</param>
    /// <param name="spread">The ocean's cos-2s spreading exponent and its normalisation (SpectrumState.SpreadNorm).</param>
    public void Record(CommandBuffer commands, int frameIndex, Vector2 wind, float spread, float spreadNorm, double renderTime)
    {
        var direction = wind.LengthSquared() > 1e-6f ? MathF.Atan2(wind.Y, wind.X) : _cachedDirection;
        var rebuild = !_cacheValid
            || MathF.Abs(MathF.IEEERemainder(direction - _cachedDirection, MathF.Tau)) > RebuildTurn
            || spread != _cachedSpread;

        if (rebuild)
        {
            _cachedDirection = direction;
            _cachedSpread = spread;
            _cacheValid = true;
        }

        var u = (BandUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformAlignment);
        u->Wind = new Vector4(_cachedDirection, spread, spreadNorm, (float)(renderTime % LoopPeriod));
        u->Params = new Vector4((float)LoopPeriod, 0f, 0f, 0f);

        if (!_initialised)
        {
            TransitionToGeneral(commands);
            _initialised = true;
        }
        else
        {
            // The previous frame's row pass read the cache and its column pass the spectrum, which this frame overwrites.
            ComputeBarrier(commands);
        }

        commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_sets[frameIndex]], []);

        if (rebuild)
        {
            commands.BindPipeline(VkPipelineBindPoint.Compute, _h0);
            commands.Dispatch(Size / 16, Size / 16, Bands);
            ComputeBarrier(commands);
        }

        commands.BindPipeline(VkPipelineBindPoint.Compute, _fft);
        Span<int> pass = [0];
        commands.PushConstants(_layout, VkShaderStageFlags.ComputeBit, 0, MemoryMarshal.AsBytes(pass));
        commands.Dispatch(Size, 1, Layers);

        // The rows must be complete, and the previous frame's draw must have finished sampling the slopes.
        Barrier(
            commands,
            VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderWriteBit,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit
        );

        pass[0] = 1;
        commands.PushConstants(_layout, VkShaderStageFlags.ComputeBit, 0, MemoryMarshal.AsBytes(pass));
        commands.Dispatch(Size, 1, Layers);

        Barrier(
            commands,
            VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.ShaderWriteBit,
            VkPipelineStageFlags.FragmentShaderBit,
            VkAccessFlags.ShaderReadBit
        );
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
                    LayerCount = i < Spec ? Bands : Layers,
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
