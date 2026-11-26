using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Physics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim;

/// <summary>
/// The simulation behind the client: the fixed tick, the physics world, the ocean's gameplay queries, the swell's wind
/// and the planet's terrain edits. It knows no renderer, window or UI, so a headless host can drive it
/// (docs/pivot/review.md, Missing 1). Main-thread only, like <see cref="PhysicsWorld"/>.
/// <para>
/// The test system's planet carries the terrain (<see cref="Planet"/>). Physics, the sea frame and the weather plane sit
/// in one surface bubble at the spawn on its coast (<see cref="Bubble"/>), fixed for now; rebasing is M3.
/// Known gap: terrain collision is still streamed into <see cref="Physics"/> by the client's TerrainWorld around the
/// camera, so a headless sim has no ground until collision streaming moves behind sim-owned anchors (M3).
/// </para>
/// </summary>
public sealed class SimWorld : IDisposable
{
    public const int TickRate = 60;

    /// <summary>Ticks between swell wind samples (1 s).</summary>
    public const int WindInterval = TickRate;

    public readonly PhysicsWorld Physics;
    public readonly OceanSurface Ocean;

    /// <summary>The planet the terrain is on: its index in <see cref="Celestial"/>, params and generator.</summary>
    public readonly int PlanetIndex;
    public readonly PlanetParams Planet;
    public readonly PlanetGenerator Generator;

    /// <summary>Authoritative edits (D32: permanent); Seq stamped here.</summary>
    public readonly EditStore EditStore = new();

    /// <summary>The surface bubble at the spawn: physics, the sea frame and the weather plane (y up, sea level at y 0 at its origin).</summary>
    public readonly PhysicsBubble Bubble;

    /// <summary>Seaward along the bubble's +X from the waterline at its origin.</summary>
    public readonly double3 Seaward;

    /// <summary>Frame forest; celestial rails and spin are written into it for the step's end at the start of every tick (see <see cref="Tick"/>).</summary>
    public readonly FrameTree Frames;

    public readonly CelestialSystem Celestial;

    /// <summary>Weather the swell wind follows when <see cref="DriveWind"/> is set; null pins the swell to <see cref="SetSwell"/>.</summary>
    public readonly WeatherField? Weather;

    /// <summary>Follow the weather's sea wind (debug toggle; off pins the swell's wind).</summary>
    public bool DriveWind = true;

    /// <summary>Debug offset (s) of weather time from sim time.</summary>
    public double WeatherTimeOffset;

    private readonly SwellWind? _wind;
    private readonly List<EditCommand> _pending = new(64);
    private FixedClock _clock = new(TickRate);

    private readonly record struct EditCommand(long Tick, EditOp Op);

    public SimWorld(ulong seed, int workerThreads, WeatherField? weather = null)
    {
        Weather = weather;
        Physics = new PhysicsWorld(workerThreads: workerThreads);
        Frames = new FrameTree();
        Celestial = CelestialSystem.CreateTest(Frames, seed, _clock.TickSeconds);
        PlanetIndex = FirstPlanet(Celestial);
        ref readonly var body = ref Celestial[PlanetIndex];
        Planet = PlanetParams.Generate(PlanetParams.BodySeed(seed, PlanetIndex), body.Radius, body.Mu / (body.Radius * body.Radius));
        Generator = new PlanetGenerator(Planet);

        PlanetSpawn.FindCoast(Generator.Surface, body.SpinAxis, out var coast, out Seaward);
        var level = Planet.HasSea ? Planet.SeaRadius : Planet.Radius + Generator.Surface.Height(coast.X, coast.Y, coast.Z);
        Bubble = PhysicsBubble.At(coast * level, Seaward);
        Physics.SetBubble(Bubble, SceneGravity(body, Bubble.Origin));

        Ocean = new OceanSurface(new OceanSwell(seed), new PlanetShoreDepth(Generator.Surface, Bubble));
        UpdateRenderPoses();

        if (weather is not null)
            _wind = new SwellWind(weather);
    }

    public ref readonly FixedClock Clock => ref _clock;

