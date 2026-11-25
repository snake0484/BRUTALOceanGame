using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.Concurrency.Jobs;

namespace BrutalGame.Ships;

/// <summary>
/// Bakes a brush list into bricks on a job scheduler, on commit only. <see cref="Start"/> copies the brushes, bins them,
/// keeps every brick whose input hash is unchanged and queues the rest on the workers, each pulling bricks from a shared
/// counter. Results land in a separate set and swap in when the bake finishes (<see cref="Poll"/>), so the main thread
/// never reads a brick a worker writes. A bake started while one runs waits and keeps only the newest brush list.
/// Mass is summed from integer occupancy, so it is the same whatever brick order or worker count.
/// </summary>
public sealed class HullBaker : IDisposable
{
    private readonly JobScheduler _scheduler;
    private readonly BrickJob[] _jobs;
    private readonly HullBins _bins = new();
    private readonly Stack<HullBrick> _free = new();
    private Dictionary<BrickKey, HullBrick> _current = new();
    private Dictionary<BrickKey, HullBrick> _next = new();
    private readonly List<HullBrick> _bricks = new();
    private readonly List<HullBrick> _work = new();
    private readonly List<ulong> _meshHashes = new();
    private readonly Dictionary<BrickKey, bool> _exactKeys = new();
    private int[] _exactBrush = [];
    private int[] _grow = [];
    private HullBrush[] _brushes = new HullBrush[64];
    private int _brushCount;
    private byte[] _hashScratch = new byte[256];
    private int[] _densities = [];
    private readonly List<PreparedMesh?> _meshes = new();
    private HullBrush[]? _pending;
    private HullMesh[]? _pendingMeshes;
    private int[]? _pendingDensities;
    private long _startTicks;
    private long _lastFinish;
    private int _nextWork;
    private bool _running;

    public HullBaker(int workers)
    {
        workers = Math.Max(1, workers);
        _scheduler = new JobScheduler((uint)workers, "HullBake", ThreadPriority.BelowNormal);
        _jobs = new BrickJob[workers];

        for (var i = 0; i < workers; i++)
            _jobs[i] = new BrickJob(this);
    }

    /// <summary>Bricks of the last finished bake, sorted by key.</summary>
    public IReadOnlyList<HullBrick> Bricks => _bricks;

    public HullMass Mass { get; private set; }

    /// <summary>Increments each time a bake finishes.</summary>
    public int Version { get; private set; }

    public bool Busy => _running || _pending is not null;

    /// <summary>Wall time of the last bake from queueing to the last worker finishing.</summary>
    public float LastBakeMs { get; private set; }

    /// <summary>Sum of per-brick bake times of the last bake (CPU on workers).</summary>
    public float LastCpuMs { get; private set; }

    public int LastRebaked { get; private set; }

    public int LastReused { get; private set; }

    public int MaxBrushesPerBrick => _bins.MaxPerBrick;

    public int TriangleCount { get; private set; }

    /// <summary>Dual contouring (sharp) by default; surface nets for comparison. Applies to the next bake.</summary>
    public HullMesher Mesher = HullMesher.DualContouring;

    /// <summary>
    /// Bricks only one Add brush reaches draw that brush's own polygons instead of the grid mesh (exact where nothing
    /// cuts or joins it). Mass and occupancy always come from the grid.
    /// </summary>
    public bool ExactSurfaces = true;

    /// <summary>Bricks of the last bake drawn from exact brush polygons.</summary>
    public int ExactBricks { get; private set; }

    /// <summary>Main-thread time preparing mesh brushes (subdivision, BVH) for the last bake; cached by mesh hash.</summary>
    public float LastPrepareMs { get; private set; }

    public void Start(ReadOnlySpan<HullBrush> brushes, ReadOnlySpan<int> densities) => Start(brushes, [], densities);

    /// <summary>
    /// Bakes <paramref name="brushes"/>; mesh brushes index <paramref name="meshes"/> by Param; <paramref name="densities"/>
    /// is kg/m³ per plan material index (mass only). If a bake is running, this one waits for it.
    /// </summary>
    public void Start(ReadOnlySpan<HullBrush> brushes, IReadOnlyList<HullMesh> meshes, ReadOnlySpan<int> densities)
    {
        if (_running)
        {
            _pending = brushes.ToArray();
            _pendingMeshes = meshes.ToArray();
            _pendingDensities = densities.ToArray();
            return;
        }

        Begin(brushes, meshes, densities);
    }

