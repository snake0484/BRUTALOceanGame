using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Brutal.Concurrency.Jobs;
using Brutal.VulkanApi;
using BrutalGame.Engine.Time;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Where rain and wind can't reach, one host-visible buffer per frame in flight (rain_cover.glsl). Per 2 m column in a
/// window around the camera, the highest solid surface: everything that draws rain or rain on water hides rain below it,
/// so nothing falls, splashes or ripples under a roof, an overhang or in a cave; and the shelter of cover, under which
/// wind waves die out (a sea cave lies calm). Over the whole bathymetry grid, at 8 m, the fetch: the open water the wind
/// has blown over, which limits how big a wind sea it raises (a pond stays rippled). It is measured everywhere the
/// ocean draws its cascades, so far water is judged as near water is.
/// </summary>
public sealed unsafe class RainCover : IDisposable
{
    public const int Side = 64;

    /// <summary>Fetch map cells per side over the bathymetry grid (rain_cover.glsl FETCH_SIDE).</summary>
    public const int FetchSide = 128;

    /// <summary>Bathymetry columns per fetch map cell side: one Vector128 of tops per row of a cell (8 m at 2 m columns).</summary>
    private const int FetchStride = 4;

    public const int Bytes = (HeaderFloats + FetchSide * FetchSide + ColumnFloats * Side * Side) * sizeof(float);

    /// <summary>Metres under cover by which wind waves have died out.</summary>
    public const float ShelterDistance = 10f;

    /// <summary>
    /// Longest fetch (m) measured: longer counts as open sea. The ocean's wind sea is full grown by here (rain_cover.glsl
    /// FETCH_REACH), so the cut never shows.
    /// </summary>
    public const float FetchReach = 400f;

    /// <summary>Wind directions the fetch is measured for; a turning wind re-measures only every 2π / this.</summary>
    private const int Headings = 64;

    /// <summary>
    /// Effective fetch (after Saville 1962): the fetch along <see cref="Radials"/> directions spread evenly over
    /// ±<see cref="RadialSpread"/> of the wind, weighted by cos², the wind sea's spreading. Normalised by the weights, so
    /// open sea keeps its full fetch. A channel open at its ends takes the chop that runs in along it even when the wind
    /// crosses it; a lone rock shelters only the radials it blocks.
    /// </summary>
    private const int Radials = 9;
    private const float RadialSpread = MathF.PI / 4f;

    /// <summary>
    /// The window moves in steps of this many cells, so a moving camera rebuilds it a quarter as often (a rebuild is
    /// about 0.08 ms); the camera always has 56 m or more of it on every side.
    /// </summary>
    private const int WindowStep = 4;

    /// <summary>A column with nothing known over it (rain_cover.glsl NO_TOP).</summary>
    private const float None = -1.0e6f;
    private const int HeaderFloats = 8;
    private const int ColumnFloats = 3;
    private const float Far = 1.0e6f;
    private const float Diagonal = 1.41421356f;

    private readonly GpuBuffer[] _frames = new GpuBuffer[Renderer.FramesInFlight];

    // The window is rebuilt only when it moves, the tops change, the wind turns a heading or a new fetch map lands, and
    // copied to a frame's buffer only when that holds an older one.
    private readonly float[] _columns = new float[ColumnFloats * Side * Side];
    private readonly bool[] _open = new bool[Side * Side];
    private readonly float[] _fetch = new float[Side * Side];
    private readonly float[] _distance = new float[Side * Side];
    private readonly (Vector2 Corner, int Version, int Heading)[] _written = new (Vector2, int, int)[Renderer.FramesInFlight];
    private (Vector2 Corner, int Version, int Heading) _built = (new Vector2(float.NaN), 0, 0);
    private float _cell = 1f;

