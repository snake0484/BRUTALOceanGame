namespace BrutalGame.Engine.Time;

/// <summary>
/// Per-frame CPU timings shared by the loop, renderer and profiler panel.
/// </summary>
public sealed class FrameStats(int samples)
{
    public readonly TimingRing Frame = new(samples);
    public readonly TimingRing Tick = new(samples);
    public readonly TimingRing Ui = new(samples);
    public readonly TimingRing GpuWait = new(samples);
    public readonly TimingRing Record = new(samples);
    public readonly TimingRing Present = new(samples);

    /// <summary>Managed bytes the main thread allocated per frame (a ring of bytes, not ms).</summary>
    public readonly TimingRing AllocBytes = new(samples);
    public int TicksThisFrame;
    public long TickIndex;
    public double SimTime;

    /// <summary>Managed bytes allocated last frame: by the main thread, and by every thread (approximate).</summary>
    public long AllocatedMain;
    public long AllocatedAll;

    private long _mainBytes = -1;
    private long _allBytes;

    /// <summary>Call once per frame on the main thread: books the managed bytes allocated since the last call.</summary>
    public void RecordAllocations()
    {
        var main = GC.GetAllocatedBytesForCurrentThread();
        var all = GC.GetTotalAllocatedBytes();

        if (_mainBytes >= 0)
        {
            AllocatedMain = main - _mainBytes;
            AllocatedAll = all - _allBytes;
            AllocBytes.Record(AllocatedMain);
        }

        _mainBytes = main;
        _allBytes = all;
    }
}
