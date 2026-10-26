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

    /// <summary>Draw breaker lip meshes (docs/breaking-waves.md). Off only for A/B comparisons.</summary>
    public bool DrawLips = true;

    /// <summary>The lip as a fold of the ocean mesh on the fine patch (ocean_common.glsl breakerFace, ocean_patch.vert); off: the ribbon (--ribbon-lip). GPU only.</summary>
    public bool FoldLip = true;

    /// <summary>Off: the surface is drawn without breaker faces (A/B comparisons; OceanSurface.FacesEnabled on the CPU).</summary>
    public bool FacesEnabled = true;

    public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.30f, 0.55f));
    public float SunIntensity = 3f;
    public Vector3 SkyZenith = new(0.16f, 0.34f, 0.66f);
    public Vector3 SkyHorizon = new(0.62f, 0.74f, 0.86f);
    public float FogDensity = 0.00012f;
    public Vector3 WaterDeep = new(0.004f, 0.026f, 0.055f);
    public Vector3 WaterScatter = new(0.0f, 0.11f, 0.10f);

    /// <summary>Rain on the surface, 0..1, and its looped time (s), shared with <see cref="RainRenderer"/> (rain_ripples.glsl).</summary>
    public float Rain;
    public float RainTime;

    private readonly Device _device;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly GpuBuffer _buffer;
    private readonly int[] _frameOffsets = new int[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;

    /// <summary>Breaker lips: one instance per crest segment, 2 × profile points − 1 quads each: out along the hook, back underneath (ocean_lip.vert).</summary>
    private readonly VkPipeline _lipPipeline;
    private const int LipProfile = 12;
    private const int LipVertices = (2 * LipProfile - 1) * 6;

    /// <summary>
    /// The fold's fine patch (<see cref="FoldLip"/>, ocean_patch.vert): one instance per crest segment within its reach,
    /// an indexed grid of PatchAlong × PatchAcross quads over the tongue's rest strip, drawn after the clipmap.
    /// </summary>
    private readonly VkPipeline _patchPipeline;
    private const int PatchAlong = 8;
    private const int PatchAcross = 20;
    private const int PatchIndexCount = PatchAlong * PatchAcross * 6;
    private readonly int _patchScope;

    /// <summary>Segments within the patch's reach this frame (breakers.patchList), per frame in flight: the patch's instance count.</summary>
    private readonly int[] _patchCount = new int[Renderer.FramesInFlight];
    private const float PatchDistance = 80f;
    private const float PatchMargin = 8f;

    /// <summary>A segment is listed for the patch only if an end's breaking cap is below this: the lip appears as the curl rises from cap 1 (LIP_MIN_CURL by about 0.98).</summary>
    private const float PatchCap = 0.985f;
    private readonly int _fullIndexCount;
    private readonly int _ringIndexCount;

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
        public Vector4 WindSea;
        public int WaveCount;
        public int RefMin;
        public int RefMax;
        public int CoastSlots;
    }

    private readonly OceanFft _fft;
    // Layer B maps: buffers shared by all frames. A new field is copied into one no frame in flight still reads, a
    // sixteenth per frame, and drawn once complete; until then the GPU keeps drawing the field it replaces.
    private const int CoastalSlots = 3;
    private const int CoastalCopyChunks = 16;
    private readonly GpuBuffer[] _coastal = new GpuBuffer[CoastalSlots];
    private readonly int[] _coastalVersion = new int[CoastalSlots];
    private readonly int[] _coastalCopied = new int[CoastalSlots];
    private readonly long[] _coastalUsed = new long[CoastalSlots];
    private long _frameNumber;
    private const int BreakerBinding = 2 + SampledMaps;
    private const int BreakerHeaderBytes = 48;
    private const int PatchListBytes = BreakerFaces.MaxSegments * sizeof(uint);
    private const int SlotRefBytes = BreakerFaces.MaxSlots * sizeof(uint);
    private const int BreakDistanceBytes = CoastalField.Resolution * CoastalField.Resolution * sizeof(ushort);
    private static int BreakerBytes => BreakerHeaderBytes + BreakerFaces.MaxSegments * sizeof(BreakerSegment) + PatchListBytes + SlotRefBytes + BreakDistanceBytes;
    private readonly GpuBuffer[] _breakers = new GpuBuffer[Renderer.FramesInFlight];
    private readonly double[] _breakerTime = new double[Renderer.FramesInFlight];

    // Spray (ocean_spray_*.comp / ocean_spray.vert): one GPU-only particle ring shared by all frames; frames run in
    // queue order and barriers order each frame's compute after the previous frame's draw.
    private const int SprayBinding = 3 + SampledMaps;
    private const int SprayMax = 16384;
    private const int SprayBytes = 16 + SprayMax * 48;
    private readonly GpuBuffer _spray;
    private readonly VkPipeline _sprayUpdate;
    private readonly VkPipeline _spraySpawn;
    private readonly VkPipeline _sprayPipeline;
    private uint _spraySeed;

    // Surf foam left behind (ocean_surf_foam.comp): one float per cell of the depth map's 512² grid, GPU only, shared by
    // all frames like the spray; cleared when the depth map re-centres.
    private const int SurfFoamBinding = 4 + SampledMaps;
    private const int SurfFoamResolution = 512;
    private const int SurfFoamBytes = SurfFoamResolution * SurfFoamResolution * sizeof(float);
    private readonly GpuBuffer _surfFoam;
    private readonly VkPipeline _surfFoamUpdate;

    // The surf that seeds the foam map moves per tick, so above the tick rate the pass waits for the next tick and decays by the frames it skipped.
    private long _foamTick = -1;
    private float _foamSeconds;

    // Lip bases (ocean_lip_base.comp): the drawn surface at each lip base slot (a crossed crest edge), for the lip mesh and spray spawn.
    private const int LipBaseBinding = 5 + SampledMaps;
    private const int LipBaseBytes = BreakerFaces.MaxSlots * 80;
    private readonly GpuBuffer _lipBases;
    private readonly VkPipeline _lipBaseUpdate;

    // Carrier atlas (ocean_carrier_atlas.comp): per tap, the surf carrier's lag, depth, shoal amplitude and active flag
    // per coastal texel and the lag's central differences, what surfCarrierAt, carrierDirection and carrierDepthAt read
    // instead of the 7-vec4 coastal texels. Shared by all frames; rebuilt when a tap's field or the carrier's wavenumber
    // or reference changes.
    private const int CarrierAtlasBinding = 6 + SampledMaps;
    private const int CarrierAtlasLayers = 4;

    /// <summary>Where rain can't reach (rain_cover.glsl): no rings under cover, and the rain pipelines read it through this set.</summary>
    private const int RainCoverBinding = 7 + SampledMaps;

    // Splashes (ocean_spray_splash.comp): where sea-zone water slammed into a wall or landed from a pour as of the last
    // tick, one host-visible buffer per frame in flight. They throw spray into the same ring as the breakers.
    private const int SplashBinding = 8 + SampledMaps;
    private const int MaxSplashes = 512;
    private static int SplashBytes => 16 + MaxSplashes * sizeof(WaterSplash);
    private readonly GpuBuffer[] _splashes = new GpuBuffer[Renderer.FramesInFlight];
    private readonly int[] _splashCount = new int[Renderer.FramesInFlight];
    private readonly VkPipeline _splashSpray;

    // Zone flow (ocean_common.glsl zoneFlowAt): the sea-zone sim's velocity and turbulence on its own 1 m cells, one
    // host-visible buffer per frame in flight. The surface's foam and ripples stream with it and roughen by it. A sim
    // bigger than MaxFlowCells goes up empty (the zone's cap is 12,000 cells, but edit discs alone can pass it).
    private const int ZoneFlowBinding = 9 + SampledMaps;
    private const int MaxFlowCells = 32768;
    private const int ZoneFlowBytes = 32 + MaxFlowCells * 8;
    private readonly GpuBuffer[] _zoneFlow = new GpuBuffer[Renderer.FramesInFlight];
    private const int Bindings = 10 + SampledMaps;
    private readonly RainCover _rainCover;
    private readonly VmaAllocator _allocator;
    private readonly VkImage _carrierAtlas;
    private readonly VmaAllocation _carrierAtlasAllocation;
    private readonly VkImageView _carrierAtlasView;
    private readonly VkPipeline _carrierAtlasUpdate;
    private readonly (int Version, float K0, float Ref)[] _atlasKey = new (int, float, float)[2];
    private readonly int[] _tapVersion = [-1, -1];
    private int _atlasRebuild;
    private bool _atlasReady;
    private Vector4 _depthRect;
    private Vector4 _surfFoamRect;

    // GPU scopes inside the ocean's own work, nested in the caller's (the profiler and --bench show each).
    private readonly GpuTimer _timer;
    private readonly int _surfaceScope;
    private readonly int _lipScope;
    private readonly int _sprayDrawScope;
    private readonly int _surfFoamScope;
    private readonly int _lipBaseScope;
    private readonly int _spawnScope;
    private readonly int _splashSprayScope;
    private readonly int _atlasScope;
    private const int CoastalBytes = CoastalField.Resolution * CoastalField.Resolution * CoastalField.TexelFloats * sizeof(float);

    private readonly VkImageView _depthView;
    private readonly VkSampler _depthSampler;

    /// <param name="depthView">Depth and sea-zone map, two array layers (latest tick, tick before), in SHADER_READ_ONLY_OPTIMAL.</param>
    public OceanRenderer(Renderer renderer, OceanFft fft, VkImageView depthView, VkSampler depthSampler, RainCover rainCover)
    {
        _rainCover = rainCover;
        _fft = fft;
        _depthView = depthView;
        _depthSampler = depthSampler;
        _device = renderer.Context.Device;

        // One allocation: clipmap and patch indices, then one uniform block per frame in flight.
        CountIndices(out _fullIndexCount, out _ringIndexCount);
        var offset = Align((_fullIndexCount + _ringIndexCount + PatchIndexCount) * sizeof(ushort));
        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            _frameOffsets[i] = offset;
            offset = Align(offset + FrameBufferSize);
        }

        _buffer = new GpuBuffer(renderer.Context, offset, VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.UniformBufferBit, true);

        for (var s = 0; s < CoastalSlots; s++)
        {
            _coastal[s] = new GpuBuffer(renderer.Context, CoastalBytes, VkBufferUsageFlags.StorageBufferBit, true);
            _coastalUsed[s] = -Renderer.FramesInFlight - 1;
        }

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            _breakers[i] = new GpuBuffer(renderer.Context, BreakerBytes, VkBufferUsageFlags.StorageBufferBit, true);
            _breakerTime[i] = double.NaN;
            _splashes[i] = new GpuBuffer(renderer.Context, SplashBytes, VkBufferUsageFlags.StorageBufferBit, true);
            _zoneFlow[i] = new GpuBuffer(renderer.Context, ZoneFlowBytes, VkBufferUsageFlags.StorageBufferBit, true);
            _zoneFlow[i].AsSpan<byte>()[..32].Clear();
        }

        _spray = new GpuBuffer(renderer.Context, SprayBytes, VkBufferUsageFlags.StorageBufferBit, true);
        _spray.AsSpan<byte>().Clear();
        _surfFoam = new GpuBuffer(renderer.Context, SurfFoamBytes, VkBufferUsageFlags.StorageBufferBit, true);
        _surfFoam.AsSpan<byte>().Clear();
        _lipBases = new GpuBuffer(renderer.Context, LipBaseBytes, VkBufferUsageFlags.StorageBufferBit, true);
        _allocator = renderer.Context.Allocator;
        _carrierAtlasView = CreateCarrierAtlas(out _carrierAtlas, out _carrierAtlasAllocation);
        _atlasKey[0] = _atlasKey[1] = (-1, 0f, 0f);

        WriteIndices(_buffer.AsSpan<ushort>(), _tileFirst, _tileCount);
        WritePatchIndices(_buffer.AsSpan<ushort>().Slice(_fullIndexCount + _ringIndexCount, PatchIndexCount));
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateFrameResources();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = PushStages, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass, "ocean.vert", "ocean.frag");
        _lipPipeline = CreatePipeline(renderer.RenderPass, "ocean_lip.vert", "ocean_lip.frag");
        _patchPipeline = CreatePipeline(renderer.RenderPass, "ocean_patch.vert", "ocean.frag");
        _sprayPipeline = CreatePipeline(renderer.RenderPass, "ocean_spray.vert", "ocean_spray.frag", depthWrite: false);
        _sprayUpdate = CreateComputePipeline("ocean_spray_update.comp");
        _spraySpawn = CreateComputePipeline("ocean_spray_spawn.comp");
        _splashSpray = CreateComputePipeline("ocean_spray_splash.comp");
        _surfFoamUpdate = CreateComputePipeline("ocean_surf_foam.comp");
        _lipBaseUpdate = CreateComputePipeline("ocean_lip_base.comp");
        _carrierAtlasUpdate = CreateComputePipeline("ocean_carrier_atlas.comp");

        _timer = renderer.GpuTimer;
        _surfaceScope = _timer.Register("Ocean surface");
        _lipScope = _timer.Register("Ocean lips");
        _patchScope = _timer.Register("Ocean patch");
        _sprayDrawScope = _timer.Register("Spray draw");
        _surfFoamScope = _timer.Register("Surf foam");
        _lipBaseScope = _timer.Register("Lip bases");
        _spawnScope = _timer.Register("Spray spawn");
        _splashSprayScope = _timer.Register("Splash spray");
        _atlasScope = _timer.Register("Carrier atlas");
    }

    private VkImageView CreateCarrierAtlas(out VkImage image, out VmaAllocation allocation)
    {
        var result = _allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = VkFormat.R32G32B32A32SFloat,
                Extent = new VkExtent3D { Width = CoastalField.Resolution, Height = CoastalField.Resolution, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = CarrierAtlasLayers,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.StorageBit,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out image,
            out allocation,
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        return _device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2DArray,
                Format = VkFormat.R32G32B32A32SFloat,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = CarrierAtlasLayers },
            },
            null
        );
    }

    private void TransitionAtlasToGeneral(CommandBuffer commands)
    {
        Span<VkImageMemoryBarrier> barriers =
        [
            new VkImageMemoryBarrier
            {
                DstAccessMask = VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit,
                OldLayout = VkImageLayout.Undefined,
                NewLayout = VkImageLayout.General,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = _carrierAtlas,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = CarrierAtlasLayers },
            },
        ];

        commands.PipelineBarrier(VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit, VkDependencyFlags.None, [], [], barriers);
    }

    /// <summary>Bit tap when that tap's atlas must be rebuilt this frame: its field (version −1 = none) or the carrier's wavenumber or reference changed.</summary>
    private int AtlasKey(int tap, int version, Vector4 carrier)
    {
        var key = (version, carrier.X, carrier.Y);
        if (_atlasKey[tap] == key)
            return 0;

        _atlasKey[tap] = key;
        return version < 0 ? 0 : 1 << tap;
    }

    private const VkShaderStageFlags PushStages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit;

    public Vector3 SkyHorizonColor => SkyHorizon;

    /// <summary>Set 0 of pipelines that read the ocean's frame and maps (<see cref="RainRenderer"/>).</summary>
    internal VkDescriptorSetLayout SetLayout => _setLayout;

    internal VkDescriptorSet Set(int frameIndex) => _sets[frameIndex];

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

    /// <summary>The patch grid: (PatchAlong + 1) × (PatchAcross + 1) vertices, row-major across the strip, as ocean_patch.vert numbers them.</summary>
    private static void WritePatchIndices(Span<ushort> indices)
    {
        const int side = PatchAlong + 1;
        var w = 0;

        for (var j = 0; j < PatchAcross; j++)
        {
            for (var i = 0; i < PatchAlong; i++)
            {
                var v00 = (ushort)(j * side + i);
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

        bindings[BreakerBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = BreakerBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        bindings[SprayBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = SprayBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers | VkShaderStageFlags.FragmentBit,
        };

        bindings[SurfFoamBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = SurfFoamBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        bindings[LipBaseBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = LipBaseBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        bindings[CarrierAtlasBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = CarrierAtlasBinding,
            DescriptorType = VkDescriptorType.StorageImage,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        bindings[RainCoverBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = RainCoverBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = stages,
        };

        bindings[SplashBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = SplashBinding,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = buffers,
        };

        bindings[ZoneFlowBinding] = new VkDescriptorSetLayoutBinding
        {
            Binding = ZoneFlowBinding,
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
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight * (CoastalSlots + 7) },
            new() { Type = VkDescriptorType.StorageImage, DescriptorCount = Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 4, PoolSizes = sizes },
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
        var atlas = new VkDescriptorImageInfo { ImageView = _carrierAtlasView, ImageLayout = VkImageLayout.General };

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

            var breakers = new VkDescriptorBufferInfo { Buffer = _breakers[i].Handle, Range = (ByteSize64)BreakerBytes };
            writes[BreakerBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = BreakerBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &breakers,
            };

            var spray = new VkDescriptorBufferInfo { Buffer = _spray.Handle, Range = (ByteSize64)SprayBytes };
            writes[SprayBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = SprayBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &spray,
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

            var lipBases = new VkDescriptorBufferInfo { Buffer = _lipBases.Handle, Range = (ByteSize64)LipBaseBytes };
            writes[LipBaseBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = LipBaseBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &lipBases,
            };

            writes[CarrierAtlasBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = CarrierAtlasBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageImage,
                ImageInfo = &atlas,
            };

            var cover = new VkDescriptorBufferInfo { Buffer = _rainCover.Buffer(i), Range = (ByteSize64)RainCover.Bytes };
            writes[RainCoverBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = RainCoverBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &cover,
            };

            var splashes = new VkDescriptorBufferInfo { Buffer = _splashes[i].Handle, Range = (ByteSize64)SplashBytes };
            writes[SplashBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = SplashBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &splashes,
            };

            var zoneFlow = new VkDescriptorBufferInfo { Buffer = _zoneFlow[i].Handle, Range = (ByteSize64)ZoneFlowBytes };
            writes[ZoneFlowBinding] = new VkWriteDescriptorSet
            {
                DstSet = _sets[i],
                DstBinding = ZoneFlowBinding,
                DescriptorCount = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                BufferInfo = &zoneFlow,
            };

            _device.UpdateDescriptorSets(writes, []);
        }
    }

    private VkPipeline CreatePipeline(VkRenderPass renderPass, string vertex, string fragment, bool depthWrite = true)
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
                    DepthWriteEnable = depthWrite,
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

                return _device.CreateGraphicsPipeline(
                    default,
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
                    },
                    null
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
    /// Writes this frame's waves, camera, coastal maps, breaker segments, splashes and zone flow. Call once per frame
    /// before <see cref="RecordSpray"/> and <see cref="Record"/>, which read them.
    /// </summary>
    /// <param name="tickAlpha">Fraction of a sim tick since the last one: blends the depth map's sea-zone layers.</param>
    /// <param name="depthRect">(origin x, origin z, world size, depth outside the map).</param>
    /// <param name="splashes">Where sea-zone water slams into a wall or lands from a pour (ZoneFlowSim); spray is thrown there at a rate, so the same tick's list may be passed for several frames.</param>
    /// <param name="flowRect">The zone sim's grid: (corner x, corner z, cell size, cells across).</param>
    /// <param name="flow">Per zone sim cell, half (velocity x, z) and half (turbulence, 0), row by row.</param>
    public void Prepare(int frameIndex, OceanSwell swell, BreakerFaces faces, double renderTime, float tickAlpha, in Matrix4x4 viewProj, Vector3 cameraPos, Vector4 depthRect,
        ReadOnlySpan<WaterSplash> splashes, Vector4 flowRect, ReadOnlySpan<uint> flow)
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
        header->FoamParams = new Vector4(Rain, RainTime, _fft.DebugView ? 1f : 0f, tickAlpha);
        header->DepthRect = depthRect;
        _depthRect = depthRect;
        WriteCoastal(header, frameIndex, swell);
        (header->Carrier, header->CarrierChop) = swell.GpuCarrier(renderTime);
        header->WindSea = new Vector4(swell.GpuSpectrum.Params.WindSpeed, 0f, 0f, 0f);
        _atlasRebuild = AtlasKey(0, _tapVersion[0], header->Carrier) | AtlasKey(1, _tapVersion[1], header->Carrier);
        WriteBreakers(frameIndex, faces, renderTime, cameraPos);
        header->WaveCount = swell.CopyGpuWaves(waves, renderTime);

        var count = Math.Min(splashes.Length, MaxSplashes);
        var splashBlock = (byte*)_splashes[frameIndex].Mapped;
        *(uint*)splashBlock = (uint)count;
        splashes[..count].CopyTo(new Span<WaterSplash>(splashBlock + 16, MaxSplashes));
        _splashCount[frameIndex] = count;
        WriteZoneFlow(frameIndex, flowRect, flow);
    }

    /// <summary>The zone flow header, (corner, cell size, 0) then (cells across, cells down, 0, 0), and its cells; none when it doesn't fit.</summary>
    private void WriteZoneFlow(int frameIndex, Vector4 rect, ReadOnlySpan<uint> flow)
    {
        var block = (byte*)_zoneFlow[frameIndex].Mapped;
        var cells = flow.Length / 2;
        var across = (int)rect.W;
        var fits = across > 0 && cells <= MaxFlowCells;
        *(Vector4*)block = rect with { W = 0f };
        ((int*)(block + 16))[0] = fits ? across : 0;
        ((int*)(block + 16))[1] = fits ? cells / across : 0;

        if (fits)
            flow.CopyTo(new Span<uint>(block + 32, 2 * MaxFlowCells));
    }

    /// <summary>
    /// Spray (docs/breaking-waves.md, rendering step 6): advances the particle ring, then spawns from breaker lips. Also
    /// updates the surf foam left behind (ocean_surf_foam.comp). Outside a render pass, after <see cref="Prepare"/>.
    /// </summary>
    public void RecordSpray(CommandBuffer commands, int frameIndex, int segments, int slots, float frameSeconds, long tickIndex)
    {
        Span<float> push = stackalloc float[PushConstantSize / sizeof(float)];
        push[0] = MathF.Min(frameSeconds, 0.1f);
        push[1] = BitConverter.Int32BitsToSingle((int)(++_spraySeed * 2654435761u));
        push[2] = segments;

        // The previous frame's draw read the ring; this frame's compute rewrites it.
        Barrier(commands, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);
        commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_sets[frameIndex]], []);

        if (!_atlasReady)
        {
            TransitionAtlasToGeneral(commands);
            _atlasReady = true;
        }

        // The carrier atlas, for the taps whose field or carrier changed (Prepare), before anything this frame reads it.
        if (_atlasRebuild != 0)
        {
            Span<float> tap = stackalloc float[PushConstantSize / sizeof(float)];
            commands.BindPipeline(VkPipelineBindPoint.Compute, _carrierAtlasUpdate);
            _timer.Begin(commands, _atlasScope);

            for (var t = 0; t < 2; t++)
            {
                if ((_atlasRebuild & 1 << t) == 0)
                    continue;

                tap[0] = t;
                commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(tap));
                commands.Dispatch(CoastalField.Resolution / 16, CoastalField.Resolution / 16, 1);
            }

            _timer.End(commands, _atlasScope);
            Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.ComputeShaderBit | VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit);
            _atlasRebuild = 0;
        }

        commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
        commands.BindPipeline(VkPipelineBindPoint.Compute, _sprayUpdate);
        commands.Dispatch((SprayMax + 255) / 256, 1, 1);

        // The surf foam map, once per tick (or at once when the depth map it lives on re-centres, since the old foam is
        // then in the wrong place), decaying by the time since its last update.
        _foamSeconds += push[0];
        if (tickIndex != _foamTick || _depthRect != _surfFoamRect)
        {
            Span<float> foam = stackalloc float[PushConstantSize / sizeof(float)];
            foam[0] = _foamSeconds;
            foam[3] = _depthRect == _surfFoamRect ? 0f : 1f;
            commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(foam));
            commands.BindPipeline(VkPipelineBindPoint.Compute, _surfFoamUpdate);
            _timer.Begin(commands, _surfFoamScope);
            commands.Dispatch(SurfFoamResolution / 16, SurfFoamResolution / 16, 1);
            _timer.End(commands, _surfFoamScope);
            commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
            _foamTick = tickIndex;
            _foamSeconds = 0f;
            _surfFoamRect = _depthRect;
        }

        if (slots > 0)
        {
            commands.BindPipeline(VkPipelineBindPoint.Compute, _lipBaseUpdate);
            _timer.Begin(commands, _lipBaseScope);
            commands.Dispatch(slots, 1, 1);
            _timer.End(commands, _lipBaseScope);
        }

        Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit);

        if (segments > 0)
        {
            commands.BindPipeline(VkPipelineBindPoint.Compute, _spraySpawn);
            _timer.Begin(commands, _spawnScope);
            commands.Dispatch((segments + 63) / 64, 1, 1);
            _timer.End(commands, _spawnScope);
        }

        // Splash spray shares the ring: slots come from the same atomic head, so it needs no barrier after the breakers'.
        var splashes = _splashCount[frameIndex];
        if (splashes > 0)
        {
            push[2] = splashes;
            commands.PushConstants(_layout, PushStages, 0, MemoryMarshal.AsBytes(push));
            commands.BindPipeline(VkPipelineBindPoint.Compute, _splashSpray);
            _timer.Begin(commands, _splashSprayScope);
            commands.Dispatch((splashes + 63) / 64, 1, 1);
            _timer.End(commands, _splashSprayScope);
        }

        Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit);
    }

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess) =>
        commands.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }], [], []);

    /// <summary>Records the surface (one draw per clipmap level), breaker lips and spray. Inside the main pass.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, Vector3 cameraPos, int segments)
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
            }

            spacing *= 2f;
        }

        TilesDrawn = drawn;

        _timer.End(commands, _surfaceScope);

        // The fold's fine patch over the tongue strips the clipmap collapsed; segments out of reach cull themselves.
        if (FoldLip && _patchCount[frameIndex] > 0)
        {
            _timer.Begin(commands, _patchScope);
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _patchPipeline);
            commands.DrawIndexed(PatchIndexCount, _patchCount[frameIndex], _fullIndexCount + _ringIndexCount, 0, 0);
            _timer.End(commands, _patchScope);
        }

        // Breaker lips on top of the surface; segments without curl cull themselves in the vertex shader.
        if (DrawLips && segments > 0)
        {
            _timer.Begin(commands, _lipScope);
            commands.BindPipeline(VkPipelineBindPoint.Graphics, _lipPipeline);
            commands.Draw(LipVertices, segments, 0, 0);
            _timer.End(commands, _lipScope);
        }

        // Spray last: soft, depth-tested but not depth-written; dead slots cull themselves.
        _timer.Begin(commands, _sprayDrawScope);
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _sprayPipeline);
        commands.Draw(6, SprayMax, 0, 0);
        _timer.End(commands, _sprayDrawScope);
    }

    private VkPipeline CreateComputePipeline(string file)
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
                            Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = name },
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

        _tapVersion[0] = -1;
        _tapVersion[1] = -1;

        if (field is null)
        {
            header->CoastRect = Vector4.Zero;
            header->CoastPrevRect = Vector4.Zero;
            return;
        }

        var previous = swell.CoastalPrevious;
        var slot = Slot(field);
        var prevSlot = previous is null ? -1 : Slot(previous);
        var fade = swell.CoastalFade;

        if (slot < 0)
        {
            if (prevSlot < 0)
            {
                header->CoastRect = Vector4.Zero;
                header->CoastPrevRect = Vector4.Zero;
                return;
            }

            (field, slot, previous, prevSlot, fade) = (previous!, prevSlot, null, -1, 1f);
        }

        Use(slot);
        Use(prevSlot);
        header->CoastRect = new Vector4(field.Origin.X, field.Origin.Y, CoastalField.CellSize, CoastalField.Resolution);
        header->CoastPrevRect = prevSlot < 0 ? Vector4.Zero : new Vector4(previous!.Origin.X, previous.Origin.Y, CoastalField.CellSize, CoastalField.Resolution);
        var (dirZ, dirX) = MathF.SinCos(field.Direction);
        header->CoastDir = new Vector4(dirX, dirZ, prevSlot < 0 && previous is not null ? 1f : fade, prevSlot < 0 ? field.Direction : previous!.Direction);
        header->CoastSlots = slot | Math.Max(prevSlot, 0) << 2;
        _tapVersion[0] = field.Version;
        _tapVersion[1] = prevSlot < 0 ? -1 : previous!.Version;
    }

    private void Use(int slot)
    {
        if (slot >= 0)
            _coastalUsed[slot] = _frameNumber;
    }

    /// <summary>
    /// Breaker crest segments the lips are swept along, their lip base slots and the break distances
    /// (docs/breaking-waves.md): copied when a tick produced new ones; the header carries the render-time offset every
    /// frame so crests advance smoothly between ticks, and whether the face is drawn. Layout: (break grid origin x, z,
    /// cell size, render − tick seconds), (segments, break grid resolution, fold, faces on), (lip base slots, 0, 0, 0),
    /// segments, patch list, slot refs, break distances (half floats).
    /// </summary>
    private void WriteBreakers(int frameIndex, BreakerFaces faces, double renderTime, Vector3 cameraPos)
    {
        var data = (byte*)_breakers[frameIndex].Mapped;
        var breaking = faces.Breaking;
        var hasBreaks = breaking is not null && !float.IsNaN(breaking.BreakOrigin.X);
        *(Vector4*)data = new Vector4(hasBreaks ? breaking!.BreakOrigin : Vector2.Zero, CoastalField.CellSize, (float)(renderTime - faces.Time));
        var info = (int*)(data + 16);
        info[0] = faces.SegmentCount;
        info[1] = hasBreaks ? CoastalField.Resolution : 0;
        info[2] = FoldLip ? 1 : 0;
        info[3] = FacesEnabled ? 1 : 0;
        info[4] = faces.SlotCount;

        if (_breakerTime[frameIndex] == faces.Time)
            return;

        _breakerTime[frameIndex] = faces.Time;
        var segments = (BreakerSegment*)(data + BreakerHeaderBytes);
        var list = (uint*)(segments + BreakerFaces.MaxSegments);
        var slots = list + BreakerFaces.MaxSegments;
        var distances = (ushort*)(slots + BreakerFaces.MaxSlots);
        faces.Segments.AsSpan(0, faces.SegmentCount).CopyTo(new Span<BreakerSegment>(segments, BreakerFaces.MaxSegments));
        faces.SlotRefs.AsSpan(0, faces.SlotCount).CopyTo(new Span<uint>(slots, BreakerFaces.MaxSlots));

        // The fine patch's instances: the segments within its reach, so culled ones never issue.
        var count = 0;
        var reach = (PatchDistance + PatchMargin) * (PatchDistance + PatchMargin);
        if (FoldLip)
        {
            for (var i = 0; i < faces.SegmentCount; i++)
            {
                ref readonly var segment = ref faces.Segments[i];
                var dx = 0.5f * (segment.Ends.X + segment.Ends.Z) - cameraPos.X;
                var dz = 0.5f * (segment.Ends.Y + segment.Ends.W) - cameraPos.Z;
                if (dx * dx + dz * dz <= reach && MathF.Min(segment.Slots.Z, segment.Slots.W) < PatchCap)
                    list[count++] = (uint)i;
            }
        }

        _patchCount[frameIndex] = count;
        if (hasBreaks)
            breaking!.BreakDistanceBits.CopyTo(new Span<ushort>(distances, CoastalField.Resolution * CoastalField.Resolution));
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

        if (_coastalCopied[slot] < total)
        {
            var chunk = Math.Min((total + CoastalCopyChunks - 1) / CoastalCopyChunks, total - _coastalCopied[slot]);
            field.Texels.AsSpan(_coastalCopied[slot], chunk).CopyTo(_coastal[slot].AsSpan<float>().Slice(_coastalCopied[slot]));
            _coastalCopied[slot] += chunk;
            return -1;
        }

        return slot;
    }

    public void Dispose()
    {
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipeline(_lipPipeline, null);
        _device.DestroyPipeline(_patchPipeline, null);
        _device.DestroyPipeline(_sprayPipeline, null);
        _device.DestroyPipeline(_sprayUpdate, null);
        _device.DestroyPipeline(_spraySpawn, null);
        _device.DestroyPipeline(_splashSpray, null);
        _device.DestroyPipeline(_surfFoamUpdate, null);
        _device.DestroyPipeline(_lipBaseUpdate, null);
        _device.DestroyPipeline(_carrierAtlasUpdate, null);
        _device.DestroyImageView(_carrierAtlasView, null);
        _allocator.DestroyImage(_carrierAtlas, _carrierAtlasAllocation);
        _spray.Dispose();
        _surfFoam.Dispose();
        _lipBases.Dispose();
        _device.DestroyPipelineLayout(_layout, null);

        _buffer.Dispose();

        foreach (var buffer in _coastal)
            buffer.Dispose();

        foreach (var buffer in _breakers)
            buffer.Dispose();

        foreach (var buffer in _splashes)
            buffer.Dispose();

        foreach (var buffer in _zoneFlow)
            buffer.Dispose();

        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
    }
}