    // The fetch map over the bathymetry grid. Its openness is re-read on the main thread when the tops change (a vector
    // compare per row of a cell); only when that changed it, the grid moved or the wind turned a heading are the radials
    // swept again, on the job system, and the result lands a frame or more later. Streaming mostly refines seabed that
    // stays under water, so it rarely re-sweeps. Copied to a frame's buffer when that holds an older revision.
    private readonly JobScheduler _scheduler;
    private readonly FetchRadialJob[] _radials = new FetchRadialJob[Radials];
    private readonly bool[] _fetchOpen = new bool[FetchSide * FetchSide];
    private readonly float[] _fetchMap = new float[FetchSide * FetchSide];
    private readonly int[] _fetchWritten = new int[Renderer.FramesInFlight];
    private (Vector2 Origin, int Version, int Heading) _fetchRead = (new Vector2(float.NaN), 0, 0);
    private (Vector2 Origin, int Heading) _swept = (new Vector2(float.NaN), -1);
    private (Vector2 Origin, int Heading) _sweeping = (new Vector2(float.NaN), -1);
    private bool _busy;
    private int _fetchRevision;
    private float _fetchCell = 1f;

    public RainCover(VulkanContext context)
    {
        // Background work nothing waits on: few workers, below normal priority, so it never starves per-tick jobs.
        _scheduler = new JobScheduler((uint)Math.Max(1, Environment.ProcessorCount / 4), "Fetch", ThreadPriority.BelowNormal);

        for (var r = 0; r < Radials; r++)
            _radials[r] = new FetchRadialJob(_fetchOpen, -RadialSpread + 2f * RadialSpread * r / (Radials - 1));

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            _frames[i] = new GpuBuffer(context, Bytes, VkBufferUsageFlags.StorageBufferBit, true);
            new Span<float>(_frames[i].Mapped, Bytes / sizeof(float)).Fill(None);
            ((float*)_frames[i].Mapped)[7] = 0f; // no fetch map yet: open sea everywhere
            _written[i] = _built;
        }
    }

    public VkBuffer Buffer(int frameIndex) => _frames[frameIndex].Handle;

    /// <summary>Main-thread time of each window rebuild or fetch map step (ms), for the profiler.</summary>
    public readonly TimingRing BuildMs = new(120);

    public int Builds { get; private set; }

    public float LastBuildMs { get; private set; }

    /// <summary>Fetch maps swept on the job system so far.</summary>
    public int FetchSweeps => _fetchRevision;

    /// <summary>Debug: wind exposure and fetch (m) of the column at world xz in the last window built; null outside it.</summary>
    public (float Exposure, float Fetch)? WindAt(Vector2 xz)
    {
        var i = (int)MathF.Floor((xz.X - _built.Corner.X) / _cell);
        var j = (int)MathF.Floor((xz.Y - _built.Corner.Y) / _cell);
        if ((uint)i >= Side || (uint)j >= Side)
            return null;

        var c = ColumnFloats * (i + j * Side);
        return (_columns[c + 1], _columns[c + 2]);
    }

    /// <summary>
    /// This frame's cover: (window corner x, corner z, cell, side), (fetch map corner x, corner z, cell, side), the fetch
    /// map row by row, then (top, wind exposure, fetch) per window column, row by row. Every frame, rain or not.
    /// </summary>
    /// <param name="tops">Highest surface per column (NaN where unknown), row-major, <paramref name="resolution"/> per side from <paramref name="origin"/>.</param>
    /// <param name="version">Changes whenever <paramref name="tops"/> do.</param>
    /// <param name="windDirection">Where the wind blows toward (radians, 0 = +X, π/2 = +Z).</param>
    public void Write(int frameIndex, Vector3 camera, ReadOnlySpan<float> tops, Vector2 origin, int resolution, float cell, float seaLevel, int version, float windDirection)
    {
        if (resolution != FetchSide * FetchStride)
            throw new ArgumentException("The fetch map needs a grid of FetchSide * FetchStride columns.", nameof(resolution));

        var cx = Snap((int)MathF.Floor((camera.X - origin.X) / cell)) - Side / 2;
        var cz = Snap((int)MathF.Floor((camera.Z - origin.Y) / cell)) - Side / 2;
        var heading = ((int)MathF.Round(windDirection / MathF.Tau * Headings) % Headings + Headings) % Headings;
        var key = (Corner: new Vector2(origin.X + cx * cell, origin.Y + cz * cell), Version: version, Heading: heading);
        var start = Stopwatch.GetTimestamp();
        var rebuilt = false;

        // A finished sweep lands: the window's open columns sample the map, so it is rebuilt.
        if (_busy && !_scheduler.IsExecuting)
        {
            CombineRadials();
            _swept = _sweeping;
            _busy = false;
            _fetchRevision++;
            _built = (new Vector2(float.NaN), 0, 0);
            rebuilt = true;
        }

        // The jobs read the openness, so it is only re-read between sweeps.
        if (!_busy && _fetchRead != (origin, version, heading))
        {
            var changed = ReadOpenness(tops, seaLevel);
            _fetchRead = (origin, version, heading);
            if (changed || _swept != (origin, heading))
                StartSweep(origin, cell, heading);

            rebuilt = true;
        }

        if (_built != key)
        {
            Build(tops, origin, cx, cz, resolution, cell, seaLevel);
            _built = key;
            _cell = cell;
            rebuilt = true;
        }

        if (rebuilt)
        {
            LastBuildMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            BuildMs.Record(LastBuildMs);
            Builds++;
        }

        var block = (float*)_frames[frameIndex].Mapped;
        if (_fetchWritten[frameIndex] != _fetchRevision)
        {
            block[4] = _swept.Origin.X;
            block[5] = _swept.Origin.Y;
            block[6] = _fetchCell;
            block[7] = FetchSide;
            _fetchMap.CopyTo(new Span<float>(block + HeaderFloats, FetchSide * FetchSide));
            _fetchWritten[frameIndex] = _fetchRevision;
        }

        if (_written[frameIndex] == _built)
            return;

        block[0] = key.Corner.X;
        block[1] = key.Corner.Y;
        block[2] = cell;
        block[3] = Side;
        _columns.CopyTo(new Span<float>(block + HeaderFloats + FetchSide * FetchSide, ColumnFloats * Side * Side));
        _written[frameIndex] = _built;
    }

    /// <summary>
    /// Re-reads which fetch map cells (<see cref="FetchStride"/>² columns each) are open, a cell's row of columns at a
    /// time: open when every column in it is (a thin bar or a berm still blocks the wind). A column is blocked when its
    /// top is at or over sea level; NaN, unknown, compares false and stays open. True if any cell changed.
    /// </summary>
    private bool ReadOpenness(ReadOnlySpan<float> tops, float seaLevel)
    {
        var rows = MemoryMarshal.Cast<float, Vector128<float>>(tops);
        var level = Vector128.Create(seaLevel);
        var changed = false;

        for (var j = 0; j < FetchSide; j++)
        {
            for (var i = 0; i < FetchSide; i++)
            {
                var blocked = Vector128<float>.Zero;
                for (var z = j * FetchStride; z < (j + 1) * FetchStride; z++)
                    blocked |= Vector128.GreaterThanOrEqual(rows[i + z * FetchSide], level);

                var c = i + j * FetchSide;
                var open = blocked.ExtractMostSignificantBits() == 0;
                changed |= open != _fetchOpen[c];
                _fetchOpen[c] = open;
            }
        }

        return changed;
    }

    /// <summary>Queues one job per radial of the effective fetch (<see cref="Radials"/>) for the map at <paramref name="origin"/>.</summary>
    private void StartSweep(Vector2 origin, float cell, int heading)
    {
        var angle = heading * MathF.Tau / Headings;
        var wind = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        _fetchCell = cell * FetchStride;

        foreach (var job in _radials)
        {
            job.Prepare(wind, _fetchCell);
            _scheduler.Queue(job, 1);
        }

        _scheduler.ExecuteJobs();
        _sweeping = (origin, heading);
        _busy = true;
    }

    /// <summary>
    /// The effective fetch: the radials' fetches weighted by cos² of their angle off the wind, over the weights. A
    /// blocked cell then takes the largest fetch of the open cells around it, so sampling between cell centres never drags
    /// the water beside a rock, a roof or a beach toward 0 (the sweeps start from 0 there regardless).
    /// </summary>
    private void CombineRadials()
    {
        var map = MemoryMarshal.Cast<float, Vector<float>>(_fetchMap.AsSpan());
        map.Clear();
        var total = 0f;
        foreach (var job in _radials)
            total += job.Weight;

        foreach (var job in _radials)
        {
            var weight = new Vector<float>(job.Weight / total);
            var radial = MemoryMarshal.Cast<float, Vector<float>>(job.Fetch.AsSpan());
            for (var i = 0; i < map.Length; i++)
                map[i] += weight * radial[i];
        }

        for (var j = 0; j < FetchSide; j++)
        {
            for (var i = 0; i < FetchSide; i++)
            {
                if (_fetchOpen[i + j * FetchSide])
                    continue;

                var beside = 0f;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (x, z) = (i + dx, j + dz);
                        if ((uint)x < FetchSide && (uint)z < FetchSide && _fetchOpen[x + z * FetchSide])
                            beside = MathF.Max(beside, _fetchMap[x + z * FetchSide]);
                    }
                }

                _fetchMap[i + j * FetchSide] = beside;
            }
        }
    }

    /// <summary>
    /// One radial's fetch over the map: the distance upwind over open cells to anything not open, by one upwind sweep of
    /// u · ∇F = 1; leaving the grid counts as open sea.
    /// </summary>
    private static void Sweep(bool[] open, Vector2 wind, float cellSize, float[] fetch)
    {
        var sx = wind.X >= 0f ? 1 : -1;
        var sz = wind.Y >= 0f ? 1 : -1;
        var ax = MathF.Abs(wind.X);
        var az = MathF.Abs(wind.Y);
        var inverse = 1f / (ax + az);

        for (var n = 0; n < FetchSide; n++)
        {
            var j = sz > 0 ? n : FetchSide - 1 - n;

            for (var m = 0; m < FetchSide; m++)
            {
                var i = sx > 0 ? m : FetchSide - 1 - m;
                var c = i + j * FetchSide;
                if (!open[c])
                {
                    fetch[c] = 0f;
                    continue;
                }

                var fx = (uint)(i - sx) < FetchSide ? fetch[c - sx] : FetchReach;
                var fz = (uint)(j - sz) < FetchSide ? fetch[c - sz * FetchSide] : FetchReach;
                fetch[c] = MathF.Min((cellSize + ax * fx + az * fz) * inverse, FetchReach);
            }
        }
    }

    /// <summary>
    /// The fetch map at world xz, bilinear between cell centres and clamped to the outer ones, as rain_cover.glsl
    /// gridFetch reads it; open sea outside the map, or before the first one has landed.
    /// </summary>
    private float FetchAt(Vector2 xz)
    {
        if (_fetchRevision == 0)
            return FetchReach;

        var f = (xz - _swept.Origin) / _fetchCell;
        if (f.X < 0f || f.Y < 0f || f.X >= FetchSide || f.Y >= FetchSide)
            return FetchReach;

        var fx = Math.Clamp(f.X - 0.5f, 0f, FetchSide - 1);
        var fz = Math.Clamp(f.Y - 0.5f, 0f, FetchSide - 1);
        var i = Math.Min((int)fx, FetchSide - 2);
        var j = Math.Min((int)fz, FetchSide - 2);
        var tx = fx - i;
        var tz = fz - j;
        var c = i + j * FetchSide;
        return float.Lerp(float.Lerp(_fetchMap[c], _fetchMap[c + 1], tx), float.Lerp(_fetchMap[c + FetchSide], _fetchMap[c + FetchSide + 1], tx), tz);
    }

    /// <summary>
    /// The window's tops; the fetch of water open to the sky, from the fetch map; and under cover, the fetch of the
    /// nearest open water with an exposure falling to 0 at <see cref="ShelterDistance"/> from it (two-pass chamfer distance).
    /// </summary>
    private void Build(ReadOnlySpan<float> tops, Vector2 origin, int cx, int cz, int resolution, float cell, float seaLevel)
    {
        for (var j = 0; j < Side; j++)
        {
            for (var i = 0; i < Side; i++)
            {
                var top = TopAt(tops, cx + i, cz + j, resolution);
                var c = i + j * Side;
                _columns[ColumnFloats * c] = float.IsNaN(top) ? None : top;
                _open[c] = Open(top, seaLevel);
                _distance[c] = _open[c] ? 0f : Far;
                _fetch[c] = _open[c] ? FetchAt(origin + new Vector2(cx + i + 0.5f, cz + j + 0.5f) * cell) : 0f;
            }
        }

        for (var j = 0; j < Side; j++)
            for (var i = 0; i < Side; i++)
                Relax(i, j, -1);

        for (var j = Side - 1; j >= 0; j--)
            for (var i = Side - 1; i >= 0; i--)
                Relax(i, j, 1);

        for (var c = 0; c < Side * Side; c++)
        {
            _columns[ColumnFloats * c + 1] = 1f - MathF.Min(_distance[c] * cell / ShelterDistance, 1f);
            _columns[ColumnFloats * c + 2] = _fetch[c];
        }
    }

    /// <summary>Water at sea level there is open to the sky: the column's highest surface is under it, or unknown (open sea).</summary>
    private static bool Open(float top, float seaLevel) => float.IsNaN(top) || top < seaLevel;

    private static int Snap(int c) => c - ((c % WindowStep) + WindowStep) % WindowStep;

    private static float TopAt(ReadOnlySpan<float> tops, int x, int z, int resolution) =>
        (uint)x < (uint)resolution && (uint)z < (uint)resolution ? tops[x + z * resolution] : float.NaN;

    /// <summary>Chamfer step on side <paramref name="s"/> (−1 forward, +1 back): a column under cover takes the fetch of the nearest open one.</summary>
    private void Relax(int i, int j, int s)
    {
        var c = i + j * Side;
        Nearer(c, i + s, j, 1f);
        Nearer(c, i, j + s, 1f);
        Nearer(c, i + s, j + s, Diagonal);
        Nearer(c, i - s, j + s, Diagonal);
    }

    private void Nearer(int c, int i, int j, float step)
    {
        if ((uint)i >= Side || (uint)j >= Side)
            return;

        var n = i + j * Side;
        if (_distance[n] + step >= _distance[c])
            return;

        _distance[c] = _distance[n] + step;
        _fetch[c] = _fetch[n];
    }

    public void Dispose()
    {
        _scheduler.AbortJobs(waitForCompletion: true);

        foreach (var frame in _frames)
            frame.Dispose();
    }

    /// <summary>One radial of the effective fetch, <paramref name="angle"/> off the wind, swept on the job system.</summary>
    private sealed class FetchRadialJob(bool[] open, float angle) : IJob
    {
        public readonly float[] Fetch = new float[FetchSide * FetchSide];

        /// <summary>cos² of the angle off the wind: the wind sea's spreading.</summary>
        public readonly float Weight = MathF.Cos(angle) * MathF.Cos(angle);

        private Vector2 _direction;
        private float _cellSize;

        public void Prepare(Vector2 wind, float cellSize)
        {
            var (sin, cos) = MathF.SinCos(angle);
            _direction = new Vector2(wind.X * cos - wind.Y * sin, wind.X * sin + wind.Y * cos);
            _cellSize = cellSize;
        }

        public void Run() => Sweep(open, _direction, _cellSize, Fetch);
    }
}
