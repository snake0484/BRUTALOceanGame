namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// First-fit range allocator over [0, capacity) in caller-defined units. Free blocks are kept sorted by offset
/// and coalesced on release. Fixed block capacity, no allocations after construction.
/// </summary>
public sealed class RangeAllocator
{
    private readonly int[] _offsets;
    private readonly int[] _sizes;
    private int _count;

    public RangeAllocator(int capacity, int maxFreeBlocks)
    {
        Capacity = capacity;
        _offsets = new int[maxFreeBlocks];
        _sizes = new int[maxFreeBlocks];
        _offsets[0] = 0;
        _sizes[0] = capacity;
        _count = 1;
    }

    public int Capacity { get; }

    public int Used { get; private set; }

    public int FreeBlocks => _count;

    /// <summary>Returns the start offset, or -1 if no free block is large enough.</summary>
    public int Allocate(int size)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_sizes[i] < size)
                continue;

            var offset = _offsets[i];
            _offsets[i] += size;
            _sizes[i] -= size;

            if (_sizes[i] == 0)
                RemoveAt(i);

            Used += size;
            return offset;
        }

        return -1;
    }

    /// <summary>Size the block table to max live ranges + 1 so this never runs out.</summary>
    public void Release(int offset, int size)
    {
        var i = LowerBound(offset);
        var mergePrev = i > 0 && _offsets[i - 1] + _sizes[i - 1] == offset;
        var mergeNext = i < _count && offset + size == _offsets[i];
        Used -= size;

        if (mergePrev && mergeNext)
        {
            _sizes[i - 1] += size + _sizes[i];
            RemoveAt(i);
            return;
        }

        if (mergePrev)
        {
            _sizes[i - 1] += size;
            return;
        }

        if (mergeNext)
        {
            _offsets[i] = offset;
            _sizes[i] += size;
            return;
        }

        if (_count == _offsets.Length)
            throw new InvalidOperationException("RangeAllocator block table full.");

        Array.Copy(_offsets, i, _offsets, i + 1, _count - i);
        Array.Copy(_sizes, i, _sizes, i + 1, _count - i);
        _offsets[i] = offset;
        _sizes[i] = size;
        _count++;
    }

    private int LowerBound(int offset)
    {
        int lo = 0, hi = _count;

        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;

            if (_offsets[mid] < offset)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private void RemoveAt(int i)
    {
        _count--;
        Array.Copy(_offsets, i + 1, _offsets, i, _count - i);
        Array.Copy(_sizes, i + 1, _sizes, i, _count - i);
    }
}
