using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// CPU swell: physics-relevant low band of the ocean, deterministic from seed, wind history and time.
/// Call <see cref="SetWind"/> when wind changes and <see cref="SetTime"/> once per tick on the main thread.
/// Sample methods are read-only between SetTime calls and safe to call from jobs. An <see cref="IShoreDepth"/> applies
/// the depth cap (see <see cref="DepthCapGamma"/>).
/// </summary>
public sealed class OceanSwell
{
    public const int MaxComponents = WaveSet.Capacity * 2;
    public const int InversionIterations = 4;

    /// <summary>Depth-limited swell: every component scaled by min(1, this × depth / Hs) (ocean_common.glsl swellCap).</summary>
    public const float DepthCapGamma = 0.55f;

    /// <summary>
    /// Choppiness × rms of the Gerstner trace Σ a·k·cos θ at most: 1 / this standard deviations to a fold (Jacobian 0),
    /// 4 here, 3e-5 of the surface, on the crests that whitecap. The worst case Σ|a·k| is 14× the rms over 128 random
    /// phases: bounding it pinned the chop at a fixed share of the slope, so a high sea peaked no harder than a low one.
    /// </summary>
    private const float MaxRmsSteepness = 0.25f;
    private static readonly int Lanes = Vector256<float>.Count;

    // Surf carrier: the wave the shore wash times its run-up from, at the dominant band's peak, carrying this share of the
    // energy of the components within the peak band (|ln(ω / ω_peak)| under BandInner, fading out by BandOuter).
    private const float CarrierShare = 0.75f;
    private const float BandInner = 0.139f;
    private const float BandOuter = 0.278f;

    /// <summary>
    /// Sets: the surf wave's height is modulated by a group envelope that travels with its crests (phase GroupRatio × the
    /// carrier's spatial phase − GroupRatio ω t, so a set every 1 / GroupRatio waves). A narrow band beats into long
    /// groups, so GroupRatio is twice the dominant band's relative bandwidth (a set every 8 waves for a wind sea, every 10
    /// for a 5%-wide groundswell), within these bounds. The envelope itself is ocean_common.glsl groupEnvelope.
    /// </summary>
    private const float MinGroupRatio = 1f / 20f;
    private const float MaxGroupRatio = 1f / 4f;

    public double BlendSeconds = 30.0;

    private readonly ulong _seed;
    private WaveSet _from = new();
    private WaveSet _to = new();
    private WaveSet _spare = new();
    private SwellParams _pending;
    private bool _hasPending;
    private bool _hasWind;
    private bool _blending;
    private double _blendStart;
    private uint _generation;

    private readonly float[] _kx = new float[MaxComponents];
    private readonly float[] _kz = new float[MaxComponents];
    private readonly float[] _phase = new float[MaxComponents];
    private readonly float[] _a = new float[MaxComponents];
    private readonly float[] _akx = new float[MaxComponents];
    private readonly float[] _akz = new float[MaxComponents];
    private readonly float[] _cx = new float[MaxComponents];
    private readonly float[] _cz = new float[MaxComponents];
    private readonly float[] _awx = new float[MaxComponents];
    private readonly float[] _awz = new float[MaxComponents];
    private readonly float[] _aw = new float[MaxComponents];
    private readonly float[] _omega = new float[MaxComponents];
    private int _count;
    private int _paddedCount;

    private float _carrierOmega;
    private float _carrierPhase;
    private float _groupPhase;
    private float _breakerHeight;
    private double _spreadNormFor = double.NaN;
    private double _spreadNorm;

    public OceanSwell(ulong seed)
    {
        _seed = seed;
    }

    /// <summary>Direction the dominant band travels toward (radians): the surf carrier's.</summary>
    public float DominantDirection => _to.CarrierDirection;

    /// <summary>Sets: one every 1 / this waves, from the dominant band's bandwidth.</summary>
    public float GroupRatio { get; private set; } = 2f * (float)WaveSet.WindSeaBandwidth;

    public double Time { get; private set; }

    public int ComponentCount => _count;

    /// <summary>Effective horizontal displacement scale after the steepness clamp.</summary>
    public float Choppiness { get; private set; }

