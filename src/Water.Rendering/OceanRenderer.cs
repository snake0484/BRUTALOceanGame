using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// Draws the sea round the camera as a clipmap in the <see cref="SeaFrame"/>'s plane, bent onto the sea sphere. Vertices
/// come from gl_VertexIndex; each level is drawn tile by tile. The first <see cref="WaveLevels"/> levels carry the
/// surface: the vertex shader evaluates the same wave components over the same sea tiles as <see cref="OceanSurface"/>
/// (<see cref="SeaSlots"/>), plus the FFT detail and the shore wash. The levels past them are the far sea out to the
/// horizon, shaded only (glint and wind roughness). From altitude the fine levels are skipped.
/// </summary>
public sealed unsafe class OceanRenderer : IDisposable
{
    /// <summary>Levels that carry waves: the last has 128 m cells and reaches 6.1 km.</summary>
    public const int WaveLevels = 10;

    /// <summary>All levels: the last reaches 786 km, past the horizon from <see cref="MaxAltitude"/>.</summary>
    public const int Levels = 17;
    public const int HalfCells = 48;
    public const float BaseSpacing = 0.25f;
    /// <summary>Wave components across the four tile slots (ocean_common.glsl MAX_WAVES): a steady swell has ~150, one blending twice that.</summary>
    public const int MaxWaves = 1536;

    /// <summary>Distance (m) from the camera within which waves are drawn: the wave levels' corner.</summary>
    public const float WaveReach = 1.4142135f * HalfCells * BaseSpacing * (1 << (WaveLevels - 1));

    /// <summary>Camera height above the sea (m) up to which the sea can be drawn: the last level still passes the horizon.</summary>
    public const double MaxAltitude = 120_000.0;

    /// <summary>The first level drawn has cells of about this share of the camera's height over the sea.</summary>
    private const float FinestCell = 1f / 96f;
    private const float MorphStart = 0.70f;
    private const float MorphEnd = 0.90f;
    private const int PushConstantSize = 32;
    private const int UniformAlignment = 256;

