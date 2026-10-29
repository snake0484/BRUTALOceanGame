# Job runner start latency (BRUTAL proposal)

A proposal for `BRUTAL/Brutal.Concurrency/Jobs/JobRunner.cs`, to put upstream. Nothing here changes `BRUTAL/` in this repo. Status: proposed, not implemented, not measured.

## Problem

The voxel water tick waits on its step jobs (`VoxelWaterSystem.RunPass`: `ExecuteJobs` then `Wait`, twice a tick). So the tick costs as much as the slowest runner's *start*, not just its work. Today the steps run inline on the main thread for up to `InlineBlocks` (24) awake blocks, because the jobs started late (`docs/voxel-water.md`, "Jobs tried again and dropped").

Measured 2026-10-29, `--lake-test hole`, 4 awake blocks, `InlineBlocks` 1, pool minimum raised to 4 threads a core: the step median fell 0.85 → 0.62 ms against inline, but in one of two runs the four runners started **2.3, 5.9, 7.3 and 23 ms** after `ExecuteJobs`, with `ThreadPool.PendingWorkItemCount` 0. Raising `SetMinThreads` did not help.

## Cause

Every scheduler in the game runs on the shared .NET thread pool:

| Scheduler | Runners | Priority |
| --- | --- | --- |
| `Terrain` (`TerrainWorld`) | cores − 2 | BelowNormal |
| `Coastal` (`CoastalBuilder`) | cores / 4 | BelowNormal |
| `Fetch` (`RainCover`) | cores / 4 | BelowNormal |
| `WaterActivate` | (cores − 2) / 2 | BelowNormal |
| `WaterMesh` | (cores − 2) / 2 | AboveNormal (default) |
| `WaterStep` | cores − 2 | AboveNormal |

Two things in `JobRunner` add up:

1. **Priority leaks between runners.** `ExecuteJobs` queues `DoWork` with `ThreadPool.UnsafeQueueUserWorkItem`; `DoWork` sets `Thread.CurrentThread.Priority = ThreadPriority` once it runs and never puts it back. A pool thread keeps the priority of the last runner it served. One that last ran a BelowNormal terrain, coastal or activation batch is woken *at BelowNormal* for the water step, and waits for a core behind the busy BelowNormal threads (same priority: round robin by quantum, which is tens of ms) before it can raise itself.
2. **The pool wakes workers one after another.** The portable thread pool keeps one outstanding thread request; a worker that dequeues an item requests the next worker (`ThreadPoolWorkQueue.Dispatch` → `EnsureThreadRequested`). So `WaterStep`'s N items wake N threads in a chain, each hop paying a wake at whatever priority that thread leaked. The staggered starts (2.3 → 5.9 → 7.3 → 23 ms) fit a chain. (From the .NET runtime source, not traced in this repo; the measurement in "How to measure" tells the two apart.)

More pool threads (`SetMinThreads`) don't help either: they are still woken one at a time, at a leaked priority.

## Option A (recommended): opt-in dedicated thread per runner

A runner may own a thread, created once at the runner's priority, parked on its own signal. `ExecuteJobs` signals it directly: no pool queue, no wake chain, no leaked priority. An AboveNormal thread made runnable preempts the BelowNormal workers at once.

API (additive; pool stays the default):

```csharp
public enum JobRunnerThreading { ThreadPool, Dedicated }

public JobRunner(
    string name = "",
    ThreadPriority threadPriority = DEFAULT_THREAD_PRIORITY,
    int capacity = DEFAULT_CAPACITY,
    Lock? @lock = null,
    JobRunnerThreading threading = JobRunnerThreading.ThreadPool,
    TimeSpan spinBeforePark = default
)

// JobScheduler forwards the same two parameters to every runner it creates.
// JobRunner and JobScheduler become IDisposable: Dispose stops and joins a dedicated thread (no-op for the pool).
```

Sketch of the change in `JobRunner`:

```csharp
// Constructor, Dedicated only.
_signal = new SemaphoreSlim(0);
_thread = new Thread(ThreadMain) { Name = name, IsBackground = true, Priority = threadPriority };
_thread.Start();

// ExecuteJobs, replacing the UnsafeQueueUserWorkItem line for Dedicated.
if (_thread != null)
    _signal.Release();
else
    ThreadPool.UnsafeQueueUserWorkItem(_workExecutor, false);

private void ThreadMain()
{
    while (true)
    {
        // Spin briefly first: the water tick runs two passes a few hundred µs apart, and a
        // parked thread's wake is the cost we are removing. Between ticks it parks.
        var deadline = Stopwatch.GetTimestamp() + _spinTicks;
        while (_signal.CurrentCount == 0 && Stopwatch.GetTimestamp() < deadline)
            Thread.SpinWait(20);

        _signal.Wait();
        if (Volatile.Read(ref _disposed))
            return;

        DoWork(_workExecutor.OnCompleteHandler);
    }
}
```