    public float SignificantWaveHeight { get; private set; }

    public float PeakPeriod => _to.CarrierOmega > 0.0 ? (float)(Math.Tau / _to.CarrierOmega) : 0f;

    /// <summary>0..1 progress of the active wind blend, 1 when settled.</summary>
    public float BlendProgress { get; private set; } = 1f;

    public SwellParams Wind => _to.Params;

    /// <summary>Effective spectrum for GPU detail, blended with the same weight as the CPU components.</summary>
    public SpectrumState GpuSpectrum { get; private set; }

    /// <summary>
    /// Queues a wind change. The spectrum crossfades over <see cref="BlendSeconds"/>; never snaps.
    /// </summary>
    public void SetWind(in SwellParams wind)
    {
        if (!_hasWind)
        {
            _to.Build(wind, SplitMix64.Mix(_seed, _generation++));
            _hasWind = true;
            Prepare();
            return;
        }

        if (_blending)
        {
            _pending = wind;
            _hasPending = true;
            return;
        }

        StartBlend(wind);
        Prepare();
    }

    private void StartBlend(in SwellParams wind)
    {
        (_from, _to, _spare) = (_to, _spare, _from);
        _to.Build(wind, SplitMix64.Mix(_seed, _generation++));
        _blendStart = Time;
        _blending = true;
    }

    /// <summary>
    /// Advances to simulation time and caches per-component phase. O(components), no allocation.
    /// </summary>
    public void SetTime(double time)
    {
        Time = time;
        Prepare();
    }

    private void Prepare()
    {
        var weight = 1.0;
        if (_blending)
        {
            weight = BlendSeconds > 0.0 ? (Time - _blendStart) / BlendSeconds : 1.0;
            if (weight >= 1.0)
            {
                _blending = false;
                weight = 1.0;

                if (_hasPending)
                {
                    _hasPending = false;
                    StartBlend(_pending);
                    weight = 0.0;
                }
            }
        }

        weight = Math.Clamp(weight, 0.0, 1.0);
        BlendProgress = (float)weight;

        var smooth = weight * weight * (3.0 - 2.0 * weight);
        UpdateGpuSpectrum(_blending ? (float)smooth : 1f);
        _count = 0;

        if (_blending)
            Append(_from, Math.Sqrt(1.0 - smooth));

        Append(_to, _blending ? Math.Sqrt(smooth) : 1.0);

        _paddedCount = (_count + Lanes - 1) / Lanes * Lanes;
        for (var i = _count; i < _paddedCount; i++)
            _kx[i] = _kz[i] = _phase[i] = _a[i] = _akx[i] = _akz[i] = _cx[i] = _cz[i] = _awx[i] = _awz[i] = _aw[i] = _omega[i] = 0f;

        var steepness = 0.0;
        var energy = 0.0;
        for (var i = 0; i < _count; i++)
        {
            steepness += 0.5 * _a[i] * _a[i] * (_kx[i] * _kx[i] + _kz[i] * _kz[i]);
            energy += 0.5 * _a[i] * _a[i];
        }

        steepness = Math.Sqrt(steepness);
        var choppiness = _to.Params.Choppiness;
        if (steepness * choppiness > MaxRmsSteepness)
            choppiness = (float)(MaxRmsSteepness / steepness);

        Choppiness = choppiness;
        SignificantWaveHeight = (float)(4.0 * Math.Sqrt(energy));

        for (var i = 0; i < _count; i++)
        {
            _cx[i] *= choppiness;
            _cz[i] *= choppiness;
        }

        PrepareCarrier();
    }

