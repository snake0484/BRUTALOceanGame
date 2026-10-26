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
    public int TicksThisFrame;
    public long TickIndex;
    public double SimTime;
}
