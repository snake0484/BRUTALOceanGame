using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Physics;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim;

/// <summary>
/// The simulation behind the client: the fixed tick, the physics world, the ocean's gameplay queries, the swell's wind
/// and the planet's terrain edits (and with them the water depth the ocean reads). It knows no renderer, window or UI, so a headless host can drive it
/// (docs/pivot/review.md, Missing 1). Main-thread only, like <see cref="PhysicsWorld"/>.
/// <para>
/// The test system's planet carries the terrain (<see cref="Planet"/>). Physics and the weather plane sit in one surface
/// bubble at the spawn on its coast (<see cref="Bubble"/>), fixed for now; rebasing is M3. The sea is planet-wide.
/// Known gap: terrain collision is still streamed into <see cref="Physics"/> by the client's TerrainWorld around the
/// camera, so a headless sim has no ground until collision streaming moves behind sim-owned anchors (M3).
/// </para>
/// </summary>
public sealed class SimWorld : IDisposable
{
    public const int TickRate = 60;

    public readonly PhysicsWorld Physics;

    /// <summary>The planet's sea: one definition everywhere on it (<see cref="OceanSurface.Sample"/> by planet position and tick).</summary>
    public readonly OceanSurface Ocean;

    /// <summary>The planet's water depth for the ocean: generated ground plus the edits that reach the sea.</summary>
    public readonly PlanetShore Shore;

    /// <summary>The generated seabed the coast maps are baked from (shores are fixed: no edits).</summary>
    public readonly ICoastDepth CoastDepth;

    /// <summary>The planet the terrain is on: its index in <see cref="Celestial"/>, params and generator.</summary>
    public readonly int PlanetIndex;
    public readonly PlanetParams Planet;
    public readonly PlanetGenerator Generator;

    /// <summary>Terrain params of every rocky body (planets and moons) by celestial index; null for stars.</summary>
    public readonly PlanetParams?[] BodyParams;

    /// <summary>Authoritative edits (D32: permanent); Seq stamped here.</summary>
    public readonly EditStore EditStore = new();

    /// <summary>The surface bubble at the spawn: physics and the weather plane (y up, sea level at y 0 at its origin).</summary>
    public readonly PhysicsBubble Bubble;

    /// <summary>Seaward along the bubble's +X from the waterline at its origin.</summary>
    public readonly double3 Seaward;

    /// <summary>Frame forest; celestial rails and spin are written into it for the step's end at the start of every tick (see <see cref="Tick"/>).</summary>
    public readonly FrameTree Frames;

    public readonly CelestialSystem Celestial;

    /// <summary>Every entity; probes step from their <see cref="EntityTable.Input"/> each tick, before physics.</summary>
    public readonly EntityTable Entities;

    /// <summary>The sea planet's weather (D31): deterministic from the world seed and the tick, read-only, job-safe.</summary>
    public readonly SphereWeatherField Weather;

    /// <summary>Each sea tile's wind per epoch, from <see cref="Weather"/>.</summary>
    public readonly SeaWindSchedule SeaWinds;

    /// <summary>The sea tile at the spawn.</summary>
    public readonly int SpawnTile;

    /// <summary>
    /// Where the host is looking from (planet frame), kept live on the sea with the bubble and the entities near sea level:
    /// the client's camera. Null for none.
    /// </summary>
    public double3? ViewAnchor;

    /// <summary>Entities this far (m) above or below the sea sphere keep its tiles live.</summary>
    public const double AnchorAltitude = 10_000.0;

    private SwellParams _swell = new();
    private bool _driveWind;
    private double3[] _anchors;
    private readonly List<EditCommand> _pending = new(64);
    private FixedClock _clock = new(TickRate);

    private readonly record struct EditCommand(long Tick, EditOp Op);

    public const string PlanetKind = "temperate-continental";
    public const string MoonKind = "barren-cratered";

    /// <summary>The test system's planet kinds by body type, until the system generator assigns them (planet-kinds.md §2).</summary>
    public static string KindOf(CelestialKind kind) => kind == CelestialKind.Moon ? MoonKind : PlanetKind;

