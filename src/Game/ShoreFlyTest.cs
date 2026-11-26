using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;

namespace BrutalGame;

/// <summary>
/// --shore-fly (M2b, docs/pivot/README.md): the camera stands off the spawn's beach, follows the planet's waterline
/// slowly and then fast (so the near depth window slides and the sea frame moves), then climbs until the surface has
/// faded into the far sea. Per leg it logs the ocean's GPU time, the main-thread frame work and what the depth maps
/// cost (jobs, far bakes, uploads). Closes the window when done.
/// </summary>
public sealed class ShoreFlyTest
{
    private static readonly LogCategory Log = LogCategory.Make("ShoreFly");

    private readonly record struct Leg(string Name, double Speed, double Height, double Offshore, double Seconds, double ClimbTo);

    private static readonly Leg[] Legs =
    [
        new("standing off the beach", 0.0, 6.0, 30.0, 10.0, 0.0),
        new("waterline at 15 m/s", 15.0, 8.0, 30.0, 30.0, 0.0),
        new("waterline at 150 m/s", 150.0, 60.0, 120.0, 30.0, 0.0),
        new("climb to 150 km", 0.0, 60.0, 120.0, 30.0, 150_000.0),
    ];

    /// <summary>The waterline is followed on the ground's features of 16 m and up (LOD 3), so the path is smooth.</summary>
    private const int PathLod = 3;
    private const double MaxStep = 8.0;
    private const double Bracket = 80.0;
    private const double GradientStep = 8.0;
    private const double TurnLength = 60.0;
    private const double LookAhead = 35.0 * Math.PI / 180.0;
    private const double LookPitch = -12.0 * Math.PI / 180.0;
    private const int MaxFrames = 1 << 15;

    private readonly FlyCamera _camera;
    private readonly PlanetSurface _surface;
    private readonly ShoreDepthGrid _depths;
    private readonly ShoreDepthTexture _depthTexture;
    private readonly GpuTimer _timer;
    private readonly int[] _scopes;
    private readonly float[][] _gpu;
    private readonly float[] _work = new float[MaxFrames];
    private readonly double _seaRadius;
    private double3 _dir;
    private double3 _along;
    private double3 _seaward;
    private int _leg = -1;
    private int _count;
    private double _legStart;
    private double _last;
    private int _jobs;
    private int _uploads;
    private int _seaFrames;
    private int _unsettled;
    private long _tiles;
    private float _maxUpload;
    private float _maxJob;

    /// <param name="scopes">GPU scopes to report: the ocean's draw, its FFT and its compute.</param>
    public ShoreFlyTest(FlyCamera camera, SimWorld sim, ShoreDepthGrid depths, ShoreDepthTexture depthTexture, GpuTimer timer, int[] scopes)
    {
        _camera = camera;
        _surface = sim.Generator.Surface;
        _depths = depths;
        _depthTexture = depthTexture;
        _timer = timer;
        _scopes = scopes;
        _gpu = new float[scopes.Length][];

        for (var i = 0; i < scopes.Length; i++)
            _gpu[i] = new float[MaxFrames];

        _seaRadius = sim.Planet.SeaRadius;
        _dir = double3.Normalize(sim.Bubble.Origin);
        _seaward = sim.Seaward;
        _along = double3.Cross(_dir, _seaward);
        Place(Legs[0], 0.0);
        LogProfile(sim.Shore);
    }

    /// <summary>The beach the test starts on: depth across the waterline, for reading the wash against.</summary>
    private void LogProfile(PlanetShore shore)
    {
        var profile = "";

        foreach (var x in (ReadOnlySpan<double>)[-80.0, -40.0, -20.0, -10.0, -5.0, 0.0, 5.0, 10.0, 20.0, 40.0, 80.0, 160.0, 320.0])
        {
            var d = Offset(_dir, _seaward, x);
            profile += $" {x:F0}: {shore.Depth(d.X, d.Y, d.Z):F2}";
        }

        Log.Info($"beach profile, metres out to sea from the spawn's waterline: depth m{profile}");
    }

    public bool Done { get; private set; }

    /// <summary>Before the frame's updates: moves the camera along the current leg. Legs start once the world has settled.</summary>
    public void Fly(double time, bool ready, int seaFrames)
    {
        if (Done)
            return;

        if (_leg < 0)
        {
            Place(Legs[0], 0.0);

            if (!ready)
                return;

            Start(0, time, seaFrames);
        }

        var leg = Legs[_leg];
        var t = time - _legStart;

        if (t > leg.Seconds)
        {
            Report(leg, t, seaFrames);

            if (_leg + 1 == Legs.Length)
            {
                Done = true;
                return;
            }

            Start(_leg + 1, time, seaFrames);
            leg = Legs[_leg];
            t = 0.0;
        }

        Advance(leg.Speed * Math.Min(time - _last, 0.1));
        _last = time;
        Place(leg, t);
    }

    /// <summary>After the frame: its GPU scopes (as of the last results read back), main-thread work and the depth maps' state.</summary>
    public void Record(in FrameSections sections, int tilesDrawn)
    {
        if (Done || _leg < 0 || _count == MaxFrames)
            return;

        for (var i = 0; i < _scopes.Length; i++)
            _gpu[i][_count] = _timer.Ring(_scopes[i]).Last;

        _work[_count++] = sections.Work;
        _tiles += tilesDrawn;
        _unsettled += _depths.Settled ? 0 : 1;
        _maxUpload = MathF.Max(_maxUpload, _depthTexture.LastUploadMs);
        _maxJob = MathF.Max(_maxJob, _depths.LastJobMs);
    }

