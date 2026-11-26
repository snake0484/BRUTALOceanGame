using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// <see cref="SphereWeatherField"/> baked onto a planet-fixed lattice: an equal-angle cube sphere at ~1 km, cut into
/// 16 × 16-cell tiles, each baked at <see cref="IntervalSeconds"/> snapshot times. A sample is bilinear in a tile and a lerp
/// between the snapshots either side of its tick, so its value depends only on the point and the tick, never on who asked:
/// the server, a headless sim and every client agree. Anchors (the camera, the sim's sea point, ships) say which tiles to
/// keep; tiles within <see cref="CoverRadius"/> are baked synchronously if missing, those within
/// <see cref="PrefetchRadius"/> a few rows per tick ahead of need. Tick on one thread; sampling is read-only between
/// ticks and safe from jobs. Off the kept tiles <see cref="Sample"/> falls back to the analytic field.
/// </summary>
public sealed class SphereWeatherCache
{
    public const int TileCells = 16;
    public const int TileNodes = TileCells + 1;
    public const int NodesPerTile = TileNodes * TileNodes;
    public const double TargetSpacing = 1_000.0;
    public const double IntervalSeconds = 5.0;
    public const double CoverRadius = 4_000.0;
    public const double PrefetchRadius = 12_000.0;
    public const int MaxTiles = 48;
    public const int Capacity = 3 * MaxTiles;

    private const int MaxRowsPerTick = 8;
    private const int MaxBakeSystems = 512;
    private const double QuarterPi = 0.25 * Math.PI;
    private const double HalfPi = 0.5 * Math.PI;
    private const double MinFaceDot = 0.5773502691896258;

    private static readonly double3[] Axes = [double3.UnitX, double3.UnitY, double3.UnitZ];

    public readonly SphereWeatherField Field;

    /// <summary>Lattice cells along a cube face edge, a multiple of <see cref="TileCells"/>.</summary>
    public readonly int FaceCells;

    public readonly long IntervalTicks;

    private readonly int _tilesPerEdge;
    private readonly double[] _tan;
    private readonly Node[] _nodes = new Node[Capacity * NodesPerTile];
    private readonly long[] _entryTile = new long[Capacity];
    private readonly long[] _entryInterval = new long[Capacity];
    private readonly int[] _entryRows = new int[Capacity];
    private readonly long[] _cover = new long[MaxTiles];
    private readonly long[] _prefetch = new long[MaxTiles];
    private readonly long[] _readyTile = new long[Capacity];
    private readonly long[] _readyInterval = new long[Capacity];
    private readonly int[] _readyEntry = new int[Capacity];
    private readonly SphereSystem[] _bakeSystems = new SphereSystem[MaxBakeSystems];
    private int _coverCount;
    private int _prefetchCount;
    private int _readyCount;
    private int _gatheredEntry = -1;
    private int _bakeSystemCount;

    public SphereWeatherCache(SphereWeatherField field)
    {
        Field = field;
        _tilesPerEdge = Math.Max(1, (int)Math.Round(HalfPi * field.Climate.Radius / (TileCells * TargetSpacing)));
        FaceCells = _tilesPerEdge * TileCells;
        IntervalTicks = Math.Max(1, (long)Math.Round(IntervalSeconds / field.TickSeconds));
        _tan = new double[FaceCells + 1];

        for (var g = 0; g <= FaceCells / 2; g++)
        {
            var t = g == 0 ? -1.0 : Math.Tan(-QuarterPi + g * HalfPi / FaceCells);
            _tan[g] = t;
            _tan[FaceCells - g] = -t;
        }

        _tan[FaceCells / 2] = 0.0;
        _entryRows.AsSpan().Fill(-1);
    }

    /// <summary>Tick of the last <see cref="Tick"/>.</summary>
    public long LastTick { get; private set; }

    /// <summary>Tile snapshots baked synchronously because a covered tile lacked one (first tick, time jump, teleport).</summary>
    public int SyncBakes { get; private set; }

    public float LastSyncMs { get; private set; }

    public long BakedRows { get; private set; }

    /// <summary>Tiles not kept because more than <see cref="MaxTiles"/> were wanted.</summary>
    public int DroppedTiles { get; private set; }

    public int ReadyEntries => _readyCount;

