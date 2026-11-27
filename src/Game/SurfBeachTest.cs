using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Sim;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// --surf-beach (breaking-waves-brief.md §8): once the coast regions round the spawn's beach have loaded it logs the
/// carrier and the break's profile out to sea, then judges the drawn face in motion: at fixed rest points across the
/// surf zone, over <see cref="Ticks"/> ticks, how far its height, normal and whitewater move in a tick and how often
/// they jump or kick. It follows the same points tick by tick through the regions' load and, after turning the swell,
/// through their rebake and cross-fade, then logs the bakes every <see cref="BakeLogSeconds"/>. With --surf-climb it
/// instead takes the camera from <see cref="ClimbLow"/> to <see cref="ClimbHigh"/> over the surf and back, judging the
/// face as the grid under it draws it. With the weather driving the sea it does not turn the swell: it keeps following the
/// points through every rebake the weather causes (a storm moving the carrier past the tolerance), logging each one and,
/// every <see cref="BakeLogSeconds"/>, the spawn tile's wind, the carrier and the rebakes so far. The swell under the
/// points is judged tick by tick alongside the face. The window stays open for captures.
/// </summary>
public sealed class SurfBeachTest
{
    private static readonly LogCategory Log = LogCategory.Make("SurfBeach");

    private const int Ticks = 600;
    private const float ProfileReach = 400f;
    private const float ProfileStep = 10f;
    private const float PointStep = 2f;
    private const float RowSpacing = 60f;
    private const int Rows = 5;
    private const int MaxPoints = 1024;
    private const int Watched = 5;
    private const float TurnRadians = 15f * MathF.PI / 180f;
    private const double TurnAfterSeconds = 4.0;
    private const int MaxBakes = 64;
    private const float SeamReach = 2600f;
    private const float SeamStep = 8f;
    private const double BakeLogSeconds = 60.0;

    /// <summary>--surf-climb: the camera climbs from the launch time below, a constant share of its height a second, and comes back down.</summary>
    public const double ClimbStartSeconds = 30.0;
    private const double ClimbSeconds = 30.0;
    private const float ClimbLow = 10f;
    private const float ClimbHigh = 4000f;
    private const float ClimbX = 60f;
    private const float ClimbZ = -40f;
    private const int ClimbPoints = 6;
    private const float ClimbPointStep = 20f;

    private enum Stage
    {
        Loading,
        Settled,
        Turning,
        Done,
        Climbing,
        Weather,
    }

    private readonly SimWorld _sim;
    private readonly CoastRegionSet _coast;
    private readonly double3[] _points = new double3[MaxPoints];
    private readonly double3[] _watch = new double3[Watched];
    private readonly FacePoint[] _last = new FacePoint[Watched];
    private readonly FacePoint[] _first = new FacePoint[Watched];
    private readonly bool _climb;
    private BreakerStability _frames;
    private double _climbStart;
    private double _bakesLogged;
    private double _watchNext;
    private int _watched;
    private Stage _stage;
    private int _pointCount;
    private readonly float[] _bakeMs = new float[MaxBakes];
    private readonly float[] _depthMs = new float[MaxBakes];
    private int _bakes;
    private int _bakeCount;
    private int _turnBakes;
    private long _allocated;
    private int _frameCount;
    private double _settledAt;
    private bool _watching;
    private readonly float[] _swell0 = new float[Watched];
    private readonly float[] _swell1 = new float[Watched];
    private float _swellKick;
    private float _swellStep;
    private CoastCarrier _weatherCarrier;
    private int _weatherRebakes;
    private bool _weatherPending;
    private double _weatherFrom;

    public SurfBeachTest(SimWorld sim, CoastRegionSet coast, bool climb)
    {
        _sim = sim;
        _coast = coast;
        _climb = climb;
    }

    /// <summary>The camera is the climb's this frame: at <see cref="CameraPosition"/> of the spawn's bubble, looking down along <see cref="CameraPitch"/>.</summary>
    public bool Climbing => _stage == Stage.Climbing;

    public System.Numerics.Vector3 CameraPosition { get; private set; }

    public float CameraPitch => -1.2f;

