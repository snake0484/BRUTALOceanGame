using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Draws voxel water surface meshes. Each mesh is cached in device-local vertex and index arenas and copied up again
/// only when its <see cref="WaterMesh.Version"/> changes; a mesh not added in a frame is freed. Main thread only. Per
/// frame, outside a render pass: Renderer.BeginFrame, BeginFrame, Add (per mesh), RecordUploads; then Draw (in the
/// water pass, after the ocean).
/// </summary>
public sealed unsafe class VoxelWaterRenderer : IDisposable
{
    public const int VertexStride = 40;
    public const long VertexArenaBytes = 32L << 20;
    public const long IndexArenaBytes = 16L << 20;
    public const int StagingRegionBytes = 8 << 20;
    public const int MaxDraws = 1024;

    /// <summary>Cached meshes: every one drawn this frame, and those dropped at the next <see cref="BeginFrame"/>.</summary>
    private const int MaxEntries = 2 * MaxDraws;

    // Live + pending-free ranges are capped so the arenas' free-block tables can never overflow.
    private const int MaxRanges = 2 * MaxEntries;
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 32;
    private const float CullMargin = 1f;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
    private readonly GpuBuffer _vertexArena;
    private readonly GpuBuffer _indexArena;
    private readonly GpuBuffer _staging;
    private readonly RangeAllocator _vertexRanges;
    private readonly RangeAllocator _indexRanges;
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;

    /// <summary>Where rain and wind can't reach (rain_cover.glsl, binding 1): no rings on water under cover, and the wind's shelter and fetch per vertex.</summary>
    private readonly RainCover _rainCover;

    /// <summary>The water fog the body is marched through (water_fog.glsl, bindings 2 and 3).</summary>
    private readonly WaterFogVolume _fog;

    /// <summary>The opaque scene copied for the water's reflections (water_ssr.glsl, set 1).</summary>
    private readonly ScreenReflections _reflections;

    /// <summary>The wind sea bands (WindSeaBands, binding 4), weighted per vertex by the wind and fetch the water feels.</summary>
    private readonly VkImageView _waveSlopes;
    private readonly VkSampler _waveSampler;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly DrawEntry[] _draws = new DrawEntry[MaxDraws];

    /// <summary>The visible draws, sorted far to near each frame: water is blended, so a near surface drawn before a far one sat behind it.</summary>
    private readonly int[] _order = new int[MaxDraws];
    private readonly float[] _distance = new float[MaxDraws];

    private readonly Dictionary<WaterMesh, int> _cache = new(MaxEntries);
    private readonly Entry[] _entries = new Entry[MaxEntries];
    private readonly int[] _freeEntries = new int[MaxEntries];
    private readonly int[] _live = new int[MaxEntries];
    private readonly PendingFree[] _pendingFrees = new PendingFree[MaxRanges];
    private readonly VkBufferCopy[] _vertexCopies = new VkBufferCopy[MaxDraws];
    private readonly VkBufferCopy[] _indexCopies = new VkBufferCopy[MaxDraws];

    private int _frameIndex = -1;
    private int _drawCount;
    private int _freeEntryCount;
    private int _liveCount;
    private int _pendingHead;
    private int _pendingCount;
    private int _copyCount;
    private int _stagingCursor;
    private long _submission;

    private struct Entry
    {
        public WaterMesh? Mesh;
        public int Version;
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public long UsedAt;
        public int LiveIndex;
    }

    private struct PendingFree
    {
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public long Stamp;
    }

    private struct DrawEntry
    {
        public Vector3 Origin;
        public float Size;
        public int FirstIndex;
        public int VertexOffset;
        public int IndexCount;
        public Vector4 Clip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
        public Vector4 Rain;
        public Vector4 Wind;
        public Vector4 WaterDeep;
        public Vector4 WaterScatter;
    }

    public VoxelWaterRenderer(Renderer renderer, RainCover rainCover, WaterFogVolume fog, ScreenReflections reflections, VkImageView waveSlopes, VkSampler waveSampler)
    {
        _rainCover = rainCover;
        _fog = fog;
        _reflections = reflections;
        _waveSlopes = waveSlopes;
        _waveSampler = waveSampler;

        if (sizeof(WaterVertex) != VertexStride)
            throw new InvalidOperationException($"WaterVertex is {sizeof(WaterVertex)} bytes, expected {VertexStride}.");

        var context = renderer.Context;
        _device = context.Device;
        _pipelines = context.Pipelines;

        var vertexCapacity = (int)(VertexArenaBytes / VertexStride);
        var indexCapacity = (int)(IndexArenaBytes / sizeof(uint));
        _vertexRanges = new RangeAllocator(vertexCapacity, MaxRanges + 1);
        _indexRanges = new RangeAllocator(indexCapacity, MaxRanges + 1);
        _vertexArena = new GpuBuffer(context, vertexCapacity * VertexStride, VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _indexArena = new GpuBuffer(context, indexCapacity * sizeof(uint), VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _staging = new GpuBuffer(context, StagingRegionBytes * Renderer.FramesInFlight, VkBufferUsageFlags.TransferSrcBit, true);
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);

        for (var i = 0; i < MaxEntries; i++)
            _freeEntries[i] = MaxEntries - 1 - i;

        _freeEntryCount = MaxEntries;

        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout, reflections.SetLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass);
    }