    /// <summary>Main thread, each frame: finishes a completed bake and starts a waiting one. True when results changed.</summary>
    public bool Poll()
    {
        if (!_running || _scheduler.IsExecuting)
            return false;

        Finish();

        if (_pending is not null)
        {
            var brushes = _pending;
            var meshes = _pendingMeshes!;
            var densities = _pendingDensities!;
            _pending = null;
            _pendingMeshes = null;
            _pendingDensities = null;
            Begin(brushes, meshes, densities);
        }

        return true;
    }

    /// <summary>Blocks until every started and waiting bake is done.</summary>
    public void Wait()
    {
        while (Busy)
        {
            if (_running)
                _scheduler.Wait();

            Poll();
        }
    }

    private void Begin(ReadOnlySpan<HullBrush> brushes, IReadOnlyList<HullMesh> meshes, ReadOnlySpan<int> densities)
    {
        _startTicks = Stopwatch.GetTimestamp();
        _lastFinish = _startTicks;
        _meshes.Clear();
        _meshHashes.Clear();

        foreach (var mesh in meshes)
        {
            _meshes.Add(mesh is null ? null : MeshCache.Get(mesh));
            _meshHashes.Add(mesh?.MeshHash ?? 0);
        }

        LastPrepareMs = (float)Stopwatch.GetElapsedTime(_startTicks).TotalMilliseconds;

        if (_brushes.Length < brushes.Length)
            _brushes = new HullBrush[Math.Max(brushes.Length, _brushes.Length * 2)];

        brushes.CopyTo(_brushes);
        _brushCount = brushes.Length;
        _densities = densities.ToArray();
        _bins.Build(_brushes.AsSpan(0, _brushCount));
        _next.Clear();
        _work.Clear();
        var reused = 0;
        Classify();

        for (var b = 0; b < _bins.BrickCount; b++)
        {
            var key = _bins.Keys[b];
            var mode = _exactBrush[b] >= 0 ? 1UL + (ulong)_grow[b] : 0UL;
            var hash = InputHash(key, _bins.Brushes(b)) ^ (ulong)Mesher ^ (mode * 0x9E3779B97F4A7C15UL);

            if (_current.TryGetValue(key, out var old) && old.InputHash == hash)
            {
                _next.Add(key, old);
                reused++;
                continue;
            }

            var brick = _free.Count > 0 ? _free.Pop() : new HullBrick();
            brick.Key = key;
            brick.InputHash = hash;
            _next.Add(key, brick);
            _work.Add(brick);
        }

        LastReused = reused;
        LastRebaked = _work.Count;
        _nextWork = 0;
        _running = true;

        if (_work.Count == 0)
            return;

        var jobs = Math.Min(_jobs.Length, _work.Count);

        for (var i = 0; i < jobs; i++)
            _scheduler.Queue(_jobs[i], 1);

        _scheduler.ExecuteJobs();
    }

    private void Finish()
    {
        _running = false;
        LastBakeMs = (float)Stopwatch.GetElapsedTime(_startTicks, Interlocked.Read(ref _lastFinish) > _startTicks ? _lastFinish : _startTicks).TotalMilliseconds;

        foreach (var (key, brick) in _current)
        {
            if (!_next.TryGetValue(key, out var kept) || !ReferenceEquals(kept, brick))
                _free.Push(brick);
        }

        (_current, _next) = (_next, _current);
        _next.Clear();
        _bricks.Clear();
        var mass = new HullMass();
        var cpu = 0f;
        var triangles = 0;

        foreach (var brick in _current.Values)
            _bricks.Add(brick);

        _bricks.Sort(static (a, b) => a.Key.CompareTo(b.Key));

        foreach (var brick in _bricks)
        {
            triangles += brick.IndexCount / 3;

            foreach (ref readonly var m in brick.MaterialSpan)
                mass.Add(m, m.Material < _densities.Length ? _densities[m.Material] : 0);
        }

        foreach (var brick in _work)
            cpu += brick.BakeMs;

        Mass = mass;
        LastCpuMs = cpu;
        TriangleCount = triangles;
        Version++;
    }

