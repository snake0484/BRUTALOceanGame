using System.Numerics;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame;

/// <summary>
/// --moon-test (M2, planets.md "Detail ladder"): once the spawn has loaded, the camera moves into the moon's surface frame
/// 1e9 m out on its day side and falls to 30 m above its ground in 60 s (altitude shrinking exponentially), looking at it,
/// then holds. The moon goes point of light → disc → impostor → octree; the focus moves from the planet to the moon on
/// the way and its 1024² impostor bakes on a worker. Logs per ladder stage the GPU ms of points, discs, impostors and
/// terrain, the frame and main-thread ms, and holes (frames where a desired leaf in view has nothing loaded over it, and
/// how many of those the impostor did not cover); then when the focus switched, the large impostor arrived and the
/// octree settled. PASS needs all three before the end and no uncovered hole above <see cref="DetailLadder.SpaceHigh"/>.
/// </summary>
public sealed class MoonTest
{
    private const double StartDistance = 1e9;
    private const double EndAltitude = 30.0;
    private const double Seconds = 60.0;
    private const double Hold = 6.0;
    private const double ApproachTilt = 0.8;
    private const double LookTilt = 0.35;
    private const int MaxFrames = 1 << 15;
    private const int Bands = 8;
    private const int LowBands = 5;

    private static readonly LogCategory Log = LogCategory.Make("MoonTest");
    private static readonly string[] BandNames = ["point", "point-disc", "disc", "relief fade-in", "impostor", "switch band", "octree, backdrop", "octree, ground"];

    private readonly FlyCamera _camera;
    private readonly FrameTree _frames;
    private readonly TerrainWorld _terrain;
    private readonly CelestialView _celestial;
    private readonly GpuTimer _timer;
    private readonly int _terrainScope;
    private readonly int _moon = -1;
    private readonly int _star = -1;
    private readonly FrameId _moonSurface;
    private readonly FrameId _starSurface;
    private readonly double _radius;
    private readonly double _base;
    private readonly PlanetSurface? _surface;

    private readonly float[] _time = new float[MaxFrames];
    private readonly double[] _altitude = new double[MaxFrames];
    private readonly float[] _diameter = new float[MaxFrames];
    private readonly Vector4[] _gpu = new Vector4[MaxFrames];
    private readonly float[] _frameMs = new float[MaxFrames];
    private readonly float[] _workMs = new float[MaxFrames];
    private readonly int[] _holes = new int[MaxFrames];
    private readonly bool[] _covered = new bool[MaxFrames];
    private readonly int[] _drawn = new int[MaxFrames];
    private readonly Vector4[] _planes = new Vector4[5];
    private double3 _dir;
    private double3 _look;
    private double _ground;
    private double _start = -1.0;
    private int _count;
    private int _switchFrame = -1;
    private int _upgradeFrame = -1;
    private int _settledFrame = -1;

    public MoonTest(FlyCamera camera, SimWorld sim, TerrainWorld terrain, CelestialView celestial, GpuTimer timer, int terrainScope)
    {
        _camera = camera;
        _frames = sim.Frames;
        _terrain = terrain;
        _celestial = celestial;
        _timer = timer;
        _terrainScope = terrainScope;

        for (var i = 0; i < sim.Celestial.Count; i++)
        {
            ref readonly var body = ref sim.Celestial[i];

            if (body.Kind == CelestialKind.Star && _star < 0)
            {
                _star = i;
                _starSurface = body.Surface;
            }
            else if (body.Kind == CelestialKind.Moon && _moon < 0 && sim.BodyParams[i] is { } p)
            {
                _moon = i;
                _moonSurface = body.Surface;
                _radius = body.Radius;
                _base = p.HasSea ? p.SeaHeight : 0.0;
                _surface = new PlanetSurface(p);
            }
        }

        if (_moon < 0 || _star < 0)
            throw new InvalidOperationException("--moon-test needs a star and a moon.");
    }

    public bool Done { get; private set; }

    /// <summary>The fall's speed over its height above the ground (1/s).</summary>
    private double FallRate => Math.Log((StartDistance - _radius - _ground) / EndAltitude) / Seconds;

