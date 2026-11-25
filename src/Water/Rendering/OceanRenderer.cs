using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// Draws the swell as a camera-centred clipmap. Vertices come from gl_VertexIndex; each level is one indexed draw.
/// The vertex shader evaluates the same wave components as <see cref="OceanSwell"/>.
/// </summary>
public sealed unsafe class OceanRenderer : IDisposable
{
    public const int Levels = 10;
    public const int HalfCells = 48;
    public const float BaseSpacing = 0.25f;
    public const int MaxWaves = OceanSwell.MaxComponents;
    private const float MorphStart = 0.70f;
    private const float MorphEnd = 0.90f;
    private const int PushConstantSize = 32;
    private const int UniformAlignment = 256;

    public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.30f, 0.55f));
    public float SunIntensity = 3f;
    public Vector3 SkyZenith = new(0.16f, 0.34f, 0.66f);
    public Vector3 SkyHorizon = new(0.62f, 0.74f, 0.86f);
    public float FogDensity = 0.00012f;
    public Vector3 WaterDeep = new(0.004f, 0.026f, 0.055f);
    public Vector3 WaterScatter = new(0.0f, 0.11f, 0.10f);

    /// <summary>Swash sheets and the foam they leave; off for an A/B bench.</summary>
    public bool ShoreWash = true;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly GpuBuffer _buffer;
    private readonly int[] _frameOffsets = new int[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;

    // Each level is drawn as 4 x 4 tiles of the grid, culled against the view frustum: the clipmap surrounds the camera,
    // so most of it is behind or beside the view, and the vertex work (the swell sum per vertex) dominates the ocean.
    private const int TileSide = 4;
    private const int Tiles = TileSide * TileSide;
    private const int TileQuads = 2 * HalfCells / TileSide;

    /// <summary>Horizontal cull margin (m): the swell's and the cascades' horizontal displacement.</summary>
    private const float CullMargin = 12f;

    private readonly int[] _tileFirst = new int[2 * Tiles];
    private readonly int[] _tileCount = new int[2 * Tiles];
    private readonly Vector4[] _frustum = new Vector4[5];
    private float _cullTop;
    private float _cullBottom;

    /// <summary>Clipmap tiles drawn last frame, out of all of them.</summary>
    public int TilesDrawn { get; private set; }

    /// <summary>Clipmap surface triangles drawn last frame.</summary>
    public int TrianglesLastFrame { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameHeader
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
        public Vector4 WaterDeep;
        public Vector4 WaterScatter;
        public Vector4 CascadeL;
        public Vector4 CascadeKLo;
        public Vector4 FoamParams;
        public Vector4 DepthRect;
        public Vector4 CoastRect;
        public Vector4 RefEnergy0;
        public Vector4 RefEnergy1;
        public Vector4 RefEnergy2;
        public Vector4 RefChop0;
        public Vector4 RefChop1;
        public Vector4 RefChop2;
        public Vector4 RefK0_0;
        public Vector4 RefK0_1;
        public Vector4 RefK0_2;
        public Vector4 CoastDir;
        public Vector4 CoastPrevRect;
        public Vector4 Carrier;
        public Vector4 CarrierChop;
        public Vector4 Shore;
        public Vector4 CoastFarRect;
        public Vector4 CoastFarPrevRect;
        public int WaveCount;
        public int RefMin;
        public int RefMax;
        public int CoastSlots;
    }

    private readonly OceanFft _fft;
    // Layer B maps: device-local buffers shared by all frames. A new field is staged and copied into one no frame in
    // flight still reads, a sixteenth per frame, and drawn once complete; until then the GPU keeps drawing the field it replaces.
    /// <summary>Near and far fields: the current pair, the pair fading out, and a field being copied.</summary>
    private const int CoastalSlots = 6;
    private const int CoastalCopyChunks = 16;
    private const int CoastalFloats = CoastalField.Resolution * CoastalField.Resolution * CoastalField.TexelFloats;
    private const int CoastalChunkFloats = (CoastalFloats + CoastalCopyChunks - 1) / CoastalCopyChunks;

    /// <summary>Chunks staged per frame: one per field of the current and previous pairs.</summary>
    private const int CoastalCopiesPerFrame = 4;
    private readonly GpuBuffer[] _coastal = new GpuBuffer[CoastalSlots];
    private readonly GpuBuffer _coastalStaging;
    private readonly VkBufferCopy[] _coastalCopies = new VkBufferCopy[CoastalCopiesPerFrame];
    private readonly int[] _coastalCopySlots = new int[CoastalCopiesPerFrame];
    private readonly int[] _coastalVersion = new int[CoastalSlots];
    private readonly int[] _coastalCopied = new int[CoastalSlots];
    private readonly long[] _coastalUsed = new long[CoastalSlots];
    private int _coastalCopyCount;
    private int _coastalStagingBase;
    private long _frameNumber;
    // Surf foam left behind (ocean_surf_foam.comp): one float per cell of the depth map's 512² grid, GPU only, shared by
    // all frames; cleared when the depth map re-centres.
    private const int SurfFoamBinding = 2 + SampledMaps;
    private const int SurfFoamResolution = 512;
    private const int SurfFoamBytes = SurfFoamResolution * SurfFoamResolution * sizeof(float);
    private readonly GpuBuffer _surfFoam;
    private readonly VkPipeline _surfFoamUpdate;

    // The surf that seeds the foam map moves per tick, so above the tick rate the pass waits for the next tick and decays by the frames it skipped.
    private long _foamTick = -1;
    private float _foamSeconds;

    private const int Bindings = 3 + SampledMaps;
    private bool _initialised;
    private Vector4 _depthRect;
    private Vector4 _surfFoamRect;

    // GPU scopes inside the ocean's own work, nested in the caller's (the profiler and --bench show each).
    private readonly GpuTimer _timer;
    private readonly int _surfaceScope;
    private readonly int _surfFoamScope;
    private const int CoastalBytes = CoastalField.Resolution * CoastalField.Resolution * CoastalField.TexelFloats * sizeof(float);

    private readonly VkImageView _depthView;
    private readonly VkSampler _depthSampler;

    /// <param name="depthView">Signed depth map (BathymetryTexture), in SHADER_READ_ONLY_OPTIMAL.</param>
    public OceanRenderer(Renderer renderer, OceanFft fft, VkImageView depthView, VkSampler depthSampler)
    {
        _fft = fft;
        _depthView = depthView;
        _depthSampler = depthSampler;
        _device = renderer.Context.Device;
        _pipelines = renderer.Context.Pipelines;

        // One allocation: clipmap indices, then one uniform block per frame in flight.
        CountIndices(out var fullIndexCount, out var ringIndexCount);
        var offset = Align((fullIndexCount + ringIndexCount) * sizeof(ushort));
        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            _frameOffsets[i] = offset;
            offset = Align(offset + FrameBufferSize);
        }

        _buffer = new GpuBuffer(renderer.Context, offset, VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.UniformBufferBit, true);

        for (var s = 0; s < CoastalSlots; s++)
        {
            _coastal[s] = new GpuBuffer(renderer.Context, CoastalBytes, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, false);
            _coastalUsed[s] = -Renderer.FramesInFlight - 1;
        }

        _coastalStaging = new GpuBuffer(renderer.Context, CoastalChunkFloats * sizeof(float) * CoastalCopiesPerFrame * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);

        _surfFoam = new GpuBuffer(renderer.Context, SurfFoamBytes, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, false);

        WriteIndices(_buffer.AsSpan<ushort>(), _tileFirst, _tileCount);
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateFrameResources();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = PushStages, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass, "ocean.vert", "ocean.frag");
        _surfFoamUpdate = CreateComputePipeline("ocean_surf_foam.comp");

        _timer = renderer.GpuTimer;
        _surfaceScope = _timer.Register("Ocean surface");
        _surfFoamScope = _timer.Register("Surf foam");
    }

    private const VkShaderStageFlags PushStages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit;

    public Vector3 SkyHorizonColor => SkyHorizon;

    private static int FrameBufferSize => sizeof(FrameHeader) + MaxWaves * sizeof(GpuWave);

    private static int Align(int value) => (value + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    private const int Hole = HalfCells / 2 - 1;

    private static void CountIndices(out int fullCount, out int ringCount)
    {
        const int quads = 2 * HalfCells * 2 * HalfCells;
        const int holeQuads = 2 * Hole * 2 * Hole;
        fullCount = quads * 6;
        ringCount = (quads - holeQuads) * 6;
    }

    /// <summary>
    /// The full grid (level 0), then the ring (coarser levels, without the hole the finer level fills), each written tile
    /// by tile so a tile is one contiguous index range: first[variant * Tiles + tile], count[...]. A ring's inner tiles
    /// keep only their quads around the hole.
    /// </summary>
    private static void WriteIndices(Span<ushort> indices, int[] first, int[] count)
    {
        const int n = HalfCells;
        const int side = 2 * n + 1;
        var w = 0;

        for (var pass = 0; pass < 2; pass++)
        {
            for (var tile = 0; tile < Tiles; tile++)
            {
                first[pass * Tiles + tile] = w;
                var i0 = -n + tile % TileSide * TileQuads;
                var j0 = -n + tile / TileSide * TileQuads;

                for (var j = j0; j < j0 + TileQuads; j++)
                {
                    for (var i = i0; i < i0 + TileQuads; i++)
                    {
                        if (pass == 1 && i >= -Hole && i < Hole && j >= -Hole && j < Hole)
                            continue;

                        var v00 = (ushort)((j + n) * side + i + n);
                        var v10 = (ushort)(v00 + 1);
                        var v01 = (ushort)(v00 + side);
                        var v11 = (ushort)(v01 + 1);

                        indices[w++] = v00;
                        indices[w++] = v01;
                        indices[w++] = v10;
                        indices[w++] = v10;
                        indices[w++] = v01;
                        indices[w++] = v11;
                    }
                }

                count[pass * Tiles + tile] = w - first[pass * Tiles + tile];
            }
        }
    }

    /// <summary>
    /// The frustum's side and near planes (a, b, c, d with a x + b y + c z + d ≥ 0 inside) from a row-vector view-projection
    /// with Vulkan clip space (−w ≤ x, y ≤ w) and an infinite reverse-Z far plane, which needs no plane.
    /// </summary>
    private void ExtractFrustum(in Matrix4x4 m)
    {
        var x = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var y = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var z = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var w = new Vector4(m.M14, m.M24, m.M34, m.M44);
        _frustum[0] = w + x;
        _frustum[1] = w - x;
        _frustum[2] = w + y;
        _frustum[3] = w - y;
        _frustum[4] = w - z;
    }

    private bool Visible(Vector3 min, Vector3 max)
    {
        foreach (var plane in _frustum)
        {
            var corner = new Vector3(plane.X >= 0f ? max.X : min.X, plane.Y >= 0f ? max.Y : min.Y, plane.Z >= 0f ? max.Z : min.Z);
            if (plane.X * corner.X + plane.Y * corner.Y + plane.Z * corner.Z + plane.W < 0f)
                return false;
        }

        return true;
    }

    private const int SampledMaps = 4;

    private VkDescriptorSetLayout CreateSetLayout()
    {
        const VkShaderStageFlags stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit | VkShaderStageFlags.ComputeBit;
        const VkShaderStageFlags buffers = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit;
        var bindings = stackalloc VkDescriptorSetLayoutBinding[Bindings];
        bindings[0] = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = stages,
        };

        for (var i = 0; i < SampledMaps; i++)
        {
            bindings[1 + i] = new VkDescriptorSetLayoutBinding
            {
                Binding = 1 + i,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = stages,
            };
        }

        bindings[1 + SampledMaps] = new VkDescriptorSetLayoutBinding
        {
            Binding = 1 + SampledMaps,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = CoastalSlots,
            StageFlags = buffers,
        };

        bindings[SurfFoamBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = SurfFoamBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        return _device.CreateDescriptorSetLayout(
            new VkDescriptorSetLayoutCreateInfo { BindingCount = Bindings, Bindings = bindings },
            null
        );
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = Renderer.FramesInFlight * SampledMaps },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight * (CoastalSlots + 1) },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 3, PoolSizes = sizes },
            null
        );
    }

    private void CreateFrameResources()
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

        var maps = stackalloc VkDescriptorImageInfo[]
        {
            new() { Sampler = _fft.Sampler, ImageView = _fft.DisplacementView, ImageLayout = VkImageLayout.General },
            new() { Sampler = _fft.Sampler, ImageView = _fft.DerivativeView, ImageLayout = VkImageLayout.General },
            new() { Sampler = _fft.Sampler, ImageView = _fft.FoamView, ImageLayout = VkImageLayout.General },
            new() { Sampler = _depthSampler, ImageView = _depthView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
        };

        Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[Bindings];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = _buffer.Handle,
                Offset = (ByteSize64)_frameOffsets[i],
                Range = (ByteSize64)FrameBufferSize,
            };

            writes[0] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.UniformBuffer,
                BufferInfo = &info,
            };

            for (var m = 0; m < SampledMaps; m++)
            {
                writes[1 + m] = new VkWriteDescriptorSet
                {
                    DstSet = _sets[i],
                    DstBinding = 1 + m,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = maps + m,
                };
            }

            var coastal = stackalloc VkDescriptorBufferInfo[CoastalSlots];
            for (var s = 0; s < CoastalSlots; s++)
                coastal[s] = new VkDescriptorBufferInfo { Buffer = _coastal[s].Handle, Range = (ByteSize64)CoastalBytes };

            writes[1 + SampledMaps] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = 1 + SampledMaps,
                DescriptorCount = CoastalSlots,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = coastal,
            };

            var surfFoam = new VkDescriptorBufferInfo { Buffer = _surfFoam.Handle, Range = (ByteSize64)SurfFoamBytes };
            writes[SurfFoamBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = SurfFoamBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &surfFoam,
            };

            _device.UpdateDescriptorSets(writes, []);
        }
    }

    private VkPipeline CreatePipeline(VkRenderPass renderPass, string vertex, string fragment)
    {
        var vert = ShaderLoader.Load(_device, vertex, ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, fragment, ShaderKind.FragmentShader);

        try
        {
            var entry = "main"u8;
            fixed (byte* name = entry)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[]
                {
                    new() { Stage = VkShaderStageFlags.VertexBit, Module = vert, Name = name },
                    new() { Stage = VkShaderStageFlags.FragmentBit, Module = frag, Name = name },
                };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo();
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
                var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = VkCullModeFlags.None,
                    FrontFace = VkFrontFace.CounterClockwise,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var depth = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = VkCompareOp.Greater,
                };
                // Blended so thin simulated-zone water fades over the terrain (the ocean draws after terrain).
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                    DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = VkBlendOp.Add,
                    SrcAlphaBlendFactor = VkBlendFactor.One,
                    DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = VkBlendOp.Add,
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
                var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

                return _pipelines.CreateGraphicsPipeline(
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &inputAssembly,
                        ViewportState = &viewport,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        DepthStencilState = &depth,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = _layout,
                        RenderPass = renderPass,
                    }
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(vert, null);
            _device.DestroyShaderModule(frag, null);
        }
    }

    /// <summary>
    /// Writes this frame's waves, camera and coastal maps. Call once per frame before <see cref="RecordCompute"/> and
    /// <see cref="Record"/>, which read them.
    /// </summary>
    /// <param name="depthRect">(origin x, origin z, world size, depth outside the map).</param>
    public void Prepare(int frameIndex, OceanSwell swell, double renderTime, in Matrix4x4 viewProj, Vector3 cameraPos, Vector4 depthRect)
    {
        _frameNumber++;
        var block = (byte*)_buffer.Mapped + _frameOffsets[frameIndex];
        var header = (FrameHeader*)block;
        var waves = new Span<GpuWave>(block + sizeof(FrameHeader), MaxWaves);

        header->ViewProj = viewProj;
        ExtractFrustum(viewProj);

        // Vertical cull range: the tallest swell crests and troughs, and water standing in dug holes well below the sea.
        var hs = swell.SignificantWaveHeight;
        (_cullTop, _cullBottom) = (2f * hs + 4f, -MathF.Max(2f * hs + 4f, 25f));
        header->CameraPos = new Vector4(cameraPos, (float)renderTime);
        header->SunDir = new Vector4(SunDirection, SunIntensity);
        header->SkyZenith = new Vector4(SkyZenith, 0f);
        header->SkyHorizon = new Vector4(SkyHorizon, FogDensity);
        header->WaterDeep = new Vector4(WaterDeep, 0f);
        header->WaterScatter = new Vector4(WaterScatter, 0f);
        header->CascadeL = _fft.CascadeSizes;
        header->CascadeKLo = OceanFft.BandLow(swell.GpuSpectrum.Params.MinWavelength) with { W = _fft.FoamStrength };
        header->FoamParams = new Vector4(_fft.DebugView ? 1f : 0f, 0f, 0f, 0f);
        header->DepthRect = depthRect;
        _depthRect = depthRect;
        WriteCoastal(header, frameIndex, swell);
        (header->Carrier, header->CarrierChop) = swell.GpuCarrier(renderTime);
        header->Shore = swell.GpuShore(ShoreWash);
        header->WaveCount = swell.CopyGpuWaves(waves, renderTime);
    }

    /// <summary>
    /// The ocean's compute work before the water pass: coastal copies and the surf foam left behind
    /// (ocean_surf_foam.comp). Outside a render pass, after <see cref="Prepare"/>.
    /// </summary>
    public void RecordCompute(CommandBuffer commands, int frameIndex, float frameSeconds, long tickIndex)
    {
        // The previous frame's draw read the surf foam; this frame's compute rewrites it.
        Barrier(commands, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_sets[frameIndex]], []);

        if (!_initialised)
        {
            commands.FillBuffer(_surfFoam.Handle, (ByteSize64)0, (ByteSize64)SurfFoamBytes, 0);
            var shaders = VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit;
            Barrier(commands, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, shaders, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
            _initialised = true;
        }

        RecordCoastalCopies(commands);

        // The surf foam map, once per tick (or at once when the depth map it lives on re-centres, since the old foam is
        // then in the wrong place), decaying by the time since its last update.
        _foamSeconds += MathF.Min(frameSeconds, 0.1f);
        if (tickIndex == _foamTick && _depthRect == _surfFoamRect)
            return;

        Span<float> push = stackalloc float[PushConstantSize / sizeof(float)];
        push[0] = _foamSeconds;
        push[3] = _depthRect == _surfFoamRect ? 0f : 1f;
        commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
        commands.BindPipeline(VkPipelineBindPoint.Compute, _surfFoamUpdate);
        _timer.Begin(commands, _surfFoamScope);
        commands.Dispatch(SurfFoamResolution / 16, SurfFoamResolution / 16, 1);
        _timer.End(commands, _surfFoamScope);
        Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit);
        _foamTick = tickIndex;
        _foamSeconds = 0f;
        _surfFoamRect = _depthRect;
    }

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess) =>
        commands.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }], [], []);

    /// <summary>Records the surface (one draw per clipmap level). Inside the water pass.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, Vector3 cameraPos)
    {
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.BindIndexBuffer(_buffer.Handle, (ByteSize64)0, VkIndexType.UInt16);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        Span<float> push = stackalloc float[PushConstantSize / sizeof(float)];
        var spacing = BaseSpacing;
        _timer.Begin(commands, _surfaceScope);

        var drawn = 0;
        var indices = 0;

        for (var level = 0; level < Levels; level++)
        {
            var snap = 2f * spacing;
            var centreX = MathF.Round(cameraPos.X / snap) * snap;
            var centreZ = MathF.Round(cameraPos.Z / snap) * snap;
            push[0] = centreX;
            push[1] = centreZ;
            push[2] = spacing;
            push[3] = HalfCells;
            push[4] = MorphStart;
            push[5] = MorphEnd;
            push[6] = level;
            push[7] = 0f;

            commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
            var variant = level == 0 ? 0 : 1;
            var tileSize = TileQuads * spacing;

            for (var tile = 0; tile < Tiles; tile++)
            {
                var count = _tileCount[variant * Tiles + tile];
                var x0 = centreX + (tile % TileSide * TileQuads - HalfCells) * spacing;
                var z0 = centreZ + (tile / TileSide * TileQuads - HalfCells) * spacing;
                var min = new Vector3(x0 - CullMargin, _cullBottom, z0 - CullMargin);
                var max = new Vector3(x0 + tileSize + CullMargin, _cullTop, z0 + tileSize + CullMargin);

                if (count == 0 || !Visible(min, max))
                    continue;

                commands.DrawIndexed(count, 1, _tileFirst[variant * Tiles + tile], 0, 0);
                drawn++;
                indices += count;
            }

            spacing *= 2f;
        }

        TilesDrawn = drawn;
        TrianglesLastFrame = indices / 3;

        _timer.End(commands, _surfaceScope);
    }

    private VkPipeline CreateComputePipeline(string file)
    {
        var module = ShaderLoader.Load(_device, file, ShaderKind.ComputeShader);

        try
        {
            fixed (byte* name = "main"u8)
            {
                return _pipelines.CreateComputePipeline(
                    new VkComputePipelineCreateInfo
                    {
                        Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = name },
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
    /// Layer B maps for this frame: the current field and, while a rebuild fades in, the one it replaces. A field still
    /// being copied isn't drawn yet; the GPU keeps drawing the one it replaces on its own until it is complete.
    /// </summary>
    private void WriteCoastal(FrameHeader* header, int frameIndex, OceanSwell swell)
    {
        var field = swell.Coastal;
        var energy = swell.RefEnergy;
        header->RefEnergy0 = new Vector4(energy[0], energy[1], energy[2], energy[3]);
        header->RefEnergy1 = new Vector4(energy[4], energy[5], energy[6], energy[7]);
        header->RefEnergy2 = new Vector4(energy[8], energy[9], energy[10], energy[11]);
        var chop = swell.RefChop;
        header->RefChop0 = new Vector4(chop[0], chop[1], chop[2], chop[3]);
        header->RefChop1 = new Vector4(chop[4], chop[5], chop[6], chop[7]);
        header->RefChop2 = new Vector4(chop[8], chop[9], chop[10], chop[11]);
        header->RefK0_0 = new Vector4(CoastalField.RefK0(0), CoastalField.RefK0(1), CoastalField.RefK0(2), CoastalField.RefK0(3));
        header->RefK0_1 = new Vector4(CoastalField.RefK0(4), CoastalField.RefK0(5), CoastalField.RefK0(6), CoastalField.RefK0(7));
        header->RefK0_2 = new Vector4(CoastalField.RefK0(8), CoastalField.RefK0(9), CoastalField.RefK0(10), CoastalField.RefK0(11));
        header->RefMin = swell.RefMin;
        header->RefMax = swell.RefMax;

        _coastalCopyCount = 0;
        _coastalStagingBase = frameIndex * CoastalCopiesPerFrame * CoastalChunkFloats;

        header->CoastFarRect = Vector4.Zero;
        header->CoastFarPrevRect = Vector4.Zero;

        if (field is null)
        {
            header->CoastRect = Vector4.Zero;
            header->CoastPrevRect = Vector4.Zero;
            return;
        }

        var previous = swell.CoastalPrevious;
        var slot = PairSlots(field, out var farSlot);
        var prevFarSlot = -1;
        var prevSlot = previous is null ? -1 : PairSlots(previous, out prevFarSlot);
        var fade = swell.CoastalFade;

        if (slot < 0)
        {
            if (prevSlot < 0)
            {
                header->CoastRect = Vector4.Zero;
                header->CoastPrevRect = Vector4.Zero;
                return;
            }

            (field, slot, farSlot, previous, prevSlot, fade) = (previous!, prevSlot, prevFarSlot, null, -1, 1f);
        }

        Use(slot);
        Use(farSlot);
        header->CoastRect = Rect(field);
        header->CoastFarRect = farSlot < 0 ? Vector4.Zero : Rect(field.Far!);
        var bits = slot | Math.Max(farSlot, 0) << 6;

        if (prevSlot >= 0)
        {
            Use(prevSlot);
            Use(prevFarSlot);
            header->CoastPrevRect = Rect(previous!);
            header->CoastFarPrevRect = prevFarSlot < 0 ? Vector4.Zero : Rect(previous!.Far!);
            bits |= prevSlot << 3 | Math.Max(prevFarSlot, 0) << 9;
        }
        else
            header->CoastPrevRect = Vector4.Zero;

        var (dirZ, dirX) = MathF.SinCos(field.Direction);
        header->CoastDir = new Vector4(dirX, dirZ, prevSlot < 0 && previous is not null ? 1f : fade, prevSlot < 0 ? field.Direction : previous!.Direction);
        header->CoastSlots = bits;
    }

    private static Vector4 Rect(CoastalField field) => new(field.Origin.X, field.Origin.Y, field.Cell, CoastalField.Resolution);

    /// <summary>
    /// A near field's buffer slot, or -1 until it and its far field are both copied; <paramref name="farSlot"/> = the far
    /// field's, -1 without one.
    /// </summary>
    private int PairSlots(CoastalField field, out int farSlot)
    {
        farSlot = -1;
        var slot = Slot(field);

        if (field.Far is null)
            return slot;

        farSlot = Slot(field.Far);
        return farSlot < 0 ? -1 : slot;
    }

    private void Use(int slot)
    {
        if (slot >= 0)
            _coastalUsed[slot] = _frameNumber;
    }

    /// <summary>
    /// The buffer holding all of <paramref name="field"/>, or -1 while it is still being copied (another sixteenth goes
    /// up each call). A new field starts in the buffer least recently drawn from, once no frame in flight reads it.
    /// </summary>
    private int Slot(CoastalField field)
    {
        var total = field.Texels.Length;
        var slot = -1;

        for (var s = 0; s < CoastalSlots && slot < 0; s++)
            if (_coastalVersion[s] == field.Version)
                slot = s;

        if (slot < 0)
        {
            for (var s = 0; s < CoastalSlots; s++)
            {
                if (_coastalUsed[s] + Renderer.FramesInFlight >= _frameNumber || (slot >= 0 && _coastalUsed[s] >= _coastalUsed[slot]))
                    continue;

                slot = s;
            }

            if (slot < 0)
                return -1;

            _coastalVersion[slot] = field.Version;
            _coastalCopied[slot] = 0;
        }

        // A field still going up holds its buffer, or the other field of its pair could take it and both restart.
        if (_coastalCopied[slot] < total)
        {
            _coastalUsed[slot] = _frameNumber;

            if (_coastalCopyCount == CoastalCopiesPerFrame)
                return -1;

            var chunk = Math.Min(CoastalChunkFloats, total - _coastalCopied[slot]);
            var staged = _coastalStagingBase + _coastalCopyCount * CoastalChunkFloats;
            field.Texels.AsSpan(_coastalCopied[slot], chunk).CopyTo(_coastalStaging.AsSpan<float>().Slice(staged, chunk));
            _coastalCopies[_coastalCopyCount] = new VkBufferCopy
            {
                SrcOffset = (ByteSize64)((long)staged * sizeof(float)),
                DstOffset = (ByteSize64)((long)_coastalCopied[slot] * sizeof(float)),
                Size = (ByteSize64)((long)chunk * sizeof(float)),
            };
            _coastalCopySlots[_coastalCopyCount++] = slot;
            _coastalCopied[slot] += chunk;
            return -1;
        }

        return slot;
    }

    /// <summary>The coastal chunks <see cref="Prepare"/> staged, then visible to every later read of the maps.</summary>
    private void RecordCoastalCopies(CommandBuffer commands)
    {
        if (_coastalCopyCount == 0)
            return;

        for (var i = 0; i < _coastalCopyCount; i++)
            commands.CopyBuffer(_coastalStaging.Handle, _coastal[_coastalCopySlots[i]].Handle, new ReadOnlySpan<VkBufferCopy>(_coastalCopies, i, 1));

        var shaders = VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.FragmentShaderBit;
        Barrier(commands, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, shaders, VkAccessFlags.ShaderReadBit);
        _coastalCopyCount = 0;
    }

    public void Dispose()
    {
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipeline(_surfFoamUpdate, null);
        _surfFoam.Dispose();
        _device.DestroyPipelineLayout(_layout, null);

        _buffer.Dispose();

        foreach (var buffer in _coastal)
            buffer.Dispose();

        _coastalStaging.Dispose();

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
