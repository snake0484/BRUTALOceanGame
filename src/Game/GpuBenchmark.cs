using Brutal.Logging;
using BrutalGame.Engine.Rendering;

namespace BrutalGame;

/// <summary>
/// --bench: vsync off, so the GPU runs flat out at stable clocks. After the world settles and a warmup, alternates a
/// feature (layer B coastal maps) on and off in short blocks, so clock drift hits both sides equally, then logs the
/// median and mean of every GPU scope per side and closes the window.
/// </summary>
public sealed class GpuBenchmark
{
    private static readonly LogCategory Log = LogCategory.Make("Bench");
    private const double WarmupSeconds = 3.0;
    private const double BlockSeconds = 2.0;
    private const double SettleSeconds = 0.25;
    private const int MaxSamples = 1 << 16;

    private readonly GpuTimer _timer;
    private readonly int _blocks;
    private readonly float[][,] _samples = new float[2][,];
    private readonly int[] _counts = new int[2];
    private double _start = -1.0;
    private double _blockStart;
    private int _block = -1;

    /// <param name="seconds">Measured time, split into alternating blocks (at least 2).</param>
    public GpuBenchmark(GpuTimer timer, double seconds)
    {
        _timer = timer;
        _blocks = Math.Max(2, (int)Math.Round(seconds / BlockSeconds));

        for (var side = 0; side < 2; side++)
            _samples[side] = new float[GpuTimer.MaxScopes, MaxSamples];
    }

    public bool Done { get; private set; }

    /// <summary>Which side the current block measures: true = feature on.</summary>
    public bool FeatureOn => _block < 0 || (_block & 1) == 0;

    /// <summary>Once per frame, after the GPU timer has collected the previous results.</summary>
    /// <param name="ready">The world has settled (terrain streamed, coastal maps built).</param>
    public void Update(double time, bool ready)
    {
        if (Done)
            return;

        if (_start < 0.0)
        {
            if (ready)
                _start = time;

            return;
        }

        if (_block < 0)
        {
            if (time - _start < WarmupSeconds)
                return;

            _block = 0;
            _blockStart = time;
            return;
        }

        if (time - _blockStart >= BlockSeconds)
        {
            _block++;
            _blockStart = time;

            if (_block == _blocks)
            {
                Report();
                Done = true;
            }

            return;
        }

        if (time - _blockStart < SettleSeconds)
            return;

        var side = FeatureOn ? 1 : 0;
        var n = _counts[side];
        if (n == MaxSamples)
            return;

        for (var s = 0; s < _timer.ScopeCount; s++)
            _samples[side][s, n] = _timer.Ring(s).Last;

        _counts[side] = n + 1;
    }

    private void Report()
    {
        Log.Info($"GPU benchmark: {_blocks} blocks of {BlockSeconds:F1} s alternating, samples on {_counts[1]} off {_counts[0]}");
        Log.Info($"{"scope",-12} {"on median",10} {"off median",10} {"delta",8} {"on mean",9} {"off mean",9}");

        var scratch = new float[MaxSamples];
        for (var s = 0; s < _timer.ScopeCount; s++)
        {
            var on = Stats(1, s, scratch);
            var off = Stats(0, s, scratch);
            Log.Info($"{_timer.Name(s),-12} {on.Median,10:F4} {off.Median,10:F4} {on.Median - off.Median,8:F4} {on.Mean,9:F4} {off.Mean,9:F4}");
        }
    }

    private (float Median, float Mean) Stats(int side, int scope, float[] scratch)
    {
        var n = _counts[side];
        if (n == 0)
            return (0f, 0f);

        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            scratch[i] = _samples[side][scope, i];
            sum += scratch[i];
        }

        Array.Sort(scratch, 0, n);
        return (scratch[n / 2], (float)(sum / n));
    }
}
