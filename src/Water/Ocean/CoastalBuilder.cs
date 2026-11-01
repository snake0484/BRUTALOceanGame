using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Engine.Jobs;

namespace BrutalGame.Water.Ocean;

/// <summary>Seabed for layer B, sampled off the main thread. Must not depend on what terrain happens to be streamed in.</summary>
public interface ICoastalDepth
{
    /// <summary>Fills rows [z0, z1) of a Resolution² grid of water depths (m, sea level − ground) at cell (m), cell (0, 0)'s corner at origin. Called from jobs.</summary>
    void Fill(Span<float> depth, Vector2 origin, float cell, int z0, int z1);
}

/// <summary>
/// Builds <see cref="CoastalField"/>s off the main thread: depth jobs (from an <see cref="ICoastalDepth"/>), one job per
/// reference frequency (eikonal phase + energy transport), then a finish job (depth channel, edge fade, active flags).
/// Triple-buffered for crossfading; main thread only coordinates.
/// </summary>
public sealed class CoastalBuilder : IDisposable
{
    private const int N = CoastalField.Resolution;
    private const int DepthJobs = 8;

    private enum Stage
    {
        Idle,
        Depth,
        References,
        Finish,
    }

    private readonly JobScheduler _scheduler;
    private readonly CoastalDepthJob[] _depthJobs = new CoastalDepthJob[DepthJobs];
    private readonly CoastalRefJob[] _refJobs = new CoastalRefJob[CoastalField.RefCount];
    private readonly CoastalFinishJob _finish = new();
    private readonly float[] _depth = new float[N * N];
    /// <summary>Versions are unique across builders: the renderer keys its buffer slots by them.</summary>
    private static int _versions;

    private readonly float _cell;
    private CoastalField _back;
    private Stage _stage;
    private long _start;

    /// <param name="cell">The fields' cell size (m): <see cref="CoastalField.CellSize"/> near, <see cref="CoastalField.FarCellSize"/> far.</param>
    public CoastalBuilder(float cell = CoastalField.CellSize)
    {
        _cell = cell;
        _back = new CoastalField(cell);

        // Background work nothing waits on: few workers, below normal priority, so it never starves per-tick jobs.
        _scheduler = new JobScheduler((uint)WorkerBudget.Side, "Coastal", ThreadPriority.BelowNormal);

        for (var i = 0; i < _depthJobs.Length; i++)
            _depthJobs[i] = new CoastalDepthJob();

        for (var j = 0; j < _refJobs.Length; j++)
            _refJobs[j] = new CoastalRefJob(j);
    }

    public CoastalField? Current { get; private set; }

    /// <summary>The field <see cref="Current"/> replaced; the owner crossfades from it.</summary>
    public CoastalField? Previous { get; private set; }

    public bool Busy => _stage != Stage.Idle;

    public float LastBuildMs { get; private set; }

    /// <param name="depth">Resolution² water depths (m) at the builder's cell, sea level − ground (≤ 0 on land), row-major z.</param>
    /// <param name="direction">Direction (radians) the incident waves travel toward.</param>
    /// <param name="far">The far field to solve inside: waves enter through the upwind edges as it has them, and the edges blend into it. Null = open ocean.</param>
    public bool TryStart(ReadOnlySpan<float> depth, Vector2 origin, float direction, CoastalField? far = null)
    {
        if (Busy)
            return false;

        depth.CopyTo(_depth);
        Begin(origin, direction, far);
        QueueReferences();
        return true;
    }

    /// <summary>As above, with the depths sampled from <paramref name="source"/> on the job system first.</summary>
    public bool TryStart(ICoastalDepth source, Vector2 origin, float direction, CoastalField? far = null)
    {
        if (Busy)
            return false;

        Begin(origin, direction, far);
        const int rows = N / DepthJobs;

        for (var i = 0; i < DepthJobs; i++)
        {
            _depthJobs[i].Prepare(source, _depth, origin, _cell, i * rows, (i + 1) * rows);
            _scheduler.Queue(_depthJobs[i], 1);
        }

        _scheduler.ExecuteJobs();
        _stage = Stage.Depth;
        return true;
    }

