namespace BrutalGame.Engine.Time;

/// <summary>
/// Fixed-step simulation clock. Sim time is derived from tick index so it stays deterministic.
/// </summary>
public struct FixedClock
{
    public const int MaxTicksPerFrame = 8;
    public const double MaxFrameSeconds = 0.25;

    public readonly int TicksPerSecond;
    public readonly double TickSeconds;
    public long TickIndex;
    public double Alpha;
    private double _accumulator;

    public FixedClock(int ticksPerSecond)
    {
        TicksPerSecond = ticksPerSecond;
        TickSeconds = 1.0 / ticksPerSecond;
    }

    public readonly double SimTime => TickIndex * TickSeconds;

    /// <summary>
    /// Adds real frame time and returns how many ticks to run. Excess time past the cap is dropped.
    /// </summary>
    public int Advance(double frameSeconds)
    {
        _accumulator += Math.Min(frameSeconds, MaxFrameSeconds);
        var ticks = (int)(_accumulator / TickSeconds);

        if (ticks > MaxTicksPerFrame)
        {
            ticks = MaxTicksPerFrame;
            _accumulator = ticks * TickSeconds;
        }

        _accumulator -= ticks * TickSeconds;
        Alpha = _accumulator / TickSeconds;
        return ticks;
    }
}
