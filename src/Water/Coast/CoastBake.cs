using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brutal.Concurrency.Jobs;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

/// <summary>
/// Bakes one coast region's <see cref="CoastAtlas"/> for its carrier, off the main thread: the carrier's phase from an
/// eikonal solve (fast sweeping, Godunov, the slowness averaged along each step, ω² = g k tanh(k h)), its amplitude from
/// energy transport along the rays (shoaling and refraction), then along the same rays the travel since each wave began
/// to break and its breaker type. A 1 km window rarely reaches deep water, so the solve runs twice: on a domain four
/// times as wide at <see cref="CoarseCell"/>, the plane wave entering through the open sea on its upwind edges, then on
/// the window and <see cref="Halo"/> cells round it inside that solution, so the cells neighbouring windows share lie
/// well inside both. Only water joined to the open sea carries the wave. Fixed buffers: one instance bakes any number of
/// regions without allocating.
/// </summary>
public sealed class CoastBake : IJob
{
    public const float CoarseCell = 32f;
    public const int CoarseCells = 128;
    public const int ProbeSide = 32;
    public const int Halo = CoastWindow.Blend;

    /// <summary>The coarse domain's depths are sampled every this many cells (128 m) and interpolated.</summary>
    public const int CoarseDepthStep = 4;

    /// <summary>
    /// The window's depths are sampled at cell corners every this many cells (16 m), then every 8 m and 4 m only where
    /// the seabed bends by more than <see cref="CurveTolerance"/> between samples within the depths the carrier is drawn
    /// in; the rest is interpolated. Rough ground raises the tolerance until a pass fits its share of samples: the
    /// planet's height costs about 1 us a call.
    /// </summary>
    public const int NodeStep = 4;
    private const float CurveTolerance = 0.25f;
    private const float BandMargin = 0.5f;
    private const int MaxMidSamples = 6000;
    private const int MaxFineSamples = 4000;
    private const int SamplesPerBlock = 3;

    /// <summary>The phase's sweeps stop once none moves a cell by this (rad).</summary>
    private const float PhaseSettled = 1e-3f;

    /// <summary>A region is baked only if it has water shallower than this × Hs / <see cref="OceanSwell.DepthCapGamma"/>.</summary>
    public const float BreakDepthMargin = 1.3f;

    private const int N = CoastWindow.Cells;
    private const int F = N + 2 * Halo;
    private const int Nodes = F + 1;
    private const float FineOrigin = -Halo * CoastWindow.CellSize;
    private const float CoarseOrigin = -0.5f * (CoarseCells * CoarseCell - CoastWindow.Size);
    private const int ParentMargin = 2;
    private const int ParentFirst = (int)((FineOrigin - CoarseOrigin) / CoarseCell) - ParentMargin;
    private const int ParentSide = (int)(F * CoastWindow.CellSize / CoarseCell) + 2 * ParentMargin;
    private const float ParentScale = CoastWindow.CellSize / CoarseCell;
    private const float ParentOffset = (FineOrigin - CoarseOrigin) / CoarseCell - 0.5f - ParentFirst;
    private const int TableSize = 1024;
    private const float Gravity = SurfCarrier.Gravity;
    private const float LandDepth = 0.2f;

    /// <summary>
    /// The phase runs on up a beach to this height over the sea (m), at the wavenumber of the last water: a crest is
    /// followed ashore, where the face lets it go, rather than lost at the waterline. Higher ground slows the solve
    /// (<see cref="LandSlowness"/>), so waves bend round it.
    /// </summary>
    private const float BeachRise = 3f;
    private const float LandSlowness = 8f;
    private const float MaxDivergence = 0.3f;
    private const float MaxAmplitude = 3f;
    private const float MinTransport = -8f;
    private const float MaxTransport = 2.2f;
    private const int PhaseIterations = 3;
    private const int TransportIterations = 2;
    private const int FillIterations = 2;
    private const int BreakIterations = 2;

    /// <summary>
    /// The break clock (the old BreakerSet.CarryBreakDistance in steady state): it runs from this breaking cap, at full
    /// speed <see cref="ClockRampCap"/> lower; once it has run <see cref="ClockStartedDistance"/> it keeps running until
    /// the wave is under its limit again by <see cref="ClockStopCap"/>, and a wave that stops breaking forgets its break
    /// over <see cref="ReformDistance"/> of travel.
    /// </summary>
    public const float ClockStartCap = 0.92f;
    private const float ClockRampCap = 0.06f;
    private const float ClockStopCap = 1.1f;
    private const float ClockStartedDistance = 1f;
    private const float ReformDistance = 10f;

    /// <summary>A wave this far under its limit with no break behind it has no clock to run and no breaker type yet.</summary>
    private const float IdleCap = 1.5f;

    /// <summary>
    /// Breaker type from the surf similarity number ξ = tan β / √(H / L₀) where the wave begins to break: spilling below
    /// <see cref="PlungeFrom"/>, plunging by <see cref="PlungeFull"/>. The bed slope is taken <see cref="SlopeReach"/>
    /// depths either side along the ray; once the clock has run <see cref="PlungeHold"/> the type is the wave's own.
    /// </summary>
    private const float PlungeFrom = 0.35f;
    private const float PlungeFull = 0.65f;
    private const float SlopeReach = 2f;
    private const float SlopeMinSpan = 4f;
    private const float SlopeMaxSpan = 20f;
    private const float PlungeHold = 2f;