    private void Begin(Vector2 origin, float direction, CoastalField? far)
    {
        _back.Origin = origin;
        _back.Direction = direction;
        _back.Far = far;
        _start = Stopwatch.GetTimestamp();
    }

    private void QueueReferences()
    {
        foreach (var job in _refJobs)
        {
            job.Prepare(_depth, _back, _back.Direction);
            _scheduler.Queue(job, 1);
        }

        _scheduler.ExecuteJobs();
        _stage = Stage.References;
    }

    /// <summary>Main thread, once per frame. Returns true when a new field has been swapped into <see cref="Current"/>.</summary>
    public bool Poll()
    {
        if (_stage == Stage.Idle || _scheduler.IsExecuting)
            return false;

        if (_stage == Stage.Depth)
        {
            QueueReferences();
            return false;
        }

        if (_stage == Stage.References)
        {
            _finish.Prepare(_depth, _back);
            _scheduler.Queue(_finish, 1);
            _scheduler.ExecuteJobs();
            _stage = Stage.Finish;
            return false;
        }

        // Three fields: the one fading out, the one fading in, and the next build's target (the owner must not start a
        // build until the fade has finished, so the retired field is free).
        _back.Version = ++_versions;
        var retired = Previous;
        Previous = Current;
        Current = _back;
        _back = retired ?? new CoastalField(_cell);
        _stage = Stage.Idle;
        LastBuildMs = (float)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        return true;
    }

    public void Dispose()
    {
        _scheduler.AbortJobs(waitForCompletion: true);
    }
}

internal sealed class CoastalDepthJob : IJob
{
    private ICoastalDepth? _source;
    private float[] _depth = [];
    private Vector2 _origin;
    private float _cell;
    private int _z0;
    private int _z1;

    public void Prepare(ICoastalDepth source, float[] depth, Vector2 origin, float cell, int z0, int z1)
    {
        _source = source;
        _depth = depth;
        _origin = origin;
        _cell = cell;
        _z0 = z0;
        _z1 = z1;
    }

    public void Run() => _source!.Fill(_depth, _origin, _cell, _z0, _z1);
}

/// <summary>Eikonal phase lag and energy-transport amplitude for one reference frequency.</summary>
internal sealed class CoastalRefJob(int reference) : IJob
{
    private const int N = CoastalField.Resolution;
    private const float Gravity = 9.81f;
    private const float LandDepth = 0.2f;
    private const float LandSlowness = 8f;
    private const float MaxDivergence = 0.3f;
    private const float MaxAmplitude = CoastalField.MaxAmplitudeFactor;
    private const int PhaseIterations = 3;
    private const int TransportIterations = 2;
    private const int FillIterations = 2;
    public const int EdgeFade = 16;
    private const float LagEpsilon = 0.01f;
    private const float AmpEpsilon = 1e-3f;

    private readonly float[] _k = new float[N * N];
    private readonly float[] _cg = new float[N * N];
    private readonly float[] _psi = new float[N * N];
    private readonly float[] _ux = new float[N * N];
    private readonly float[] _uz = new float[N * N];
    private readonly float[] _f = new float[N * N];
    private readonly float[] _lag = new float[N * N];
    private readonly float[] _amp = new float[N * N];
    private readonly byte[] _fixed = new byte[N * N];
    private readonly byte[] _wet = new byte[N * N];
    private float[] _depth = [];
    private CoastalField? _field;
    private CoastalField? _far;
    private float _cell;
    private float _cg0;
    private Vector2 _dir;

    public void Prepare(float[] depth, CoastalField field, float direction)
    {
        _depth = depth;
        _field = field;
        _far = field.Far;
        _cell = field.Cell;
        _dir = new Vector2(MathF.Cos(direction), MathF.Sin(direction));
    }

