using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// One draw's placement (review C9), std430 as in shaders/instance.glsl:
/// camera-relative = <see cref="RelOrigin"/> + rotate(<see cref="Rotation"/>, local × <see cref="Scale"/>), in the
/// camera frame's axes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InstanceRecord
{
    public Vector3 RelOrigin;
    public float Scale;
    public Quaternion Rotation;
}

/// <summary>
/// The per-draw instance records, bound as set 1, binding 0; a draw reads its record at gl_InstanceIndex, so it passes
/// the record's index as firstInstance. Two stores, one region each per frame in flight: records the CPU writes
/// (<see cref="Add"/>, <see cref="Set"/>) stay in a persistently mapped host-visible buffer, VRAM under ReBAR; records a
/// compute pass writes (<see cref="Reserve"/>, <see cref="GpuSet"/>) are device-local, never crossing PCIe. Main thread
/// only. Per frame: <see cref="BeginFrame"/> after Renderer.BeginFrame, then renderers add or reserve records and bind
/// the matching set.
/// </summary>
public sealed unsafe class InstanceBuffer : IDisposable
{
    public const int Capacity = 16384;
    public const int GpuCapacity = 8192;

    // 32 B records: 512 KiB and 256 KiB a frame, multiples of 256 so per-frame offsets meet storage buffer alignment.
    private const int FrameBytes = Capacity * 32;
    private const int GpuFrameBytes = GpuCapacity * 32;

    private readonly Device _device;
    private readonly GpuBuffer _buffer;
    private readonly GpuBuffer _gpuBuffer;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkDescriptorSet[] _gpuSets = new VkDescriptorSet[Renderer.FramesInFlight];
    private InstanceRecord* _records;
    private int _frame;
    private int _count;
    private int _gpuCount;

    public InstanceBuffer(Renderer renderer)
    {
        if (sizeof(InstanceRecord) * Capacity != FrameBytes)
            throw new InvalidOperationException($"InstanceRecord is {sizeof(InstanceRecord)} bytes, expected 32.");

        var context = renderer.Context;
        _device = context.Device;
        _buffer = new GpuBuffer(context, FrameBytes * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, true);
        _gpuBuffer = new GpuBuffer(context, GpuFrameBytes * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, false);
        SetLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets(_sets, _buffer, FrameBytes);
        CreateSets(_gpuSets, _gpuBuffer, GpuFrameBytes);
        BeginFrame(0);
    }

    /// <summary>Set 1 of every pipeline that reads instance records.</summary>
    public VkDescriptorSetLayout SetLayout { get; }

    /// <summary>This frame's CPU-written records.</summary>
    public VkDescriptorSet Set => _sets[_frame];

    /// <summary>This frame's GPU-written records.</summary>
    public VkDescriptorSet GpuSet => _gpuSets[_frame];

    public int Count => _count;

    /// <summary>Starts writing the region of <paramref name="frameIndex"/>, which its fence has already freed.</summary>
    public void BeginFrame(int frameIndex)
    {
        _frame = frameIndex;
        _count = 0;
        _gpuCount = 0;
        _records = (InstanceRecord*)((byte*)_buffer.Mapped + frameIndex * FrameBytes);
    }

    /// <summary>Writes a record straight into mapped memory. Returns its index (the draw's firstInstance), or -1 when full.</summary>
    public int Add(double3 relOrigin, float scale, Quaternion rotation)
    {
        if (_count == Capacity)
            return -1;

        ref var record = ref _records[_count];
        record.RelOrigin = new Vector3((float)relOrigin.X, (float)relOrigin.Y, (float)relOrigin.Z);
        record.Scale = scale;
        record.Rotation = rotation;
        return _count++;
    }

    /// <summary>Reserves <paramref name="count"/> device-local records (<see cref="GpuSet"/>) for a compute pass to write this frame. Returns the first index, or -1 when full.</summary>
    public int Reserve(int count)
    {
        if (_gpuCount + count > GpuCapacity)
            return -1;

        var first = _gpuCount;
        _gpuCount += count;
        return first;
    }

    private VkDescriptorSetLayout CreateSetLayout()
    {
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.StorageBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit,
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * Renderer.FramesInFlight };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = 2 * Renderer.FramesInFlight, PoolSizeCount = 1, PoolSizes = &size },
            null
        );
    }

    private void CreateSets(VkDescriptorSet[] sets, GpuBuffer buffer, int frameBytes)
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = SetLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _pool,
                DescriptorSetCount = Renderer.FramesInFlight,
                SetLayouts = layouts,
            },
            sets
        );

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = buffer.Handle,
                Offset = (ByteSize64)(i * frameBytes),
                Range = (ByteSize64)frameBytes,
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = sets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &info,
                    },
                ],
                []
            );
        }
    }

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer is disposed.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(SetLayout, null);
        _gpuBuffer.Dispose();
        _buffer.Dispose();
    }
}
