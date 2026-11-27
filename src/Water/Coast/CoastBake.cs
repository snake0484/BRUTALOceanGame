using System.Diagnostics;
using System.Runtime.CompilerServices;
using Brutal.Concurrency.Jobs;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

/// <summary>
/// Bakes one coast region's <see cref="CoastAtlas"/> for its carrier, off the main thread: the carrier's phase from an
/// eikonal solve (fast sweeping, first-order Godunov, ω² = g k tanh(k h)), its amplitude from energy transport along the
/// rays (shoaling and refraction), then along the same rays the travel since each wave began to break and its breaker
/// type. A 1 km window rarely reaches deep water, so the solve runs twice: on a domain four times as wide at
/// <see cref="CoarseCell"/>, the plane wave entering through its upwind edges, then on the window itself inside that
/// solution. A probe of the window's depths first settles whether there is a coast to bake at all. Fixed buffers: one
/// instance bakes any number of regions without allocating.
/// </summary>
public sealed class CoastBake : IJob
{
    public const float CoarseCell = 32f;
    public const int CoarseCells = 128;
    public const int ProbeSide = 32;

    /// <summary>
    /// Depths are sampled every this many cells of a level (8 m in the window, 64 m in the coarse domain) and
    /// interpolated: the planet's height costs 0.7 us a call, and a sample a cell would be 50 ms of the bake.
    /// </summary>
    public const int DepthStep = 2;

    /// <summary>A region is baked only if it has water shallower than this × Hs / <see cref="OceanSwell.DepthCapGamma"/>.</summary>
    public const float BreakDepthMargin = 1.3f;

    private const int N = CoastWindow.Cells;
    private const float CoarseOrigin = -0.5f * (CoarseCells * CoarseCell - CoastWindow.Size);
    private const int ParentMargin = 2;
    private const int ParentFirst = (int)(-CoarseOrigin / CoarseCell) - ParentMargin;
    private const int ParentSide = (int)(CoastWindow.Size / CoarseCell) + 2 * ParentMargin;
    private const int TableSize = 1024;
    private const float Gravity = SurfCarrier.Gravity;
    private const float LandDepth = 0.2f;
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

    private readonly float[] _samples = new float[N * N / (DepthStep * DepthStep)];
    private readonly float[] _depth = new float[N * N];
    private readonly float[] _k = new float[N * N];
    private readonly float[] _cg = new float[N * N];
    private readonly float[] _psi = new float[N * N];
    private readonly float[] _ux = new float[N * N];
    private readonly float[] _uz = new float[N * N];
    private readonly float[] _f = new float[N * N];
    private readonly float[] _lag = new float[N * N];
    private readonly float[] _amp = new float[N * N];
    private readonly float[] _unbroken = new float[N * N];
    private readonly float[] _break = new float[N * N];
    private readonly float[] _plunge = new float[N * N];
    private readonly byte[] _fixed = new byte[N * N];
    private readonly byte[] _wet = new byte[N * N];
    private readonly float[] _parentLag = new float[ParentSide * ParentSide];
    private readonly float[] _parentAmp = new float[ParentSide * ParentSide];
    private readonly float[] _parentBreak = new float[ParentSide * ParentSide];
    private readonly float[] _parentPlunge = new float[ParentSide * ParentSide];
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
    private int _n = N;
    private float _cell;
    private float _origin;
    private bool _nested;

    /// <summary>The window's shallowest and deepest probed depths (m); a coast if it has water shallower than the break depth.</summary>
    public float ProbeMin { get; private set; }

    public float ProbeMax { get; private set; }

    /// <summary>The atlas was baked (the probe found a coast).</summary>
    public bool Baked { get; private set; }

    /// <summary>Worker time of the last run (ms), and of its depth sampling alone.</summary>
    public float Ms { get; private set; }

    public float DepthMs { get; private set; }

    public CoastRegionKey Key => _key;

    public CoastAtlas? Atlas => _atlas;

    /// <summary>The scratch buffers' size (bytes), held for the instance's life.</summary>
    public static int ScratchBytes => N * N * (12 * sizeof(float) + 2) + N * N / (DepthStep * DepthStep) * sizeof(float) + 4 * ParentSide * ParentSide * sizeof(float) + 2 * TableSize * sizeof(float);

    public static bool Coastal(float probeMin, float probeMax, float breakDepth) => probeMax > LandDepth && probeMin < breakDepth;

    public static float BreakDepth(float significantHeight) => BreakDepthMargin * significantHeight / OceanSwell.DepthCapGamma;

