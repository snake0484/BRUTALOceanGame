using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;
using BrutalGame.Terrain;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Draws terrain chunks from one device-local vertex arena and one index arena with one indirect draw (review C8, P4):
/// a GPU chunk table mirrors the live slots, and a compute pass frustum-culls it into device-local instance records and
/// VkDrawIndexedIndirectCommands. Uploads (meshes and table records) go through a host-visible staging ring and are
/// copied before the main pass. The host never reads GPU-written memory in the frame loop: draw stats are copied to a
/// small readback buffer only while <see cref="StatsEnabled"/>. Main thread only. Per frame: Upload/Free (any time),
/// RecordUploads (after BeginFrame), Cull (after InstanceBuffer.BeginFrame, outside a pass), Draw (in the pass).
/// </summary>
public sealed unsafe class TerrainRenderer : IDisposable
{
    public const int MaxHandles = 8192;
    public const int VertexStride = PackedTerrainVertex.SizeInBytes;
    public const long VertexArenaBytes = 48L << 20;
    public const long IndexArenaBytes = 96L << 20;
    public const int StagingRegionBytes = 16 << 20;
    public const int MaxUploadsPerFrame = 1024;

    // Live + pending-free ranges are capped so the arenas' free-block tables can never overflow.
    private const int MaxRanges = 2 * MaxHandles;

    // One more region than frames in flight: Upload may run before BeginFrame waits on the oldest fence.
    private const int StagingRegions = Renderer.FramesInFlight + 1;
    private const int UniformAlignment = 256;

    // GPU chunk table (terrain_cull.comp Chunk): one 32 B record per handle. Each staging region ends with room for a
    // full table rewrite.
    private const int ChunkStride = 32;
    private const int TableStagingBytes = MaxHandles * ChunkStride;
    private const int MeshStagingBytes = StagingRegionBytes - TableStagingBytes;
    private const double ChunkMetres = ChunkConstants.Size * ChunkConstants.VoxelSize;

    /// <summary>Past this the camera's whole-chunk cast nears int range; no terrain is visible from that far anyway.</summary>
    private const double MaxCameraMetres = (1 << 24) * ChunkMetres;

    // Per frame in flight, device-local: a 16 B header (draw count, index total) then one VkDrawIndexedIndirectCommand per slot.
    private const int DrawHeaderBytes = 16;
    private const int StatsBytes = 8;
    private const int DrawCommandStride = 20;
    private const int DrawRegionBytes = (DrawHeaderBytes + MaxHandles * DrawCommandStride + UniformAlignment - 1) / UniformAlignment * UniformAlignment;
    private const int CullGroupSize = 64;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
    private readonly GpuBuffer _vertexArena;
    private readonly GpuBuffer _indexArena;
    private readonly GpuBuffer _staging;
    private readonly GpuBuffer _uniforms;
    private readonly GpuBuffer _chunkTable;
    private readonly GpuBuffer _draws;
    private readonly GpuBuffer _cullUniforms;
    private readonly GpuBuffer _statsReadback;
    private readonly InstanceBuffer _instances;
    private readonly bool _indirectCount;
    private readonly RangeAllocator _vertexRanges;
    private readonly RangeAllocator _indexRanges;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly VkDescriptorSetLayout _cullSetLayout;
    private readonly VkDescriptorSet[] _cullSets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _cullLayout;
    private readonly VkPipeline _cullPipeline;

    private readonly Slot[] _slots = new Slot[MaxHandles];
    private readonly int[] _freeHandles = new int[MaxHandles];
    private readonly int[] _live = new int[MaxHandles];
    private readonly PendingFree[] _pendingFrees = new PendingFree[MaxRanges];
    private readonly VkBufferCopy[] _vertexCopies = new VkBufferCopy[MaxUploadsPerFrame];
    private readonly VkBufferCopy[] _indexCopies = new VkBufferCopy[MaxUploadsPerFrame];
    private readonly int[] _dirty = new int[MaxHandles];
    private readonly VkBufferCopy[] _tableCopies = new VkBufferCopy[MaxHandles];
    private readonly StatsState[] _stats = new StatsState[Renderer.FramesInFlight];

