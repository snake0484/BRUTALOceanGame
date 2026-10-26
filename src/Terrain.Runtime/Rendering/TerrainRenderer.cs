using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Terrain;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Draws terrain chunks from one device-local vertex arena and one index arena.
/// Uploads go through a host-visible staging ring and are copied before the main pass.
/// Main thread only. Per frame: Upload/Free (any time), RecordUploads (after BeginFrame, before the pass), Draw (in the pass).
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    public const int MaxHandles = 8192;
    public const int VertexStride = 28;
    public const long VertexArenaBytes = 96L << 20;
    public const long IndexArenaBytes = 96L << 20;
    public const int StagingRegionBytes = 16 << 20;
    public const int MaxUploadsPerFrame = 1024;

    // Live + pending-free ranges are capped so the arenas' free-block tables can never overflow.
    private const int MaxRanges = 2 * MaxHandles;

    // One more region than frames in flight: Upload may run before BeginFrame waits on the oldest fence.
    private const int StagingRegions = Renderer.FramesInFlight + 1;
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 16;
    // Surface Nets apron cells and skirts put vertices up to 2 voxels outside the chunk cube.
    private const float CullMarginVoxels = 2f;

    private readonly Device _device;
    private readonly GpuBuffer _vertexArena;
    private readonly GpuBuffer _indexArena;
    private readonly GpuBuffer _staging;
    private readonly GpuBuffer _uniforms;
    private readonly RangeAllocator _vertexRanges;
    private readonly RangeAllocator _indexRanges;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;

    private readonly Slot[] _slots = new Slot[MaxHandles];
    private readonly int[] _freeHandles = new int[MaxHandles];
    private readonly int[] _live = new int[MaxHandles];
    private readonly PendingFree[] _pendingFrees = new PendingFree[MaxRanges];
    private readonly VkBufferCopy[] _vertexCopies = new VkBufferCopy[MaxUploadsPerFrame];
    private readonly VkBufferCopy[] _indexCopies = new VkBufferCopy[MaxUploadsPerFrame];

    private int _freeHandleCount;
    private int _liveCount;
    private int _pendingHead;
    private int _pendingCount;
    private int _copyCount;
    private int _stagingRegion;
    private int _stagingCursor;
    private long _submission;

    private struct Slot
    {
        public Vector3 Origin;
        public float VoxelScale;
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public long CopiedAt;
        public int LiveIndex;
        public bool Used;
    }

    private struct PendingFree
    {
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public long Stamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
    }

    public TerrainRenderer(Renderer renderer)
    {
        if (sizeof(TerrainVertex) != VertexStride)
            throw new InvalidOperationException($"TerrainVertex is {sizeof(TerrainVertex)} bytes, expected {VertexStride}.");

        var context = renderer.Context;
        _device = context.Device;

        var vertexCapacity = (int)(VertexArenaBytes / VertexStride);
        var indexCapacity = (int)(IndexArenaBytes / sizeof(uint));
        _vertexRanges = new RangeAllocator(vertexCapacity, MaxRanges + 1);
        _indexRanges = new RangeAllocator(indexCapacity, MaxRanges + 1);

        _vertexArena = new GpuBuffer(context, vertexCapacity * VertexStride, VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _indexArena = new GpuBuffer(context, indexCapacity * sizeof(uint), VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _staging = new GpuBuffer(context, StagingRegionBytes * StagingRegions, VkBufferUsageFlags.TransferSrcBit, true);
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);

        for (var i = 0; i < MaxHandles; i++)
            _freeHandles[i] = MaxHandles - 1 - i;

        _freeHandleCount = MaxHandles;

        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass);
    }

    public int ChunkCount => _liveCount;

    public int DrawnLastFrame { get; private set; }

    public long VertexBytesUsed => (long)_vertexRanges.Used * VertexStride;

    public long IndexBytesUsed => (long)_indexRanges.Used * sizeof(uint);

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>
    /// Copies the mesh into staging and reserves arena space. Returns a handle, or -1 if out of staging, arena or handle space (retry next frame).
    /// </summary>
    public int Upload(ChunkCoord coord, ChunkMesh mesh)
    {
        if (_freeHandleCount == 0 || _liveCount + _pendingCount >= MaxRanges)
            return -1;

        var vertexCount = mesh.VertexCount;
        var indexCount = mesh.IndexCount;
        var empty = vertexCount == 0 || indexCount == 0;
        int vertexOffset = 0, indexOffset = 0;

        if (empty)
        {
            vertexCount = 0;
            indexCount = 0;
        }
        else if (!Stage(mesh, vertexCount, indexCount, out vertexOffset, out indexOffset))
        {
            return -1;
        }

        var handle = _freeHandles[--_freeHandleCount];
        ref var slot = ref _slots[handle];
        slot.Origin = coord.WorldOrigin;
        slot.VoxelScale = 1 << coord.Lod;
        slot.VertexOffset = vertexOffset;
        slot.VertexCount = vertexCount;
        slot.IndexOffset = indexOffset;
        slot.IndexCount = indexCount;
        slot.CopiedAt = _submission;
        slot.LiveIndex = _liveCount;
        slot.Used = true;
        _live[_liveCount++] = handle;
        return handle;
    }

    private bool Stage(ChunkMesh mesh, int vertexCount, int indexCount, out int vertexOffset, out int indexOffset)
    {
        vertexOffset = 0;
        indexOffset = 0;
        var vertexBytes = vertexCount * VertexStride;
        var indexBytes = indexCount * sizeof(uint);

        if (_copyCount == MaxUploadsPerFrame || _stagingCursor + vertexBytes + indexBytes > StagingRegionBytes)
            return false;

        vertexOffset = _vertexRanges.Allocate(vertexCount);

        if (vertexOffset < 0)
            return false;

        indexOffset = _indexRanges.Allocate(indexCount);

        if (indexOffset < 0)
        {
            _vertexRanges.Release(vertexOffset, vertexCount);
            return false;
        }

        var regionStart = (long)_stagingRegion * StagingRegionBytes;
        var src = regionStart + _stagingCursor;
        var dst = (byte*)_staging.Mapped + src;

        MemoryMarshal.AsBytes(mesh.Vertices.AsSpan(0, vertexCount)).CopyTo(new Span<byte>(dst, vertexBytes));
        MemoryMarshal.AsBytes(mesh.Indices.AsSpan(0, indexCount)).CopyTo(new Span<byte>(dst + vertexBytes, indexBytes));

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

    /// <summary>Removes the chunk now; its arena ranges are reused only once in-flight frames can no longer read them.</summary>
    public void Free(int handle)
    {
        if ((uint)handle >= MaxHandles || !_slots[handle].Used)
            return;

        ref var slot = ref _slots[handle];
        var last = _live[--_liveCount];
        _live[slot.LiveIndex] = last;
        _slots[last].LiveIndex = slot.LiveIndex;
        slot.Used = false;
        _freeHandles[_freeHandleCount++] = handle;

        if (slot.VertexCount == 0)
            return;

        ref var pending = ref _pendingFrees[(_pendingHead + _pendingCount) % MaxRanges];
        pending.VertexOffset = slot.VertexOffset;
        pending.VertexCount = slot.VertexCount;
        pending.IndexOffset = slot.IndexOffset;
        pending.IndexCount = slot.IndexCount;
        pending.Stamp = _submission;
        _pendingCount++;
    }

    /// <summary>
    /// Call once per submitted frame, after Renderer.BeginFrame and before the main pass.
    /// Records staged copies plus a transfer -> vertex input barrier, and releases deferred frees.
    /// </summary>
    public void RecordUploads(CommandBuffer commands, int frameIndex)
    {
        ReleaseDeferred();

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
        _stagingRegion = (_stagingRegion + 1) % StagingRegions;
        _submission++;
    }

    // A range freed at stamp S was last read or written by submission S; it is idle once submission S + FramesInFlight has begun.
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

    /// <summary>Inside the main pass: frustum-culls chunks and draws the visible ones, one DrawIndexed each.</summary>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, Vector3 cameraPos, in TerrainLighting lighting)
    {
        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->CameraPos = new Vector4(cameraPos, 0f);
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, lighting.FogDensity);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.BindVertexBuffers(0, [_vertexArena.Handle], [(ByteSize64)0]);
        commands.BindIndexBuffer(_indexArena.Handle, (ByteSize64)0, VkIndexType.UInt32);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        var planes = stackalloc Vector4[6];
        ExtractPlanes(viewProj, planes);
        var drawn = 0;
        var push = Vector4.Zero;

        for (var i = 0; i < _liveCount; i++)
        {
            ref var slot = ref _slots[_live[i]];

            if (slot.IndexCount == 0 || slot.CopiedAt >= _submission || !Visible(planes, slot.Origin, slot.VoxelScale))
                continue;

            push = new Vector4(slot.Origin, 0f);
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&push, PushConstantSize));
            commands.DrawIndexed(slot.IndexCount, 1, slot.IndexOffset, slot.VertexOffset, 0);
            drawn++;
        }

        DrawnLastFrame = drawn;
    }

    // Row-vector convention (clip = v * M): clip component j is column j. Planes: w±x, w±y, z >= 0, w - z >= 0 (reverse-Z near).
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

    private static bool Visible(Vector4* planes, Vector3 origin, float voxelScale)
    {
        var margin = CullMarginVoxels * voxelScale;
        var min = origin - new Vector3(margin);
        var max = origin + new Vector3(ChunkConstants.Size * voxelScale + margin);

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
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 1, PoolSizes = &size },
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
                ],
                []
            );
        }
    }

    // Meshes wind CCW seen from air (cross(b - a, c - a) points into air). The projection's -f on Y flips clip Y,
    // which cancels Vulkan's y-down framebuffer, so world CCW stays CCW in Vulkan's framebuffer area test: cull back, front = CCW.
    private VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        var vert = ShaderLoader.Load(_device, "terrain.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "terrain.frag", ShaderKind.FragmentShader);

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
                    new() { Location = 2, Binding = 0, Format = VkFormat.R32UInt, Offset = (ByteSize32)24 },
                };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 3,
                    VertexAttributeDescriptions = attributes,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
                var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = VkCullModeFlags.BackBit,
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

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer (and its VulkanContext) is disposed.</summary>
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
