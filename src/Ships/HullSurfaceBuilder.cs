using System.Diagnostics;
using Brutal.Concurrency.Jobs;

namespace BrutalGame.Ships;

/// <summary>
/// Builds the exact drawn surface (<see cref="HullCsg"/>) off the main thread: the scene is prepared on the caller (it
/// reads the mesh cache, well under a millisecond), the solids' surfaces run on the job workers, and the last worker to
/// finish joins them (T-junctions, bricks, flair). The main thread polls for the result. A start while one is running
/// waits and replaces any earlier waiting one, so a drag never queues stale builds.
/// </summary>
public sealed class HullSurfaceBuilder : IDisposable
{
    private readonly JobScheduler _scheduler;
    private readonly SurfaceJob[] _jobs;
    private HullCsg.Scene? _scene;
    private HullCsg.Pieces[] _pieces = [];
    private HullCsgResult? _done;
    private (HullSolid[] Solids, HullMesh[] Meshes, bool Layered, ExpandedHull? Flair, IReadOnlyList<Components.SurfacePolygon>? Extras)? _pending;
    private int _next;
    private int _remaining;
    private long _start;
    private bool _running;

    public HullSurfaceBuilder(int workers)
    {
        workers = Math.Max(1, workers);
        _scheduler = new JobScheduler((uint)workers, "HullSurface", ThreadPriority.BelowNormal);
        _jobs = new SurfaceJob[workers];

        for (var i = 0; i < workers; i++)
            _jobs[i] = new SurfaceJob(this);
    }

    /// <summary>The last finished surface.</summary>
    public HullCsgResult? Result { get; private set; }

    /// <summary>Wall time of the last build, start to join, ms.</summary>
    public float LastMs { get; private set; }

    public int Version { get; private set; }

    public bool Busy => _running || _pending is not null;

    public void Start(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, bool layered, ExpandedHull? flair, IReadOnlyList<Components.SurfacePolygon>? extras = null)
    {
        if (_running)
        {
            _pending = (solids.ToArray(), meshes.ToArray(), layered, flair, extras);
            return;
        }

        Begin(solids, meshes, layered, flair, extras);
    }

    /// <summary>Main thread, each frame: publishes a finished build and starts a waiting one. True when the result changed.</summary>
    public bool Poll()
    {
        if (!_running || Volatile.Read(ref _done) is null)
            return false;

        _scheduler.Wait();
        Result = _done;
        LastMs = (float)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        _done = null;
        _running = false;
        Version++;

        if (_pending is { } p)
        {
            _pending = null;
            Begin(p.Solids, p.Meshes, p.Layered, p.Flair, p.Extras);
        }

        return true;
    }

    /// <summary>Blocks until every started and waiting build is done.</summary>
    public void Wait()
    {
        while (Busy)
        {
            _scheduler.Wait();
            Poll();
        }
    }

    private void Begin(ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, bool layered, ExpandedHull? flair, IReadOnlyList<Components.SurfacePolygon>? extras)
    {
        _start = Stopwatch.GetTimestamp();
        _scene = HullCsg.Prepare(solids, meshes, layered, flair, extras);
        _pieces = new HullCsg.Pieces[_scene.Count];
        _next = 0;
        _remaining = _scene.Count;
        _done = null;
        _running = true;

        if (_scene.Count == 0)
        {
            _done = HullCsg.Finish(_scene, _pieces);
            return;
        }

        var jobs = Math.Min(_jobs.Length, _scene.Count);

        for (var i = 0; i < jobs; i++)
            _scheduler.Queue(_jobs[i], 1);

        _scheduler.ExecuteJobs();
    }

    private void RunWorker()
    {
        var scene = _scene!;

        while (true)
        {
            var index = Interlocked.Increment(ref _next) - 1;

            if (index >= _pieces.Length)
                return;

            _pieces[index] = HullCsg.SolidSurface(scene, index);

            // The last solid done joins everything, still on a worker.
            if (Interlocked.Decrement(ref _remaining) == 0)
                Volatile.Write(ref _done, HullCsg.Finish(scene, _pieces));
        }
    }

    public void Dispose() => _scheduler.AbortJobs(waitForCompletion: true);

    private sealed class SurfaceJob(HullSurfaceBuilder owner) : IJob
    {
        public void Run() => owner.RunWorker();
    }
}