    /// <summary>The surf carrier this tick: phase −ω t, the set phase, and its breaker height.</summary>
    private void PrepareCarrier()
    {
        var omega = (float)_to.CarrierOmega;
        _carrierOmega = omega;
        var phase = -(double)omega * Time;
        _carrierPhase = (float)(phase - Math.Floor(phase / Math.Tau) * Math.Tau);
        GroupRatio = Math.Clamp(2f * (float)_to.CarrierBandwidth, MinGroupRatio, MaxGroupRatio);
        var group = phase * GroupRatio;
        _groupPhase = (float)(group - Math.Floor(group / Math.Tau) * Math.Tau);

        if (omega <= 0f)
        {
            _breakerHeight = 0f;
            return;
        }

        var energy = 0f;
        for (var i = 0; i < _count; i++)
        {
            var band = 1f - Smooth((MathF.Abs(MathF.Log(_omega[i] / omega)) - BandInner) / (BandOuter - BandInner));
            energy += _a[i] * _a[i] * band;
        }

        // Komar and Gaylord (1972): H_b = 0.39 g^0.2 (T H0²)^0.4, H0 the carrier's deep-water height.
        var h0 = 2f * MathF.Sqrt(CarrierShare * energy);
        _breakerHeight = 0.39f * MathF.Pow(9.81f, 0.2f) * MathF.Pow(MathF.Tau / omega * h0 * h0, 0.4f);
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The surf carrier for the GPU at <paramref name="renderTime"/>: (ω, phase, set group ratio, set group phase).
    /// </summary>
    public System.Numerics.Vector4 GpuCarrier(double renderTime)
    {
        var phase = _carrierPhase - _carrierOmega * (renderTime - Time);
        phase -= Math.Floor(phase / Math.Tau) * Math.Tau;
        var group = _groupPhase - _carrierOmega * GroupRatio * (renderTime - Time);
        group -= Math.Floor(group / Math.Tau) * Math.Tau;
        return new System.Numerics.Vector4(_carrierOmega, (float)phase, GroupRatio, (float)group);
    }

    /// <summary>
    /// The shore wash for the GPU: (carrier travel direction x, z, breaker height, open swell Hs); breaker height 0 turns
    /// the swash off.
    /// </summary>
    public System.Numerics.Vector4 GpuShore(bool swash)
    {
        var (dirZ, dirX) = MathF.SinCos(DominantDirection);
        return new System.Numerics.Vector4(dirX, dirZ, swash ? _breakerHeight : 0f, SignificantWaveHeight);
    }

    private void UpdateGpuSpectrum(float t)
    {
        var from = _blending ? _from.Params : _to.Params;
        var to = _to.Params;
        var p = to;

        p.WindSpeed = float.Lerp(from.WindSpeed, to.WindSpeed, t);
        p.Fetch = float.Lerp(from.Fetch, to.Fetch, t);
        p.Gamma = float.Lerp(from.Gamma, to.Gamma, t);
        p.Spread = float.Lerp(from.Spread, to.Spread, t);
        p.MinWavelength = float.Lerp(from.MinWavelength, to.MinWavelength, t);
        p.Choppiness = float.Lerp(from.Choppiness, to.Choppiness, t);
        p.Depth = float.IsFinite(from.Depth) && float.IsFinite(to.Depth) ? float.Lerp(from.Depth, to.Depth, t) : to.Depth;

        var delta = MathF.IEEERemainder(to.WindDirection - from.WindDirection, MathF.Tau);
        p.WindDirection = from.WindDirection + delta * t;

        if (p.Spread != _spreadNormFor)
        {
            _spreadNormFor = p.Spread;
            _spreadNorm = Jonswap.SpreadingNormalisation(p.Spread);
        }

        var calm = p.WindSpeed < 0.1f;
        var fetch = Math.Max(p.Fetch, Jonswap.MinFetch);
        GpuSpectrum = new SpectrumState
        {
            Params = p,
            Alpha = calm ? 0f : (float)Jonswap.Alpha(p.WindSpeed, fetch),
            PeakOmega = calm ? 1f : (float)Jonswap.PeakOmega(p.WindSpeed, fetch),
            SpreadNorm = (float)_spreadNorm,
        };
    }

    private void Append(WaveSet set, double scale)
    {
        for (var j = 0; j < set.Count; j++)
        {
            var i = _count++;
            var a = (float)(set.Amplitude[j] * scale);
            var omega = set.Omega[j];
            var phase = set.Phase0[j] - omega * Time;
            phase -= Math.Floor(phase / Math.Tau) * Math.Tau;
            var aw = a * omega;

            _kx[i] = set.Kx[j];
            _kz[i] = set.Kz[j];
            _phase[i] = (float)phase;
            _a[i] = a;
            _akx[i] = a * set.Kx[j];
            _akz[i] = a * set.Kz[j];
            _cx[i] = a * set.DirX[j];
            _cz[i] = a * set.DirZ[j];
            _aw[i] = aw;
            _omega[i] = omega;
            _awx[i] = aw * set.DirX[j] * set.Coth[j];
            _awz[i] = aw * set.DirZ[j] * set.Coth[j];
        }
    }

    /// <summary>
    /// Writes the active components for the GPU, phase advanced from the last tick to <paramref name="renderTime"/>
    /// in double precision so the GPU low band matches CPU sampling at that time. Returns the count written.
    /// </summary>
    public int CopyGpuWaves(Span<GpuWave> destination, double renderTime)
    {
        var count = Math.Min(_count, destination.Length);
        var dt = renderTime - Time;

        for (var i = 0; i < count; i++)
        {
            var phase = _phase[i] - _omega[i] * dt;
            phase -= Math.Floor(phase / Math.Tau) * Math.Tau;
            destination[i] = new GpuWave
            {
                Kx = _kx[i],
                Kz = _kz[i],
                Phase = (float)phase,
                Amplitude = _a[i],
                Cx = _cx[i],
                Cz = _cz[i],
                Omega = _omega[i],
            };
        }

        return count;
    }

    /// <summary>Surface position of the water particle whose rest position is (x0, z0).</summary>
    public float3 SurfacePoint(float x0, float z0, IShoreDepth? shore = null)
    {
        var cap = Cap(shore, x0, z0);
        SumDisplacement(x0, z0, out var dx, out var dz);
        return new float3(x0 + cap * dx, cap * SumHeight(x0, z0), z0 + cap * dz);
    }

    /// <summary>Water surface height at world (x, z).</summary>
    public float SampleHeight(float x, float z, IShoreDepth? shore = null)
    {
        var cap = Invert(x, z, shore, out var x0, out var z0);
        return cap * SumHeight(x0, z0);
    }

    public float3 SampleNormal(float x, float z, IShoreDepth? shore = null) => Sample(x, z, shore).Normal;

    public float3 SampleVelocity(float x, float z, IShoreDepth? shore = null) => Sample(x, z, shore).Velocity;

    public void SampleHeights(ReadOnlySpan<float2> positions, Span<float> heights, IShoreDepth? shore = null)
    {
        for (var i = 0; i < positions.Length; i++)
            heights[i] = SampleHeight(positions[i].X, positions[i].Y, shore);
    }

    /// <summary>Height, normal, orbital velocity and Jacobian at world (x, z).</summary>
    // Fully optimised from its first call: few callers sample the whole wave, so tiered compilation left it in its slow
    // early tiers and promoted it on whichever tick crossed the threshold (10 samples took 13 ms on first use, against
    // 0.05 ms once promoted).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public WaveSample Sample(float x, float z, IShoreDepth? shore = null)
    {
        var cap = Invert(x, z, shore, out var x0, out var z0);

        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var height = Vector256<float>.Zero;
        var hx = Vector256<float>.Zero;
        var hz = Vector256<float>.Zero;
        var jxx = Vector256<float>.Zero;
        var jzz = Vector256<float>.Zero;
        var jxz = Vector256<float>.Zero;
        var ux = Vector256<float>.Zero;
        var uz = Vector256<float>.Zero;
        var uy = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var kx = Load(_kx, i);
            var kz = Load(_kz, i);
            var theta = Vector256.FusedMultiplyAdd(kx, vx, Vector256.FusedMultiplyAdd(kz, vz, Load(_phase, i)));
            var (sin, cos) = Vector256.SinCos(theta);
            var cx = Load(_cx, i);
            var cz = Load(_cz, i);

            height = Vector256.FusedMultiplyAdd(Load(_a, i), cos, height);
            hx = Vector256.FusedMultiplyAdd(Load(_akx, i), sin, hx);
            hz = Vector256.FusedMultiplyAdd(Load(_akz, i), sin, hz);
            jxx = Vector256.FusedMultiplyAdd(cx * kx, cos, jxx);
            jzz = Vector256.FusedMultiplyAdd(cz * kz, cos, jzz);
            jxz = Vector256.FusedMultiplyAdd(cx * kz, cos, jxz);
            ux = Vector256.FusedMultiplyAdd(Load(_awx, i), cos, ux);
            uz = Vector256.FusedMultiplyAdd(Load(_awz, i), cos, uz);
            uy = Vector256.FusedMultiplyAdd(Load(_aw, i), sin, uy);
        }

        var slopeX = -cap * Vector256.Sum(hx);
        var slopeZ = -cap * Vector256.Sum(hz);
        var dxx = 1f - cap * Vector256.Sum(jxx);
        var dzz = 1f - cap * Vector256.Sum(jzz);
        var dxz = -cap * Vector256.Sum(jxz);

        // Tangents Tx = (dxx, slopeX, dxz), Tz = (dxz, slopeZ, dzz); normal = Tz × Tx.
        var normal = new float3(
            slopeZ * dxz - dzz * slopeX,
            dzz * dxx - dxz * dxz,
            dxz * slopeX - slopeZ * dxx
        );

        return new WaveSample
        {
            Height = cap * Vector256.Sum(height),
            Normal = float3.Normalize(normal),
            Velocity = cap * new float3(Vector256.Sum(ux), Vector256.Sum(uy), Vector256.Sum(uz)),
            Jacobian = dxx * dzz - dxz * dxz,
        };
    }