    /// <summary>Once per frame after the coast regions' update.</summary>
    /// <param name="time">Seconds since launch.</param>
    /// <param name="allocated">Bytes the main thread allocated this frame in the coast regions' update and upload.</param>
    public void Update(double time, double renderTime, bool worldReady, long allocated)
    {
        _allocated += allocated;
        _frameCount++;

        if (_coast.Bakes != _bakes)
        {
            _bakes = _coast.Bakes;

            if (_bakeCount < MaxBakes)
            {
                _bakeMs[_bakeCount] = _coast.LastBakeMs;
                _depthMs[_bakeCount++] = _coast.LastBakeDepthMs;
            }
        }

        if (_stage == Stage.Weather)
        {
            Watch(renderTime);
            FollowWeather(time);
            return;
        }

        if (_stage == Stage.Done)
        {
            if (time - _bakesLogged >= BakeLogSeconds)
            {
                _bakesLogged = time;
                var carrier = _coast.Carrier;
                Log.Info($"at {time:F0} s: {_coast.Bakes} bakes so far, {_coast.Drawn} regions drawn; carrier period {(carrier.Calm ? 0f : MathF.Tau / carrier.Omega):F2} s, height {carrier.Height:F2} m, direction {carrier.Direction * 180f / MathF.PI:F1} degrees; main thread allocated {_allocated} bytes in {_frameCount} frames");
            }

            return;
        }

        if (_stage == Stage.Climbing)
        {
            Climb(time);
            return;
        }

        if (_watching)
            Watch(renderTime);

        var settled = worldReady && _coast.Settled && _coast.Drawn > 0;

        if (_stage == Stage.Loading)
        {
            if (!_watching && _coast.Drawn > 0)
                StartWatch(renderTime);

            if (!settled)
                return;

            Log.Info($"regions loaded: {_coast.Drawn} drawn, {_coast.Probes} probes; {Bakes()}; {CoastRegionSet.ResidentBytes / 1048576.0:F1} MB resident on the CPU; main thread allocated {_allocated} bytes in {_frameCount} frames of region update and upload");
            Log.Info($"through the load, tick by tick at {Watched} points: {_frames}");
            Profile(renderTime);
            Stability();
            Seams(renderTime);
            _frames = default;
            _bakeCount = 0;
            _allocated = 0;
            _frameCount = 0;
            _settledAt = time;
            _stage = Stage.Settled;
        }
        else if (_stage == Stage.Settled && _climb)
        {
            if (time < ClimbStartSeconds)
                return;

            _climbStart = time;
            _stage = Stage.Climbing;
            Log.Info($"climb starts at {time:F1} s: {ClimbLow:F0} m to {ClimbHigh:F0} m and back, {ClimbSeconds:F0} s each way, over {ClimbPoints} points across the surf");
            ClimbStability(renderTime);
        }
        else if (_stage == Stage.Settled && _sim.DriveWind)
        {
            _frames = default;
            (_swellKick, _swellStep) = (0f, 0f);
            _weatherCarrier = _coast.Carrier;
            _bakesLogged = time;
            _stage = Stage.Weather;
            Log.Info($"weather drives the sea: following the points through its rebakes; spawn tile wind {_sim.SpawnWind.Speed} m/s");
        }
        else if (_stage == Stage.Settled && time - _settledAt > TurnAfterSeconds)
        {
            var swell = _sim.Ocean.Swells.Base;
            swell.SwellDirection += TurnRadians;
            swell.WindDirection += TurnRadians;
            _sim.SetSwell(swell);
            _frames = default;
            _turnBakes = _coast.Bakes;
            _stage = Stage.Turning;
            Log.Info($"swell turned {TurnRadians * 180f / MathF.PI:F0} degrees: the regions bake again");
        }
        else if (_stage == Stage.Turning && settled && _coast.Bakes > _turnBakes && SameCarrier())
        {
            Log.Info($"rebaked: {Bakes()}; main thread allocated {_allocated} bytes in {_frameCount} frames of region update and upload");
            Log.Info($"through the rebake and cross-fade, tick by tick at {Watched} points: {_frames}");
            Stability();
            Seams(renderTime);
            _allocated = 0;
            _frameCount = 0;
            _bakesLogged = time;
            _stage = Stage.Done;
        }
    }

