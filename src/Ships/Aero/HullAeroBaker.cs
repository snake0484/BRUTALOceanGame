using Brutal.Concurrency.Jobs;

namespace BrutalGame.Ships.Aero;

/// <summary>
/// Bakes one part's <see cref="AeroTable"/> off the main thread (one worker). A start while one runs waits and replaces
/// any earlier waiting one. The main thread polls for the result.
/// </summary>
public sealed class HullAeroBaker : IDisposable
{
    private readonly JobScheduler _scheduler = new(1, "HullAero", ThreadPriority.Lowest);
    private readonly BakeJob _job;
    private (IReadOnlyList<HullBrick> Bricks, Vector3d Reference)? _pending, _current;
    private AeroTable? _done;
    private bool _running;

    public HullAeroBaker() => _job = new BakeJob(this);

    public AeroTable? Result { get; private set; }

    public bool Busy => _running || _pending is not null;

    public void Start(IReadOnlyList<HullBrick> bricks, Vector3d reference)
    {
        if (_running)
        {
            _pending = (bricks, reference);
            return;
        }

        Begin(bricks, reference);
    }

    /// <summary>Main thread: publishes a finished bake and starts a waiting one. True when the result changed.</summary>
    public bool Poll()
    {
        if (!_running || Volatile.Read(ref _done) is null)
            return false;

        _scheduler.Wait();
        Result = _done;
        _done = null;
        _running = false;

        if (_pending is { } p)
        {
            _pending = null;
            Begin(p.Bricks, p.Reference);
        }

        return true;
    }

    public void Wait()
    {
        while (Busy)
        {
            _scheduler.Wait();
            Poll();
        }
    }

    private void Begin(IReadOnlyList<HullBrick> bricks, Vector3d reference)
    {
        _current = (bricks, reference);
        _done = null;
        _running = true;
        _scheduler.Queue(_job, 1);
        _scheduler.ExecuteJobs();
    }

    public void Dispose() => _scheduler.AbortJobs(waitForCompletion: true);

    private sealed class BakeJob(HullAeroBaker owner) : IJob
    {
        public void Run()
        {
            var (bricks, reference) = owner._current!.Value;
            Volatile.Write(ref owner._done, HullAero.Bake(bricks, reference));
        }
    }
}