    public int MeshesLastFrame { get; private set; }

    public int TrianglesLastFrame { get; private set; }

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    public int CachedMeshes => _liveCount;

    public long VertexBytesUsed => (long)_vertexRanges.Used * VertexStride;

    public long IndexBytesUsed => (long)_indexRanges.Used * sizeof(uint);

    /// <summary>Bytes copied up by the last <see cref="RecordUploads"/>.</summary>
    public int UploadBytesLastFrame { get; private set; }

    /// <summary>
    /// Starts this frame's draw list and drops the meshes the last frame didn't add. Call after Renderer.BeginFrame so the
    /// frame's fence (and with it its staging region) has been waited on.
    /// </summary>
    public void BeginFrame(int frameIndex)
    {
        _frameIndex = frameIndex;
        _drawCount = 0;
        ReleaseDeferred();

        for (var i = _liveCount - 1; i >= 0; i--)
        {
            var e = _live[i];

            if (_entries[e].UsedAt < _submission - 1)
                Drop(e);
        }
    }

    /// <summary>
    /// Draws the mesh this frame, copying it up first if it is new or changed. A mesh that can't be copied this frame (staging
    /// or arena full) keeps its last version, or is left out until it can. Returns false once the draw list is full.
    /// </summary>
    /// <param name="clip">A body's far plane: its centre (world x, z) and radius, outside which nothing is drawn; zero for a block's mesh.</param>
    public bool Add(ChunkCoord coord, WaterMesh mesh, Vector4 clip = default)
    {
        if (_frameIndex < 0 || _drawCount == MaxDraws)
            return false;

        if (mesh.VertexCount == 0 || mesh.IndexCount == 0)
            return true;

        var e = Cached(mesh);

        if (e < 0)
            return true;

        ref var entry = ref _entries[e];
        _draws[_drawCount++] = new DrawEntry
        {
            Origin = coord.WorldOrigin,
            Size = coord.SizeMetres,
            FirstIndex = entry.IndexOffset,
            VertexOffset = entry.VertexOffset,
            IndexCount = entry.IndexCount,
            Clip = clip,
        };
        return true;
    }

    /// <summary>The mesh's entry, staged again if its version changed; -1 if it has none and can't be staged now.</summary>
    private int Cached(WaterMesh mesh)
    {
        if (_cache.TryGetValue(mesh, out var e))
        {
            ref var entry = ref _entries[e];
            entry.UsedAt = _submission;

            if (entry.Version != mesh.Version && Stage(mesh, out var vertexOffset, out var indexOffset))
            {
                FreeRanges(entry);
                entry.Version = mesh.Version;
                entry.VertexOffset = vertexOffset;
                entry.VertexCount = mesh.VertexCount;
                entry.IndexOffset = indexOffset;
                entry.IndexCount = mesh.IndexCount;
            }

            return e;
        }

        if (_freeEntryCount == 0 || !Stage(mesh, out var vertex, out var index))
            return -1;

        e = _freeEntries[--_freeEntryCount];
        _entries[e] = new Entry
        {
            Mesh = mesh,
            Version = mesh.Version,
            VertexOffset = vertex,
            VertexCount = mesh.VertexCount,
            IndexOffset = index,
            IndexCount = mesh.IndexCount,
            UsedAt = _submission,
            LiveIndex = _liveCount,
        };
        _live[_liveCount++] = e;
        _cache.Add(mesh, e);
        return e;
    }

