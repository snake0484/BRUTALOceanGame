namespace BrutalGame.Crafting;

/// <summary>A scheduled batch: <c>(station, recipe, start tick, end tick)</c>; outputs were computed at start.</summary>
public struct Batch
{
    public long Id;
    public int Station;
    public RecipeId Recipe;
    public long StartTick, EndTick, StarvedApplied;
    internal int Payload;
}

/// <summary>
/// Batches as scheduled events (exotic-matter.md, Batches run on events): a min-heap by <c>(EndTick, Id)</c>, popped on
/// their tick, so a running batch costs nothing per tick. Power starvation is counted per station by the ledger; a batch
/// popped with uncounted starved ticks is re-pushed with <c>EndTick += starved</c> (lazy, no decrease-key, deterministic).
/// Preallocated; scheduling and popping allocate nothing.
/// </summary>
public sealed class ProductionQueue
{
    public const int MaxOutputs = 4;

    public int Count { get; private set; }
    public int Capacity => _heap.Length;
    public long NextDueTick => Count > 0 ? _heap[0].EndTick : long.MaxValue;

    private readonly Batch[] _heap;
    private readonly ItemStack[] _outputs;
    private readonly int[] _outputCount;
    private readonly int[] _free;
    private readonly long[] _starved;
    private int _freeCount;
    private long _nextId = 1;

    public ProductionQueue(int capacity, int stations)
    {
        _heap = new Batch[capacity];
        _outputs = new ItemStack[capacity * MaxOutputs];
        _outputCount = new int[capacity];
        _free = new int[capacity];
        _starved = new long[stations];

        for (var i = 0; i < capacity; i++)
            _free[i] = capacity - 1 - i;

        _freeCount = capacity;
    }

    /// <summary>The id the next scheduled batch will get (seeds a crude refinery's error before it is scheduled).</summary>
    public long PeekNextId => _nextId;

    public bool TrySchedule(int station, RecipeId recipe, long startTick, long durationTicks, ReadOnlySpan<ItemStack> outputs, out long batchId)
    {
        batchId = 0;

        if (_freeCount == 0 || outputs.Length > MaxOutputs || (uint)station >= (uint)_starved.Length || durationTicks < 0)
            return false;

        var payload = _free[--_freeCount];
        outputs.CopyTo(_outputs.AsSpan(payload * MaxOutputs, MaxOutputs));
        _outputCount[payload] = outputs.Length;
        batchId = _nextId++;
        var batch = new Batch
        {
            Id = batchId, Station = station, Recipe = recipe, StartTick = startTick, EndTick = startTick + durationTicks,
            StarvedApplied = _starved[station], Payload = payload,
        };
        _heap[Count] = batch;
        SiftUp(Count++);
        return true;
    }

    /// <summary>The ledger reports ticks on which a station's power was short; its running batches end that much later.</summary>
    public void AddStarved(int station, long ticks) => _starved[station] += ticks;

    /// <summary>Pops the next batch due at or before <paramref name="tick"/> and copies its outputs.</summary>
    public bool TryPopDue(long tick, out Batch batch, Span<ItemStack> outputs, out int outputCount)
    {
        while (Count > 0 && _heap[0].EndTick <= tick)
        {
            ref var top = ref _heap[0];
            var starved = _starved[top.Station] - top.StarvedApplied;

            if (starved > 0)
            {
                top.EndTick += starved;
                top.StarvedApplied += starved;
                SiftDown(0);
                continue;
            }

            batch = top;
            _heap[0] = _heap[--Count];
            SiftDown(0);
            outputCount = _outputCount[batch.Payload];
            _outputs.AsSpan(batch.Payload * MaxOutputs, outputCount).CopyTo(outputs);
            _free[_freeCount++] = batch.Payload;
            return true;
        }

        batch = default;
        outputCount = 0;
        return false;
    }

    private static bool Before(in Batch a, in Batch b) => a.EndTick < b.EndTick || (a.EndTick == b.EndTick && a.Id < b.Id);

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) >> 1;

            if (!Before(_heap[i], _heap[parent]))
                return;

            (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        while (true)
        {
            int left = 2 * i + 1, right = left + 1, best = i;

            if (left < Count && Before(_heap[left], _heap[best]))
                best = left;

            if (right < Count && Before(_heap[right], _heap[best]))
                best = right;

            if (best == i)
                return;

            (_heap[i], _heap[best]) = (_heap[best], _heap[i]);
            i = best;
        }
    }
}