    private int _freeHandleCount;
    private int _liveCount;
    private int _pendingHead;
    private int _pendingCount;
    private int _copyCount;
    private int _stagingRegion;
    private int _stagingCursor;
    private long _submission;
    private int _dirtyCount;
    private int _highWater;
    private int _tableHighWater;
    private int _drawSlots;
    private long _cullTicks;

    private struct Slot
    {
        public ChunkCoord Coord;
        public float VoxelScale;
        public int VertexOffset;
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public int LiveIndex;
        public bool Used;
        public bool Dirty;
    }

    private enum StatsState : byte
    {
        None,
        Copied,
        NothingDrawn,
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

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuChunk
    {
        public int BaseX;
        public int BaseY;
        public int BaseZ;
        public float VoxelScale;
        public int IndexCount;
        public int FirstIndex;
        public int VertexOffset;
        public float MeshScale;
    }

    // std140 as terrain_cull.comp's Cull block; Size covers its padded uvec4.
    [StructLayout(LayoutKind.Sequential, Size = 160)]
    private struct CullUniforms
    {
        public fixed float Planes[24];
        public int CameraChunkX;
        public int CameraChunkY;
        public int CameraChunkZ;
        public int Slots;
        public Vector4 CameraFrac;
        public Quaternion Rotation;
        public uint FirstInstance;
        public uint Compact;
    }