    /// <summary>The break clock's sweeps stop once none moves a cell by this (m).</summary>
    private const float BreakSettled = 0.01f;

    private readonly float[] _samples = new float[CoarseCells * CoarseCells / (CoarseDepthStep * CoarseDepthStep)];
    private readonly float[] _node = new float[Nodes * Nodes];
    private readonly byte[] _sampled = new byte[Nodes * Nodes];
    private readonly float[] _depth = new float[F * F];
    private readonly float[] _k = new float[F * F];
    private readonly float[] _cg = new float[F * F];
    private readonly float[] _psi = new float[F * F];
    private readonly float[] _ux = new float[F * F];
    private readonly float[] _uz = new float[F * F];
    private readonly float[] _f = new float[F * F];
    private readonly float[] _lag = new float[F * F];
    private readonly float[] _amp = new float[F * F];
    private readonly float[] _unbroken = new float[F * F];
    private readonly float[] _break = new float[F * F];
    private readonly float[] _plunge = new float[F * F];
    private readonly byte[] _fixed = new byte[F * F];
    private readonly byte[] _wet = new byte[F * F];
    private readonly byte[] _sea = new byte[F * F];
    private readonly byte[] _phased = new byte[F * F];
    private readonly int[] _stack = new int[F * F];
    private readonly float[] _parentLag = new float[ParentSide * ParentSide];
    private readonly float[] _parentAmp = new float[ParentSide * ParentSide];
    private readonly float[] _parentBreak = new float[ParentSide * ParentSide];
    private readonly float[] _parentPlunge = new float[ParentSide * ParentSide];
    private readonly byte[] _parentSea = new byte[ParentSide * ParentSide];
    private readonly float[] _kTable = new float[TableSize];
    private readonly float[] _cgTable = new float[TableSize];

    private ICoastDepth? _source;
    private CoastWindow _window;
    private CoastAtlas? _atlas;
    private CoastRegionKey _key;
    private float _breakDepth;
    private float _omega;
    private float _k0;
    private float _cg0;
    private float _deepDepth;
    private float _dirX;
    private float _dirZ;
    private float _band;
    private int _n = F;
    private float _cell;
    private float _origin;
    private bool _nested;

    /// <summary>The window's shallowest and deepest probed depths (m); a coast if it has water shallower than the break depth.</summary>
    public float ProbeMin { get; private set; }

    public float ProbeMax { get; private set; }

    /// <summary>The atlas was baked: the window has water joined to the open sea that is shallower than the break depth.</summary>
    public bool Baked { get; private set; }

    /// <summary>The solve ran, so <see cref="SeaMin"/> is known: the window's shallowest water joined to the open sea (m), +∞ if it has none.</summary>
    public bool Solved { get; private set; }

    public float SeaMin { get; private set; }

    /// <summary>Worker time of the last run (ms), of its depth sampling alone, and the depths it sampled.</summary>
    public float Ms { get; private set; }

    public float DepthMs { get; private set; }

    public int DepthSamples { get; private set; }

    public CoastRegionKey Key => _key;

    public CoastAtlas? Atlas => _atlas;

    /// <summary>The scratch buffers' size (bytes), held for the instance's life.</summary>
    public static int ScratchBytes => F * F * (12 * sizeof(float) + 4 + sizeof(int)) + Nodes * Nodes * (sizeof(float) + 1)
        + CoarseCells * CoarseCells / (CoarseDepthStep * CoarseDepthStep) * sizeof(float) + ParentSide * ParentSide * (4 * sizeof(float) + 1) + 2 * TableSize * sizeof(float);

    public static bool Coastal(float probeMin, float probeMax, float breakDepth) => probeMax > LandDepth && probeMin < breakDepth;

    public static float BreakDepth(float significantHeight) => BreakDepthMargin * significantHeight / OceanSwell.DepthCapGamma;

    /// <param name="breakDepth">Regions with no water shallower than this (m) are not baked (<see cref="BreakDepth"/>).</param>
    /// <param name="probeMin">The window's probed depths (<see cref="Probe"/>) if the caller has them: the run then does not probe again.</param>
    public void Prepare(ICoastDepth source, in CoastWindow window, in CoastRegionKey key, CoastAtlas atlas, float breakDepth, float probeMin = float.NaN, float probeMax = float.NaN)
    {
        _source = source;
        _window = window;
        _key = key;
        _atlas = atlas;
        _breakDepth = breakDepth;
        ProbeMin = probeMin;
        ProbeMax = probeMax;
    }