    /// <param name="breakDepth">Regions with no water shallower than this (m) are not baked (<see cref="BreakDepth"/>).</param>
    public void Prepare(ICoastDepth source, in CoastWindow window, in CoastRegionKey key, CoastAtlas atlas, float breakDepth)
    {
        _source = source;
        _window = window;
        _key = key;
        _atlas = atlas;
        _breakDepth = breakDepth;
    }

    public void Run()
    {
        var start = Stopwatch.GetTimestamp();
        Baked = false;
        DepthMs = 0f;
        Probe(_source!, _window, out var min, out var max);
        ProbeMin = min;
        ProbeMax = max;

        if (Coastal(ProbeMin, ProbeMax, _breakDepth) && !_key.Carrier.Calm)
        {
            Bake();
            Baked = true;
        }

        Ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>The shallowest and deepest of <see cref="ProbeSide"/>� depths across a window. Called from jobs.</summary>
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
        (_dirZ, _dirX) = MathF.SinCos(carrier.Direction);
        BuildTables();

        Solve(CoarseCells, CoarseCell, CoarseOrigin, nested: false);
        KeepParent();
        Solve(N, CoastWindow.CellSize, 0f, nested: true);
        Write();
    }

    /// <summary>The level's depths: sampled every <see cref="DepthStep"/> cells, bilinear between the samples.</summary>
    private void FillDepth()
    {
        var start = Stopwatch.GetTimestamp();
        var side = _n / DepthStep;
        _window.FillDepth(_source!, _samples, _origin, _origin, _cell * DepthStep, side, 0, side);
        DepthMs += (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        for (var z = 0; z < _n; z++)
        {
            var fz = Math.Clamp((z + 0.5f) / DepthStep - 0.5f, 0f, side - 1.001f);
            var iz = (int)fz;
            var tz = fz - iz;

            for (var x = 0; x < _n; x++)
            {
                var fx = Math.Clamp((x + 0.5f) / DepthStep - 0.5f, 0f, side - 1.001f);
                var ix = (int)fx;
                var s = ix + iz * side;
                var tx = fx - ix;
                _depth[x + z * _n] = float.Lerp(float.Lerp(_samples[s], _samples[s + 1], tx), float.Lerp(_samples[s + side], _samples[s + side + 1], tx), tz);
            }
        }
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

    private void Solve(int n, float cell, float origin, bool nested)
    {
        _n = n;
        _cell = cell;
        _origin = origin;
        _nested = nested;
        FillDepth();
        Wavenumbers();
        SolvePhase();
        RayDirections();
        SolveTransport();

        for (var c = 0; c < _n * _n; c++)
        {
            _lag[c] = _psi[c] - PlanePhase(c % _n, c / _n);
            _amp[c] = Math.Clamp(MathF.Sqrt(MathF.Exp(_f[c]) * _cg0 / _cg[c]), 0f, MaxAmplitude);
        }

        FillLand();
        SolveBreak();
    }

    /// <summary>The open carrier's phase k₀ d̂·x at a cell's centre, in the window's local metres.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float PlanePhase(int x, int z) => _k0 * (_dirX * (_origin + (x + 0.5f) * _cell) + _dirZ * (_origin + (z + 0.5f) * _cell));

    private void Wavenumbers()
    {
        Dispersion(LandDepth, out var kLand, out _);
        kLand *= LandSlowness;

        for (var c = 0; c < _n * _n; c++)
        {
            var h = _depth[c];
            _wet[c] = h > LandDepth ? (byte)1 : (byte)0;

            if (_wet[c] == 0)
            {
                _k[c] = kLand;
                _cg[c] = 1f;
                continue;
            }

            Dispersion(h, out _k[c], out _cg[c]);
        }
    }

    /// <summary>Fast sweeping; the plane wave on the upwind edges, lagged as the coarse solve has it there.</summary>
    private void SolvePhase()
    {
        Array.Fill(_psi, float.PositiveInfinity);
        Array.Clear(_fixed);

        for (var i = 0; i < _n; i++)
        {
            Fix(i, 0, 0f, 1f);
            Fix(i, _n - 1, 0f, -1f);
            Fix(0, i, 1f, 0f);
            Fix(_n - 1, i, -1f, 0f);
        }

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
    }

    private void Order(int order, out int x0, out int dx, out int z0, out int dz)
    {
        x0 = (order & 1) == 0 ? 0 : _n - 1;
        dx = (order & 1) == 0 ? 1 : -1;
        z0 = (order & 2) == 0 ? 0 : _n - 1;
        dz = (order & 2) == 0 ? 1 : -1;
    }

    private void Fix(int x, int z, float inwardX, float inwardZ)
    {
        if (_dirX * inwardX + _dirZ * inwardZ <= 0.05f)
            return;

        var c = x + z * _n;
        _psi[c] = PlanePhase(x, z) + (_nested ? Parent(_parentLag, x, z) : 0f);
        _fixed[c] = 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool PhaseUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_fixed[c] != 0)
            return false;

        var a = MathF.Min(x > 0 ? _psi[c - 1] : float.PositiveInfinity, x < _n - 1 ? _psi[c + 1] : float.PositiveInfinity);
        var b = MathF.Min(z > 0 ? _psi[c - _n] : float.PositiveInfinity, z < _n - 1 ? _psi[c + _n] : float.PositiveInfinity);

        if (float.IsPositiveInfinity(a) && float.IsPositiveInfinity(b))
            return false;

        var f = _k[c] * _cell;
        var next = MathF.Abs(a - b) >= f
            ? MathF.Min(a, b) + f
            : 0.5f * (a + b + MathF.Sqrt(2f * f * f - (a - b) * (a - b)));

        if (next >= _psi[c])
            return false;

        _psi[c] = next;
        return true;
    }

    /// <summary>Unit ray direction from the upwind phase gradient (the neighbours the Godunov update used).</summary>
    private void RayDirections()
    {
        for (var z = 0; z < _n; z++)
        {
            for (var x = 0; x < _n; x++)
            {
                var c = x + z * _n;
                var left = x > 0 ? _psi[c - 1] : float.PositiveInfinity;
                var right = x < _n - 1 ? _psi[c + 1] : float.PositiveInfinity;
                var down = z > 0 ? _psi[c - _n] : float.PositiveInfinity;
                var up = z < _n - 1 ? _psi[c + _n] : float.PositiveInfinity;
                var a = MathF.Min(left, right);
                var b = MathF.Min(down, up);
                var gx = a < _psi[c] ? (_psi[c] - a) * (left <= right ? 1f : -1f) : 0f;
                var gz = b < _psi[c] ? (_psi[c] - b) * (down <= up ? 1f : -1f) : 0f;
                var length = MathF.Sqrt(gx * gx + gz * gz);

                (_ux[c], _uz[c]) = _fixed[c] != 0 || length < 1e-6f || _wet[c] == 0 ? (_dirX, _dirZ) : (gx / length, gz / length);
            }
        }
    }

    /// <summary>u·∇F = −∇·u with F = ln(E c_g) against the open sea (F = 0); the upwind edges take the coarse solve's.</summary>
    private void SolveTransport()
    {
        Array.Clear(_f);

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

    private void TransportUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_fixed[c] != 0 || _wet[c] == 0)
            return;

        var ux = _ux[c];
        var uz = _uz[c];
        var div = (Neighbour(_ux, c, x + 1, z) - Neighbour(_ux, c, x - 1, z) + Neighbour(_uz, c, x, z + 1) - Neighbour(_uz, c, x, z - 1)) / (2f * _cell);
        div = Math.Clamp(div, -MaxDivergence, MaxDivergence);

        var fx = UpwindTransport(c, x - (int)MathF.Sign(ux), z);
        var fz = UpwindTransport(c, x, z - (int)MathF.Sign(uz));
        var wx = MathF.Abs(ux) / _cell;
        var wz = MathF.Abs(uz) / _cell;

        _f[c] = Math.Clamp((wx * fx + wz * fz - div) / (wx + wz), MinTransport, MaxTransport);
    }