    public TerrainRenderer(Renderer renderer, InstanceBuffer instances)
    {
        if (sizeof(PackedTerrainVertex) != VertexStride)
            throw new InvalidOperationException($"PackedTerrainVertex is {sizeof(PackedTerrainVertex)} bytes, expected {VertexStride}.");

        if (sizeof(GpuChunk) != ChunkStride || sizeof(CullUniforms) > UniformAlignment)
            throw new InvalidOperationException("GpuChunk or CullUniforms layout does not match terrain_cull.comp.");

        var context = renderer.Context;
        _device = context.Device;
        _pipelines = context.Pipelines;
        _instances = instances;
        _indirectCount = context.DrawIndirectCount;

        var vertexCapacity = (int)(VertexArenaBytes / VertexStride);
        var indexCapacity = (int)(IndexArenaBytes / sizeof(uint));
        _vertexRanges = new RangeAllocator(vertexCapacity, MaxRanges + 1);
        _indexRanges = new RangeAllocator(indexCapacity, MaxRanges + 1);

        _vertexArena = new GpuBuffer(context, vertexCapacity * VertexStride, VkBufferUsageFlags.VertexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _indexArena = new GpuBuffer(context, indexCapacity * sizeof(uint), VkBufferUsageFlags.IndexBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _staging = new GpuBuffer(context, StagingRegionBytes * StagingRegions, VkBufferUsageFlags.TransferSrcBit, true);
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _chunkTable = new GpuBuffer(context, MaxHandles * ChunkStride, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.TransferDstBit, false);
        _draws = new GpuBuffer(
            context,
            DrawRegionBytes * Renderer.FramesInFlight,
            VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.IndirectBufferBit | VkBufferUsageFlags.TransferDstBit | VkBufferUsageFlags.TransferSrcBit,
            false
        );
        _cullUniforms = new GpuBuffer(context, UniformAlignment * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _statsReadback = new GpuBuffer(context, StatsBytes * Renderer.FramesInFlight, VkBufferUsageFlags.TransferDstBit, false, hostRead: true);

        for (var i = 0; i < MaxHandles; i++)
            _freeHandles[i] = MaxHandles - 1 - i;

        _freeHandleCount = MaxHandles;

        _setLayout = CreateSetLayout();
        _cullSetLayout = CreateCullSetLayout();
        _pool = CreatePool();
        CreateSets();
        CreateCullSets();

        _layout = _device.CreatePipelineLayout([_setLayout, instances.SetLayout], [], null);
        _pipeline = CreatePipeline(renderer.RenderPass);
        _cullLayout = _device.CreatePipelineLayout([_cullSetLayout, instances.SetLayout], [], null);
        _cullPipeline = CreateCullPipeline();
    }

    /// <summary>CPU time of <see cref="Cull"/> plus <see cref="Draw"/>: terrain command recording.</summary>
    public readonly TimingRing DrawMs = new(256);

    public int ChunkCount => _liveCount;

    /// <summary>Copy the GPU cull's draw count and index total back for <see cref="DrawnLastFrame"/> (a stats panel is open).</summary>
    public bool StatsEnabled { get; set; }

    /// <summary>Chunks the GPU cull drew, read once the frame's fence has signalled (frames in flight behind); held while stats are off.</summary>
    public int DrawnLastFrame { get; private set; }

    public int TrianglesLastFrame { get; private set; }

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
        slot.Coord = coord;
        slot.VoxelScale = 1 << coord.Lod;
        slot.VertexOffset = vertexOffset;
        slot.VertexCount = vertexCount;
        slot.IndexOffset = indexOffset;
        slot.IndexCount = indexCount;
        slot.LiveIndex = _liveCount;
        slot.Used = true;
        _live[_liveCount++] = handle;
        _highWater = Math.Max(_highWater, handle + 1);
        MarkDirty(handle);
        return handle;
    }

    // The slot's table record is rewritten at the next RecordUploads, in order with its mesh copy.
    private void MarkDirty(int handle)
    {
        ref var slot = ref _slots[handle];

        if (slot.Dirty)
            return;

        slot.Dirty = true;
        _dirty[_dirtyCount++] = handle;
    }

    private bool Stage(ChunkMesh mesh, int vertexCount, int indexCount, out int vertexOffset, out int indexOffset)
    {
        vertexOffset = 0;
        indexOffset = 0;
        var vertexBytes = vertexCount * VertexStride;
        var indexBytes = indexCount * sizeof(uint);

        if (_copyCount == MaxUploadsPerFrame || _stagingCursor + vertexBytes + indexBytes > MeshStagingBytes)
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

        MemoryMarshal.AsBytes(mesh.Packed.AsSpan(0, vertexCount)).CopyTo(new Span<byte>(dst, vertexBytes));
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
        MarkDirty(handle);

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
    /// Call once per submitted frame, after Renderer.BeginFrame and before <see cref="Cull"/>. Zeroes this frame's draw
    /// counters, records staged mesh and chunk table copies plus one transfer -> vertex input and compute barrier, and
    /// releases deferred frees.
    /// </summary>
    public void RecordUploads(CommandBuffer commands, int frameIndex)
    {
        ReleaseDeferred();
        var tableCopies = StageTable();

        // The region's last reader finished before BeginFrame's fence wait. Filled here, at the frame's start, so the
        // barrier below covers it with the uploads instead of draining the pipeline before the cull.
        commands.FillBuffer(_draws.Handle, (ByteSize64)((long)frameIndex * DrawRegionBytes), (ByteSize64)(long)DrawHeaderBytes, 0);

        if (_copyCount > 0)
        {
            commands.CopyBuffer(_staging.Handle, _vertexArena.Handle, new ReadOnlySpan<VkBufferCopy>(_vertexCopies, 0, _copyCount));
            commands.CopyBuffer(_staging.Handle, _indexArena.Handle, new ReadOnlySpan<VkBufferCopy>(_indexCopies, 0, _copyCount));
        }

        // The table is rewritten in place: wait for earlier frames' culls still reading it.
        if (tableCopies > 0)
        {
            commands.PipelineBarrier(VkPipelineStageFlags.ComputeShaderBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, [], [], []);
            commands.CopyBuffer(_staging.Handle, _chunkTable.Handle, new ReadOnlySpan<VkBufferCopy>(_tableCopies, 0, tableCopies));
        }

        Barrier(
            commands,
            VkPipelineStageFlags.TransferBit,
            VkAccessFlags.TransferWriteBit,
            VkPipelineStageFlags.VertexInputBit | VkPipelineStageFlags.ComputeShaderBit,
            VkAccessFlags.VertexAttributeReadBit | VkAccessFlags.IndexReadBit | VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit
        );

        _tableHighWater = _highWater;
        _copyCount = 0;
        _stagingCursor = 0;
        _stagingRegion = (_stagingRegion + 1) % StagingRegions;
        _submission++;
    }

    // Dirty slots' records into the end of this staging region: live chunks as drawn, free slots as empty.
    private int StageTable()
    {
        var regionStart = (long)_stagingRegion * StagingRegionBytes + MeshStagingBytes;
        var records = (GpuChunk*)((byte*)_staging.Mapped + regionStart);

        for (var i = 0; i < _dirtyCount; i++)
        {
            var handle = _dirty[i];
            ref var slot = ref _slots[handle];
            slot.Dirty = false;
            records[i] = slot.Used ? ToGpu(slot) : default;
            _tableCopies[i] = new VkBufferCopy
            {
                SrcOffset = (ByteSize64)(regionStart + (long)i * ChunkStride),
                DstOffset = (ByteSize64)((long)handle * ChunkStride),
                Size = (ByteSize64)(long)ChunkStride,
            };
        }

        var count = _dirtyCount;
        _dirtyCount = 0;
        return count;
    }

    // Base in LOD 0 chunks keeps the GPU's camera-relative origin exact (terrain_cull.comp).
    private static GpuChunk ToGpu(in Slot slot)
    {
        var coord = slot.Coord;

        return new GpuChunk
        {
            BaseX = coord.X << coord.Lod,
            BaseY = coord.Y << coord.Lod,
            BaseZ = coord.Z << coord.Lod,
            VoxelScale = slot.VoxelScale,
            IndexCount = slot.IndexCount,
            FirstIndex = slot.IndexOffset,
            VertexOffset = slot.VertexOffset,
            MeshScale = slot.VoxelScale * PackedTerrainVertex.InvStepsPerVoxel,
        };
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

    /// <summary>
    /// Outside a render pass, after <see cref="RecordUploads"/>: dispatches the GPU frustum cull over every chunk slot,
    /// which writes this frame's instance records (reserved here) and indirect draws for <see cref="Draw"/>.
    /// Camera-relative: each record is <paramref name="rotation"/> × (origin − <paramref name="camera"/>), the
    /// difference taken in whole chunks plus the camera's remainder, so it is exact at any distance.
    /// </summary>
    /// <param name="camera">The camera in the terrain's frame, whose sea level is y = 0; nothing is drawn past <see cref="MaxCameraMetres"/> or for NaN.</param>
    /// <param name="rotation">The terrain frame's axes to the camera frame's (the axes <paramref name="viewProj"/> takes).</param>
    public void Cull(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj, double3 camera, doubleQuat rotation)
    {
        var start = Stopwatch.GetTimestamp();
        ReadStats(frameIndex);
        _drawSlots = 0;

        var inRange = Math.Abs(camera.X) < MaxCameraMetres && Math.Abs(camera.Y) < MaxCameraMetres && Math.Abs(camera.Z) < MaxCameraMetres;
        var first = _tableHighWater > 0 && inRange ? _instances.Reserve(_tableHighWater) : -1;

        if (first >= 0)
        {
            // Counters zeroed by RecordUploads.
            var region = (long)frameIndex * DrawRegionBytes;
            WriteCullUniforms(frameIndex, viewProj, camera, rotation, first);
            commands.BindPipeline(VkPipelineBindPoint.Compute, _cullPipeline);
            commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _cullLayout, 0, [_cullSets[frameIndex], _instances.GpuSet], []);
            commands.Dispatch((_tableHighWater + CullGroupSize - 1) / CullGroupSize, 1, 1);
            Barrier(
                commands,
                VkPipelineStageFlags.ComputeShaderBit,
                VkAccessFlags.ShaderWriteBit,
                VkPipelineStageFlags.DrawIndirectBit | VkPipelineStageFlags.VertexShaderBit | VkPipelineStageFlags.TransferBit,
                VkAccessFlags.IndirectCommandReadBit | VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferReadBit
            );
            _drawSlots = _tableHighWater;

            if (StatsEnabled)
                CopyStats(commands, frameIndex, region);
        }
        else if (StatsEnabled)
        {
            _stats[frameIndex] = StatsState.NothingDrawn;
        }

        _cullTicks = Stopwatch.GetTimestamp() - start;
    }

    // The stats this region's last frame copied back: its fence has signalled (BeginFrame), so they are complete.
    private void ReadStats(int frameIndex)
    {
        var state = _stats[frameIndex];
        _stats[frameIndex] = StatsState.None;

        if (state == StatsState.None)
            return;

        var stats = (uint*)((byte*)_statsReadback.Mapped + frameIndex * StatsBytes);
        var copied = state == StatsState.Copied;
        DrawnLastFrame = copied ? (int)stats[0] : 0;
        TrianglesLastFrame = copied ? (int)(stats[1] / 3) : 0;
    }

    private void CopyStats(CommandBuffer commands, int frameIndex, long region)
    {
        commands.CopyBuffer(
            _draws.Handle,
            _statsReadback.Handle,
            [new VkBufferCopy { SrcOffset = (ByteSize64)region, DstOffset = (ByteSize64)(long)(frameIndex * StatsBytes), Size = (ByteSize64)(long)StatsBytes }]
        );
        Barrier(commands, VkPipelineStageFlags.TransferBit, VkAccessFlags.TransferWriteBit, VkPipelineStageFlags.HostBit, VkAccessFlags.HostReadBit);
        _stats[frameIndex] = StatsState.Copied;
    }

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess) =>
        commands.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }], [], []);