`DoWork` stays as it is; its priority line only calls the OS when the value differs (`ThreadPriority` stays settable).

Trade-offs:

- **+** Start latency becomes one direct wake at the runner's own priority (expected tens of µs), in parallel across runners, and zero within the spin window.
- **+** Additive and opt-in: existing users keep the pool.
- **−** More threads: one per dedicated runner (`WaterStep` is cores − 2), parked almost all the time. Each reserves a 1 MB stack (committed only as used).
- **−** Oversubscription: dedicated threads and pool threads together can exceed the cores. That is the point for a latency-critical runner (it preempts BelowNormal streaming), but terrain and coastal batches finish a little later when water is busy.
- **−** Spinning burns a core per runner for `spinBeforePark` after each batch, stealing from the BelowNormal work. Default 0; the game picks it by measurement (start at 50–100 µs, enough to catch the second pass).
- **−** API grows `IDisposable`; owners must dispose (the thread is a background thread, so a missed dispose only leaks until exit).

## Option B: restore priority when `DoWork` finishes

Two lines in `DoWork`: remember the thread's priority on entry, put it back at the very end of `finally` (after the state goes Idle):

```csharp
var thread = Thread.CurrentThread;
var previous = thread.Priority;
if (previous != ThreadPriority)
    thread.Priority = ThreadPriority;
try { ... }
finally
{
    ...
    WriteStateSynchronised(JobState.Idle);
    if (thread.Priority != previous)
        thread.Priority = previous;
}
```

Trade-offs:

- **+** Tiny, no API change, and a bug fix on its own: today a BelowNormal runner also slows unrelated pool work (`Task` continuations, timers, other libraries) that lands on the thread afterwards.
- **+** Pool threads stay at Normal, which preempts the BelowNormal workers when woken.
- **−** Does not remove the wake chain: N runners still start one after another, each a pool wake apart.
- **−** Normal, not AboveNormal, until `DoWork` runs: it still shares cores with every Normal thread (main thread, audio, GC).
- **−** One more `SetThreadPriority` call per batch per runner whose priority differs (~1 µs).

## What to put upstream

One PR to `ahwootech/BRUTAL`, `Brutal.Concurrency`:

1. Option B in `JobRunner.DoWork` (pool path): restore the thread's entry priority in `finally`. Fixes the leak for every user.
2. Option A: `JobRunnerThreading.Dedicated` and `spinBeforePark` on `JobRunner` and `JobScheduler`, both `IDisposable`.
3. A test/bench in BRUTAL: saturate the pool with a BelowNormal scheduler (cores runners spinning on busy jobs), then run an AboveNormal scheduler of 4 runners with empty jobs 1000 times and report start latency (first job's start − `ExecuteJobs`) p50 / p99 / max per runner, for pool, pool + B and dedicated. Accept A if its p99 is under 0.1 ms with the pool saturated.

If upstream only wants one change, B is the safe minimum; A is the one the water tick needs.

## Game follow-up (after the BRUTAL change lands)

1. Update the `BRUTAL` submodule.
2. `VoxelWaterSystem`: create `_stepScheduler` with `JobRunnerThreading.Dedicated` (spin 0 first) and dispose it in `Dispose`. Leave the other schedulers on the pool: they only add latency, nothing waits on them in the tick.
3. Measure start latency directly: per runner, `JobRunnerMetrics.LastTickTimeMillis` minus the jobs' own time (the timer starts in `ExecuteJobs`), shown in the profiler panel next to the step time.
4. A/B with `--lake-test hole`, alternating builds ABAB, at least three runs each (the 23 ms run was one in two):
   - **A:** `InlineBlocks` 24 (today, inline).
   - **B:** `InlineBlocks` 1, dedicated `WaterStep` threads.
   - Record the step median (`LastStepMs`), the worst "peak tick" the test logs (`TakePeakStepMs`), and the worst runner start latency. Earlier pool jobs: median 0.62 ms against 0.85 inline.
   - Repeat with streaming busy (`--seaside-test` dig, terrain and coastal batches running) where the leak bit hardest.
5. Then try `spinBeforePark` 50 and 100 µs and keep the smallest that removes the second pass's wake from the peak tick.
6. Pick `InlineBlocks` from the same runs: the awake count where jobs beat inline in both median and peak. If dedicated threads hold their start latency under ~0.1 ms, that is likely 1–2 blocks; if the peak tick still exceeds inline in any run, keep 24 and record why in `docs/voxel-water.md`.