    /// <summary>Before the frame: places the camera on the fall once the world is ready.</summary>
    public void Fly(double time, bool ready)
    {
        if (Done)
            return;

        if (_start < 0.0)
        {
            if (!ready)
                return;

            Begin();
            _start = time;
        }

        var s = Math.Clamp((time - _start) / Seconds, 0.0, 1.0);
        var top = StartDistance - _radius - _ground;
        var agl = top * Math.Pow(EndAltitude / top, s);
        _camera.Position = _dir * (_radius + _ground + agl);
        _camera.LookAlong(_look);
    }

    /// <summary>After the frame: its timings, the moon's size on screen, holes against the view it drew.</summary>
    /// <param name="covered">The focus body's impostor drew opaque this frame (in front of the octree or behind it).</param>
    public void Record(double time, float frameMs, float workMs, int terrainDrawn, in Matrix4x4 viewProj, float viewportHeight, bool covered)
    {
        if (Done || _start < 0.0)
            return;

        var t = time - _start;

        if (t > Seconds + Hold || _count == MaxFrames)
        {
            Report();
            Done = true;
            return;
        }

        var i = _count++;
        var distance = _camera.Position.Length();
        var moonFocus = _celestial.Focus == _moon;
        _time[i] = (float)t;
        _altitude[i] = distance - _radius;
        _diameter[i] = DetailLadder.PixelDiameter(_radius + _base, distance, 2.0 * Math.Tan(_camera.FovY * 0.5) / viewportHeight);
        _gpu[i] = new Vector4(_timer.Ring(_celestial.PointScope).Last, _timer.Ring(_celestial.DiscScope).Last, _timer.Ring(_celestial.ImpostorScope).Last,
            terrainDrawn > 0 ? _timer.Ring(_terrainScope).Last : 0f);
        _frameMs[i] = frameMs;
        _workMs[i] = workMs;
        _drawn[i] = terrainDrawn;
        PlanetFlyTest.ExtractPlanes(viewProj, _planes);
        _holes[i] = moonFocus && _terrain.Focus == _celestial.FocusTerrainBody ? _terrain.CountHoles(_planes) : 0;
        _covered[i] = covered;

        if (_switchFrame < 0 && moonFocus)
            _switchFrame = i;

        if (_upgradeFrame < 0 && moonFocus && _celestial.FocusUpgraded)
            _upgradeFrame = i;

        if (_settledFrame < 0 && moonFocus && _terrain.Settled && _terrain.Focus == _celestial.FocusTerrainBody)
            _settledFrame = i;
    }

    /// <summary>Into the moon's surface frame, 1e9 m out between the sun and the terminator, looking just past its centre.</summary>
    private void Begin()
    {
        _frames.TryRelativeRender(_starSurface, _moonSurface, out var star);
        var sun = double3.Normalize(star.Position);
        var side = double3.Normalize(double3.Cross(sun, Math.Abs(sun.Y) < 0.9 ? double3.UnitY : double3.UnitX));
        _dir = double3.Normalize(sun + side * ApproachTilt);
        _look = double3.Normalize(-_dir + double3.Normalize(double3.Cross(_dir, side)) * LookTilt);
        _ground = PlanetImpostor.VisibleHeight(_surface!, _dir.X, _dir.Y, _dir.Z);
        _camera.Frame = _moonSurface;
        _camera.Position = _dir * StartDistance;
        _camera.LookAlong(_look);
    }

    private static int Band(double altitude, float diameter)
    {
        if (altitude < DetailLadder.SpaceHigh)
            return 7;

        if (altitude < DetailLadder.SwitchLow)
            return 6;

        if (altitude < DetailLadder.SwitchHigh)
            return 5;

        if (diameter >= DetailLadder.ReliefFull)
            return 4;

        if (diameter >= DetailLadder.ReliefStart)
            return 3;

        return diameter >= DetailLadder.DiscFull ? 2 : diameter >= DetailLadder.PointFull ? 1 : 0;
    }