    private void WriteCullUniforms(int frameIndex, in Matrix4x4 viewProj, double3 camera, doubleQuat rotation, int firstInstance)
    {
        var uniforms = (CullUniforms*)((byte*)_cullUniforms.Mapped + frameIndex * UniformAlignment);
        var axes = new Quaternion((float)rotation.X, (float)rotation.Y, (float)rotation.Z, (float)rotation.W);
        ExtractPlanes(viewProj, Quaternion.Conjugate(axes), (Vector4*)uniforms->Planes);

        var chunkX = Math.Floor(camera.X / ChunkMetres);
        var chunkY = Math.Floor(camera.Y / ChunkMetres);
        var chunkZ = Math.Floor(camera.Z / ChunkMetres);
        uniforms->CameraChunkX = (int)chunkX;
        uniforms->CameraChunkY = (int)chunkY;
        uniforms->CameraChunkZ = (int)chunkZ;
        uniforms->Slots = _tableHighWater;
        uniforms->CameraFrac = new Vector4(
            (float)(camera.X - chunkX * ChunkMetres),
            (float)(camera.Y - chunkY * ChunkMetres),
            (float)(camera.Z - chunkZ * ChunkMetres),
            (float)ChunkMetres
        );
        uniforms->Rotation = axes;
        uniforms->FirstInstance = (uint)firstInstance;
        uniforms->Compact = _indirectCount ? 1u : 0u;
    }