    public void Run()
    {
        var start = Stopwatch.GetTimestamp();
        Baked = false;
        Solved = false;
        SeaMin = float.PositiveInfinity;
        DepthMs = 0f;
        DepthSamples = 0;

        if (float.IsNaN(ProbeMin) || float.IsNaN(ProbeMax))
        {
            Probe(_source!, _window, out var min, out var max);
            ProbeMin = min;
            ProbeMax = max;
        }

        if (Coastal(ProbeMin, ProbeMax, _breakDepth) && !_key.Carrier.Calm)
        {
            Bake();
            Solved = true;
            Baked = SeaMin < _breakDepth;
        }

        Ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>The shallowest and deepest of <see cref="ProbeSide"/>² depths across a window. Called from jobs.</summary>
    public static void Probe(ICoastDepth source, in CoastWindow window, out float min, out float max)
    {
        const double step = CoastWindow.Size / ProbeSide;
        min = float.PositiveInfinity;
        max = float.NegativeInfinity;

        for (var z = 0; z < ProbeSide; z++)
        {
            for (var x = 0; x < ProbeSide; x++)
            {
                var depth = source.GeneratedDepth(window.Direction((x + 0.5) * step, (z + 0.5) * step));
                min = MathF.Min(min, depth);
                max = MathF.Max(max, depth);
            }
        }
    }

    private void Bake()
    {
        var carrier = _key.Carrier;
        _omega = carrier.Omega;
        _k0 = _omega * _omega / Gravity;
        _cg0 = 0.5f * Gravity / _omega;
        _band = SurfCarrier.FadeDepth * carrier.Height * (1f + CoastRegionSet.HeightTolerance) + BandMargin;
        (_dirZ, _dirX) = MathF.SinCos(carrier.Direction);
        BuildTables();

        if (!Solve(CoarseCells, CoarseCell, CoarseOrigin, nested: false))
            return;

        KeepParent();

        if (Solve(F, CoastWindow.CellSize, FineOrigin, nested: true))
            Write();
    }

    private void FillDepth()
    {
        var start = Stopwatch.GetTimestamp();

        if (_nested)
            FillFineDepth();
        else
            FillCoarseDepth();

        DepthMs += (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void FillCoarseDepth()
    {
        const int step = CoarseDepthStep;
        var side = _n / step;
        _window.FillDepth(_source!, _samples, _origin, _origin, _cell * step, side, 0, side);
        DepthSamples += side * side;

        for (var z = 0; z < _n; z++)
        {
            var fz = Math.Clamp((z + 0.5f) / step - 0.5f, 0f, side - 1.001f);
            var iz = (int)fz;
            var tz = fz - iz;

            for (var x = 0; x < _n; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / step - 0.5f, 0f, side - 1.001f);
                var ix = (int)fx;
                var s = ix + iz * side;
                var tx = fx - ix;
                _depth[x + z * _n] = float.Lerp(float.Lerp(_samples[s], _samples[s + 1], tx), float.Lerp(_samples[s + side], _samples[s + side + 1], tx), tz);
            }
        }
    }

    private void FillFineDepth()
    {
        Array.Clear(_sampled);

        for (var z = 0; z <= F; z += NodeStep)
        {
            for (var x = 0; x <= F; x += NodeStep)
                Sample(x, z);
        }

        Refine(NodeStep, MaxMidSamples);
        Refine(NodeStep / 2, MaxFineSamples);

        for (var z = 0; z < F; z++)
        {
            for (var x = 0; x < F; x++)
            {
                var n = x + z * Nodes;
                _depth[x + z * F] = 0.25f * (_node[n] + _node[n + 1] + _node[n + Nodes] + _node[n + Nodes + 1]);
            }
        }
    }

    private void Sample(int x, int z)
    {
        var n = x + z * Nodes;

        if (_sampled[n] != 0)
            return;

        _node[n] = _source!.GeneratedDepth(_window.Direction(FineOrigin + x * (double)CoastWindow.CellSize, FineOrigin + z * (double)CoastWindow.CellSize));
        _sampled[n] = 1;
        DepthSamples++;
    }

    /// <summary>Halves the corner spacing of blocks of <paramref name="step"/> cells: sampled where the seabed bends, interpolated elsewhere.</summary>
    private void Refine(int step, int limit)
    {
        var half = step / 2;
        var tolerance = CurveTolerance;

        while (Bending(step, tolerance) * SamplesPerBlock > limit)
            tolerance *= 1.25f;

        for (var z = 0; z < F; z += step)
        {
            for (var x = 0; x < F; x += step)
            {
                if (!Bends(x, z, step, tolerance))
                    continue;

                Sample(x + half, z);
                Sample(x, z + half);
                Sample(x + half, z + half);
                Sample(x + step, z + half);
                Sample(x + half, z + step);
            }
        }

        for (var z = 0; z < F; z += step)
        {
            for (var x = 0; x < F; x += step)
            {
                var n = x + z * Nodes;
                Between(n + half, n, n + step);
                Between(n + half * Nodes, n, n + step * Nodes);
                Between(n + step + half * Nodes, n + step, n + step + step * Nodes);
                Between(n + half + step * Nodes, n + step * Nodes, n + step + step * Nodes);
                var centre = n + half + half * Nodes;

                if (_sampled[centre] == 0)
                    _node[centre] = 0.25f * (_node[n + half] + _node[n + half * Nodes] + _node[n + step + half * Nodes] + _node[n + half + step * Nodes]);
            }
        }
    }

    private void Between(int node, int a, int b)
    {
        if (_sampled[node] == 0)
            _node[node] = 0.5f * (_node[a] + _node[b]);
    }

    private int Bending(int step, float tolerance)
    {
        var count = 0;

        for (var z = 0; z < F; z += step)
        {
            for (var x = 0; x < F; x += step)
                count += Bends(x, z, step, tolerance) ? 1 : 0;
        }

        return count;
    }

    /// <summary>The block's corners lie within the depths the carrier is drawn in and the seabed bends at one of them.</summary>
    private bool Bends(int x, int z, int step, float tolerance)
    {
        var n = x + z * Nodes;
        var a = _node[n];
        var b = _node[n + step];
        var c = _node[n + step * Nodes];
        var d = _node[n + step + step * Nodes];
        var min = MathF.Min(MathF.Min(a, b), MathF.Min(c, d));
        var max = MathF.Max(MathF.Max(a, b), MathF.Max(c, d));

        if (min > _band || max < -(BeachRise + BandMargin))
            return false;

        return Curve(x, z, step) > tolerance || Curve(x + step, z, step) > tolerance
            || Curve(x, z + step, step) > tolerance || Curve(x + step, z + step, step) > tolerance;
    }

    private float Curve(int x, int z, int step)
    {
        var n = x + z * Nodes;
        var here = 2f * _node[n];
        var alongX = x >= step && x + step <= F ? MathF.Abs(_node[n - step] - here + _node[n + step]) : 0f;
        var alongZ = z >= step && z + step <= F ? MathF.Abs(_node[n - step * Nodes] - here + _node[n + step * Nodes]) : 0f;
        return MathF.Max(alongX, alongZ);
    }

    /// <summary>k(h) and the group speed on a table in √h up to deep water (k₀ h = 10), for this carrier.</summary>
    private void BuildTables()
    {
        _deepDepth = 10f / _k0;

        for (var i = 0; i < TableSize; i++)
        {
            var t = i / (float)(TableSize - 1);
            var h = MathF.Max(_deepDepth * t * t, 1e-3f);
            var k = (float)Jonswap.WaveNumber(_omega, h);
            var kh = k * h;
            var n = kh > 10f ? 0.5f : 0.5f * (1f + 2f * kh / MathF.Sinh(2f * kh));
            _kTable[i] = k;
            _cgTable[i] = n * _omega / k;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Dispersion(float depth, out float k, out float cg)
    {
        if (depth >= _deepDepth)
        {
            k = _k0;
            cg = _cg0;
            return;
        }

        var f = MathF.Sqrt(depth / _deepDepth) * (TableSize - 1);
        var i = Math.Min((int)f, TableSize - 2);
        var t = f - i;
        k = float.Lerp(_kTable[i], _kTable[i + 1], t);
        cg = float.Lerp(_cgTable[i], _cgTable[i + 1], t);
    }

    /// <summary>False if the level has no water joined to the open sea, or none of it on an upwind edge.</summary>
    private bool Solve(int n, float cell, float origin, bool nested)
    {
        _n = n;
        _cell = cell;
        _origin = origin;
        _nested = nested;
        FillDepth();
        Wavenumbers();

        if (!Flood() || !SolvePhase())
            return false;

        RayDirections();
        SolveTransport();

        for (var c = 0; c < _n * _n; c++)
        {
            _lag[c] = _psi[c] - PlanePhase(c % _n, c / _n);
            _amp[c] = _sea[c] != 0 ? Math.Clamp(MathF.Sqrt(MathF.Exp(_f[c]) * _cg0 / _cg[c]), 0f, MaxAmplitude) : 0f;
        }

        SolveBreak();
        FillLand();
        return true;
    }

    /// <summary>The open carrier's phase k₀ d̂·x at a cell's centre, in the window's local metres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float PlanePhase(int x, int z) => _k0 * (_dirX * (_origin + (x + 0.5f) * _cell) + _dirZ * (_origin + (z + 0.5f) * _cell));

    private void Wavenumbers()
    {
        Dispersion(LandDepth, out var kLand, out _);
        Dispersion(SurfCarrier.SpentDepthK, out var kBeach, out _);
        kLand *= LandSlowness;

        for (var c = 0; c < _n * _n; c++)
        {
            var h = _depth[c];
            _wet[c] = h > LandDepth ? (byte)1 : (byte)0;
            _phased[c] = h > -BeachRise ? (byte)1 : (byte)0;

            if (_wet[c] == 0)
            {
                _k[c] = _phased[c] != 0 ? kBeach : kLand;
                _cg[c] = 1f;
                continue;
            }

            Dispersion(MathF.Max(h, SurfCarrier.SpentDepthK), out _k[c], out _cg[c]);
        }
    }

    /// <summary>
    /// Marks the water joined to the open sea: flooded through water from the domain's edge cells, which at the coarse
    /// level are the open sea and at the window's are sea where the coarse solve has it. Inland basins and landlocked
    /// lagoons are left out and carry no wave. False if there is none.
    /// </summary>
    private bool Flood()
    {
        Array.Clear(_sea, 0, _n * _n);
        var top = 0;

        for (var i = 0; i < _n; i++)
        {
            Seed(i, 0, ref top);
            Seed(i, _n - 1, ref top);
            Seed(0, i, ref top);
            Seed(_n - 1, i, ref top);
        }

        var any = top > 0;

        while (top > 0)
        {
            var c = _stack[--top];
            var x = c % _n;
            var z = c / _n;

            if (x > 0)
                Spread(c - 1, ref top);

            if (x < _n - 1)
                Spread(c + 1, ref top);

            if (z > 0)
                Spread(c - _n, ref top);

            if (z < _n - 1)
                Spread(c + _n, ref top);
        }

        return any;
    }

    private void Seed(int x, int z, ref int top)
    {
        if (!_nested || ParentSea(x, z))
            Spread(x + z * _n, ref top);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Spread(int c, ref int top)
    {
        if (_wet[c] == 0 || _sea[c] != 0)
            return;

        _sea[c] = 1;
        _stack[top++] = c;
    }

    /// <summary>The coarse solve has open sea in one of the cells round the fine cell (x, z).</summary>
    private bool ParentSea(int x, int z)
    {
        var ix = (int)Math.Clamp((x + 0.5f) * ParentScale + ParentOffset, 0f, ParentSide - 1.001f);
        var iz = (int)Math.Clamp((z + 0.5f) * ParentScale + ParentOffset, 0f, ParentSide - 1.001f);
        var c = ix + iz * ParentSide;
        return (_parentSea[c] | _parentSea[c + 1] | _parentSea[c + ParentSide] | _parentSea[c + ParentSide + 1]) != 0;
    }

    /// <summary>
    /// Fast sweeping; the plane wave on the open sea of the upwind edges, lagged as the coarse solve has it there. False
    /// if no edge cell takes it.
    /// </summary>
    private bool SolvePhase()
    {
        Array.Fill(_psi, float.PositiveInfinity, 0, _n * _n);
        Array.Clear(_fixed, 0, _n * _n);
        var any = false;

        for (var i = 0; i < _n; i++)
        {
            any |= Fix(i, 0, 0f, 1f);
            any |= Fix(i, _n - 1, 0f, -1f);
            any |= Fix(0, i, 1f, 0f);
            any |= Fix(_n - 1, i, -1f, 0f);
        }

        if (!any)
            return false;

        for (var it = 0; it < PhaseIterations; it++)
        {
            var changed = false;

            for (var order = 0; order < 4; order++)
            {
                Order(order, out var x0, out var dx, out var z0, out var dz);

                for (int z = z0, zi = 0; zi < _n; z += dz, zi++)
                {
                    for (int x = x0, xi = 0; xi < _n; x += dx, xi++)
                        changed |= PhaseUpdate(x, z);
                }
            }

            if (!changed)
                break;
        }

        return true;
    }

    private void Order(int order, out int x0, out int dx, out int z0, out int dz)
    {
        x0 = (order & 1) == 0 ? 0 : _n - 1;
        dx = (order & 1) == 0 ? 1 : -1;
        z0 = (order & 2) == 0 ? 0 : _n - 1;
        dz = (order & 2) == 0 ? 1 : -1;
    }

    private bool Fix(int x, int z, float inwardX, float inwardZ)
    {
        var c = x + z * _n;

        if (_dirX * inwardX + _dirZ * inwardZ <= 0.05f || (!_nested && _sea[c] == 0))
            return false;

        _psi[c] = PlanePhase(x, z) + (_nested ? Parent(_parentLag, x, z) : 0f);
        _fixed[c] = 1;
        return true;
    }

    /// <summary>Godunov's update with the slowness averaged between the cell and the neighbours it steps from, which is second order along a ray.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PhaseUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_fixed[c] != 0)
            return false;

        var a = float.PositiveInfinity;
        var b = float.PositiveInfinity;
        var ka = 0f;
        var kb = 0f;

        if (x > 0)
            (a, ka) = (_psi[c - 1], _k[c - 1]);

        if (x < _n - 1 && _psi[c + 1] < a)
            (a, ka) = (_psi[c + 1], _k[c + 1]);

        if (z > 0)
            (b, kb) = (_psi[c - _n], _k[c - _n]);

        if (z < _n - 1 && _psi[c + _n] < b)
            (b, kb) = (_psi[c + _n], _k[c + _n]);

        var hasA = !float.IsPositiveInfinity(a);
        var hasB = !float.IsPositiveInfinity(b);

        if (!hasA && !hasB)
            return false;

        var from = hasA && hasB ? 0.5f * (ka + kb) : hasA ? ka : kb;
        var f = 0.5f * (_k[c] + from) * _cell;
        var next = MathF.Abs(a - b) >= f
            ? MathF.Min(a, b) + f
            : 0.5f * (a + b + MathF.Sqrt(2f * f * f - (a - b) * (a - b)));

        var before = _psi[c];

        if (next >= before)
            return false;

        _psi[c] = next;
        return before - next > PhaseSettled;
    }

    /// <summary>Unit ray direction from the upwind phase gradient (the neighbours the Godunov update used); on the edges that take the coarse solve's phase, from its gradient.</summary>
    private void RayDirections()
    {
        for (var z = 0; z < _n; z++)
        {
            for (var x = 0; x < _n; x++)
            {
                var c = x + z * _n;

                if (_phased[c] == 0 || (_fixed[c] != 0 && !_nested))
                {
                    (_ux[c], _uz[c]) = (_dirX, _dirZ);
                    continue;
                }

                float gx;
                float gz;

                if (_fixed[c] != 0)
                {
                    gx = _k0 * _dirX + (Parent(_parentLag, x + 1, z) - Parent(_parentLag, x - 1, z)) / (2f * _cell);
                    gz = _k0 * _dirZ + (Parent(_parentLag, x, z + 1) - Parent(_parentLag, x, z - 1)) / (2f * _cell);
                }
                else
                {
                    var left = x > 0 ? _psi[c - 1] : float.PositiveInfinity;
                    var right = x < _n - 1 ? _psi[c + 1] : float.PositiveInfinity;
                    var down = z > 0 ? _psi[c - _n] : float.PositiveInfinity;
                    var up = z < _n - 1 ? _psi[c + _n] : float.PositiveInfinity;
                    var a = MathF.Min(left, right);
                    var b = MathF.Min(down, up);
                    gx = a < _psi[c] ? (_psi[c] - a) * (left <= right ? 1f : -1f) : 0f;
                    gz = b < _psi[c] ? (_psi[c] - b) * (down <= up ? 1f : -1f) : 0f;
                }

                var length = MathF.Sqrt(gx * gx + gz * gz);
                (_ux[c], _uz[c]) = length < 1e-6f ? (_dirX, _dirZ) : (gx / length, gz / length);
            }
        }
    }

    /// <summary>u·∇F = −∇·u with F = ln(E c_g) against the open sea (F = 0); the upwind edges take the coarse solve's.</summary>
    private void SolveTransport()
    {
        Array.Clear(_f, 0, _n * _n);

        if (_nested)
        {
            for (var c = 0; c < _n * _n; c++)
            {
                if (_fixed[c] != 0)
                    _f[c] = ParentTransport(c % _n, c / _n, c);
            }
        }

        for (var it = 0; it < TransportIterations; it++)
        {
            for (var order = 0; order < 4; order++)
            {
                Order(order, out var x0, out var dx, out var z0, out var dz);

                for (int z = z0, zi = 0; zi < _n; z += dz, zi++)
                {
                    for (int x = x0, xi = 0; xi < _n; x += dx, xi++)
                        TransportUpdate(x, z);
                }
            }
        }
    }

    /// <summary>The energy a cell takes from its two upwind neighbours, by the ray's share through each; none comes through land or landlocked water, so water in their lee is sheltered.</summary>
    private void TransportUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_fixed[c] != 0 || _sea[c] == 0)
            return;

        var ux = _ux[c];
        var uz = _uz[c];
        var wx = MathF.Abs(ux);
        var wz = MathF.Abs(uz);
        var sum = MathF.Max(wx + wz, 1e-6f);
        var openX = UpwindTransport(c, x - (int)MathF.Sign(ux), z, out var fx);
        var openZ = UpwindTransport(c, x, z - (int)MathF.Sign(uz), out var fz);
        float carried;

        if (openX && openZ)
            carried = (wx * fx + wz * fz) / sum;
        else if (openX && wx > 1e-3f)
            carried = fx + MathF.Log(wx / sum);
        else if (openZ && wz > 1e-3f)
            carried = fz + MathF.Log(wz / sum);
        else
            carried = MinTransport;

        var div = (Neighbour(_ux, c, x + 1, z) - Neighbour(_ux, c, x - 1, z) + Neighbour(_uz, c, x, z + 1) - Neighbour(_uz, c, x, z - 1)) / (2f * _cell);
        div = Math.Clamp(div, -MaxDivergence, MaxDivergence);
        _f[c] = Math.Clamp(carried - div * _cell / sum, MinTransport, MaxTransport);
    }

    private float Neighbour(float[] field, int c, int x, int z)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return field[c];

        var n = x + z * _n;
        return _wet[n] != 0 ? field[n] : field[c];
    }

    /// <summary>F of the upwind cell (x, z), past the edge the coarse solve's or the open sea's; false where no wave comes from it.</summary>
    private bool UpwindTransport(int c, int x, int z, out float f)
    {
        if ((uint)x >= _n || (uint)z >= _n)
        {
            f = _nested ? ParentTransport(x, z, c) : 0f;
            return true;
        }

        var n = x + z * _n;
        f = _f[n];
        return _sea[n] != 0;
    }

    /// <summary>F of the coarse solve's amplitude at this level's cell (x, z), which may lie a cell past the edge, against cell c's group speed.</summary>
    private float ParentTransport(int x, int z, int c)
    {
        var amp = Parent(_parentAmp, x, z);
        return Math.Clamp(MathF.Log(MathF.Max(amp * amp * _cg[c] / _cg0, 1e-6f)), MinTransport, MaxTransport);
    }

    /// <summary>Bilinear in the kept part of the coarse solve at the fine cell (x, z).</summary>
    private float Parent(float[] field, int x, int z)
    {
        var fx = Math.Clamp((x + 0.5f) * ParentScale + ParentOffset, 0f, ParentSide - 1.001f);
        var fz = Math.Clamp((z + 0.5f) * ParentScale + ParentOffset, 0f, ParentSide - 1.001f);
        var ix = (int)fx;
        var iz = (int)fz;
        var c = ix + iz * ParentSide;
        var tx = fx - ix;
        return float.Lerp(float.Lerp(field[c], field[c + 1], tx), float.Lerp(field[c + ParentSide], field[c + ParentSide + 1], tx), fz - iz);
    }

    private void KeepParent()
    {
        for (var z = 0; z < ParentSide; z++)
        {
            for (var x = 0; x < ParentSide; x++)
            {
                var from = ParentFirst + x + (ParentFirst + z) * _n;
                var to = x + z * ParentSide;
                _parentLag[to] = _lag[from];
                _parentAmp[to] = _amp[from];
                _parentBreak[to] = _break[from];
                _parentPlunge[to] = _plunge[from];
                _parentSea[to] = _sea[from];
            }
        }
    }

    /// <summary>
    /// Ground takes what the water beside it has, so samples across the waterline stay smooth: the lag where the phase
    /// was not solved, the amplitude, break clock and breaker type where there is no water. Landlocked water keeps its
    /// zero amplitude.
    /// </summary>
    private void FillLand()
    {
        for (var c = 0; c < _n * _n; c++)
        {
            if (_phased[c] == 0)
                _lag[c] = float.NaN;

            if (_wet[c] == 0)
                _amp[c] = float.NaN;
        }

        for (var it = 0; it < FillIterations; it++)
        {
            for (var order = 0; order < 4; order++)
            {
                Order(order, out var x0, out var dx, out var z0, out var dz);

                for (int z = z0, zi = 0; zi < _n; z += dz, zi++)
                {
                    for (int x = x0, xi = 0; xi < _n; x += dx, xi++)
                        FillUpdate(x, z);
                }
            }
        }

        for (var c = 0; c < _n * _n; c++)
        {
            if (float.IsNaN(_lag[c]))
                _lag[c] = 0f;

            if (float.IsNaN(_amp[c]))
                (_amp[c], _break[c], _plunge[c]) = (1f, 0f, 0f);
        }
    }

    private void FillUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (float.IsNaN(_lag[c]))
        {
            var lag = 0f;
            var count = 0;
            Accumulate(_lag, x - 1, z, ref lag, ref count);
            Accumulate(_lag, x + 1, z, ref lag, ref count);
            Accumulate(_lag, x, z - 1, ref lag, ref count);
            Accumulate(_lag, x, z + 1, ref lag, ref count);

            if (count > 0)
                _lag[c] = lag / count;
        }

        if (!float.IsNaN(_amp[c]))
            return;

        var amp = 0f;
        var clock = 0f;
        var plunge = 0f;
        var water = 0;
        Accumulate(x - 1, z, ref amp, ref clock, ref plunge, ref water);
        Accumulate(x + 1, z, ref amp, ref clock, ref plunge, ref water);
        Accumulate(x, z - 1, ref amp, ref clock, ref plunge, ref water);
        Accumulate(x, z + 1, ref amp, ref clock, ref plunge, ref water);

        if (water > 0)
            (_amp[c], _break[c], _plunge[c]) = (amp / water, clock / water, plunge / water);
    }