    /// <summary>A rebake starts when the carrier the regions are baked for moves, and ends once every drawn region has it and nothing fades.</summary>
    private void FollowWeather(double time)
    {
        var carrier = _coast.Carrier;

        if (carrier != _weatherCarrier)
        {
            Log.Info($"at {time:F0} s (weather {_sim.WeatherTime / 60.0:F1} min): carrier {Describe(_weatherCarrier)} -> {Describe(carrier)}; spawn tile wind {_sim.SpawnWind.Speed} m/s {_sim.SpawnWind.Direction * 10} deg");

            if (!_weatherPending)
            {
                _weatherPending = true;
                _weatherFrom = time;
                _frames = default;
                (_swellKick, _swellStep) = (0f, 0f);
            }

            _weatherCarrier = carrier;
            _weatherRebakes++;
        }

        if (_weatherPending && _coast.Settled && SameCarrier())
        {
            _weatherPending = false;
            Log.Info($"rebake settled after {time - _weatherFrom:F1} s, tick by tick at {Watched} points: face {_frames}; swell height step max {_swellStep:F4} m, second difference max {_swellKick:F4} m");
            _frames = default;
            (_swellKick, _swellStep) = (0f, 0f);
        }

        if (time - _bakesLogged >= BakeLogSeconds)
        {
            _bakesLogged = time;
            var swell = _sim.Ocean.Swells.Near(_sim.Bubble.Origin);
            Log.Info($"at {time:F0} s (weather {_sim.WeatherTime / 60.0:F1} min): spawn tile wind {_sim.SpawnWind.Speed} m/s, Hs {swell.SignificantWaveHeight:F2} m; carrier {Describe(carrier)}; {_weatherRebakes} weather rebakes, {_coast.Bakes} bakes; main thread allocated {_allocated} bytes in {_frameCount} frames");

            if (!_weatherPending)
            {
                Log.Info($"steady since last: face {_frames}; swell height step max {_swellStep:F4} m, second difference max {_swellKick:F4} m");
                _frames = default;
                (_swellKick, _swellStep) = (0f, 0f);
            }
        }
    }

    private static string Describe(CoastCarrier c) => c.Calm ? "calm" : $"T {MathF.Tau / c.Omega:F2} s H {c.Height:F2} m dir {c.Direction * 180f / MathF.PI:F0}";

    private static float ClimbAltitude(double t) => ClimbLow * MathF.Pow(ClimbHigh / ClimbLow, (float)(t < 1.0 ? t : 2.0 - t));

    /// <summary>One frame of the climb: the camera's place, for the captures.</summary>
    private void Climb(double time)
    {
        var t = (time - _climbStart) / ClimbSeconds;

        if (t >= 2.0)
        {
            _allocated = 0;
            _frameCount = 0;
            _bakesLogged = time;
            _stage = Stage.Done;
            return;
        }

        CameraPosition = new System.Numerics.Vector3(ClimbX, ClimbAltitude(t), ClimbZ);
    }

    /// <summary>
    /// The climb judged tick by tick (the regions stand, so it is a pure function of the tick). At points across the surf
    /// below the camera the face is taken twice: as it is drawn, filtered at the spacing the camera's height calls for
    /// (never under the grid's own there), and at the grid's own spacing alone, which steps where the finest level
    /// drawn does (as it was drawn before). The CPU mirror of the shader's filter: the GPU is not read back.
    /// </summary>
    private void ClimbStability(double renderTime)
    {
        var dt = _sim.Clock.TickSeconds;
        var ticks = (int)(2.0 * ClimbSeconds / dt);
        var drawnStats = default(BreakerStability);
        var gridStats = default(BreakerStability);
        Span<FacePoint> last = stackalloc FacePoint[2 * ClimbPoints];
        Span<FacePoint> first = stackalloc FacePoint[2 * ClimbPoints];
        var before = -1;

        for (var tick = 0; tick <= ticks; tick++)
        {
            var altitude = ClimbAltitude(tick * dt / ClimbSeconds);
            var level = Water.Rendering.OceanRenderer.FirstLevel(altitude);
            var worstDrawn = 0f;
            var worstGrid = 0f;

            for (var i = 0; i < ClimbPoints; i++)
            {
                var x = ClimbX - 0.5f * (ClimbPoints - 1) * ClimbPointStep + i * ClimbPointStep;
                var point = Sea(x, ClimbZ);
                var grid = Water.Rendering.OceanRenderer.GridSpacing(altitude, MathF.Abs(x - ClimbX));
                _coast.Sample(point, renderTime + tick * dt, out var drawn, MathF.Max(grid, Water.Rendering.OceanRenderer.FaceSpacing(altitude)));
                _coast.Sample(point, renderTime + tick * dt, out var stepped, grid);
                var g = ClimbPoints + i;

                if (tick > 0)
                {
                    worstDrawn = MathF.Max(worstDrawn, MathF.Max(MathF.Abs(drawn.Height - last[i].Height), MathF.Abs(drawn.Foam - last[i].Foam)));
                    worstGrid = MathF.Max(worstGrid, MathF.Max(MathF.Abs(stepped.Height - last[g].Height), MathF.Abs(stepped.Foam - last[g].Foam)));
                    drawnStats.Step(last[i], drawn);
                    gridStats.Step(last[g], stepped);
                }

                if (tick > 1)
                {
                    drawnStats.Kick(first[i], last[i], drawn);
                    gridStats.Kick(first[g], last[g], stepped);
                }

                (first[i], last[i]) = (last[i], drawn);
                (first[g], last[g]) = (last[g], stepped);
            }

            if (level != before && before >= 0)
                Log.Info($"at {altitude:F0} m the finest level goes {before} to {level}: largest step of height (m) or whitewater that tick {worstDrawn:F3} as drawn, {worstGrid:F3} at the grid's own spacing");

            before = level;
        }

        Log.Info($"climb, {ticks} ticks at {ClimbPoints} points. As drawn: {drawnStats}");
        Log.Info($"the same at the grid's own spacing: {gridStats}");
    }