    /// <param name="kind">Debug override of the terrain planet's and the moons' kind (--planet-kind), with its own seed for the kind's parameter ranges.</param>
    /// <param name="weather">The weather drives the sea's wind (<see cref="DriveWind"/>).</param>
    /// <param name="edgeSpawn">--surf-edge: the spawn's beach lies on a sea tile's row edge (<see cref="EdgeLatitudes"/>), where two tiles' swells meet.</param>
    public SimWorld(ulong seed, int workerThreads, bool weather = false, int entityCapacity = EntityTable.DefaultCapacity, string? kind = null, ulong? kindSeed = null, bool edgeSpawn = false)
    {
        Seed = seed;
        _driveWind = weather;
        _anchors = new double3[entityCapacity + 2];
        Physics = new PhysicsWorld(workerThreads: workerThreads);
        Frames = new FrameTree();
        Celestial = CelestialSystem.CreateTest(Frames, seed, _clock.TickSeconds);
        Entities = new EntityTable(entityCapacity);
        PlanetIndex = FirstPlanet(Celestial);
        ref readonly var body = ref Celestial[PlanetIndex];
        Planet = BodyTerrain(body, PlanetParams.BodySeed(seed, PlanetIndex), kind, kindSeed);
        Generator = new PlanetGenerator(Planet);
        BodyParams = new PlanetParams?[Celestial.Count];

        for (var i = 0; i < Celestial.Count; i++)
            BodyParams[i] = i == PlanetIndex ? Planet : RockyParams(Celestial, seed, i, kind, kindSeed);

        var coast = default(double3);

        if (!edgeSpawn || !PlanetSpawn.FindCoast(Generator.Surface, body.SpinAxis, EdgeLatitudes(new SeaTiles(Planet.SeaRadius, body.SpinAxis)), out coast, out Seaward))
            PlanetSpawn.FindCoast(Generator.Surface, body.SpinAxis, out coast, out Seaward);

        var level = Planet.HasSea ? Planet.SeaRadius : Planet.Radius + Generator.Surface.Height(coast.X, coast.Y, coast.Z);
        Bubble = PhysicsBubble.At(coast * level, Seaward);
        Physics.SetBubble(Bubble, SceneGravity(body, Bubble.Origin));

        Shore = new PlanetShore(Generator.Surface);
        var tiles = new SeaTiles(level, body.SpinAxis);
        var depth = new PlanetShoreDepth(Shore);
        CoastDepth = depth;
        SpawnTile = TileAt(tiles, coast);
        Weather = new SphereWeatherField(Climate(body, PlanetParams.BodySeed(seed, PlanetIndex), _clock.TickSeconds), _clock.TickSeconds);
        SeaWinds = new SeaWindSchedule(Weather, tiles);
        Ocean = new OceanSurface(new SeaSwells(seed, tiles, _clock.TickSeconds, SeaWinds), _clock.TickSeconds, depth);
        SetSwell(_swell);
        UpdateRenderPoses();
    }

    public ulong Seed { get; }

    /// <summary>The sea tiles' row edges (degrees of latitude) within 25° of 30° north or south, nearest 30° first.</summary>
    public static double[] EdgeLatitudes(SeaTiles tiles)
    {
        var edges = new List<double>();

        for (var row = 1; row < tiles.Rows; row++)
        {
            var latitude = tiles.RowEdge(row) * 180.0 / Math.PI;

            if (Math.Abs(Math.Abs(latitude) - 30.0) <= 25.0)
                edges.Add(latitude);
        }

        edges.Sort((a, b) => (Math.Abs(Math.Abs(a) - 30.0), a < 0).CompareTo((Math.Abs(Math.Abs(b) - 30.0), b < 0)));
        return edges.ToArray();
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

    /// <summary>The spawn tile's sea wind this epoch (reads the weather: debug and tests).</summary>
    public SeaWindKey SpawnWind => SeaWinds.At(SpawnTile, SeaWind.Epoch(_clock.TickIndex));

    /// <summary>Each sea tile follows its weather (off pins the swell to <see cref="SetSwell"/>'s wind everywhere).</summary>
    public bool DriveWind
    {
        get => _driveWind;
        set
        {
            if (value == _driveWind)
                return;

            _driveWind = value;
            SetSwell(_swell);
        }
    }

    /// <summary>Debug offset (s) of weather time from sim time, whole ticks.</summary>
    public double WeatherTimeOffset
    {
        get => SeaWinds.TickOffset * _clock.TickSeconds;
        set => SeaWinds.TickOffset = (long)Math.Round(value / _clock.TickSeconds);
    }

    /// <summary>Weather tick of the current tick.</summary>
    public long WeatherTick => _clock.TickIndex + SeaWinds.TickOffset;

    /// <summary>Weather time (s) of the current tick.</summary>
    public double WeatherTime => WeatherTick * _clock.TickSeconds;

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
    /// The groundswell and the wind used when weather doesn't drive it (directions from east toward south, the same over
    /// the whole planet). With weather driving, this epoch's sea wind replaces the wind.
    /// </summary>
    public void SetSwell(in SwellParams swell)
    {
        _swell = swell;
        Ocean.Swells.SetBase(swell, _driveWind);
        TickSea(_clock.TickIndex);
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
        Celestial.Update(_clock.TickIndex + 1);

        var dt = _clock.TickSeconds;
        StepEntities(dt);
        Physics.Step((float)dt);
        TickSea(_clock.TickIndex + 1);

        _clock.TickIndex++;
    }

    /// <summary>The sea's swells to <paramref name="tick"/>, live round the bubble, the view and the entities near sea level.</summary>
    private void TickSea(long tick)
    {
        var count = 0;
        _anchors[count++] = Bubble.Origin;

        if (ViewAnchor is { } view)
            _anchors[count++] = view;

        var surface = Celestial[PlanetIndex].Surface;
        var radius = Ocean.Tiles.SeaRadius;

        foreach (var index in Entities.Alive)
        {
            ref readonly var placement = ref Entities.Placement[index];

            if (placement.Frame == surface && Math.Abs(placement.Position.Length() - radius) < AnchorAltitude)
                _anchors[count++] = placement.Position;
        }

        Ocean.Swells.Tick(tick, _anchors.AsSpan(0, count));
        LastSwellMs = Ocean.Swells.LastTickMs;
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

            Shore.Add(EditStore.Add(command.Op with { Seq = 0 }));
            EditVersion++;
        }

        if (applied > 0)
            _pending.RemoveRange(0, applied);
    }

