using System.Diagnostics;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Sim;

/// <summary>
/// The simulation behind the client: the fixed tick, the physics world and the ocean's gameplay queries. It knows no
/// renderer, window or UI, so a headless host can drive it (docs/pivot/review.md, Missing 1). Main-thread only, like
/// <see cref="PhysicsWorld"/>.
/// </summary>
public sealed class SimWorld : IDisposable
{
    public const int TickRate = 60;

    public readonly PhysicsWorld Physics;
    public readonly OceanSurface Ocean;

    private readonly TerrainShoreDepth _shoreDepth = new();
    private FixedClock _clock = new(TickRate);
    private int _shoreOps = -1;

    public SimWorld(ulong seed, int workerThreads)
    {
        Physics = new PhysicsWorld(workerThreads: workerThreads);
        Ocean = new OceanSurface(new OceanSwell(seed), _shoreDepth);
    }

    public ref readonly FixedClock Clock => ref _clock;

    /// <summary>Milliseconds the last tick spent moving the swell to its time.</summary>
    public float LastSwellMs { get; private set; }

    /// <summary>Adds real frame time and returns how many ticks to run.</summary>
    public int Advance(double frameSeconds) => _clock.Advance(frameSeconds);

    public void Tick()
    {
        var dt = _clock.TickSeconds;
        Physics.Step((float)dt);

        var start = Stopwatch.GetTimestamp();
        Ocean.Swell.SetTime(_clock.TickIndex * dt);
        LastSwellMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _clock.TickIndex++;
    }

    /// <summary>The ocean's depth cap reads the analytic terrain plus edits; re-snapshot when an edit lands (between ticks).</summary>
    public void SnapshotTerrain(in TerrainParams terrain, IReadOnlyList<BrushOp> ops)
    {
        if (ops.Count == _shoreOps)
            return;

        _shoreDepth.Snapshot(terrain, ops);
        _shoreOps = ops.Count;
    }

    public void Dispose() => Physics.Dispose();
}