    public void Run()
    {
        var omega = CoastalField.RefOmega(reference);
        var k0 = omega * omega / Gravity;
        _cg0 = 0.5f * Gravity / omega;

        Wavenumbers(omega);
        SolvePhase(k0);
        RayDirections();
        SolveTransport();

        for (var c = 0; c < N * N; c++)
        {
            _lag[c] = _psi[c] - k0 * Vector2.Dot(_dir, CellCentre(c));
            _amp[c] = Math.Clamp(MathF.Sqrt(MathF.Exp(_f[c]) * _cg0 / _cg[c]), 0f, MaxAmplitude);
        }

        FillLand();
        Write();
    }

    private Vector2 CellCentre(int c) => new(((c % N) + 0.5f) * _cell, ((c / N) + 0.5f) * _cell);

    /// <summary>The far field's lag and amplitude for this reference at local cell (x, z), which may lie past the edge; open ocean where it has nothing.</summary>
    private (float Lag, float Amp) FarAt(int x, int z)
    {
        var s = new CoastalSample();
        var p = _field!.Origin + new Vector2((x + 0.5f) * _cell, (z + 0.5f) * _cell);
        return _far is not null && _far.Sample(p.X, p.Y, ref s) ? (s.Lag[reference], s.Amp[reference]) : (0f, 1f);
    }

    /// <summary>F = ln(E c_g) of the far field's amplitude at local cell (x, z), against cell c's group speed.</summary>
    private float FarF(int x, int z, int c)
    {
        var amp = FarAt(x, z).Amp;
        return Math.Clamp(MathF.Log(MathF.Max(amp * amp * _cg[c] / _cg0, 1e-6f)), -8f, 2.2f);
    }

    private void Wavenumbers(float omega)
    {
        var kLand = (float)Jonswap.WaveNumber(omega, LandDepth) * LandSlowness;

        for (var c = 0; c < N * N; c++)
        {
            var h = _depth[c];
            _wet[c] = h > LandDepth ? (byte)1 : (byte)0;

            if (_wet[c] == 0)
            {
                _k[c] = kLand;
                _cg[c] = 1f;
                continue;
            }

            var k = (float)Jonswap.WaveNumber(omega, h);
            var kh = k * h;
            var n = kh > 10f ? 0.5f : 0.5f * (1f + 2f * kh / MathF.Sinh(2f * kh));
            _k[c] = k;
            _cg[c] = n * omega / k;
        }
    }

    /// <summary>Fast sweeping, first-order Godunov, plane-wave boundary on the upwind edges, lagged as the far field has it there.</summary>
    private void SolvePhase(float k0)
    {
        Array.Fill(_psi, float.PositiveInfinity);
        Array.Clear(_fixed);

        for (var i = 0; i < N; i++)
        {
            Fix(i, 0, new Vector2(0f, 1f), k0);
            Fix(i, N - 1, new Vector2(0f, -1f), k0);
            Fix(0, i, new Vector2(1f, 0f), k0);
            Fix(N - 1, i, new Vector2(-1f, 0f), k0);
        }

        for (var it = 0; it < PhaseIterations; it++)
            for (var order = 0; order < 4; order++)
                Sweep(order, PhaseUpdate);
    }

    private void Fix(int x, int z, Vector2 inward, float k0)
    {
        if (Vector2.Dot(_dir, inward) <= 0.05f)
            return;

        var c = x + z * N;
        _psi[c] = k0 * Vector2.Dot(_dir, CellCentre(c)) + (_far is null ? 0f : FarAt(x, z).Lag);
        _fixed[c] = 1;
    }

    private static void Sweep(int order, Action<int, int> update)
    {
        var x0 = (order & 1) == 0 ? 0 : N - 1;
        var dx = (order & 1) == 0 ? 1 : -1;
        var z0 = (order & 2) == 0 ? 0 : N - 1;
        var dz = (order & 2) == 0 ? 1 : -1;

        for (int z = z0, zi = 0; zi < N; z += dz, zi++)
            for (int x = x0, xi = 0; xi < N; x += dx, xi++)
                update(x, z);
    }