    private void Report()
    {
        Log.Info($"moon test: {_count} frames over {Seconds + Hold:F0} s, {StartDistance:E0} m from the moon's centre to {EndAltitude} m above its ground; moon radius {_radius / 1000.0:F0} km");
        var seen = 0;
        Log.Info("stage             frames  altitude km (max..min)  diameter px  gpu points p50/max  gpu discs p50/max  gpu impostors p50/max  gpu terrain p50/max  terrain drawn max  frame p50 ms  work p50 ms  hole frames  uncovered");

        for (var band = 0; band < Bands; band++)
        {
            var points = new List<float>();
            var discs = new List<float>();
            var impostors = new List<float>();
            var terrain = new List<float>();
            var frame = new List<float>();
            var work = new List<float>();
            double hi = 0.0, lo = double.MaxValue;
            float dLo = float.MaxValue, dHi = 0f;
            var drawn = 0;
            var holes = 0;
            var uncovered = 0;

            for (var i = 0; i < _count; i++)
            {
                if (Band(_altitude[i], _diameter[i]) != band)
                    continue;

                points.Add(_gpu[i].X);
                discs.Add(_gpu[i].Y);
                impostors.Add(_gpu[i].Z);
                terrain.Add(_gpu[i].W);
                frame.Add(_frameMs[i]);
                work.Add(_workMs[i]);
                (hi, lo) = (Math.Max(hi, _altitude[i]), Math.Min(lo, _altitude[i]));
                (dLo, dHi) = (MathF.Min(dLo, _diameter[i]), MathF.Max(dHi, _diameter[i]));
                drawn = Math.Max(drawn, _drawn[i]);
                holes += _holes[i] > 0 ? 1 : 0;
                uncovered += _holes[i] > 0 && !_covered[i] ? 1 : 0;
            }

            seen += band < Bands - 1 ? uncovered : 0;

            if (frame.Count == 0)
            {
                Log.Info($"{BandNames[band],-17} no frames");
                continue;
            }

            Log.Info($"{BandNames[band],-17} {frame.Count,6}  {hi / 1000.0,10:G4}..{lo / 1000.0,-10:G4}  {dLo,5:F1}..{dHi,-7:F0}  {Stat(points)}  {Stat(discs)}  {Stat(impostors)}  {Stat(terrain)}  {drawn,17}  {Median(frame),12:F2}  {Median(work),11:F2}  {holes,11}  {uncovered,9}");
        }

        Span<int> low = stackalloc int[LowBands];

        for (var i = 0; i < _count; i++)
        {
            if (_holes[i] > 0 && _altitude[i] < DetailLadder.SpaceHigh)
                low[Math.Clamp((int)Math.Floor(Math.Log10(Math.Max(_altitude[i] - _ground, 1.0))), 0, LowBands - 1)]++;
        }

        Log.Info($"hole frames below {DetailLadder.SpaceHigh / 1000.0:F0} km by decade of height above ground (1-10 m .. 10-100 km): {low[0]} {low[1]} {low[2]} {low[3]} {low[4]}; the fall is {FallRate:F3} of that height a second");
        Log.Info($"at the end: {_holes[_count - 1]} holes, per LOD [{string.Join(' ', _terrain.HoleLods.ToArray())}], settled {_terrain.Settled}, desired {_terrain.DesiredLeaves}, loaded {_terrain.LoadedChunks}, meshed {_terrain.MeshedChunks}, pending {_terrain.PendingJobs}, dirty {_terrain.DirtyChunks}, finest LOD {_terrain.MinLod}, forced retires {_terrain.ForcedRetires}");

        var pass = _switchFrame >= 0 && _upgradeFrame >= 0 && _settledFrame >= 0 && seen == 0;
        Log.Info($"focus to the moon at {At(_switchFrame)}; its {PlanetImpostor.FocusSize}² impostor drawn from {At(_upgradeFrame)} (bake {_celestial.FocusBakeMs:F0} ms on {Engine.Jobs.WorkerBudget.Side} worker(s)); octree settled at {At(_settledFrame)}; base bakes {_celestial.BaseBakeMs:F0} ms; terrain focus switches {_terrain.FocusSwitches}");
        Log.Info($"moon test {(pass ? "PASS" : "FAIL")}: focus switch {Mark(_switchFrame)}, large impostor {Mark(_upgradeFrame)}, settled octree {Mark(_settledFrame)}, {seen} frames with a hole the impostor did not cover above {DetailLadder.SpaceHigh / 1000.0:F0} km");
    }

    private static string Mark(int frame) => frame >= 0 ? "reached" : "missed";

    private string At(int frame) => frame < 0 ? "never" : $"{_time[frame]:F1} s, {_altitude[frame] / 1000.0:F0} km altitude";

    private static string Stat(List<float> values)
    {
        values.Sort();
        return $"{Median(values),9:F4}/{values[^1],-8:F4}";
    }

    private static float Median(List<float> sorted)
    {
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }
}
