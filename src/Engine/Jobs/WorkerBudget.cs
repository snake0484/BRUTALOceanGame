namespace BrutalGame.Engine.Jobs;

/// <summary>
/// One worker budget for the game, from the CPU's core count. Every <c>JobScheduler</c> queues its runners on the shared
/// .NET thread pool (about one thread a core), so runner counts that each assume the whole CPU queue behind one another,
/// and tick work the main thread waits on can start late (docs/job-runner-priority.md). Background schedulers therefore
/// split one budget instead of each taking the CPU. On 8 cores: tick work 6, terrain 3, the rest 1 each.
/// </summary>
public static class WorkerBudget
{
    public static readonly int Cores = Environment.ProcessorCount;

    /// <summary>Work the main thread waits on (the water step, Jolt's physics step): every core but the main thread's and one spare.</summary>
    public static readonly int Tick = Math.Max(1, Cores - 2);

    /// <summary>All background runners together.</summary>
    public static readonly int Background = Math.Max(2, Cores - 2);

    /// <summary>Terrain streaming, the bulk of the background work: half the budget.</summary>
    public static readonly int Streaming = Math.Max(1, Background / 2);

    /// <summary>Each smaller background scheduler: bursty work that rarely runs alongside the others.</summary>
    public static readonly int Side = Math.Max(1, Background / 6);
}