    private void PhaseUpdate(int x, int z)
    {
        var c = x + z * N;
        if (_fixed[c] != 0)
            return;

        var a = MathF.Min(x > 0 ? _psi[c - 1] : float.PositiveInfinity, x < N - 1 ? _psi[c + 1] : float.PositiveInfinity);
        var b = MathF.Min(z > 0 ? _psi[c - N] : float.PositiveInfinity, z < N - 1 ? _psi[c + N] : float.PositiveInfinity);
        var f = _k[c] * _cell;

        if (float.IsPositiveInfinity(a) && float.IsPositiveInfinity(b))
            return;

        var next = MathF.Abs(a - b) >= f
            ? MathF.Min(a, b) + f
            : 0.5f * (a + b + MathF.Sqrt(2f * f * f - (a - b) * (a - b)));

        if (next < _psi[c])
            _psi[c] = next;
    }

    /// <summary>Unit ray direction from the upwind phase gradient (the same neighbours the Godunov update used).</summary>
    private void RayDirections()
    {
        for (var z = 0; z < N; z++)
        {
            for (var x = 0; x < N; x++)
            {
                var c = x + z * N;
                var left = x > 0 ? _psi[c - 1] : float.PositiveInfinity;
                var right = x < N - 1 ? _psi[c + 1] : float.PositiveInfinity;
                var down = z > 0 ? _psi[c - N] : float.PositiveInfinity;
                var up = z < N - 1 ? _psi[c + N] : float.PositiveInfinity;
                var a = MathF.Min(left, right);
                var b = MathF.Min(down, up);
                var gx = a < _psi[c] ? (_psi[c] - a) * (left <= right ? 1f : -1f) : 0f;
                var gz = b < _psi[c] ? (_psi[c] - b) * (down <= up ? 1f : -1f) : 0f;
                var length = MathF.Sqrt(gx * gx + gz * gz);

                (_ux[c], _uz[c]) = _fixed[c] != 0 || length < 1e-6f || _wet[c] == 0 ? (_dir.X, _dir.Y) : (gx / length, gz / length);
            }
        }
    }

    /// <summary>u·∇F = −∇·u with F = ln(E c_g), relative to the open sea (F = 0); the upwind edges take the far field's.</summary>
    private void SolveTransport()
    {
        Array.Clear(_f);

        if (_far is not null)
            for (var c = 0; c < N * N; c++)
                if (_fixed[c] != 0)
                    _f[c] = FarF(c % N, c / N, c);

        for (var it = 0; it < TransportIterations; it++)
            for (var order = 0; order < 4; order++)
                Sweep(order, TransportUpdate);
    }

    private void TransportUpdate(int x, int z)
    {
        var c = x + z * N;
        if (_fixed[c] != 0 || _wet[c] == 0)
            return;

        var ux = _ux[c];
        var uz = _uz[c];
        var div = (Neighbour(_ux, c, x + 1, z) - Neighbour(_ux, c, x - 1, z) + Neighbour(_uz, c, x, z + 1) - Neighbour(_uz, c, x, z - 1)) / (2f * _cell);
        div = Math.Clamp(div, -MaxDivergence, MaxDivergence);

        var ax = x - MathF.Sign(ux);
        var az = z - MathF.Sign(uz);
        var fx = Upwind(c, (int)ax, z);
        var fz = Upwind(c, x, (int)az);
        var wx = MathF.Abs(ux) / _cell;
        var wz = MathF.Abs(uz) / _cell;

        _f[c] = Math.Clamp((wx * fx + wz * fz - div) / (wx + wz), -8f, 2.2f);
    }

    private float Neighbour(float[] field, int c, int x, int z)
    {
        if ((uint)x >= N || (uint)z >= N)
            return field[c];

        var n = x + z * N;
        return _wet[n] != 0 ? field[n] : field[c];
    }