    private bool Stage(WaterMesh mesh, out int vertexOffset, out int indexOffset)
    {
        vertexOffset = 0;
        indexOffset = 0;
        var vertexBytes = mesh.VertexCount * VertexStride;
        var indexBytes = mesh.IndexCount * sizeof(uint);

        if (_copyCount == MaxDraws || _liveCount + _pendingCount >= MaxRanges || _stagingCursor + vertexBytes + indexBytes > StagingRegionBytes)
            return false;

        vertexOffset = _vertexRanges.Allocate(mesh.VertexCount);

        if (vertexOffset < 0)
            return false;

        indexOffset = _indexRanges.Allocate(mesh.IndexCount);

        if (indexOffset < 0)
        {
            _vertexRanges.Release(vertexOffset, mesh.VertexCount);
            return false;
        }

        var src = (long)_frameIndex * StagingRegionBytes + _stagingCursor;
        var dst = (byte*)_staging.Mapped + src;
        MemoryMarshal.AsBytes(mesh.Vertices.AsSpan(0, mesh.VertexCount)).CopyTo(new Span<byte>(dst, vertexBytes));
        MemoryMarshal.AsBytes(mesh.Indices.AsSpan(0, mesh.IndexCount)).CopyTo(new Span<byte>(dst + vertexBytes, indexBytes));

        _vertexCopies[_copyCount] = new VkBufferCopy
        {
            SrcOffset = (ByteSize64)src,
            DstOffset = (ByteSize64)((long)vertexOffset * VertexStride),
            Size = (ByteSize64)(long)vertexBytes,
        };
        _indexCopies[_copyCount] = new VkBufferCopy
        {
            SrcOffset = (ByteSize64)(src + vertexBytes),
            DstOffset = (ByteSize64)((long)indexOffset * sizeof(uint)),
            Size = (ByteSize64)(long)indexBytes,
        };
        _copyCount++;
        _stagingCursor += vertexBytes + indexBytes;
        return true;
    }

    private void Drop(int e)
    {
        ref var entry = ref _entries[e];
        _cache.Remove(entry.Mesh!);
        FreeRanges(entry);
        entry.Mesh = null;

        var last = _live[--_liveCount];
        _live[entry.LiveIndex] = last;
        _entries[last].LiveIndex = entry.LiveIndex;
        _freeEntries[_freeEntryCount++] = e;
    }

    /// <summary>The entry's arena ranges are reused only once in-flight frames can no longer read them.</summary>
    private void FreeRanges(in Entry entry)
    {
        ref var pending = ref _pendingFrees[(_pendingHead + _pendingCount) % MaxRanges];
        pending.VertexOffset = entry.VertexOffset;
        pending.VertexCount = entry.VertexCount;
        pending.IndexOffset = entry.IndexOffset;
        pending.IndexCount = entry.IndexCount;
        pending.Stamp = _submission;
        _pendingCount++;
    }

    // A range freed at stamp S was last read by submission S at the latest; it is idle once submission S + FramesInFlight has begun.
    private void ReleaseDeferred()
    {
        while (_pendingCount > 0)
        {
            ref var pending = ref _pendingFrees[_pendingHead];

            if (_submission < pending.Stamp + Renderer.FramesInFlight)
                break;

            _vertexRanges.Release(pending.VertexOffset, pending.VertexCount);
            _indexRanges.Release(pending.IndexOffset, pending.IndexCount);
            _pendingHead = (_pendingHead + 1) % MaxRanges;
            _pendingCount--;
        }
    }

    /// <summary>Once per frame after the Adds, outside a render pass: copies the staged meshes into the arenas.</summary>
    public void RecordUploads(CommandBuffer commands)
    {
        UploadBytesLastFrame = _stagingCursor;

        if (_copyCount > 0)
        {
            commands.CopyBuffer(_staging.Handle, _vertexArena.Handle, new ReadOnlySpan<VkBufferCopy>(_vertexCopies, 0, _copyCount));
            commands.CopyBuffer(_staging.Handle, _indexArena.Handle, new ReadOnlySpan<VkBufferCopy>(_indexCopies, 0, _copyCount));
            commands.PipelineBarrier(
                VkPipelineStageFlags.TransferBit,
                VkPipelineStageFlags.VertexInputBit,
                VkDependencyFlags.None,
                [new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.VertexAttributeReadBit | VkAccessFlags.IndexReadBit }],
                [],
                []
            );
        }

