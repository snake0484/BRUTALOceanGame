using Brutal;
using Brutal.VulkanApi;

namespace BrutalGame.Engine.Rendering;

/// <summary>Which render pass a pipeline statistics scope is drawn in, for per-pass totals.</summary>
public enum PipelinePass
{
    Opaque,
    Water,
    Overlay,
}

/// <summary>Counts one pipeline statistics scope produced in a frame.</summary>
public struct PipelineCounts
{
    public ulong Primitives;
    public ulong VertexInvocations;
    public ulong ClippingInvocations;
    public ulong ClippingPrimitives;
    public ulong FragmentInvocations;

    public void Add(in PipelineCounts other)
    {
        Primitives += other.Primitives;
        VertexInvocations += other.VertexInvocations;
        ClippingInvocations += other.ClippingInvocations;
        ClippingPrimitives += other.ClippingPrimitives;
        FragmentInvocations += other.FragmentInvocations;
    }
}

/// <summary>
/// Named pipeline statistics scopes (primitives in, vertex and fragment shader invocations, clipping), one pool per
/// frame in flight. Like <see cref="GpuTimer"/>, results are read once the frame's fence has signalled, so readback
/// never stalls. Scopes must not nest: only one pipeline statistics query may be active at a time.
/// </summary>
public sealed unsafe class PipelineStats : IDisposable
{
    public const int MaxScopes = 8;

    // Written in bit order: one ulong per flag, laid out as PipelineCounts.
    private const VkQueryPipelineStatisticFlags Flags =
        VkQueryPipelineStatisticFlags.InputAssemblyPrimitivesBit
        | VkQueryPipelineStatisticFlags.VertexShaderInvocationsBit
        | VkQueryPipelineStatisticFlags.ClippingInvocationsBit
        | VkQueryPipelineStatisticFlags.ClippingPrimitivesBit
        | VkQueryPipelineStatisticFlags.FragmentShaderInvocationsBit;

    public readonly bool Supported;

    /// <summary>Off: scopes record nothing. Set outside recording, never between a Begin and its End.</summary>
    public bool Enabled = true;

    private readonly Device _device;
    private readonly VkQueryPool[] _pools;
    private readonly uint[] _written;
    private readonly string[] _names = new string[MaxScopes];
    private readonly PipelinePass[] _passes = new PipelinePass[MaxScopes];
    private readonly PipelineCounts[] _last = new PipelineCounts[MaxScopes];
    private int _scopeCount;
    private int _frame;

    public PipelineStats(VulkanContext context, int framesInFlight)
    {
        _device = context.Device;
        _pools = new VkQueryPool[framesInFlight];
        _written = new uint[framesInFlight];
        Supported = context.PipelineStatistics;

        if (!Supported)
            return;

        for (var i = 0; i < framesInFlight; i++)
        {
            _pools[i] = _device.CreateQueryPool(
                new VkQueryPoolCreateInfo { QueryType = VkQueryType.PipelineStatistics, QueryCount = MaxScopes, PipelineStatistics = Flags },
                null
            );
        }
    }

    public int ScopeCount => _scopeCount;

    public string Name(int scope) => _names[scope];

    public PipelinePass Pass(int scope) => _passes[scope];

    /// <summary>The scope's counts from the latest frame whose results came back.</summary>
    public ref readonly PipelineCounts Last(int scope) => ref _last[scope];

    public int Register(string name, PipelinePass pass)
    {
        if (_scopeCount == MaxScopes)
            throw new InvalidOperationException("Too many pipeline statistics scopes");

        _names[_scopeCount] = name;
        _passes[_scopeCount] = pass;
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
        PipelineCounts counts = default;
        var bytes = new Span<byte>(&counts, sizeof(PipelineCounts));

        for (var scope = 0; written != 0; scope++, written >>= 1)
        {
            if ((written & 1) == 0)
                continue;

            var result = _device.GetQueryPoolResults(pool, scope, 1, bytes, (ByteSize64)sizeof(PipelineCounts), VkQueryResultFlags._64Bit);

            if (result == VkResult.Success)
                _last[scope] = counts;
        }

        _written[frameIndex] = 0;
        commands.ResetQueryPool(pool, 0, MaxScopes);
    }

    public void Begin(CommandBuffer commands, int scope)
    {
        if (Supported && Enabled)
            commands.BeginQuery(_pools[_frame], scope, VkQueryControlFlags.None);
    }

    public void End(CommandBuffer commands, int scope)
    {
        if (!Supported || !Enabled)
            return;

        commands.EndQuery(_pools[_frame], scope);
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
