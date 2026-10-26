using System.Numerics;
using Brutal.Logging;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>CPU time (ms) of one frame's main-thread sections, measured by <see cref="GameApp"/>.</summary>
public struct FrameSections
{
    public float Ticks;
    public float Terrain;
    public float Water;
    public float Coastal;
    public float Ui;
    public float Uploads;
    public float Prepare;
    public float Record;
    public float Present;
    public float GpuWait;

    /// <summary>Main-thread work, everything but waiting on the GPU. Record spans all command recording, Uploads and Prepare included.</summary>
    public readonly float Work => Ticks + Terrain + Water + Coastal + Ui + Record + Present;
}

/// <summary>
/// --fly-test: once the world has loaded, flies the camera round the main island's coast at <see cref="Speed"/>, which
/// streams terrain and re-centres the bathymetry and coastal maps as it goes. It records each frame's main-thread
/// sections, then logs percentiles and the slowest frames with what ran in them, and closes the window.
/// </summary>
public sealed class FlyTest(FlyCamera camera, TerrainWorld world, BathymetryTexture map, CoastalSystem coastal, RainCover cover)
{
    private static readonly LogCategory Log = LogCategory.Make("FlyTest");
    private const float Radius = 270f;
    private const float Height = 22f;
    private const float Speed = 40f;
    private const double Seconds = 30.0;
    private const int MaxFrames = 1 << 16;
    private const int Worst = 10;

    private readonly FrameSections[] _frames = new FrameSections[MaxFrames];
    private readonly (float Select, float Uploads, float Stream, float Bathymetry)[] _terrain = new (float, float, float, float)[MaxFrames];
    private readonly (int Cells, float Reach, float Total)[] _map = new (int, float, float)[MaxFrames];
    private readonly float[] _retire = new float[MaxFrames];
    private readonly int[] _events = new int[MaxFrames];
    private readonly float[] _coverMs = new float[MaxFrames];
    private int _coverCount;
    private int _coverBuilds;
    private int _count;
    private double _start = -1.0;
    private int _selects;
    private int _rebuilds;
    private int _uploads;
    private int _builds;
    private int _collections;

    public bool Done { get; private set; }

    /// <summary>Before the frame's work: moves the camera along the path.</summary>
    public void Fly(double time, bool ready)
    {
        if (Done)
            return;

        if (_start < 0.0)
        {
            if (!ready)
                return;

            _start = time;
            (_selects, _rebuilds, _uploads, _builds) = (world.Selects, world.Bathymetry.Version, map.Uploads, coastal.Builds);
            _collections = GC.CollectionCount(0);
            _coverBuilds = cover.Builds;
        }

        var angle = (float)((time - _start) * Speed / Radius);
        var (sin, cos) = MathF.SinCos(angle);
        camera.Position = new Vector3(Radius * cos, Height, Radius * sin);
        camera.Yaw = MathF.Atan2(cos, -sin) - 0.35f;
        camera.Pitch = -0.25f;
    }

    /// <summary>After the frame: records its sections and which costly steps ran.</summary>
    public void Record(double time, in FrameSections sections)
    {
        if (Done || _start < 0.0)
            return;

        if (time - _start > Seconds || _count == MaxFrames)
        {
            Report();
            Done = true;
            return;
        }

        var events = 0;
        events |= world.Selects != _selects ? 1 : 0;
        events |= world.Bathymetry.Version != _rebuilds ? (world.LastRebuildFull ? 4 : 2) : 0;
        events |= map.Uploads != _uploads ? (map.LastUploadCells >= 512 * 512 ? 16 : 8) : 0;
        events |= coastal.Builds != _builds ? 32 : 0;
        events |= GC.CollectionCount(0) != _collections ? 64 : 0;
        (_selects, _rebuilds, _uploads, _builds) = (world.Selects, world.Bathymetry.Version, map.Uploads, coastal.Builds);
        _collections = GC.CollectionCount(0);
        _terrain[_count] = world.LastUpdate;
        _retire[_count] = world.LastRetireMs;
        _map[_count] = (events & 24) != 0 ? (map.LastUploadCells, map.LastReachMs, map.LastUploadMs) : default;

        _frames[_count] = sections;
        _events[_count] = events;
        _count++;

        if (cover.Builds != _coverBuilds)
            _coverMs[_coverCount++] = cover.LastBuildMs;

        _coverBuilds = cover.Builds;
    }

    private void Report()
    {
        var work = new float[_count];
        for (var i = 0; i < _count; i++)
            work[i] = _frames[i].Work;

        var sorted = (float[])work.Clone();
        Array.Sort(sorted);
        Log.Info($"fly test: {_count} frames over {Seconds:F0} s at {Speed:F0} m/s; main-thread work p50 {Pick(sorted, 0.5):F2} ms, p95 {Pick(sorted, 0.95):F2}, p99 {Pick(sorted, 0.99):F2}, max {sorted[^1]:F2}");

        // Rain/wind cover rebuilds (tops, fetch, shelter), once per 2 m the camera moves.
        var coverMs = _coverMs.AsSpan(0, _coverCount).ToArray();
        var coverTotal = 0f;
        foreach (var ms in coverMs)
            coverTotal += ms;

        Array.Sort(coverMs);
        if (coverMs.Length > 0)
            Log.Info($"cover rebuilds: {coverMs.Length} in {_count} frames, p50 {Pick(coverMs, 0.5):F3} ms, p95 {Pick(coverMs, 0.95):F3}, max {coverMs[^1]:F3}, total {coverTotal:F1} ms");

        var order = new int[_count];
        for (var i = 0; i < _count; i++)
            order[i] = i;

        Array.Sort(work, order);
        Log.Info("slowest frames (ms): work = ticks + terrain + water + coastal + ui + record (uploads, prepare) + present | gpu wait | events");

        for (var k = 0; k < Math.Min(Worst, _count); k++)
        {
            var i = order[_count - 1 - k];
            var f = _frames[i];
            Log.Info($"  #{i,5} {f.Work,6:F2} = {f.Ticks:F2} + {f.Terrain:F2} + {f.Water:F2} + {f.Coastal:F2} + {f.Ui:F2} + {f.Record:F2} ({f.Uploads:F2}, {f.Prepare:F2}) + {f.Present:F2} | {f.GpuWait:F2} | {Describe(_events[i])} | terrain: select {_terrain[i].Select:F2}, uploads {_terrain[i].Uploads:F2}, stream {_terrain[i].Stream:F2} (retire {_retire[i]:F2}), bathymetry {_terrain[i].Bathymetry:F2} | map: {_map[i].Cells} cells, reach {_map[i].Reach:F2}, total {_map[i].Total:F2}");
        }
    }

    private static float Pick(float[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    private static string Describe(int events) =>
        events == 0 ? "-" : string.Join(' ',
            (events & 1) != 0 ? "select" : "",
            (events & 4) != 0 ? "rebuild-full" : (events & 2) != 0 ? "rebuild" : "",
            (events & 16) != 0 ? "upload-full" : (events & 8) != 0 ? "upload" : "",
            (events & 32) != 0 ? "coastal-build" : "",
            (events & 64) != 0 ? "gc" : "").Trim();
}