    /// <summary>Keeps the tiles round <paramref name="anchors"/> (planet-frame points) baked for <paramref name="tick"/> and the next interval.</summary>
    public void Tick(long tick, ReadOnlySpan<double3> anchors)
    {
        LastTick = tick;
        var interval = FloorDiv(tick, IntervalTicks);
        _coverCount = 0;
        _prefetchCount = 0;

        foreach (var anchor in anchors)
            AddTiles(double3.Normalize(anchor), CoverRadius, _cover, ref _coverCount);

        _cover.AsSpan(0, _coverCount).CopyTo(_prefetch);
        _prefetchCount = _coverCount;

        foreach (var anchor in anchors)
            AddTiles(double3.Normalize(anchor), PrefetchRadius, _prefetch, ref _prefetchCount);

        Release(interval);
        CompleteCover(interval);

        for (var t = 0; t < _prefetchCount; t++)
        {
            Find(_prefetch[t], interval + 1, true);
            Find(_prefetch[t], interval + 2, true);
        }

        var ticksLeft = Math.Max(1, (interval + 1) * IntervalTicks - tick);
        var dueNow = PendingRows(interval + 1);
        var dueNext = PendingRows(interval + 2) - dueNow;
        var rows = (dueNow + ticksLeft - 1) / ticksLeft + (dueNext + ticksLeft + IntervalTicks - 1) / (ticksLeft + IntervalTicks);
        BakeRows((int)Math.Min(MaxRowsPerTick, rows));
        IndexReady();
    }

    public WeatherSample Sample(double3 planetLocal, long tick) =>
        TrySample(planetLocal, tick, out var sample) ? sample : Field.Sample(planetLocal, tick);

    /// <summary>As <see cref="Sample"/> from baked tiles alone: false, without the analytic fallback, where none is kept.</summary>
    public bool TrySample(double3 planetLocal, long tick, out WeatherSample sample)
    {
        sample = default;
        var unit = double3.Normalize(planetLocal);
        var interval = FloorDiv(tick, IntervalTicks);
        Locate(unit, out var face, out var i, out var j);
        var ti = Math.Min((int)i / TileCells, _tilesPerEdge - 1);
        var tj = Math.Min((int)j / TileCells, _tilesPerEdge - 1);
        var tile = TileKey(face, ti, tj);
        var from = FindReady(tile, interval);
        var to = FindReady(tile, interval + 1);

        if (from < 0 || to < 0)
            return false;

        var x = i - ti * TileCells;
        var y = j - tj * TileCells;
        var t = (float)((tick - interval * IntervalTicks) / (double)IntervalTicks);
        var node = Node.Lerp(Bilinear(from, x, y), Bilinear(to, x, y), t);
        Field.Basis(unit, out var east, out var north);
        var wind = new double3(node.Wind.X, node.Wind.Y, node.Wind.Z);
        sample = new WeatherSample
        {
            Wind = new Vector2((float)double3.Dot(wind, east), (float)double3.Dot(wind, north)),
            Cloud = node.Cloud,
            Rain = node.Rain,
            Storm = node.Storm,
            Pressure = node.Pressure,
            Gust = node.Gust,
        };
        return true;
    }

    /// <summary>Planet-frame unit direction of lattice node (<paramref name="i"/>, <paramref name="j"/>) on a cube face.</summary>
    public double3 NodeDirection(int face, int i, int j)
    {
        FaceAxes(face, out var n, out var u, out var v);
        return double3.Normalize(n + _tan[i] * u + _tan[j] * v);
    }

    /// <summary>Cube face and continuous lattice coordinates (0..<see cref="FaceCells"/>) of a unit direction.</summary>
    public void Locate(double3 unit, out int face, out double i, out double j)
    {
        var ax = Math.Abs(unit.X);
        var ay = Math.Abs(unit.Y);
        var az = Math.Abs(unit.Z);
        var axis = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        var c = Component(unit, axis);
        face = 2 * axis + (c >= 0.0 ? 0 : 1);
        var scale = FaceCells / HalfPi;
        i = Math.Clamp((Math.Atan(Component(unit, (axis + 1) % 3) / Math.Abs(c)) + QuarterPi) * scale, 0.0, FaceCells);
        j = Math.Clamp((Math.Atan(Component(unit, (axis + 2) % 3) / Math.Abs(c)) + QuarterPi) * scale, 0.0, FaceCells);
    }

    /// <summary>Frees snapshots outside [interval, interval + 2] or of tiles no longer wanted.</summary>
    private void Release(long interval)
    {
        for (var e = 0; e < Capacity; e++)
        {
            if (_entryRows[e] < 0)
                continue;

            var age = _entryInterval[e] - interval;

            if (age >= 0 && age <= 2 && Contains(_prefetch, _prefetchCount, _entryTile[e]))
                continue;

            _entryRows[e] = -1;

            if (_gatheredEntry == e)
                _gatheredEntry = -1;
        }
    }