    private float Neighbour(float[] field, int c, int x, int z)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return field[c];

        var n = x + z * _n;
        return _wet[n] != 0 ? field[n] : field[c];
    }

    private float UpwindTransport(int c, int x, int z)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return _nested ? ParentTransport(x, z, c) : 0f;

        var n = x + z * _n;
        return _wet[n] != 0 ? _f[n] : _f[c];
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
        const float scale = CoastWindow.CellSize / CoarseCell;
        const float offset = -CoarseOrigin / CoarseCell - 0.5f - ParentFirst;
        var fx = Math.Clamp((x + 0.5f) * scale + offset, 0f, ParentSide - 1.001f);
        var fz = Math.Clamp((z + 0.5f) * scale + offset, 0f, ParentSide - 1.001f);
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
            }
        }
    }

    /// <summary>Land takes the lag and amplitude of neighbouring water, so shoreline samples stay smooth.</summary>
    private void FillLand()
    {
        for (var c = 0; c < _n * _n; c++)
        {
            if (_wet[c] == 0)
                _lag[c] = float.NaN;
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
                (_lag[c], _amp[c]) = (0f, 1f);
        }
    }

    private void FillUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_wet[c] != 0)
            return;

        var lag = 0f;
        var amp = 0f;
        var count = 0;
        Accumulate(x - 1, z, ref lag, ref amp, ref count);
        Accumulate(x + 1, z, ref lag, ref amp, ref count);
        Accumulate(x, z - 1, ref lag, ref amp, ref count);
        Accumulate(x, z + 1, ref lag, ref amp, ref count);

        if (count > 0)
            (_lag[c], _amp[c]) = (lag / count, amp / count);
    }

    private void Accumulate(int x, int z, ref float lag, ref float amp, ref int count)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return;

        var n = x + z * _n;

        if (float.IsNaN(_lag[n]))
            return;

        lag += _lag[n];
        amp += _amp[n];
        count++;
    }

    /// <summary>
    /// The break clock and the breaker type, carried along the rays: each cell takes its upwind neighbours' values a
    /// cell's travel back and runs the clock over that travel.
    /// </summary>
    private void SolveBreak()
    {
        var carrier = _key.Carrier;

        var incoherent = SurfCarrier.IncoherentKh / _k0;

        for (var c = 0; c < _n * _n; c++)
            _unbroken[c] = _depth[c] < incoherent ? SurfCarrier.Unbroken(carrier.Height, _omega, _depth[c], _amp[c]) : 0f;

        Array.Clear(_break);
        Array.Clear(_plunge);

        for (var it = 0; it < BreakIterations; it++)
        {
            for (var order = 0; order < 4; order++)
            {
                Order(order, out var x0, out var dx, out var z0, out var dz);

                for (int z = z0, zi = 0; zi < _n; z += dz, zi++)
                {
                    for (int x = x0, xi = 0; xi < _n; x += dx, xi++)
                        BreakUpdate(x, z);
                }
            }
        }
    }

    private void BreakUpdate(int x, int z)
    {
        var c = x + z * _n;

        if (_wet[c] == 0)
            return;

        var ux = _ux[c];
        var uz = _uz[c];
        var wx = MathF.Abs(ux);
        var wz = MathF.Abs(uz);
        var sum = MathF.Max(wx + wz, 1e-6f);
        var ax = x - (int)MathF.Sign(ux);
        var az = z - (int)MathF.Sign(uz);
        var upwind = (wx * UpwindBreak(_break, _parentBreak, c, ax, z) + wz * UpwindBreak(_break, _parentBreak, c, x, az)) / sum;
        var raw = SurfCarrier.RawCap(_unbroken[c], _depth[c]);

        if (upwind <= 0f && raw > IdleCap)
        {
            _break[c] = 0f;
            _plunge[c] = 0f;
            return;
        }

        var travel = _cell / sum;
        var rate = SurfCarrier.Smooth(((upwind > ClockStartedDistance ? ClockStopCap : ClockStartCap) - raw) / ClockRampCap);
        _break[c] = MathF.Min(upwind * MathF.Exp(-travel * (1f - rate) / ReformDistance) + travel * rate, CoastAtlas.MaxBreakDistance);

        var carried = (wx * UpwindBreak(_plunge, _parentPlunge, c, ax, z) + wz * UpwindBreak(_plunge, _parentPlunge, c, x, az)) / sum;
        _plunge[c] = float.Lerp(LocalPlunge(x, z, ux, uz), carried, SurfCarrier.Smooth(upwind / PlungeHold));
    }

    private float UpwindBreak(float[] field, float[] parent, int c, int x, int z)
    {
        if ((uint)x >= _n || (uint)z >= _n)
            return _nested ? Parent(parent, x, z) : 0f;

        var n = x + z * _n;
        return _wet[n] != 0 ? field[n] : field[c];
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
    /// The atlas: the spatial phase (the plane wave plus the lag, land filled from the water beside it) and the direction
    /// of travel from its central differences, which are continuous where the upwind gradient the solve used is not.
    /// </summary>
    private void Write()
    {
        var atlas = _atlas!;
        atlas.Key = _key;
        atlas.OriginU = _window.OriginU;
        atlas.OriginV = _window.OriginV;

        for (var c = 0; c < _n * _n; c++)
            _psi[c] = PlanePhase(c % _n, c / _n) + _lag[c];

        for (var z = 0; z < _n; z++)
        {
            for (var x = 0; x < _n; x++)
            {
                var c = x + z * _n;
                var gx = _psi[x < _n - 1 ? c + 1 : c] - _psi[x > 0 ? c - 1 : c];
                var gz = _psi[z < _n - 1 ? c + _n : c] - _psi[z > 0 ? c - _n : c];

                if (x == 0 || x == _n - 1)
                    gx *= 2f;

                if (z == 0 || z == _n - 1)
                    gz *= 2f;

                var length = MathF.Sqrt(gx * gx + gz * gz);
                var (dirX, dirZ) = length > 1e-6f ? (gx / length, gz / length) : (_dirX, _dirZ);
                atlas.Write(c, _psi[c], _depth[c], _amp[c], _break[c], _plunge[c], dirX, dirZ);
            }
        }
    }
}
