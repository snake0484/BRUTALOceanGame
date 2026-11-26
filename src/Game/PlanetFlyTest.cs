using System.Numerics;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>CPU time (ms) of one frame's main-thread sections, measured by <see cref="GameApp"/>.</summary>
public struct FrameSections
{
    public float Ticks;
    public float Terrain;
    public float Weather;
    public float Ui;
    public float Uploads;
    public float Prepare;
    public float Record;
    public float Present;
    public float GpuWait;

    /// <summary>Main-thread work, everything but waiting on the GPU. Record spans all command recording, Uploads and Prepare included.</summary>
    public readonly float Work => Ticks + Terrain + Weather + Ui + Record + Present;
}

/// <summary>
/// --planet-fly (M2 exit check, docs/pivot/README.md): scripted flights from the spawn's coast inland along a great circle
/// at 30, 300 and 3000 m/s close to the ground (terrain-following on the generated surface), then a descent from 800 km
/// to the ground. Each leg starts once the world has settled at its start pose; per leg it logs leaves loaded/s, chunk
/// jobs/s, main-thread ms (terrain update and the whole frame), the selection, and holes: frames where a desired leaf
/// in view has nothing loaded over it (<see cref="TerrainWorld.CountHoles"/>). Closes the window when done.
/// </summary>
public sealed class PlanetFlyTest
{
    private static readonly LogCategory Log = LogCategory.Make("PlanetFly");

    private readonly record struct Leg(string Name, double Speed, double Agl, double Seconds, bool Descent);

    private static readonly Leg[] Legs =
    [
        new("30 m/s at 30 m", 30.0, 30.0, 20.0, false),
        new("300 m/s at 100 m", 300.0, 100.0, 20.0, false),
        new("3000 m/s at 300 m", 3000.0, 300.0, 20.0, false),
        new("descent 800 km to 30 m", 0.0, 30.0, 60.0, true),
    ];

    private const double DescentTop = 800_000.0;
    private const double SettleTimeout = 120.0;
    private const double FlightPitch = -0.15;
    private const double DescentPitch = -0.6;
    private const int MaxFrames = 1 << 15;

    private enum Phase : byte
    {
        Start,
        Settling,
        Flying,
    }

    private readonly FlyCamera _camera;
    private readonly TerrainWorld _world;
    private readonly PlanetSurface _surface;
    private readonly double3 _start;
    private readonly double3 _heading;
    private readonly float _ground;
    private readonly float[] _update = new float[MaxFrames];
    private readonly float[] _work = new float[MaxFrames];
    private readonly int[] _holes = new int[MaxFrames];
    private readonly int[] _desired = new int[MaxFrames];
    private readonly float[] _select = new float[MaxFrames];
    private readonly Vector4[] _planes = new Vector4[5];
    private readonly long[] _holeLods = new long[PlanetLeafSelector.MaxLeafLod + 1];
    private int _leg;
    private int _count;
    private Phase _phase;
    private double _phaseStart;
    private long _leaves;
    private long _jobs;
    private int _selects;
    private int _selectCount;
    private int _selectSeen;
    private long _bounded;
    private long _visited;
    private int _collections;
    private long _allocated;
    private int _maxMinLod;
    private int _maxMeshed;
    private long _maxMeshBytes;
    private long _forced;

    /// <param name="steep">Fly over the planet's steepest ground (<see cref="SteepGround"/>) instead of inland from the spawn.</param>
    public PlanetFlyTest(FlyCamera camera, SimWorld sim, TerrainWorld world, bool steep = false)
    {
        _camera = camera;
        _world = world;
        _surface = sim.Generator.Surface;
        _start = double3.Normalize(sim.Bubble.Origin);
        _heading = -sim.Seaward;

        if (steep)
        {
            SteepGround.Find(_surface, out _start, out _heading);
            Log.Info($"Steep start at ({_start.X:F4}, {_start.Y:F4}, {_start.Z:F4}), ground {_surface.Height(_start.X, _start.Y, _start.Z):F0} m, kind {sim.Planet.KindId}");
        }

        _ground = _surface.HasSea ? _surface.SeaHeight : float.MinValue;
    }