    public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.30f, 0.55f));
    public float SunIntensity = 3f;
    public Vector3 SkyZenith = new(0.16f, 0.34f, 0.66f);
    public Vector3 SkyHorizon = new(0.62f, 0.74f, 0.86f);
    public float FogDensity = 0.00012f;

    /// <summary>Scales the fog (the air's density at the camera over sea level's).</summary>
    public float FogFade = 1f;

    /// <summary>
    /// 1 on the ground .. 0 from altitude, where the surface has faded out into the terrain's far sea (the impostor's
    /// look); nothing is worth drawing at 0.
    /// </summary>
    public float Opacity = 1f;
    public Vector3 WaterDeep = new(0.004f, 0.026f, 0.055f);
    public Vector3 WaterScatter = new(0.0f, 0.11f, 0.10f);

    /// <summary>Swash sheets and the foam they leave; off for an A/B bench.</summary>
    public bool ShoreWash = true;

    /// <summary>The breaking faces over the coast regions (ocean_breaker.glsl); off for an A/B bench.</summary>
    public bool BreakerFaces = true;

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

    /// <summary>Horizontal cull margin (m): the swell's and the cascades' horizontal displacement, and more where a breaking face draws its wall back.</summary>
    private const float CullMargin = 12f;
    private const float FaceCullMargin = 12f;

    private readonly int[] _tileFirst = new int[2 * Tiles];
    private readonly int[] _tileCount = new int[2 * Tiles];
    private readonly Vector4[] _frustum = new Vector4[5];
    private float _cullReach;
    private float _cullMargin;
    private int _firstLevel;

    /// <summary>Clipmap tiles drawn last frame, out of all of them.</summary>
    public int TilesDrawn { get; private set; }

    /// <summary>Clipmap surface triangles drawn last frame.</summary>
    public int TrianglesLastFrame { get; private set; }

    /// <summary>Coast regions drawn last frame.</summary>
    public int CoastDrawn { get; private set; }

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
        public Vector4 CascadeAnchorX;
        public Vector4 CascadeAnchorZ;
        public Vector4 DepthRect;
        public Vector4 DepthFar;
        public Vector4 DepthWrap;
        public Vector4 DepthValid;
        public Vector4 Planet;
        public Vector4 BandRow;
        public Vector4 BandSouth;
        public Vector4 BandNorth;
        public fixed float SlotToTile[4 * SeaTiles.MaxBlend];
        public fixed float SlotFromTile[4 * SeaTiles.MaxBlend];
        public fixed float SlotSide[4 * SeaTiles.MaxBlend];
        public fixed float SlotCarrier[4 * SeaTiles.MaxBlend];
        public fixed float SlotShore[4 * SeaTiles.MaxBlend];
        public fixed float Coast[CoastFloats * CoastRegionSet.Capacity];
        public fixed int SlotWaves[4 * SeaTiles.MaxBlend];
        public int WaveCount;
        public int SlotCount;
        public float DebugView;
        public int CoastCount;
    }

    /// <summary>Floats per coast region in the frame block: ocean_common.glsl CoastRegion (three vec4).</summary>
    private const int CoastFloats = 12;

    /// <summary>What the sea is drawn with this frame, beyond the swell.</summary>
    public struct View
    {
        /// <summary>Translation-free view-projection from the sea frame's axes.</summary>
        public Matrix4x4 ViewProj;

        /// <summary>The camera in the sea frame: x, z in its plane, y above the plane.</summary>
        public double3 Camera;

        /// <summary>
        /// The camera's place in the FFT tiles and the foam lace (m): its sea frame x, z plus whatever keeps them
        /// continuous when the client moves to another sea frame.
        /// </summary>
        public double DetailX;
        public double DetailZ;

        /// <summary>Near and far depth maps (ShoreDepthGrid.NearRect / FarRect): corner relative to the camera, world size (0 = none), depth outside.</summary>
        public Vector4 DepthNear;
        public Vector4 DepthFar;

        /// <summary>The part of the near window that holds its depth (ShoreDepthGrid.NearValid): min x, z and max x, z relative to the camera.</summary>
        public Vector4 DepthValid;

        /// <summary>The near window's corner cell (the map and the surf foam are stored toroidally) and a number that changes when its cells mean another place (a new sea frame).</summary>
        public int DepthCellX;
        public int DepthCellZ;
        public int DepthVersion;
    }

    /// <summary>
    /// Shader time wraps here (s): a multiple of the foam lace's LACE_WRAP (600 s) and <see cref="OceanFft.LoopPeriod"/>,
    /// so nothing jumps at the wrap and float time keeps millisecond precision however long the game runs.
    /// </summary>
    private const double TimeWrap = 3000.0;

    /// <summary>The foam lace's period (m): water_shading.glsl LACE_PERIOD cells over LACE_SCALE cells a metre.</summary>
    private const double LacePeriod = 256.0 / 1.4;

    private readonly OceanFft _fft;
    // Surf foam left behind (ocean_surf_foam.comp): one float per cell of the near depth map's 512² window, GPU only,
    // shared by all frames; stored toroidally like the map, so it stays put when the window moves.
    private const int SurfFoamBinding = 1 + SampledMaps;
    private const int SurfFoamResolution = 512;
    private const int SurfFoamBytes = SurfFoamResolution * SurfFoamResolution * sizeof(float);
    private readonly GpuBuffer _surfFoam;
    private readonly VkPipeline _surfFoamUpdate;

    // The surf that seeds the foam map moves per tick, so above the tick rate the pass waits for the next tick and decays by the frames it skipped.
    private long _foamTick = -1;
    private float _foamSeconds;

    private const int CoastBinding = SurfFoamBinding + 1;
    private const int CoastMaps = 3;
    private const int Bindings = 2 + SampledMaps + CoastMaps;
    private bool _initialised;
    private double3 _camera;
    private double _seaRadius;
    private bool _foamOn;
    private bool _foamValid;
    private int _depthCellX;
    private int _depthCellZ;
    private int _depthVersion;
    private int _foamCellX;
    private int _foamCellZ;
    private int _foamVersion = -1;

    // GPU scopes inside the ocean's own work, nested in the caller's (the profiler and --bench show each).
    private readonly GpuTimer _timer;
    private readonly int _surfaceScope;
    private readonly int _surfFoamScope;

    private readonly VkImageView _depthView;
    private readonly VkSampler _depthSampler;
    private readonly VkImageView _depthFarView;
    private readonly VkSampler _depthFarSampler;
    private readonly CoastAtlasGpu _coast;

    /// <param name="depthView">Near signed depth map (ShoreDepthTexture, repeat sampler), in SHADER_READ_ONLY_OPTIMAL.</param>
    /// <param name="depthFarView">Far signed depth map (clamped), in SHADER_READ_ONLY_OPTIMAL.</param>
    /// <param name="coast">The coast regions' atlas textures (ocean_breaker.glsl), in SHADER_READ_ONLY_OPTIMAL once recorded.</param>
    public OceanRenderer(Renderer renderer, OceanFft fft, VkImageView depthView, VkSampler depthSampler, VkImageView depthFarView, VkSampler depthFarSampler, CoastAtlasGpu coast)
    {
        _fft = fft;
        _coast = coast;
        _depthView = depthView;
        _depthSampler = depthSampler;
        _depthFarView = depthFarView;
        _depthFarSampler = depthFarSampler;
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

    /// <summary>std140 starts the wave array on a 16-byte boundary after the header.</summary>
    private static int WavesOffset => (sizeof(FrameHeader) + 15) & ~15;

    private static int FrameBufferSize => WavesOffset + MaxWaves * sizeof(GpuWave);

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

    private const int SampledMaps = 5;

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

        bindings[SurfFoamBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = SurfFoamBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        for (var i = 0; i < CoastMaps; i++)
        {
            bindings[CoastBinding + i] = new VkDescriptorSetLayoutBinding
            {
                Binding = CoastBinding + i,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = stages,
            };
        }

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
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = Renderer.FramesInFlight * (SampledMaps + CoastMaps) },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight },
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
            new() { Sampler = _depthFarSampler, ImageView = _depthFarView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
        };

        var coastMaps = stackalloc VkDescriptorImageInfo[]
        {
            new() { Sampler = _coast.NearestSampler, ImageView = _coast.PhaseView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
            new() { Sampler = _coast.LinearSampler, ImageView = _coast.FieldsView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
            new() { Sampler = _coast.LinearSampler, ImageView = _coast.DirectionView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
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

            var surfFoam = new VkDescriptorBufferInfo { Buffer = _surfFoam.Handle, Range = (ByteSize64)SurfFoamBytes };
            writes[SurfFoamBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = SurfFoamBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &surfFoam,
            };

            for (var m = 0; m < CoastMaps; m++)
            {
                writes[CoastBinding + m] = new VkWriteDescriptorSet
                {
                    DstSet = _sets[i],
                    DstBinding = CoastBinding + m,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = coastMaps + m,
                };
            }

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
                // Blended so thin water at the shore shows the terrain through it (the ocean draws after terrain).
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
    /// Writes this frame's waves, camera and shore wash. Call once per frame before <see cref="RecordCompute"/> and
    /// <see cref="Record"/>, which read them. The GPU works camera-relative in <paramref name="frame"/>: x, z relative to
    /// the camera and y the height above sea level; everything with a world phase (waves per sea tile, carrier, cascade
    /// tiles, depth maps) is rebased in double here.
    /// </summary>
    /// <param name="coast">The coast regions whose breaking faces are drawn (uploaded by <see cref="CoastAtlasGpu.Record"/>); null draws none.</param>
    /// <param name="spectrum">The FFT detail's spectrum about the camera (<see cref="SeaSwells.SpectrumAt"/>).</param>
    public void Prepare(int frameIndex, OceanSurface sea, in SeaFrame frame, double renderTime, in View view, in SpectrumState spectrum, CoastRegionSet? coast = null)
    {
        SeaSlots.Build(sea.Tiles, frame, view.Camera.X, view.Camera.Z, WaveReach, out var slots);
        var block = (byte*)_buffer.Mapped + _frameOffsets[frameIndex];
        var header = (FrameHeader*)block;
        var waves = new Span<GpuWave>(block + WavesOffset, MaxWaves);
        var tallest = 0f;

        for (var s = 0; s < slots.Count; s++)
            tallest = MathF.Max(tallest, sea.Swells.For(slots.Tile[s])?.SignificantWaveHeight ?? 0f);
        var camera = view.Camera;
        var height = (float)camera.Y;
        _camera = camera;
        _seaRadius = frame.SeaRadius;

        // Sea positions are heights above the frame's plane: lower them by the camera's height into camera-relative space.
        header->ViewProj = Matrix4x4.CreateTranslation(0f, -height, 0f) * view.ViewProj;
        ExtractFrustum(header->ViewProj);

        // Vertical cull range: the tallest swell crests and troughs.
        _cullReach = 2f * tallest + 4f;
        var altitude = (float)(camera.Y + frame.Drop(camera.X, camera.Z));
        _firstLevel = FirstLevel(altitude);

        header->CameraPos = new Vector4(0f, height, 0f, (float)(renderTime % TimeWrap));
        header->SunDir = new Vector4(SunDirection, SunIntensity);
        header->SkyZenith = new Vector4(SkyZenith, Glitter(spectrum.Params.WindSpeed));
        header->SkyHorizon = new Vector4(SkyHorizon, FogDensity * FogFade);
        header->WaterDeep = new Vector4(WaterDeep, Opacity);
        header->WaterScatter = new Vector4(WaterScatter, 0f);
        header->CascadeL = _fft.CascadeSizes;
        header->CascadeKLo = OceanFft.BandLow(spectrum.Params.MinWavelength) with { W = _fft.FoamStrength };
        (header->CascadeAnchorX, header->CascadeAnchorZ) = CascadeAnchors(header->CascadeL, view.DetailX, view.DetailZ);
        header->DepthRect = view.DepthNear;
        header->DepthFar = view.DepthFar;
        header->DepthValid = view.DepthValid;
        var primary = sea.Swells.For(slots.Tile[slots.Primary]);
        header->DepthWrap = new Vector4(view.DepthCellX & (SurfFoamResolution - 1), view.DepthCellZ & (SurfFoamResolution - 1), FaceSpacing(altitude), primary?.SignificantWaveHeight ?? 0f);
        _foamOn = view.DepthNear.Z > 0f;
        (_depthCellX, _depthCellZ, _depthVersion) = (view.DepthCellX, view.DepthCellZ, view.DepthVersion);

        // The sea tiles round the camera: each slot's tile's own waves, phased at the camera in its plane, and its plane map.
        header->SlotCount = slots.Count;
        header->BandRow = slots.BandRow;
        header->BandSouth = slots.BandSouth;
        header->BandNorth = slots.BandNorth;
        var count = 0;

        for (var s = 0; s < slots.Count; s++)
        {
            header->SlotWaves[4 * s] = count;

            if (sea.Swells.For(slots.Tile[s]) is { } own)
                count += own.CopyGpuWaves(waves[count..], renderTime, slots.U[s], slots.V[s]);

            header->SlotWaves[4 * s + 1] = count;

            for (var k = 0; k < 4; k++)
            {
                header->SlotSide[4 * s + k] = slots.Side[4 * s + k];
                header->SlotToTile[4 * s + k] = slots.ToTile[4 * s + k];
                header->SlotFromTile[4 * s + k] = slots.FromTile[4 * s + k];
            }
        }

        header->WaveCount = count;

        // Each slot's surf carrier for the shore wash is its tile's; its direction k·(T x) = (Tᵀ k)·x in the frame's plane.
        var breaker = 0f;

        for (var s = 0; s < slots.Count; s++)
        {
            var carrier = default(Vector4);
            var shore = default(Vector4);

            if (sea.Swells.For(slots.Tile[s]) is { } own)
            {
                carrier = own.GpuCarrier(renderTime, slots.U[s], slots.V[s]);
                var open = own.GpuShore(ShoreWash);
                shore = new Vector4(
                    slots.ToTile[4 * s] * open.X + slots.ToTile[4 * s + 2] * open.Y,
                    slots.ToTile[4 * s + 1] * open.X + slots.ToTile[4 * s + 3] * open.Y,
                    open.Z, open.W);
                breaker = MathF.Max(breaker, open.Z);
            }

            ((Vector4*)header->SlotCarrier)[s] = carrier;
            ((Vector4*)header->SlotShore)[s] = shore;
        }

        header->Planet = new Vector4((float)camera.X, (float)camera.Z, breaker, (float)frame.SeaRadius);
        header->DebugView = _fft.DebugView ? 1f : 0f;
        header->CoastCount = BreakerFaces && coast is not null ? PlaceCoast(header->Coast, coast, slots, renderTime) : 0;
        CoastDrawn = header->CoastCount;
        _cullMargin = CullMargin;

        if (CoastDrawn > 0)
        {
            _cullReach += FaceCullHeight * coast!.DrawnHeight;
            _cullMargin += FaceCullMargin;
        }
    }

    /// <summary>A shoaled, held breaker stands at most about this × the carrier's deep-water height over the sea.</summary>
    private const float FaceCullHeight = 3f;

    /// <summary>
    /// Writes the regions drawn this frame (ocean_common.glsl CoastRegion): each placed against the camera through its
    /// tile's slot, with its atlas layer, its map's phase at the render time and its fade. Returns the count.
    /// </summary>
    private int PlaceCoast(float* block, CoastRegionSet coast, in SeaSlots slots, double renderTime)
    {
        var regions = coast.Regions;
        var count = 0;

        for (var slot = 0; slot < regions.Length; slot++)
        {
            ref readonly var region = ref regions[slot];

            if (!region.Drawn || !_coast.Ready(slot, region) || !CoastPlacement.Of(region, slots, out var placement))
                continue;

            var carrier = region.Key.Carrier;
            var to = (Vector4*)(block + CoastFloats * count++);
            to[0] = placement.CellX with { W = placement.Slot };
            to[1] = placement.CellZ with { W = slot };
            var atlas = coast.Atlas(slot);
            to[2] = new Vector4(coast.PhaseAt(slot, renderTime), carrier.Omega, atlas.Height, region.Weight);
        }

        return count;
    }

    /// <summary>The finest level's spacing (m) without <see cref="FirstLevel"/>'s steps: the breaking faces are filtered at it.</summary>
    public static float FaceSpacing(float altitude) => MathF.Max(altitude, 0f) * FinestCell;

    /// <summary>The effective spacing (m) of the grid drawn a horizontal distance from a camera at an altitude: its level's, morphing to the next's (ocean.vert).</summary>
    public static float GridSpacing(float altitude, float distance)
    {
        var level = FirstLevel(altitude);
        var spacing = BaseSpacing * (1 << level);

        while (distance > HalfCells * spacing && level < WaveLevels - 1)
        {
            spacing *= 2f;
            level++;
        }

        var morph = Math.Clamp((distance / (HalfCells * spacing) - MorphStart) / (MorphEnd - MorphStart), 0f, 1f);
        return spacing * (1f + morph);
    }

    /// <summary>The finest level drawn from <paramref name="altitude"/> (m above the sea): cells of about <see cref="FinestCell"/> of it.</summary>
    public static int FirstLevel(float altitude) =>
        Math.Clamp((int)MathF.Floor(MathF.Log2(MathF.Max(altitude * FinestCell / BaseSpacing, 1f))), 0, Levels - 2);

    /// <summary>
    /// What the waves a pixel can't resolve add to the squared roughness of the sun's glint: their slope variance from
    /// the wind (Cox and Munk 1954: 0.003 + 0.00512 U), as GGX's α = sqrt(2 × variance), less water_shading.glsl's
    /// ROUGHNESS² that is already there.
    /// </summary>
    private static float Glitter(float windSpeed) => MathF.Max(0f, MathF.Sqrt(2f * (0.003f + 0.00512f * windSpeed)) - 0.0064f);

    /// <summary>
    /// The ocean's compute work before the water pass: the surf foam left behind
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

        // No near window (from altitude): nothing reads the foam, and what it holds is stale when the window returns.
        if (!_foamOn)
        {
            _foamVersion = -1;
            return;
        }

        // The surf foam map, once per tick, decaying by the time since its last update; at once when the window moved
        // (the cells that came in hold foam from wherever they were before) or shows another sea frame.
        _foamSeconds += MathF.Min(frameSeconds, 0.1f);
        var clear = _foamVersion != _depthVersion;
        var moved = clear || _foamCellX != _depthCellX || _foamCellZ != _depthCellZ;

        if (tickIndex == _foamTick && !moved)
            return;

        Span<float> push = stackalloc float[PushConstantSize / sizeof(float)];
        push[0] = _foamSeconds;
        push[1] = _depthCellX - _foamCellX;
        push[2] = _depthCellZ - _foamCellZ;
        push[3] = clear ? 1f : 0f;
        commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
        commands.BindPipeline(VkPipelineBindPoint.Compute, _surfFoamUpdate);
        _timer.Begin(commands, _surfFoamScope);
        commands.Dispatch(SurfFoamResolution / 16, SurfFoamResolution / 16, 1);
        _timer.End(commands, _surfFoamScope);
        Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit);
        _foamTick = tickIndex;
        _foamSeconds = 0f;
        (_foamCellX, _foamCellZ, _foamVersion) = (_depthCellX, _depthCellZ, _depthVersion);
    }

    /// <summary>
    /// Per cascade, the camera's position in its tile, frac(camera / L), in double: the shader samples a cascade at
    /// x / L + this (x camera-relative), so texture coordinates stay small anywhere in the world. W is the camera modulo
    /// the foam lace's period (<see cref="LacePeriod"/>), wrapped the same way.
    /// </summary>
    private static (Vector4 X, Vector4 Z) CascadeAnchors(Vector4 sizes, double x, double z)
    {
        static float Frac(double v) => (float)(v - Math.Floor(v));
        static float Wrap(double v) => (float)(v - LacePeriod * Math.Floor(v / LacePeriod));

        return (
            new Vector4(Frac(x / sizes.X), Frac(x / sizes.Y), Frac(x / sizes.Z), Wrap(x)),
            new Vector4(Frac(z / sizes.X), Frac(z / sizes.Y), Frac(z / sizes.Z), Wrap(z)));
    }

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess) =>
        commands.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }], [], []);

    /// <summary>
    /// Records the surface, camera-relative from <see cref="Prepare"/>: each level from the finest the camera's height
    /// calls for, tile by tile, culled against the frustum with the sphere's drop. Inside the water pass.
    /// </summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent)
    {
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.BindIndexBuffer(_buffer.Handle, (ByteSize64)0, VkIndexType.UInt16);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        Span<float> push = stackalloc float[PushConstantSize / sizeof(float)];
        var spacing = BaseSpacing * (1 << _firstLevel);
        _timer.Begin(commands, _surfaceScope);

        var drawn = 0;
        var indices = 0;
        var limb = Limb * _seaRadius;

        for (var level = _firstLevel; level < Levels; level++)
        {
            // Snapped in the sea frame in double, then made camera-relative.
            double snap = 2f * spacing;
            var centreX = (float)(Math.Round(_camera.X / snap) * snap - _camera.X);
            var centreZ = (float)(Math.Round(_camera.Z / snap) * snap - _camera.Z);
            var shell = level >= WaveLevels;
            push[0] = centreX;
            push[1] = centreZ;
            push[2] = spacing;
            push[3] = HalfCells;
            push[4] = MorphStart;
            push[5] = MorphEnd;
            push[6] = level;
            push[7] = shell ? 1f : 0f;

            commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
            var variant = level == _firstLevel ? 0 : 1;
            var tileSize = TileQuads * spacing;
            var margin = shell ? 0f : _cullMargin;
            var reach = shell ? 0f : _cullReach;

            for (var tile = 0; tile < Tiles; tile++)
            {
                var count = _tileCount[variant * Tiles + tile];

                if (count == 0)
                    continue;

                var x0 = centreX + (tile % TileSide * TileQuads - HalfCells) * spacing;
                var z0 = centreZ + (tile / TileSide * TileQuads - HalfCells) * spacing;

                // The tile's nearest and farthest points from the frame's origin give its highest and lowest sea.
                var ox = x0 + _camera.X;
                var oz = z0 + _camera.Z;
                var nearX = Math.Max(Math.Max(ox, -(ox + tileSize)), 0.0);
                var nearZ = Math.Max(Math.Max(oz, -(oz + tileSize)), 0.0);
                var farX = Math.Max(Math.Abs(ox), Math.Abs(ox + tileSize));
                var farZ = Math.Max(Math.Abs(oz), Math.Abs(oz + tileSize));
                var near = Math.Sqrt(nearX * nearX + nearZ * nearZ);
                var far = Math.Sqrt(farX * farX + farZ * farZ);

                // Wholly past the limb: every vertex collapses onto it. Partly past it: vertices are pulled inward, out of the box.
                if (near > limb)
                    continue;

                if (far <= limb)
                {
                    var min = new Vector3(x0 - margin, -reach - Drop(far), z0 - margin);
                    var max = new Vector3(x0 + tileSize + margin, reach - Drop(near), z0 + tileSize + margin);

                    if (!Visible(min, max))
                        continue;
                }

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

    /// <summary>ocean.vert LIMB.</summary>
    private const double Limb = 0.999;

    /// <summary>How far the sea sphere lies below the frame's plane at distance r from its origin (ocean_common.glsl seaDrop).</summary>
    private float Drop(double r)
    {
        var r2 = Math.Min(r * r, _seaRadius * _seaRadius);
        return (float)(r2 / (_seaRadius + Math.Sqrt(_seaRadius * _seaRadius - r2)));
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

    public void Dispose()
    {
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipeline(_surfFoamUpdate, null);
        _surfFoam.Dispose();
        _device.DestroyPipelineLayout(_layout, null);

        _buffer.Dispose();

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