    private void Accumulate(float[] field, int x, int z, ref float sum, ref int count)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return;

        var value = field[x + z * _n];

        if (float.IsNaN(value))
            return;

        sum += value;
        count++;
    }

    private void Accumulate(int x, int z, ref float amp, ref float clock, ref float plunge, ref int count)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return;

        var n = x + z * _n;

        if (float.IsNaN(_amp[n]))
            return;

        amp += _amp[n];
        clock += _break[n];
        plunge += _plunge[n];
        count++;
    }

    /// <summary>
    /// The break clock and the breaker type, carried along the rays: each cell takes its upwind neighbours' values a
    /// cell's travel back and runs the clock over that travel.
    /// </summary>
    private void SolveBreak()
    {
        var carrier = _key.Carrier;
        var drawn = MathF.Min(SurfCarrier.IncoherentKh / _k0, _band);

        // The breaker type each cell would give a wave beginning to break there, where one could (the transport's F is done with).
        for (var c = 0; c < _n * _n; c++)
        {
            _unbroken[c] = _depth[c] < drawn ? SurfCarrier.Unbroken(carrier.Height, _omega, _depth[c], _amp[c]) : 0f;
            _f[c] = _sea[c] != 0 && SurfCarrier.RawCap(_unbroken[c], _depth[c]) <= IdleCap ? LocalPlunge(c % _n, c / _n, _ux[c], _uz[c]) : 0f;
        }

        Array.Clear(_break, 0, _n * _n);
        Array.Clear(_plunge, 0, _n * _n);

        for (var it = 0; it < BreakIterations; it++)
        {
            var changed = false;

            for (var order = 0; order < 4; order++)
            {
                Order(order, out var x0, out var dx, out var z0, out var dz);

                for (int z = z0, zi = 0; zi < _n; z += dz, zi++)
                {
                    for (int x = x0, xi = 0; xi < _n; x += dx, xi++)
                        changed |= BreakUpdate(x, z);
                }
            }

            if (!changed)
                break;
        }
    }

    /// <summary>True if the cell's clock moved by more than <see cref="BreakSettled"/>.</summary>
    private bool BreakUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_sea[c] == 0)
            return false;

        var ux = _ux[c];
        var uz = _uz[c];
        var wx = MathF.Abs(ux);
        var wz = MathF.Abs(uz);
        var sum = MathF.Max(wx + wz, 1e-6f);
        var ax = x - (int)MathF.Sign(ux);
        var az = z - (int)MathF.Sign(uz);
        var upwind = (wx * UpwindBreak(_break, _parentBreak, c, ax, z) + wz * UpwindBreak(_break, _parentBreak, c, x, az)) / sum;
        var raw = SurfCarrier.RawCap(_unbroken[c], _depth[c]);

        var before = _break[c];

        if (upwind <= 0f && raw > IdleCap)
        {
            _break[c] = 0f;
            _plunge[c] = 0f;
            return before > BreakSettled;
        }

        var travel = _cell / sum;
        var rate = SurfCarrier.Smooth(((upwind > ClockStartedDistance ? ClockStopCap : ClockStartCap) - raw) / ClockRampCap);
        var clock = MathF.Min(upwind * MathF.Exp(-travel * (1f - rate) / ReformDistance) + travel * rate, CoastAtlas.MaxBreakDistance);
        var carried = (wx * UpwindBreak(_plunge, _parentPlunge, c, ax, z) + wz * UpwindBreak(_plunge, _parentPlunge, c, x, az)) / sum;
        _break[c] = clock;
        _plunge[c] = float.Lerp(_f[c], carried, SurfCarrier.Smooth(upwind / PlungeHold));
        return MathF.Abs(clock - before) > BreakSettled;
    }

    private float UpwindBreak(float[] field, float[] parent, int c, int x, int z)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return _nested ? Parent(parent, x, z) : 0f;

        var n = x + z * _n;
        return _sea[n] != 0 ? field[n] : field[c];
    }

    private float LocalPlunge(int x, int z, float ux, float uz)
    {
        var c = x + z * _n;
        var span = Math.Clamp(SlopeReach * MathF.Max(_depth[c], 0f), SlopeMinSpan, SlopeMaxSpan) / _cell;
        var deep = DepthAt(x - ux * span, z - uz * span);
        var shallow = DepthAt(x + ux * span, z + uz * span);
        var tanBeta = MathF.Max(deep - shallow, 0f) / (2f * span * _cell);
        var l0 = MathF.Tau * Gravity / (_omega * _omega);
        var xi = tanBeta / MathF.Sqrt(MathF.Max(_unbroken[c], 0.05f) / l0);
        return SurfCarrier.Smooth((xi - PlungeFrom) / (PlungeFull - PlungeFrom));
    }

    /// <summary>Bilinear depth at cell coordinates (centres at integers), clamped to the grid.</summary>
    private float DepthAt(float x, float z)
    {
        x = Math.Clamp(x, 0f, _n - 1.001f);
        z = Math.Clamp(z, 0f, _n - 1.001f);
        var ix = (int)x;
        var iz = (int)z;
        var c = ix + iz * _n;
        var tx = x - ix;
        return float.Lerp(float.Lerp(_depth[c], _depth[c + 1], tx), float.Lerp(_depth[c + _n], _depth[c + _n + 1], tx), z - iz);
    }

    /// <summary>
    /// The atlas, from the window's part of the solve: the spatial phase (the plane wave plus the lag, land filled from
    /// the water beside it) and the direction of travel from its central differences, which are continuous where the
    /// upwind gradient the solve used is not.
    /// </summary>
    private void Write()
    {
        var atlas = _atlas!;
        atlas.Key = _key;
        atlas.Height = _key.Carrier.Height;
        atlas.OriginU = _window.OriginU;
        atlas.OriginV = _window.OriginV;

        for (var c = 0; c < F * F; c++)
            _psi[c] = PlanePhase(c % F, c / F) + _lag[c];

        var seaMin = float.PositiveInfinity;

        for (var z = 0; z < N; z++)
        {
            for (var x = 0; x < N; x++)
            {
                var c = x + Halo + (z + Halo) * F;
                var gx = _psi[c + 1] - _psi[c - 1];
                var gz = _psi[c + F] - _psi[c - F];
                var length = MathF.Sqrt(gx * gx + gz * gz);
                var (dirX, dirZ) = length > 1e-6f ? (gx / length, gz / length) : (_dirX, _dirZ);
                atlas.Write(x + z * N, _psi[c], _depth[c], _amp[c], _break[c], _plunge[c], dirX, dirZ);

                if (_sea[c] != 0)
                    seaMin = MathF.Min(seaMin, _depth[c]);
            }
        }

        SeaMin = seaMin;
    }
}
