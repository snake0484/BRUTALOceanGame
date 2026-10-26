using Brutal;
using Brutal.VulkanApi;
using BrutalGame.Engine.Time;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Named GPU scopes from timestamp queries, one pool per frame in flight.
/// Results are read once the frame's fence has signalled, so readback never stalls.
/// </summary>
public sealed unsafe class GpuTimer : IDisposable
{
    public const int MaxScopes = 24;
    private const int Samples = 256;

    public readonly bool Supported;

    private readonly Device _device;
    private readonly VkQueryPool[] _pools;
    private readonly uint[] _written;
    private readonly string[] _names = new string[MaxScopes];
    private readonly TimingRing[] _rings = new TimingRing[MaxScopes];
    private readonly double _msPerTick;
    private readonly ulong _mask;
    private int _scopeCount;
    private int _frame;

    public GpuTimer(Device device, int queueFamily, int framesInFlight)
    {
        _device = device;
        _pools = new VkQueryPool[framesInFlight];
        _written = new uint[framesInFlight];

        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[device.PhysicalDevice.GetQueueFamilyProperties_Count()];
        device.PhysicalDevice.GetQueueFamilyProperties_Write(families);
        var validBits = families[queueFamily].TimestampValidBits;
        Supported = validBits > 0;
        _mask = validBits >= 64 ? ulong.MaxValue : (1UL << validBits) - 1;
        _msPerTick = device.PhysicalDevice.GetProperties().Limits.TimestampPeriod / 1_000_000.0;

        if (!Supported)
            return;

        for (var i = 0; i < framesInFlight; i++)
        {
            _pools[i] = device.CreateQueryPool(
                new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = MaxScopes * 2 },
                null
            );
        }
    }

    public int ScopeCount => _scopeCount;

    public string Name(int scope) => _names[scope];

    public TimingRing Ring(int scope) => _rings[scope];

    public int Register(string name)
    {
        if (_scopeCount == MaxScopes)
            throw new InvalidOperationException("Too many GPU scopes");

        _names[_scopeCount] = name;
        _rings[_scopeCount] = new TimingRing(Samples);
        return _scopeCount++;
    }

    /// <summary>
    /// Collects the results this frame slot produced last time, then resets its pool. Call outside a render pass.
    /// </summary>
    public void BeginFrame(CommandBuffer commands, int frameIndex)
    {
        if (!Supported)
            return;

        _frame = frameIndex;
        var pool = _pools[frameIndex];
        var written = _written[frameIndex];
        Span<ulong> pair = stackalloc ulong[2];

        for (var scope = 0; written != 0; scope++, written >>= 1)
        {
            if ((written & 1) == 0)
                continue;

            var result = _device.GetQueryPoolResults(
                pool,
                scope * 2,
                2,
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(pair),
                (ByteSize64)sizeof(ulong),
                VkQueryResultFlags._64Bit
            );

            if (result == VkResult.Success)
                _rings[scope].Record((float)(((pair[1] - pair[0]) & _mask) * _msPerTick));
        }

        _written[frameIndex] = 0;
        commands.ResetQueryPool(pool, 0, MaxScopes * 2);
    }

    public void Begin(CommandBuffer commands, int scope)
    {
        if (Supported)
            commands.WriteTimestamp(VkPipelineStageFlags.TopOfPipeBit, _pools[_frame], scope * 2);
    }

    public void End(CommandBuffer commands, int scope)
    {
        if (!Supported)
            return;

        commands.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, _pools[_frame], scope * 2 + 1);
        _written[_frame] |= 1u << scope;
    }

    public void Dispose()
    {
        if (!Supported)
            return;

        foreach (var pool in _pools)
            _device.DestroyQueryPool(pool, null);
    }
}