    private void CompleteCover(long interval)
    {
        var start = 0L;
        var baked = 0;

        for (var t = 0; t < _coverCount; t++)
        {
            for (var step = 0; step <= 1; step++)
            {
                var e = Find(_cover[t], interval + step, true);

                if (e < 0 || _entryRows[e] == TileNodes)
                    continue;

                if (baked++ == 0)
                    start = Stopwatch.GetTimestamp();

                Bake(e, TileNodes);
            }
        }

        if (baked == 0)
            return;

        SyncBakes += baked;
        LastSyncMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Bakes up to <paramref name="rows"/> node rows of pending snapshots, soonest interval first.</summary>
    private void BakeRows(int rows)
    {
        while (rows > 0)
        {
            var e = NextPending();

            if (e < 0)
                return;

            var todo = Math.Min(rows, TileNodes - _entryRows[e]);
            Bake(e, todo);
            rows -= todo;
        }
    }

    private void Bake(int e, int rows)
    {
        if (_gatheredEntry != e || _entryRows[e] == 0)
            Gather(e);

        var tile = _entryTile[e];
        var face = (int)(tile >> 40);
        var gi = (int)(tile >> 20 & 0xFFFFF) * TileCells;
        var gj = (int)(tile & 0xFFFFF) * TileCells;
        var systems = _bakeSystems.AsSpan(0, _bakeSystemCount);
        var end = Math.Min(TileNodes, _entryRows[e] + rows);

        for (var b = _entryRows[e]; b < end; b++)
        {
            var row = _nodes.AsSpan(e * NodesPerTile + b * TileNodes, TileNodes);

            for (var a = 0; a < TileNodes; a++)
            {
                var weather = Field.Accumulate(NodeDirection(face, gi + a, gj + b), systems, out var wind, out _, out _);
                row[a] = new Node
                {
                    Wind = new Vector3((float)wind.X, (float)wind.Y, (float)wind.Z),
                    Cloud = weather.Cloud,
                    Rain = weather.Rain,
                    Storm = weather.Storm,
                    Pressure = weather.Pressure,
                    Gust = weather.Gust,
                };
            }
        }

        BakedRows += end - _entryRows[e];
        _entryRows[e] = end;
    }

    /// <summary>Systems that reach the tile at the entry's snapshot time, from a cap round its centre node.</summary>
    private void Gather(int e)
    {
        var tile = _entryTile[e];
        var face = (int)(tile >> 40);
        var gi = (int)(tile >> 20 & 0xFFFFF) * TileCells;
        var gj = (int)(tile & 0xFFFFF) * TileCells;
        var centre = NodeDirection(face, gi + TileCells / 2, gj + TileCells / 2);
        var extent = 0.0;

        for (var corner = 0; corner < 4; corner++)
        {
            var c = NodeDirection(face, gi + (corner & 1) * TileCells, gj + (corner >> 1) * TileCells);
            extent = Math.Max(extent, Math.Acos(Math.Clamp(double3.Dot(centre, c), -1.0, 1.0)));
        }

        var time = _entryInterval[e] * IntervalTicks * Field.TickSeconds;
        _bakeSystemCount = Field.Gather(centre, extent * 1.01, time, _bakeSystems);
        _gatheredEntry = e;
    }

    /// <summary>The entry for a tile snapshot, allocating a pending one when asked; −1 if none (or the pool is full).</summary>
    private int Find(long tile, long interval, bool allocate)
    {
        var free = -1;

        for (var e = 0; e < Capacity; e++)
        {
            if (_entryRows[e] < 0)
            {
                if (free < 0)
                    free = e;

                continue;
            }

            if (_entryTile[e] == tile && _entryInterval[e] == interval)
                return e;
        }

        if (!allocate || free < 0)
            return -1;

        _entryTile[free] = tile;
        _entryInterval[free] = interval;
        _entryRows[free] = 0;
        return free;
    }

    private int NextPending()
    {
        var best = -1;

        for (var e = 0; e < Capacity; e++)
        {
            if (_entryRows[e] is < 0 or TileNodes)
                continue;

            if (best < 0 || _entryInterval[e] < _entryInterval[best])
                best = e;
        }

        return best;
    }

    /// <summary>Rows still to bake in snapshots up to <paramref name="interval"/>, which are due by its start.</summary>
    private long PendingRows(long interval)
    {
        var rows = 0L;

        for (var e = 0; e < Capacity; e++)
        {
            if (_entryRows[e] >= 0 && _entryInterval[e] <= interval)
                rows += TileNodes - _entryRows[e];
        }

        return rows;
    }

    private void IndexReady()
    {
        _readyCount = 0;

        for (var e = 0; e < Capacity; e++)
        {
            if (_entryRows[e] != TileNodes)
                continue;

            _readyTile[_readyCount] = _entryTile[e];
            _readyInterval[_readyCount] = _entryInterval[e];
            _readyEntry[_readyCount++] = e;
        }
    }

    private int FindReady(long tile, long interval)
    {
        for (var r = 0; r < _readyCount; r++)
        {
            if (_readyTile[r] == tile && _readyInterval[r] == interval)
                return _readyEntry[r];
        }

        return -1;
    }

    /// <summary>
    /// Adds every tile that may come within <paramref name="radius"/> m of <paramref name="unit"/>. On each face that can
    /// be reached, a face angle moves at most ρ / (n·p − ρ) per ρ of great-circle travel, which bounds the tile window.
    /// </summary>
    private void AddTiles(double3 unit, double radius, long[] tiles, ref int count)
    {
        var rho = radius / Field.Climate.Radius;
        var scale = FaceCells / HalfPi;

        for (var face = 0; face < 6; face++)
        {
            FaceAxes(face, out var n, out var u, out var v);
            var pn = double3.Dot(unit, n);

            if (pn + rho < MinFaceDot)
                continue;

            var delta = pn - rho > 0.1 ? rho / (pn - rho) : HalfPi;
            var a = Math.Atan2(double3.Dot(unit, u), pn);
            var b = Math.Atan2(double3.Dot(unit, v), pn);

            if (a + delta < -QuarterPi || a - delta > QuarterPi || b + delta < -QuarterPi || b - delta > QuarterPi)
                continue;

            var ti0 = TileIndex((a - delta + QuarterPi) * scale);
            var ti1 = TileIndex((a + delta + QuarterPi) * scale);
            var tj0 = TileIndex((b - delta + QuarterPi) * scale);
            var tj1 = TileIndex((b + delta + QuarterPi) * scale);

            for (var tj = tj0; tj <= tj1; tj++)
            {
                for (var ti = ti0; ti <= ti1; ti++)
                {
                    var key = TileKey(face, ti, tj);

                    if (Contains(tiles, count, key))
                        continue;

                    if (count == tiles.Length)
                    {
                        DroppedTiles++;
                        continue;
                    }

                    tiles[count++] = key;
                }
            }
        }
    }

    private int TileIndex(double cell) => Math.Clamp((int)Math.Floor(cell) / TileCells, 0, _tilesPerEdge - 1);

    private Node Bilinear(int e, double x, double y)
    {
        var cx = Math.Min((int)x, TileCells - 1);
        var cy = Math.Min((int)y, TileCells - 1);
        var fx = (float)(x - cx);
        var fy = (float)(y - cy);
        var c = e * NodesPerTile + cy * TileNodes + cx;
        var near = Node.Lerp(_nodes[c], _nodes[c + 1], fx);
        var far = Node.Lerp(_nodes[c + TileNodes], _nodes[c + TileNodes + 1], fx);
        return Node.Lerp(near, far, fy);
    }

    private static void FaceAxes(int face, out double3 n, out double3 u, out double3 v)
    {
        var axis = face >> 1;
        n = (face & 1) == 0 ? Axes[axis] : -Axes[axis];
        u = Axes[(axis + 1) % 3];
        v = Axes[(axis + 2) % 3];
    }

    private static double Component(double3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

    private static long TileKey(int face, int ti, int tj) => (long)face << 40 | (long)ti << 20 | (long)tj;

    private static bool Contains(long[] keys, int count, long key) => keys.AsSpan(0, count).Contains(key);

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>One lattice node: planet-frame wind, so lerps stay right where east and north turn fast (the poles).</summary>
    private struct Node
    {
        public Vector3 Wind;
        public float Cloud;
        public float Rain;
        public float Storm;
        public float Pressure;
        public float Gust;

        public static Node Lerp(in Node a, in Node b, float t) => new()
        {
            Wind = Vector3.Lerp(a.Wind, b.Wind, t),
            Cloud = float.Lerp(a.Cloud, b.Cloud, t),
            Rain = float.Lerp(a.Rain, b.Rain, t),
            Storm = float.Lerp(a.Storm, b.Storm, t),
            Pressure = float.Lerp(a.Pressure, b.Pressure, t),
            Gust = float.Lerp(a.Gust, b.Gust, t),
        };
    }
}