    /// <summary>The bakes since the last report: count, median and slowest worker time, and the depth sampling's share.</summary>
    private string Bakes()
    {
        if (_bakeCount == 0)
            return "no bakes";

        var ms = _bakeMs.AsSpan(0, _bakeCount);
        var depth = _depthMs.AsSpan(0, _bakeCount);
        ms.Sort(depth);
        return $"{_bakeCount} bakes, worker ms median {ms[_bakeCount / 2]:F1} (depth sampling {depth[_bakeCount / 2]:F1}) slowest {ms[^1]:F1} (depth sampling {depth[^1]:F1})";
    }

    /// <summary>
    /// Near and far along the shore, 60 m out: how much of the line the regions cover, and where two of them overlap how
    /// far apart their carriers' phases and heights are (each is solved alone, inside its own coarse domain).
    /// </summary>
    private void Seams(double time)
    {
        var worstPhase = 0f;
        var meanPhase = 0f;
        var worstHeight = 0f;
        var overlaps = 0;
        var covered = 0;
        var total = 0;
        var first = float.NaN;
        var last = float.NaN;

        for (var z = -SeamReach; z <= SeamReach; z += SeamStep)
        {
            var dir = Sea(60f, z);
            var count = 0;
            SurfCarrierPoint a = default;
            SurfCarrierPoint b = default;
            total++;

            for (var slot = 0; slot < CoastRegionSet.Capacity; slot++)
            {
                ref readonly var region = ref _coast.Regions[slot];

                if (!region.Drawn)
                    continue;

                region.Window.ToLocal(dir, out var x, out var lz);

                if (CoastWindow.Weight((float)x / CoastWindow.CellSize, (float)lz / CoastWindow.CellSize) < 0.1f)
                    continue;

                var atlas = _coast.Atlas(slot);
                var point = SurfCarrier.At(atlas, (float)x, (float)lz, _coast.PhaseAt(slot, time));

                if (count++ == 0)
                    a = point;
                else
                    b = point;
            }

            if (count > 0)
            {
                covered++;
                first = float.IsNaN(first) ? z : first;
                last = z;
            }

            if (count < 2)
                continue;

            var phase = MathF.Abs(SurfCarrier.Wrap(a.Phase - b.Phase));
            worstPhase = MathF.Max(worstPhase, phase);
            meanPhase += phase;
            worstHeight = MathF.Max(worstHeight, MathF.Abs(a.Unbroken - b.Unbroken));
            overlaps++;
        }

        Log.Info($"along the shore 60 m out, {-SeamReach:F0} .. {SeamReach:F0} m: regions cover {first:F0} .. {last:F0} m ({covered} of {total} samples); in {overlaps} samples two regions overlap, their carriers' phases apart by mean {(overlaps > 0 ? meanPhase / overlaps : 0f):F2} rad, worst {worstPhase:F2} rad, heights by at most {worstHeight:F2} m");
    }

    private bool SameCarrier()
    {
        foreach (var region in _coast.Regions)
        {
            if (region.Drawn && region.Key.Carrier != _coast.Carrier)
                return false;
        }

        return true;
    }

    /// <summary>The point of the sea at (x, z) of the spawn's bubble: +x out to sea from the waterline.</summary>
    private double3 Sea(float x, float z) => double3.Normalize(_sim.Bubble.ToPlanet(new System.Numerics.Vector3(x, 0f, z)));