    /// <summary>Depth cap at a rest position: 1 without a depth source.</summary>
    private float Cap(IShoreDepth? shore, float x0, float z0)
    {
        if (shore is null || SignificantWaveHeight <= 1e-4f)
            return 1f;

        return Math.Clamp(DepthCapGamma * shore.Depth(x0, z0) / SignificantWaveHeight, 0f, 1f);
    }

    /// <summary>The rest position under world (x, z), with the depth cap there (it scales the displacement too).</summary>
    private float Invert(float x, float z, IShoreDepth? shore, out float x0, out float z0)
    {
        x0 = x;
        z0 = z;
        var cap = Cap(shore, x0, z0);

        if (Choppiness <= 0f)
            return cap;

        for (var i = 0; i < InversionIterations; i++)
        {
            SumDisplacement(x0, z0, out var dx, out var dz);
            x0 = x - cap * dx;
            z0 = z - cap * dz;
            cap = Cap(shore, x0, z0);
        }

        return cap;
    }

    private float SumHeight(float x0, float z0)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var height = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var theta = Vector256.FusedMultiplyAdd(Load(_kx, i), vx, Vector256.FusedMultiplyAdd(Load(_kz, i), vz, Load(_phase, i)));
            height = Vector256.FusedMultiplyAdd(Load(_a, i), Vector256.Cos(theta), height);
        }

        return Vector256.Sum(height);
    }

    private void SumDisplacement(float x0, float z0, out float dx, out float dz)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var sx = Vector256<float>.Zero;
        var sz = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var theta = Vector256.FusedMultiplyAdd(Load(_kx, i), vx, Vector256.FusedMultiplyAdd(Load(_kz, i), vz, Load(_phase, i)));
            var sin = Vector256.Sin(theta);
            sx = Vector256.FusedMultiplyAdd(Load(_cx, i), sin, sx);
            sz = Vector256.FusedMultiplyAdd(Load(_cz, i), sin, sz);
        }

        dx = -Vector256.Sum(sx);
        dz = -Vector256.Sum(sz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Load(float[] array, int index) =>
        Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(array), (nuint)index);
}

public struct WaveSample
{
    public float Height;
    public float3 Normal;
    public float3 Velocity;

    /// <summary>Surface area ratio; below ~0.3 the surface is folding (feeds foam).</summary>
    public float Jacobian;
}

/// <summary>
/// std140-compatible wave component: (kx, kz, phase, amplitude), (cx, cz, omega, unused).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 32)]
public struct GpuWave
{
    public float Kx;
    public float Kz;
    public float Phase;
    public float Amplitude;
    public float Cx;
    public float Cz;
    public float Omega;
}

public struct SpectrumState
{
    public SwellParams Params;
    public float Alpha;
    public float PeakOmega;
    public float SpreadNorm;
}