    /// <summary>
    /// Exact bricks: one binned brush, an Add, a primitive or a prepared mesh. Each grows its clip box one cell down an
    /// axis when a grid-meshed brick lies in the layer below it on that axis (face, edge or corner neighbour): the grid
    /// mesh of that brick stops about half a cell short of the shared plane.
    /// </summary>
    private void Classify()
    {
        var count = _bins.BrickCount;

        if (_exactBrush.Length < count)
        {
            _exactBrush = new int[Math.Max(count, _exactBrush.Length * 2)];
            _grow = new int[_exactBrush.Length];
        }

        _exactKeys.Clear();
        var exactCount = 0;

        for (var b = 0; b < count; b++)
        {
            var binned = _bins.Brushes(b);
            _exactBrush[b] = -1;
            _grow[b] = 0;

            if (ExactSurfaces && binned.Length == 1)
            {
                ref readonly var brush = ref _brushes[binned[0]];
                var drawable = BrushGeometry.IsSupported(brush.Shape) || (brush.Shape == BrushShape.Mesh && brush.Param < _meshes.Count && _meshes[brush.Param] is not null);

                if (brush.Op == BrushOp.Add && drawable)
                {
                    _exactBrush[b] = 0;
                    exactCount++;
                }
            }

            _exactKeys[_bins.Keys[b]] = _exactBrush[b] >= 0;
        }

        ExactBricks = exactCount;

        for (var b = 0; b < count; b++)
        {
            if (_exactBrush[b] < 0)
                continue;

            var key = _bins.Keys[b];

            for (var axis = 0; axis < 3; axis++)
            {
                for (var du = -1; du <= 1; du++)
                {
                    for (var dv = -1; dv <= 1; dv++)
                    {
                        var n = Neighbour(key, axis, du, dv);

                        if (_exactKeys.TryGetValue(n, out var exact) && !exact)
                            _grow[b] |= 1 << axis;
                    }
                }
            }
        }
    }

    /// <summary>The brick one below along <paramref name="axis"/>, offset by (du, dv) along the other two axes.</summary>
    private static BrickKey Neighbour(BrickKey k, int axis, int du, int dv) => axis switch
    {
        0 => new BrickKey((short)(k.X - 1), (short)(k.Y + du), (short)(k.Z + dv)),
        1 => new BrickKey((short)(k.X + du), (short)(k.Y - 1), (short)(k.Z + dv)),
        _ => new BrickKey((short)(k.X + du), (short)(k.Y + dv), (short)(k.Z - 1)),
    };

    private ulong InputHash(BrickKey key, ReadOnlySpan<int> binned)
    {
        var bytes = 6 + binned.Length * HullBrush.SizeInBytes;

        if (_hashScratch.Length < bytes)
            _hashScratch = new byte[Math.Max(bytes, _hashScratch.Length * 2)];

        var span = _hashScratch.AsSpan(0, bytes);
        MemoryMarshal.Write(span, key.X);
        MemoryMarshal.Write(span[2..], key.Y);
        MemoryMarshal.Write(span[4..], key.Z);
        var offset = 6;

        var meshSeed = 0UL;

        foreach (var index in binned)
        {
            ref readonly var brush = ref _brushes[index];
            MemoryMarshal.Write(span[offset..], brush);
            offset += HullBrush.SizeInBytes;

            if (brush.Shape == BrushShape.Mesh && brush.Param < _meshHashes.Count)
                meshSeed = meshSeed * 31 + _meshHashes[brush.Param];
        }

        return XxHash64.Hash(span, meshSeed);
    }

    private void RunWorker(HullBrickBaker baker)
    {
        var brushes = _brushes.AsSpan(0, _brushCount);

        while (true)
        {
            var index = Interlocked.Increment(ref _nextWork) - 1;

            if (index >= _work.Count)
                break;

            var brick = _work[index];
            var slot = BinIndex(brick.Key);
            baker.Mesher = Mesher;
            baker.Bake(brick.Key, brushes, _bins.Brushes(slot), _meshes, brick, _exactBrush[slot], _grow[slot]);
        }

        var now = Stopwatch.GetTimestamp();
        long seen;

        do
        {
            seen = Interlocked.Read(ref _lastFinish);
        }
        while (now > seen && Interlocked.CompareExchange(ref _lastFinish, now, seen) != seen);
    }

    private int BinIndex(BrickKey key)
    {
        var keys = _bins.Keys;
        int lo = 0, hi = keys.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var c = keys[mid].CompareTo(key);

            if (c == 0)
                return mid;

            if (c < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        throw new InvalidOperationException($"Brick {key} is not binned");
    }

    public void Dispose() => _scheduler.AbortJobs(waitForCompletion: true);

    private sealed class BrickJob(HullBaker owner) : IJob
    {
        private readonly HullBrickBaker _baker = new();

        public void Run() => owner.RunWorker(_baker);
    }
}