    private void Start(int leg, double time, int seaFrames)
    {
        (_leg, _legStart, _last, _count) = (leg, time, time, 0);
        (_jobs, _uploads, _seaFrames, _unsettled, _tiles) = (_depths.Jobs, _depthTexture.Uploads, seaFrames, 0, 0);
        (_maxUpload, _maxJob) = (0f, 0f);
    }

    /// <summary>Moves the waterline point <paramref name="distance"/> m along the shore, in steps short enough to follow its bends.</summary>
    private void Advance(double distance)
    {
        while (distance > 0.0)
        {
            var step = Math.Min(distance, MaxStep);
            distance -= step;
            var d = double3.Normalize(_dir + _along * (step / _seaRadius));
            var seaward = Tangent(_seaward, d);

            // Back onto the waterline across the heading: land (above sea level) inshore, sea offshore.
            var inshore = -Bracket;
            var offshore = Bracket;

            if (Level(Offset(d, seaward, inshore)) > 0f && Level(Offset(d, seaward, offshore)) < 0f)
            {
                while (offshore - inshore > 0.25)
                {
                    var mid = 0.5 * (inshore + offshore);

                    if (Level(Offset(d, seaward, mid)) > 0f)
                        inshore = mid;
                    else
                        offshore = mid;
                }

                d = Offset(d, seaward, 0.5 * (inshore + offshore));
            }

            // The shore runs across the ground's gradient there; seaward is down it.
            var east = Tangent(_along, d);
            var across = double3.Cross(d, east);
            var gx = Level(Offset(d, east, GradientStep)) - Level(Offset(d, east, -GradientStep));
            var gz = Level(Offset(d, across, GradientStep)) - Level(Offset(d, across, -GradientStep));
            var down = -(east * gx + across * gz);

            if (down.LengthSquared() > 1e-12)
            {
                // Eased over TurnLength of travel, so the view doesn't swing with every bump of the shore.
                var along = double3.Cross(d, double3.Normalize(down));
                along = double3.Dot(along, _along) >= 0.0 ? along : -along;
                _along = Tangent(_along + (along - _along) * Math.Min(1.0, step / TurnLength), d);
                _seaward = double3.Dot(double3.Cross(_along, d), down) >= 0.0 ? double3.Cross(_along, d) : double3.Cross(d, _along);
            }

            _dir = d;
        }
    }

    private float Level(double3 d) => _surface.Height(d.X, d.Y, d.Z, PathLod) - _surface.SeaHeight;

    private double3 Offset(double3 d, double3 tangent, double metres) => double3.Normalize(d + tangent * (metres / _seaRadius));

    private static double3 Tangent(double3 v, double3 up) => double3.Normalize(v - up * double3.Dot(v, up));

    /// <summary>The camera off the waterline point, looking along the shore and in at the beach; the climb goes straight up from there.</summary>
    private void Place(in Leg leg, double t)
    {
        var seaward = Tangent(_seaward, _dir);
        var along = Tangent(_along, _dir);
        var height = leg.Height;
        var pitch = LookPitch;

        if (leg.ClimbTo > 0.0)
        {
            var s = Math.Clamp(t / leg.Seconds, 0.0, 1.0);
            height = leg.Height * Math.Pow(leg.ClimbTo / leg.Height, s);
            pitch = LookPitch + (-1.2 - LookPitch) * s;
        }

        var point = Offset(_dir, seaward, leg.Offshore);
        _camera.Position = point * (_seaRadius + height);
        var level = along * Math.Cos(LookAhead) - seaward * Math.Sin(LookAhead);
        _camera.LookAlong(level * Math.Cos(pitch) + point * Math.Sin(pitch));
    }

    private void Report(in Leg leg, double seconds, int seaFrames)
    {
        if (_count == 0)
            return;

        var work = _work.AsSpan(0, _count).ToArray();
        Array.Sort(work);
        var gpu = "";

        for (var i = 0; i < _scopes.Length; i++)
        {
            var samples = _gpu[i].AsSpan(0, _count).ToArray();
            Array.Sort(samples);
            gpu += $"{_timer.Name(_scopes[i])} p50 {PlanetFlyTest.Pick(samples, 0.5):F4} p99 {PlanetFlyTest.Pick(samples, 0.99):F4} max {samples[^1]:F4}; ";
        }

        Log.Info($"{leg.Name}: {_count} frames in {seconds:F1} s; GPU ms: {gpu}clipmap tiles drawn avg {_tiles / _count}");
        Log.Info($"{leg.Name}: frame work ms p50 {PlanetFlyTest.Pick(work, 0.5):F2} p95 {PlanetFlyTest.Pick(work, 0.95):F2} max {work[^1]:F2}; sea frames {seaFrames - _seaFrames}, depth jobs {_depths.Jobs - _jobs} (slowest {_maxJob:F1} ms worker), last far bake {_depths.LastFarBakeMs:F0} ms worker, uploads {_depthTexture.Uploads - _uploads} (max {_maxUpload:F3} ms), frames with depth jobs pending {_unsettled}");
    }
}