        _copyCount = 0;
        _stagingCursor = 0;
        _submission++;
    }

    /// <summary>Inside the water pass, after the ocean: frustum-culls and draws this frame's meshes.</summary>
    /// <param name="rain">Rain intensity 0..1 and its looped time (s), as the ocean gets them: rings on the water (rain_ripples.glsl).</param>
    /// <param name="wind">10 m wind (m/s, the way it blows toward): raises the wind sea where the water's shelter and fetch let it.</param>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, Vector3 cameraPos, in SceneLighting lighting, float time, Vector2 rain, Vector2 wind)
    {
        MeshesLastFrame = 0;
        TrianglesLastFrame = 0;

        if (frameIndex != _frameIndex || _drawCount == 0)
            return;

        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->CameraPos = new Vector4(cameraPos, time);
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, lighting.FogDensity);
        uniforms->Rain = new Vector4(rain, 0f, 0f);
        uniforms->Wind = new Vector4(wind, 0f, 0f);
        uniforms->WaterDeep = new Vector4(lighting.WaterDeep, 0f);
        uniforms->WaterScatter = new Vector4(lighting.WaterScatter, 0f);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex], _reflections.Set(frameIndex)], []);
        commands.BindVertexBuffers(0, [_vertexArena.Handle], [(ByteSize64)0]);
        commands.BindIndexBuffer(_indexArena.Handle, (ByteSize64)0, VkIndexType.UInt32);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        var planes = stackalloc Vector4[6];
        ExtractPlanes(viewProj, planes);
        var meshes = 0;
        var indices = 0;
        var push = stackalloc Vector4[2];
        var visible = 0;

        for (var i = 0; i < _drawCount; i++)
        {
            ref var draw = ref _draws[i];

            if (!Visible(planes, draw.Origin, draw.Size))
                continue;

            // Far to near (the key is negated so the ascending sort puts the farthest first).
            _order[visible] = i;
            _distance[visible++] = -Vector3.DistanceSquared(cameraPos, draw.Origin + new Vector3(draw.Size * 0.5f));
        }

        Array.Sort(_distance, _order, 0, visible);

        for (var k = 0; k < visible; k++)
        {
            ref var draw = ref _draws[_order[k]];
            push[0] = new Vector4(draw.Origin, 0f);
            push[1] = draw.Clip;
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(push, PushConstantSize));
            commands.DrawIndexed(draw.IndexCount, 1, draw.FirstIndex, draw.VertexOffset, 0);
            meshes++;
            indices += draw.IndexCount;
        }

        MeshesLastFrame = meshes;
        TrianglesLastFrame = indices / 3;
    }

    // Row-vector convention (clip = v * M); reverse-Z near plane is z <= w.
    private static void ExtractPlanes(in Matrix4x4 m, Vector4* planes)
    {
        var cx = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var cy = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var cz = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var cw = new Vector4(m.M14, m.M24, m.M34, m.M44);

        planes[0] = cw + cx;
        planes[1] = cw - cx;
        planes[2] = cw + cy;
        planes[3] = cw - cy;
        planes[4] = cz;
        planes[5] = cw - cz;
    }

    private static bool Visible(Vector4* planes, Vector3 origin, float size)
    {
        var min = origin - new Vector3(CullMargin);
        var max = origin + new Vector3(size + CullMargin);

        for (var i = 0; i < 6; i++)
        {
            var p = planes[i];
            var x = p.X >= 0f ? max.X : min.X;
            var y = p.Y >= 0f ? max.Y : min.Y;
            var z = p.Z >= 0f ? max.Z : min.Z;

            if (p.X * x + p.Y * y + p.Z * z + p.W < 0f)
                return false;
        }

        return true;
    }

    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new()
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 5, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 * Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 3, PoolSizes = sizes },
            null
        );
    }

    private void CreateSets()
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

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(i * UniformStride),
                Range = (ByteSize64)sizeof(FrameUniforms),
            };
            var cover = new VkDescriptorBufferInfo { Buffer = _rainCover.Buffer(i), Range = (ByteSize64)RainCover.Bytes };
            var fogInfo = new VkDescriptorBufferInfo
            {
                Buffer = _fog.UniformBuffer,
                Offset = (ByteSize64)_fog.UniformOffset(i),
                Range = (ByteSize64)WaterFogVolume.UniformBytes,
            };
            var fogVolume = new VkDescriptorImageInfo { Sampler = _fog.Sampler, ImageView = _fog.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var slopes = new VkDescriptorImageInfo { Sampler = _waveSampler, ImageView = _waveSlopes, ImageLayout = VkImageLayout.General };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &info,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &cover,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 2,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &fogInfo,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 3,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = &fogVolume,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 4,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = &slopes,
                    },
                ],
                []
            );
        }
    }

    // Alpha blended, reverse-Z depth test with writes (a nearer water fragment wins over a farther one, within a mesh and
    // across them: a tower of water once sat behind the lake's far surface), no culling so the surface shows from below too.
    private VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        var vert = ShaderLoader.Load(_device, "voxel_water.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "voxel_water.frag", ShaderKind.FragmentShader);

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

                var binding = new VkVertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = (ByteSize32)VertexStride,
                    InputRate = VkVertexInputRate.Vertex,
                };
                var attributes = stackalloc VkVertexInputAttributeDescription[]
                {
                    new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (ByteSize32)0 },
                    new() { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (ByteSize32)12 },
                    new() { Location = 2, Binding = 0, Format = VkFormat.R32SFloat, Offset = (ByteSize32)24 },
                    new() { Location = 3, Binding = 0, Format = VkFormat.R32G32SFloat, Offset = (ByteSize32)28 },
                    new() { Location = 4, Binding = 0, Format = VkFormat.R32SFloat, Offset = (ByteSize32)36 },
                };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 5,
                    VertexAttributeDescriptions = attributes,
                };
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

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer is disposed.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);

        _uniforms.Dispose();
        _staging.Dispose();
        _indexArena.Dispose();
        _vertexArena.Dispose();
    }
}
