using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// VMA sub-allocated buffer. Host-visible buffers stay persistently mapped.
/// </summary>
public sealed unsafe class GpuBuffer : IDisposable
{
    public readonly VkBuffer Handle;
    public readonly int Size;
    public readonly void* Mapped;
    private readonly VmaAllocator _allocator;
    private readonly VmaAllocation _allocation;

    public GpuBuffer(VulkanContext context, int size, VkBufferUsageFlags usage, bool hostVisible)
    {
        _allocator = context.Allocator;
        Size = size;

        var bufferInfo = new VkBufferCreateInfo
        {
            Size = (ByteSize64)size,
            Usage = usage,
            SharingMode = VkSharingMode.Exclusive,
        };

        var allocationInfo = hostVisible
            ? new VmaAllocationCreateInfo
            {
                Usage = VmaMemoryUsage.Auto,
                Flags = VmaAllocationCreateFlags.HostAccessSequentialWriteBit | VmaAllocationCreateFlags.MappedBit,
                RequiredFlags = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            }
            : new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice };

        var result = _allocator.CreateBuffer(bufferInfo, allocationInfo, out Handle, out _allocation, out var info);
        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateBuffer failed: {result}");

        if (hostVisible)
            Mapped = (void*)info.PMappedData;
    }

    public Span<T> AsSpan<T>() where T : unmanaged => new(Mapped, Size / sizeof(T));

    public void Dispose() => _allocator.DestroyBuffer(Handle, _allocation);
}