    /// <summary>
    /// Inside the opaque pass: draws what <see cref="Cull"/> kept with one DrawIndexedIndirectCount (without that
    /// feature, one DrawIndexedIndirect over every slot, culled ones with no instances). <paramref name="viewProj"/>
    /// has no translation.
    /// </summary>
    /// <param name="camera">The camera in the terrain's frame, whose sea level is y = 0.</param>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in TerrainLighting lighting)
    {
        var start = Stopwatch.GetTimestamp();

        if (_drawSlots > 0)
        {
            var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
            uniforms->ViewProj = viewProj;
            uniforms->CameraPos = new Vector4(0f, (float)camera.Y, 0f, 0f);
            uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
            uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
            uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, lighting.FogDensity);

            commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
            commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex], _instances.GpuSet], []);
            commands.BindVertexBuffers(0, [_vertexArena.Handle], [(ByteSize64)0]);
            commands.BindIndexBuffer(_indexArena.Handle, (ByteSize64)0, VkIndexType.UInt32);
            commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
            commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

            var region = (long)frameIndex * DrawRegionBytes;

            if (_indirectCount)
                commands.DrawIndexedIndirectCount(_draws.Handle, (ByteSize64)(region + DrawHeaderBytes), _draws.Handle, (ByteSize64)region, _drawSlots, DrawCommandStride);
            else
                commands.DrawIndexedIndirect(_draws.Handle, (ByteSize64)(region + DrawHeaderBytes), _drawSlots, (ByteSize32)DrawCommandStride);
        }

        DrawMs.Record((float)((_cullTicks + Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency));
    }

    // Row-vector convention (clip = v * M): clip component j is column j. Planes: w±x, w±y, z >= 0, w - z >= 0 (reverse-Z near),
    // their normals turned by toTerrain so boxes are tested in terrain axes.
    private static void ExtractPlanes(in Matrix4x4 m, Quaternion toTerrain, Vector4* planes)
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

        if (toTerrain.IsIdentity)
            return;

        for (var i = 0; i < 6; i++)
        {
            var p = planes[i];
            planes[i] = new Vector4(Vector3.Transform(new Vector3(p.X, p.Y, p.Z), toTerrain), p.W);
        }
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
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = bindings }, null);
    }

    // terrain_cull.comp set 0: cull uniforms, chunk table, this frame's draws.
    private VkDescriptorSetLayout CreateCullSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit },
            new() { Binding = 2, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.ComputeBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 3, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 2 * Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = 2 * Renderer.FramesInFlight, PoolSizeCount = 2, PoolSizes = sizes },
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

    private void CreateCullSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = _cullSetLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _pool,
                DescriptorSetCount = Renderer.FramesInFlight,
                SetLayouts = layouts,
            },
            _cullSets
        );

        var table = new VkDescriptorBufferInfo
        {
            Buffer = _chunkTable.Handle,
            Offset = (ByteSize64)0,
            Range = (ByteSize64)(MaxHandles * ChunkStride),
        };

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var uniforms = new VkDescriptorBufferInfo
            {
                Buffer = _cullUniforms.Handle,
                Offset = (ByteSize64)(i * UniformAlignment),
                Range = (ByteSize64)sizeof(CullUniforms),
            };
            var draws = new VkDescriptorBufferInfo
            {
                Buffer = _draws.Handle,
                Offset = (ByteSize64)(i * DrawRegionBytes),
                Range = (ByteSize64)DrawRegionBytes,
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _cullSets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &uniforms,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _cullSets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &table,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _cullSets[i],
                        DstBinding = 2,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &draws,
                    },
                ],
                []
            );
        }
    }

    private VkPipeline CreateCullPipeline()
    {
        var module = ShaderLoader.Load(_device, "terrain_cull.comp", ShaderKind.ComputeShader);

        try
        {
            fixed (byte* name = "main"u8)
            {
                return _pipelines.CreateComputePipeline(
                    new VkComputePipelineCreateInfo
                    {
                        Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = name },
                        Layout = _cullLayout,
                    }
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(module, null);
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
                    new() { Location = 0, Binding = 0, Format = VkFormat.R16G16B16A16UInt, Offset = (ByteSize32)0 },
                    new() { Location = 1, Binding = 0, Format = VkFormat.R16G16SNorm, Offset = (ByteSize32)8 },
                };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 2,
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

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer (and its VulkanContext) is disposed.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyPipeline(_cullPipeline, null);
        _device.DestroyPipelineLayout(_cullLayout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _device.DestroyDescriptorSetLayout(_cullSetLayout, null);

        _statsReadback.Dispose();
        _cullUniforms.Dispose();
        _draws.Dispose();
        _chunkTable.Dispose();
        _uniforms.Dispose();
        _staging.Dispose();
        _indexArena.Dispose();
        _vertexArena.Dispose();
    }
}