    private void StartWatch(double renderTime)
    {
        for (var i = 0; i < Watched; i++)
        {
            _watch[i] = Sea(20f + 25f * i, 15f * i);
            _coast.Sample(_watch[i], renderTime, out _last[i]);
            _swell0[i] = _swell1[i] = float.NaN;
        }

        _watching = true;
        _watched = 0;
        _watchNext = renderTime + _sim.Clock.TickSeconds;
    }

    /// <summary>Frames are not evenly spaced, so the points are taken at every tick's time the frame has reached, against the regions as they stand that frame.</summary>
    private void Watch(double renderTime)
    {
        var dt = _sim.Clock.TickSeconds;

        for (; _watchNext <= renderTime; _watchNext += dt)
        {
            for (var i = 0; i < Watched; i++)
            {
                _coast.Sample(_watch[i], _watchNext, out var face);
                _frames.Step(_last[i], face);

                if (_watched > 0)
                    _frames.Kick(_first[i], _last[i], face);

                (_first[i], _last[i]) = (_last[i], face);
                SwellStep(i, _sim.Ocean.SampleHeight(_watch[i] * _sim.Ocean.Tiles.SeaRadius, (long)Math.Round(_watchNext / dt)));
            }

            _watched++;
        }
    }

    /// <summary>The open swell (OceanSurface, what buoyancy reads) at a watched point: its largest step and second difference.</summary>
    private void SwellStep(int i, float h)
    {
        if (!float.IsNaN(_swell1[i]))
            _swellStep = MathF.Max(_swellStep, MathF.Abs(h - _swell1[i]));

        if (!float.IsNaN(_swell0[i]))
            _swellKick = MathF.Max(_swellKick, MathF.Abs(h - 2f * _swell1[i] + _swell0[i]));

        (_swell0[i], _swell1[i]) = (_swell1[i], h);
    }

    /// <summary>Logs the carrier's state every <see cref="ProfileStep"/> out to sea, and takes the rest points across the surf zone for <see cref="Stability"/>.</summary>
    private void Profile(double time)
    {
        var carrier = _coast.Carrier;
        Log.Info($"carrier: period {MathF.Tau / carrier.Omega:F1} s, deep-water height {carrier.Height:F2} m, direction {carrier.Direction * 180f / MathF.PI:F0} degrees (tile east toward south); Hs {_sim.Ocean.Swells.Near(_sim.Bubble.Origin).SignificantWaveHeight:F2} m");
        var line = "";
        var outer = 0f;

        for (var x = 0f; x <= ProfileReach; x += ProfileStep)
        {
            if (!_coast.CarrierAt(Sea(x, 0f), time, out var c))
                continue;

            var cap = SurfCarrier.RawCap(c.Unbroken, c.Depth);
            line += $" {x:F0}: h {c.Depth:F2} H {c.Unbroken:F2} cap {MathF.Min(cap, 9.99f):F2} clock {c.BreakDistance:F0} plunge {c.Plunge:F2} L {MathF.Tau / c.K:F0};";

            if (cap < 1.3f || c.BreakDistance > 0f)
                outer = x;
        }

        Log.Info($"break profile, metres out to sea from the spawn's waterline (depth, unbroken height, breaking cap, break clock m, plunge, wavelength):{line}");
        Log.Info($"surf zone reaches {outer:F0} m out");
        _pointCount = 0;

        for (var row = 0; row < Rows; row++)
        {
            for (var x = 2f; x <= outer + 20f && _pointCount < MaxPoints; x += PointStep)
                _points[_pointCount++] = Sea(x, (row - Rows / 2) * RowSpacing);
        }
    }

    /// <summary>The blended face at every rest point over <see cref="Ticks"/> ticks from now: a pure function of the tick while the regions stand.</summary>
    private void Stability()
    {
        var stats = default(BreakerStability);
        var dt = _sim.Clock.TickSeconds;
        var first = _sim.Clock.TickIndex;

        for (var p = 0; p < _pointCount; p++)
        {
            _coast.Sample(_points[p], first * dt, out var before);
            var earlier = before;

            for (var i = 1; i <= Ticks; i++)
            {
                _coast.Sample(_points[p], (first + i) * dt, out var face);
                stats.Step(before, face);

                if (i > 1)
                    stats.Kick(earlier, before, face);

                (earlier, before) = (before, face);
            }
        }

        Log.Info($"per tick over {Ticks} ticks at {_pointCount} rest points across the surf zone ({Rows} rows {RowSpacing:F0} m apart, every {PointStep:F0} m): {stats}");
    }
}
