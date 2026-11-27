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
/// they jump. It follows the same points frame by frame through the regions' load and, after turning the swell, through
/// their rebake and cross-fade. The window stays open for captures.
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

    private enum Stage
    {
        Loading,
        Settled,
        Turning,
        Done,
    }

    private readonly SimWorld _sim;
    private readonly CoastRegionSet _coast;
    private readonly double3[] _points = new double3[MaxPoints];
    private readonly double3[] _watch = new double3[Watched];
    private readonly FacePoint[] _last = new FacePoint[Watched];
    private BreakerStability _frames;
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

    public SurfBeachTest(SimWorld sim, CoastRegionSet coast)
    {
        _sim = sim;
        _coast = coast;
    }

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

        if (_stage == Stage.Done)
            return;

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
            Log.Info($"through the load, frame by frame at {Watched} points: {_frames}");
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
        else if (_stage == Stage.Settled && time - _settledAt > TurnAfterSeconds)
        {
            var swell = _sim.Ocean.Swell.Wind;
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
            Log.Info($"through the rebake and cross-fade, frame by frame at {Watched} points: {_frames}");
            Stability();
            Seams(renderTime);
            _stage = Stage.Done;
        }
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
                var point = SurfCarrier.At(atlas, (float)x, (float)lz, atlas.PhaseAt(time));

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
        }

        _watching = true;
    }

    private void Watch(double renderTime)
    {
        for (var i = 0; i < Watched; i++)
        {
            _coast.Sample(_watch[i], renderTime, out var face);
            _frames.Step(_last[i], face);
            _last[i] = face;
        }
    }

    /// <summary>Logs the carrier's state every <see cref="ProfileStep"/> out to sea, and takes the rest points across the surf zone for <see cref="Stability"/>.</summary>
    private void Profile(double time)
    {
        var carrier = _coast.Carrier;
        Log.Info($"carrier: period {MathF.Tau / carrier.Omega:F1} s, deep-water height {carrier.Height:F2} m, direction {carrier.Direction * 180f / MathF.PI:F0} degrees (tile east toward south); Hs {_sim.Ocean.Swell.SignificantWaveHeight:F2} m");
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

            for (var i = 1; i <= Ticks; i++)
            {
                _coast.Sample(_points[p], (first + i) * dt, out var face);
                stats.Step(before, face);
                before = face;
            }
        }

        Log.Info($"per tick over {Ticks} ticks at {_pointCount} rest points across the surf zone ({Rows} rows {RowSpacing:F0} m apart, every {PointStep:F0} m): {stats}");
    }
}
