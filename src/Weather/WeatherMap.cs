using System.Diagnostics;
using System.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// The weather around the camera: <see cref="WeatherField"/> baked into a coarse grid every <see cref="Interval"/>
/// seconds, sampled by lerping the snapshots either side of now. The snapshot after next bakes a row per tick while the
/// current pair is in use, so most ticks do nothing. Origins snap to the grid spacing, so any snapshot covering a point
/// gives it the same value; points off the grid fall back to the analytic field.
/// Tick on the main thread before sampling; <see cref="Sample(float, float)"/> is read-only between ticks and safe from jobs.
/// </summary>
public sealed class WeatherMap
{
    public const int Size = 32;
    public const float Spacing = 1000f;
    public const double Interval = 5.0;
    public const int MaxSystems = 64;

    private const int RowsPerTick = 1;

    private readonly PressureSystem[] _systems = new PressureSystem[MaxSystems];
    private Snapshot _from = new();
    private Snapshot _to = new();
    private Snapshot _baking = new();
    private Snapshot _target;
    private int _systemCount;
    private int _bakeRow = Size;
    private long _interval = long.MinValue;

    public readonly WeatherField Field;

    public WeatherMap(WeatherField field)
    {
        Field = field;
        _target = _baking;
    }

    /// <summary>Weather time of the last tick (s).</summary>
    public double Time { get; private set; }

    /// <summary>Systems gathered for the latest bake, placed at its time (up to two intervals ahead).</summary>
    public ReadOnlySpan<PressureSystem> Systems => _systems.AsSpan(0, _systemCount);

    public float LastRebuildMs { get; private set; }

    public int Rebuilds { get; private set; }

    /// <summary>Advances to weather time <paramref name="time"/> (s); snapshots started from now on centre on <paramref name="centre"/>.</summary>
    public void Tick(double time, Vector2 centre)
    {
        Time = time;
        var interval = (long)Math.Floor(time / Interval);

        if (interval == _interval + 1)
            Advance(centre);
        else if (interval != _interval || !Covers(_to, centre))
            Rebuild(interval, centre);

        BakeRows(RowsPerTick);
    }

    /// <summary>Drops the snapshots so the next tick rebakes them. Call after changing <see cref="WeatherField.Climate"/>.</summary>
    public void Invalidate() => _interval = long.MinValue;

    public WeatherSample Sample(float x, float z)
    {
        var t = (float)Math.Clamp((Time - _from.Time) / Interval, 0.0, 1.0);
        return WeatherSample.Lerp(Sample(_from, x, z), Sample(_to, x, z), t);
    }

    private WeatherSample Sample(Snapshot snapshot, float x, float z)
    {
        var u = (x - snapshot.Origin.X) / Spacing;
        var v = (z - snapshot.Origin.Y) / Spacing;
        var i = (int)MathF.Floor(u);
        var j = (int)MathF.Floor(v);

        if (i < 0 || j < 0 || i >= Size - 1 || j >= Size - 1)
            return Field.Sample(x, z, snapshot.Time);

        var cells = snapshot.Cells;
        var c = j * Size + i;
        var near = WeatherSample.Lerp(cells[c], cells[c + 1], u - i);
        var far = WeatherSample.Lerp(cells[c + Size], cells[c + Size + 1], u - i);
        return WeatherSample.Lerp(near, far, v - j);
    }

    /// <summary>Into the next interval: the baked snapshot becomes the far end and the one after starts baking.</summary>
    private void Advance(Vector2 centre)
    {
        BakeRows(Size);
        (_from, _to, _baking) = (_to, _baking, _from);
        _interval++;
        StartBake(_baking, (_interval + 2) * Interval, centre);
    }

    private void Rebuild(long interval, Vector2 centre)
    {
        var start = Stopwatch.GetTimestamp();
        _interval = interval;

        StartBake(_from, interval * Interval, centre);
        BakeRows(Size);
        StartBake(_to, (interval + 1) * Interval, centre);
        BakeRows(Size);
        StartBake(_baking, (interval + 2) * Interval, centre);

        LastRebuildMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Rebuilds++;
    }

    private void StartBake(Snapshot snapshot, double time, Vector2 centre)
    {
        const float half = Size / 2 * Spacing;
        snapshot.Origin = new Vector2(MathF.Floor(centre.X / Spacing) * Spacing - half, MathF.Floor(centre.Y / Spacing) * Spacing - half);
        snapshot.Time = time;
        _systemCount = Field.Gather(snapshot.Origin, snapshot.Origin + new Vector2((Size - 1) * Spacing), time, _systems);
        _target = snapshot;
        _bakeRow = 0;
    }

    private void BakeRows(int rows)
    {
        var end = Math.Min(Size, _bakeRow + rows);
        var systems = Systems;

        for (; _bakeRow < end; _bakeRow++)
        {
            var z = _target.Origin.Y + _bakeRow * Spacing;
            var row = _target.Cells.AsSpan(_bakeRow * Size, Size);

            for (var i = 0; i < Size; i++)
                row[i] = Field.Evaluate(new Vector2(_target.Origin.X + i * Spacing, z), systems);
        }
    }

    /// <summary>True while <paramref name="p"/> is in the snapshot's middle half, where a camera stays between re-centres.</summary>
    private static bool Covers(Snapshot snapshot, Vector2 p)
    {
        const float inner = Size / 4 * Spacing;
        const float outer = Size * 3 / 4 * Spacing;
        var local = p - snapshot.Origin;
        return local.X >= inner && local.Y >= inner && local.X <= outer && local.Y <= outer;
    }

    private sealed class Snapshot
    {
        public readonly WeatherSample[] Cells = new WeatherSample[Size * Size];
        public Vector2 Origin;
        public double Time;
    }
}