    public bool Done { get; private set; }

    /// <summary>The scripted camera speed (m/s) for the terrain's speed LOD bias; 0 while settling.</summary>
    public double Speed { get; private set; }

    /// <summary>Before the frame's terrain update: moves the camera along the current leg.</summary>
    public void Fly(double time)
    {
        if (Done)
            return;

        var leg = Legs[_leg];

        if (_phase == Phase.Start)
        {
            Place(leg, 0.0);
            Speed = 0.0;
            (_phase, _phaseStart) = (Phase.Settling, time);
            return;
        }

        if (_phase == Phase.Settling)
        {
            Place(leg, 0.0);

            if (!_world.Settled && time - _phaseStart < SettleTimeout)
                return;

            Log.Info($"{leg.Name}: settled at the start in {time - _phaseStart:F1} s ({_world.DesiredLeaves} leaves desired, per LOD [{string.Join(' ', _world.LeavesPerLod.ToArray())}], {_world.LoadedChunks} loaded, {_world.MeshedChunks} with geometry, {_world.MeshBytes / 1048576.0:F1} MB){(_world.Settled ? "" : $" TIMED OUT, {_world.PendingJobs} jobs pending")}");
            (_phase, _phaseStart, _count) = (Phase.Flying, time, 0);
            (_leaves, _jobs, _selects, _collections, _maxMinLod, _maxMeshed) = (_world.LeavesLoaded, _world.JobsCompleted, _world.Selects, GC.CollectionCount(0), 0, 0);
            (_maxMeshBytes, _forced) = (0, _world.ForcedRetires);
            (_selectCount, _selectSeen, _bounded, _visited) = (0, _world.Selects, 0, 0);
            Array.Clear(_holeLods);
            _allocated = GC.GetTotalAllocatedBytes();
        }

        var t = time - _phaseStart;

        if (t > leg.Seconds || _count == MaxFrames)
        {
            Report(leg, t);
            _leg++;
            _phase = Phase.Start;
            Done = _leg == Legs.Length;
            return;
        }

        Speed = Place(leg, t);
    }

    /// <summary>After the frame: its main-thread times and holes against the view it drew.</summary>
    public void Record(in FrameSections sections, in Matrix4x4 viewProj)
    {
        if (Done || _phase != Phase.Flying || _count == MaxFrames)
            return;

        var update = _world.LastUpdate;
        ExtractPlanes(viewProj, _planes);
        _update[_count] = update.Select + update.Uploads + update.Stream;
        _work[_count] = sections.Work;
        _holes[_count] = _world.CountHoles(_planes);
        _desired[_count] = _world.DesiredLeaves;

        if (_world.Selects != _selectSeen)
        {
            _selectSeen = _world.Selects;
            _select[_selectCount++] = _world.LastSelectMs;
            _bounded += _world.LastSelectClassified;
            _visited += _world.LastSelectVisits;
        }

        _maxMinLod = Math.Max(_maxMinLod, _world.MinLod);
        _maxMeshed = Math.Max(_maxMeshed, _world.MeshedChunks);
        _maxMeshBytes = Math.Max(_maxMeshBytes, _world.MeshBytes);
        var lods = _world.HoleLods;

        for (var i = 0; i < lods.Length; i++)
            _holeLods[i] += lods[i];
        _count++;
    }

    /// <summary>Puts the camera at the leg's pose <paramref name="t"/> s in; returns the speed there (m/s).</summary>
    private double Place(in Leg leg, double t)
    {
        if (leg.Descent)
        {
            var rate = Math.Log(DescentTop / leg.Agl) / leg.Seconds;
            var altitude = DescentTop * Math.Exp(-rate * Math.Min(t, leg.Seconds));
            var up = _start;
            _camera.Position = up * (_surface.Radius + Ground(up) + altitude);
            _camera.LookAlong(_heading * Math.Cos(DescentPitch) + up * Math.Sin(DescentPitch));
            return altitude * rate;
        }

        var angle = leg.Speed * t / _surface.Radius;
        var (sin, cos) = Math.SinCos(angle);
        var dir = _start * cos + _heading * sin;
        var heading = _heading * cos - _start * sin;
        _camera.Position = dir * (_surface.Radius + Ground(dir) + leg.Agl);
        _camera.LookAlong(heading * Math.Cos(FlightPitch) + dir * Math.Sin(FlightPitch));
        return leg.Speed;
    }

