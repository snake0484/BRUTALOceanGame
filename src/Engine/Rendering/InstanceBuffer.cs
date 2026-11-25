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
/// The per-draw instance records shared by terrain and hull draws: a persistently mapped storage buffer with one region
/// per frame in flight, bound as set 1, binding 0. A draw reads its record at gl_InstanceIndex, so it passes the
/// record's index as firstInstance. Main thread only. Per frame: <see cref="BeginFrame"/> after Renderer.BeginFrame,
/// then renderers <see cref="Add"/> records (or <see cref="Reserve"/> them for a compute pass to write) and bind <see cref="Set"/>.
/// </summary>
public sealed unsafe class InstanceBuffer : IDisposable
{
    public const int Capacity = 16384;

    // 16384 * 32 B = 512 KiB a frame, a multiple of 256 so per-frame offsets meet storage buffer alignment.
    private const int FrameBytes = Capacity * 32;

    private readonly Device _device;
    private readonly GpuBuffer _buffer;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private InstanceRecord* _records;
    private int _frame;
    private int _count;

    public InstanceBuffer(Renderer renderer)
    {
        if (sizeof(InstanceRecord) * Capacity != FrameBytes)
            throw new InvalidOperationException($"InstanceRecord is {sizeof(InstanceRecord)} bytes, expected 32.");

        var context = renderer.Context;
        _device = context.Device;
        _buffer = new GpuBuffer(context, FrameBytes * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, true);
        SetLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();
        BeginFrame(0);
    }

    /// <summary>Set 1 of every pipeline that reads instance records.</summary>
    public VkDescriptorSetLayout SetLayout { get; }

    /// <summary>This frame's records.</summary>
    public VkDescriptorSet Set => _sets[_frame];

    public int Count => _count;

    /// <summary>Starts writing the region of <paramref name="frameIndex"/>, which its fence has already freed.</summary>
    public void BeginFrame(int frameIndex)
    {
        _frame = frameIndex;
        _count = 0;
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

    /// <summary>Reserves <paramref name="count"/> records for the GPU to write this frame. Returns the first index, or -1 when full.</summary>
    public int Reserve(int count)
    {
        if (_count + count > Capacity)
            return -1;

        var first = _count;
        _count += count;
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
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 1, PoolSizes = &size },
            null
        );
    }

    private void CreateSets()
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
            _sets
        );

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = _buffer.Handle,
                Offset = (ByteSize64)(i * FrameBytes),
                Range = (ByteSize64)FrameBytes,
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
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
        _buffer.Dispose();
    }
}