    private float Upwind(int c, int x, int z)
    {
        if ((uint)x >= N || (uint)z >= N)
            return _far is null ? 0f : FarF(x, z, c);

        var n = x + z * N;
        return _wet[n] != 0 ? _f[n] : _f[c];
    }

    /// <summary>Land takes the lag and amplitude of neighbouring water, so shoreline samples stay smooth.</summary>
    private void FillLand()
    {
        for (var c = 0; c < N * N; c++)
            if (_wet[c] == 0)
                _lag[c] = float.NaN;

        for (var it = 0; it < FillIterations; it++)
            for (var order = 0; order < 4; order++)
                Sweep(order, FillUpdate);

        for (var c = 0; c < N * N; c++)
            if (float.IsNaN(_lag[c]))
                (_lag[c], _amp[c]) = (0f, 1f);
    }

    private void FillUpdate(int x, int z)
    {
        var c = x + z * N;
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
        if ((uint)x >= N || (uint)z >= N)
            return;

        var n = x + z * N;
        if (float.IsNaN(_lag[n]))
            return;

        lag += _lag[n];
        amp += _amp[n];
        count++;
    }

    private void Write()
    {
        var texels = _field!.Texels;
        var o = 4 + 2 * reference;

        for (var c = 0; c < N * N; c++)
        {
            // The edges blend into the far field (the open ocean for a far field itself). Solver rounding (~1e-3 rad
            // over 1 km of phase) must not mark deep water as coastal.
            var w = EdgeWeight(c % N, c / N);
            var (farLag, farAmp) = w < 1f ? FarAt(c % N, c / N) : (0f, 1f);
            var lag = float.Lerp(farLag, _lag[c], w);
            var amp = float.Lerp(farAmp, _amp[c], w);
            lag = MathF.Abs(lag) < LagEpsilon ? 0f : lag;
            amp = MathF.Abs(amp - 1f) < AmpEpsilon ? 1f : amp;
            texels[c * CoastalField.TexelFloats + o] = lag;
            texels[c * CoastalField.TexelFloats + o + 1] = amp;
        }
    }

    public static float EdgeWeight(int x, int z)
    {
        var edge = Math.Min(Math.Min(x, N - 1 - x), Math.Min(z, N - 1 - z));
        var t = Math.Clamp(edge / (float)EdgeFade, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>Depth channel (faded at the edges to the far field's, or to deep) and per-texel active flags.</summary>
internal sealed class CoastalFinishJob : IJob
{
    private const int N = CoastalField.Resolution;
    private const float ActiveDepth = 60f;
    private const float Epsilon = 1e-4f;

    private float[] _depth = [];
    private CoastalField? _field;

    public void Prepare(float[] depth, CoastalField field)
    {
        _depth = depth;
        _field = field;
    }

    public void Run()
    {
        var texels = _field!.Texels;

        for (var c = 0; c < N * N; c++)
        {
            var t = c * CoastalField.TexelFloats;
            var w = CoastalRefJob.EdgeWeight(c % N, c / N);
            var depth = w < 1f ? float.Lerp(FarDepth(c), _depth[c], w) : _depth[c];
            var active = depth < ActiveDepth;

            for (var j = 0; j < CoastalField.RefCount && !active; j++)
                active = MathF.Abs(texels[t + 4 + 2 * j]) > Epsilon || MathF.Abs(texels[t + 5 + 2 * j] - 1f) > Epsilon;

            texels[t] = depth;
            texels[t + 1] = active ? 1f : 0f;
            texels[t + 2] = 0f;
            texels[t + 3] = 0f;
        }
    }

    /// <summary>The far field's depth under texel c; deep without one, or where it has nothing.</summary>
    private float FarDepth(int c)
    {
        var field = _field!;
        var p = field.Origin + new Vector2((c % N + 0.5f) * field.Cell, (c / N + 0.5f) * field.Cell);
        return field.Far is not null && field.Far.Depth(p.X, p.Y, out var depth) ? depth : CoastalField.DeepDepth;
    }
}