    /// <summary>The generated ground or the sea, whichever is higher, about the datum.</summary>
    private double Ground(double3 dir) => Math.Max(_surface.Height(dir.X, dir.Y, dir.Z), _ground);

    private void Report(in Leg leg, double seconds)
    {
        if (_count == 0)
            return;

        var allocated = GC.GetTotalAllocatedBytes() - _allocated;
        var leaves = _world.LeavesLoaded - _leaves;
        var jobs = _world.JobsCompleted - _jobs;
        var holeFrames = 0;
        var maxHoles = 0;
        var firstHole = -1;
        var lastHole = -1;
        var desired = 0L;
        var maxDesired = 0;

        for (var i = 0; i < _count; i++)
        {
            holeFrames += _holes[i] > 0 ? 1 : 0;
            maxHoles = Math.Max(maxHoles, _holes[i]);
            firstHole = _holes[i] > 0 && firstHole < 0 ? i : firstHole;
            lastHole = _holes[i] > 0 ? i : lastHole;
            desired += _desired[i];
            maxDesired = Math.Max(maxDesired, _desired[i]);
        }

        var update = _update.AsSpan(0, _count).ToArray();
        var work = _work.AsSpan(0, _count).ToArray();
        var select = _select.AsSpan(0, Math.Max(_selectCount, 1)).ToArray();
        Array.Sort(update);
        Array.Sort(work);
        Array.Sort(select);

        Log.Info($"{leg.Name}: {_count} frames in {seconds:F1} s; leaves {leaves / seconds:F0}/s ({leaves}), chunk jobs {jobs / seconds:F0}/s ({jobs}), selections {_world.Selects - _selects}, finest speed LOD {_maxMinLod}, leaves desired avg {desired / _count} max {maxDesired}");
        Log.Info($"{leg.Name}: terrain main-thread ms p50 {Pick(update, 0.5):F3} p95 {Pick(update, 0.95):F3} p99 {Pick(update, 0.99):F3} max {update[^1]:F3}; frame work ms p50 {Pick(work, 0.5):F2} p95 {Pick(work, 0.95):F2} max {work[^1]:F2}; gen0 GCs {GC.CollectionCount(0) - _collections}, allocated {allocated / 1024} KB (all threads)");
        Log.Info($"{leg.Name}: selection ms worker p50 {Pick(select, 0.5):F2} p95 {Pick(select, 0.95):F2} max {select[^1]:F2} over {_selectCount} selections, {_bounded / Math.Max(_selectCount, 1)} of {_visited / Math.Max(_selectCount, 1)} nodes bounded a selection");
        Log.Info($"{leg.Name}: holes in {holeFrames} of {_count} frames (first {firstHole}, last {lastHole}), at most {maxHoles} leaves, leaf-frames per LOD [{string.Join(' ', _holeLods)}]; last selection {_world.LastSelectMs:F2} ms worker, {_world.LastSelectVisits} nodes ({_world.LastSelectClassified} bounded); with geometry max {_maxMeshed} ({_maxMeshBytes / 1048576.0:F1} MB), forced retires {_world.ForcedRetires - _forced}; collision bodies {_world.CollisionBodies}");
    }

    internal static float Pick(float[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    /// <summary>
    /// Frustum planes (n, d), n·p + d ≥ 0 inside, of a translation-free row-vector view-projection with infinite
    /// reverse-Z: four sides and the near plane.
    /// </summary>
    internal static void ExtractPlanes(in Matrix4x4 m, Span<Vector4> planes)
    {
        var c0 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c1 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c2 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c3 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        planes[0] = c3 + c0;
        planes[1] = c3 - c0;
        planes[2] = c3 + c1;
        planes[3] = c3 - c1;
        planes[4] = c3 - c2;
    }
}