    /// <summary>Milliseconds the last tick spent moving the swell to its time.</summary>
    public float LastSwellMs { get; private set; }

    /// <summary>Every terrain edit the sim accepted, in Seq order.</summary>
    public ReadOnlySpan<EditOp> Edits => EditStore.Ops;

    /// <summary>Bumped on every accepted edit.</summary>
    public int EditVersion { get; private set; }

    /// <summary>Edits dropped unlogged because every voxel they reach is protected shore, seabed or dig floor (D30).</summary>
    public int RefusedEdits { get; private set; }

    /// <summary>Sea wind (m/s, blowing toward) as of the last wind sample; zero without weather.</summary>
    public System.Numerics.Vector2 SeaWind => _wind?.Wind ?? default;

    /// <summary>Weather time (s) of the current tick.</summary>
    public double WeatherTime => _clock.SimTime + WeatherTimeOffset;

    /// <summary>Adds real frame time and returns how many ticks to run.</summary>
    public int Advance(double frameSeconds) => _clock.Advance(frameSeconds);

    /// <summary>
    /// Render time (s): <see cref="RenderTick"/> plus <see cref="FixedClock.Alpha"/>, one tick behind the sim, so drawing
    /// interpolates between the last two ticks' states (as grids will) and matches the ocean's render time.
    /// </summary>
    public double RenderTime => (RenderTick + _clock.Alpha) * _clock.TickSeconds;

    public long RenderTick => _clock.TickIndex - 1;

    /// <summary>Per frame, after the ticks: rails and spin at <see cref="RenderTime"/> into <see cref="FrameTree.Render"/>.</summary>
    public void UpdateRenderPoses() => Celestial.Evaluate(RenderTick, _clock.Alpha);

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
    public void QueueEdit(in EditOp op) => _pending.Add(new EditCommand(_clock.TickIndex, op));

    /// <summary>
    /// Steps the sim from <c>TickIndex dt</c> to <c>(TickIndex + 1) dt</c>. Convention: when it returns, physics, the swell and the frame
    /// poses all describe <see cref="FixedClock.SimTime"/> with the incremented <see cref="FixedClock.TickIndex"/>, so rails
    /// and spin are written for the step's end before the physics step runs (<c>Celestial.Tick == TickIndex</c>).
    /// </summary>
    public void Tick()
    {
        ApplyEdits();
        UpdateWind();
        Celestial.Update(_clock.TickIndex + 1);

        var dt = _clock.TickSeconds;
        Physics.Step((float)dt);

        var start = Stopwatch.GetTimestamp();
        Ocean.Swell.SetTime((_clock.TickIndex + 1) * dt);
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

            if (Generator.IsFullyProtected(command.Op))
            {
                RefusedEdits++;
                continue;
            }

            EditStore.Add(command.Op with { Seq = 0 });
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

    private static int FirstPlanet(CelestialSystem celestial)
    {
        for (var i = 0; i < celestial.Count; i++)
        {
            if (celestial[i].Kind == CelestialKind.Planet)
                return i;
        }

        throw new InvalidOperationException("The celestial system has no planet for the terrain.");
    }

    /// <summary>
    /// Scene gravity in the bubble's axes (review C2): g at its origin plus the centrifugal term of the planet's spin.
    /// Coriolis and the per-body difference from the origin are M3's.
    /// </summary>
    private System.Numerics.Vector3 SceneGravity(in CelestialBody body, double3 origin)
    {
        var r = origin.Length();
        var g = -origin * (body.Mu / (r * r * r));

        if (body.DayTicks > 0)
        {
            var omega = 2.0 * Math.PI / (body.DayTicks * _clock.TickSeconds);
            var axial = origin - body.SpinAxis * double3.Dot(origin, body.SpinAxis);
            g += axial * (omega * omega);
        }

        var local = doubleQuat.Conjugate(Bubble.Axes) * g;
        return new System.Numerics.Vector3((float)local.X, (float)local.Y, (float)local.Z);
    }

    public void Dispose() => Physics.Dispose();
}