    private void StepEntities(double dt)
    {
        var entities = Entities;

        foreach (var index in entities.Alive)
        {
            ref var placement = ref entities.Placement[index];

            if (entities.Kind[index] == EntityKind.Probe && placement.Mode == PlacementMode.Simulated)
                ProbeMotion.Step(ref placement, entities.Input[index], dt);
        }
    }

    private static unsafe int TileAt(SeaTiles tiles, double3 dir)
    {
        tiles.Locate(dir, out var blend);
        return blend.Tile[0];
    }

    /// <summary>
    /// Weather systems' size on the rocky planets against the island's: 45–65 km storms, so one covers a good part of a
    /// 64 km sea tile and its sea builds (a tile takes the mean wind over its area; island-sized storms never showed).
    /// </summary>
    public const float WeatherSizeScale = 4f;

    /// <summary>The planet's climate: its own seed from the body's, its radius, day and axis.</summary>
    private static PlanetClimate Climate(in CelestialBody body, ulong bodySeed, double tickSeconds)
    {
        var day = body.DayTicks > 0 ? body.DayTicks * tickSeconds : 1e9;
        return PlanetClimate.Rocky(bodySeed ^ WeatherSalt, body.Radius, day, body.SpinAxis) with { SizeScale = WeatherSizeScale };
    }

    private const ulong WeatherSalt = 0x2545F4914F6CDD1DUL;

    private static int FirstPlanet(CelestialSystem celestial)
    {
        for (var i = 0; i < celestial.Count; i++)
        {
            if (celestial[i].Kind == CelestialKind.Planet)
                return i;
        }

        throw new InvalidOperationException("The celestial system has no planet for the terrain.");
    }

    /// <summary>A moon's terrain: airless and dry, barren rock; null for a star. Planets other than the terrain's get seas.</summary>
    private static PlanetParams? RockyParams(CelestialSystem celestial, ulong seed, int index, string? kind, ulong? kindSeed)
    {
        ref readonly var body = ref celestial[index];

        if (body.Kind is not (CelestialKind.Planet or CelestialKind.Moon))
            return null;

        return BodyTerrain(body, PlanetParams.BodySeed(seed, index), body.Kind == CelestialKind.Moon ? kind : null, kindSeed);
    }

    /// <summary>A body's terrain from its kind: the kind decides the sea, the relief range and the materials; moons stay dry.</summary>
    private static PlanetParams BodyTerrain(in CelestialBody body, ulong bodySeed, string? kind, ulong? kindSeed)
    {
        var p = PlanetParams.Generate(bodySeed, body.Radius, body.Mu / (body.Radius * body.Radius), ocean: body.Kind != CelestialKind.Moon, kind ?? KindOf(body.Kind), kindSeed);
        return body.SpinAxis == new double3(p.AxisX, p.AxisY, p.AxisZ) ? p : p.WithAxis(body.SpinAxis.X, body.SpinAxis.Y, body.SpinAxis.Z);
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
