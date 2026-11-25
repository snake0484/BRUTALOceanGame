using System.Diagnostics;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim;

/// <summary>
/// The simulation behind the client: the fixed tick, the physics world, the ocean's gameplay queries, the swell's wind
/// and the terrain edits. It knows no renderer, window or UI, so a headless host can drive it (docs/pivot/review.md,
/// Missing 1). Main-thread only, like <see cref="PhysicsWorld"/>.
/// <para>
/// Known M0 gap: terrain collision is still streamed into <see cref="Physics"/> by the client's TerrainWorld around the
/// camera, so a headless sim has no ground until M2 moves collision streaming behind a sim-owned interest point.
/// </para>
/// </summary>
public sealed class SimWorld : IDisposable
{
    public const int TickRate = 60;

    /// <summary>Ticks between swell wind samples (1 s).</summary>
    public const int WindInterval = TickRate;

    public readonly PhysicsWorld Physics;
    public readonly OceanSurface Ocean;
    public readonly TerrainParams Terrain;

    /// <summary>Weather the swell wind follows when <see cref="DriveWind"/> is set; null pins the swell to <see cref="SetSwell"/>.</summary>
    public readonly WeatherField? Weather;

    /// <summary>Follow the weather's sea wind (debug toggle; off pins the swell's wind).</summary>
    public bool DriveWind = true;

    /// <summary>Debug offset (s) of weather time from sim time.</summary>
    public double WeatherTimeOffset;

    private readonly TerrainShoreDepth _shoreDepth;
    private readonly SwellWind? _wind;
    private readonly List<EditCommand> _pending = new(64);
    private FixedClock _clock = new(TickRate);

    private readonly record struct EditCommand(long Tick, BrushOp Op);

    public SimWorld(ulong seed, int workerThreads, in TerrainParams terrain, WeatherField? weather = null)
    {
        Terrain = terrain;
        Weather = weather;
        Physics = new PhysicsWorld(workerThreads: workerThreads);
        _shoreDepth = new TerrainShoreDepth(terrain);
        Ocean = new OceanSurface(new OceanSwell(seed), _shoreDepth);

        if (weather is not null)
            _wind = new SwellWind(weather);
    }

    public ref readonly FixedClock Clock => ref _clock;

    /// <summary>Milliseconds the last tick spent moving the swell to its time.</summary>
    public float LastSwellMs { get; private set; }

    /// <summary>Every terrain edit the sim accepted, in tick order.</summary>
    public ReadOnlySpan<BrushOp> Edits => _shoreDepth.Ops;

    /// <summary>Bumped on every accepted edit.</summary>
    public int EditVersion { get; private set; }

    /// <summary>Edits refused because they dug into the protected shore and seabed (<see cref="ShoreGuard"/>).</summary>
    public int RefusedEdits { get; private set; }

    /// <summary>Sea wind (m/s, blowing toward) as of the last wind sample; zero without weather.</summary>
    public System.Numerics.Vector2 SeaWind => _wind?.Wind ?? default;

    /// <summary>Weather time (s) of the current tick.</summary>
    public double WeatherTime => _clock.SimTime + WeatherTimeOffset;

    /// <summary>Adds real frame time and returns how many ticks to run.</summary>
    public int Advance(double frameSeconds) => _clock.Advance(frameSeconds);

    /// <summary>
    /// The groundswell and the wind used when weather doesn't drive it. With weather driving, the sea wind at this tick
    /// replaces the wind at once (the first wind never blends).
    /// </summary>
    public void SetSwell(in SwellParams swell)
    {
        if (_wind is null || !DriveWind)
        {
            Ocean.Swell.SetWind(swell);
            return;
        }

        _wind.Sample(WeatherTime);
        Ocean.Swell.SetWind(_wind.With(swell));
    }

    /// <summary>Queues a brush edit, stamped with the next tick to run; it is checked and applied at that tick's start.</summary>
    public void QueueEdit(in BrushOp op) => _pending.Add(new EditCommand(_clock.TickIndex, op));

    public void Tick()
    {
        ApplyEdits();
        UpdateWind();

        var dt = _clock.TickSeconds;
        Physics.Step((float)dt);

        var start = Stopwatch.GetTimestamp();
        Ocean.Swell.SetTime(_clock.TickIndex * dt);
        LastSwellMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _clock.TickIndex++;
    }

    private void ApplyEdits()
    {
        var applied = 0;

        for (; applied < _pending.Count; applied++)
        {
            var command = _pending[applied];

            if (command.Tick > _clock.TickIndex)
                break;

            if (!ShoreGuard.Allows(command.Op, Terrain))
            {
                RefusedEdits++;
                continue;
            }

            _shoreDepth.Add(command.Op);
            EditVersion++;
        }

        if (applied > 0)
            _pending.RemoveRange(0, applied);
    }

    /// <summary>Tick-quantised: the sea wind is sampled every <see cref="WindInterval"/> ticks at the tick's weather time.</summary>
    private void UpdateWind()
    {
        if (_wind is null)
            return;

        if (!DriveWind)
        {
            _wind.Reset();
            return;
        }

        if (_clock.TickIndex % WindInterval == 0)
            _wind.Update(Ocean.Swell, WeatherTime);
    }

    public void Dispose() => Physics.Dispose();
}
