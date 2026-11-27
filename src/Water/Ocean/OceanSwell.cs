using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// CPU swell: physics-relevant low band of the ocean, deterministic from seed, wind history and time.
/// Call <see cref="SetWind"/> when wind changes and <see cref="SetTime"/> once per tick on the main thread.
/// Sample methods are read-only between SetTime calls and safe to call from jobs. The components live in an abstract
/// wave plane (x, z): directions are radians from +x toward +z. <see cref="OceanSurface"/> lays that plane on the sea
/// sphere, one copy per <see cref="SeaTiles"/> tile (x east, z south), and applies the depth cap
/// (<see cref="DepthCapGamma"/>).
/// </summary>
public sealed class OceanSwell
{
    public const int MaxComponents = WaveSet.Capacity * 2;
    public const int InversionIterations = 4;

    /// <summary>Depth-limited swell: every component scaled by min(1, this × depth / Hs) (ocean_common.glsl oceanAt).</summary>
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
    private ulong _pendingSeed;
    private ulong _spareSeed;
    private bool _spareReady;
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
    private float _carrierDirection;
    private double _carrierGap;
    private double _groupGap;
    private float _breakerHeight;
    private double _spreadNormFor = double.NaN;
    private double _spreadNorm;

    public OceanSwell(ulong seed)
    {
        _seed = seed;
    }

    /// <summary>Direction the dominant band travels toward (radians): the surf carrier's.</summary>
    public float DominantDirection => _carrierDirection;

    /// <summary>Sets: one every 1 / this waves, from the dominant band's bandwidth.</summary>
    public float GroupRatio { get; private set; } = 2f * (float)WaveSet.WindSeaBandwidth;

    public double Time { get; private set; }

    public int ComponentCount => _count;

    /// <summary>Effective horizontal displacement scale after the steepness clamp.</summary>
    public float Choppiness { get; private set; }

    public float SignificantWaveHeight { get; private set; }

    public float PeakPeriod => _carrierOmega > 0f ? MathF.Tau / _carrierOmega : 0f;

    /// <summary>0..1 progress of the active wind blend, 1 when settled.</summary>
    public float BlendProgress { get; private set; } = 1f;

    public SwellParams Wind => _to.Params;

    /// <summary>The surf carrier of the swell being blended to: angular frequency (0 when calm), direction of travel, deep-water height crest to trough, and that swell's Hs.</summary>
    public float TargetCarrierOmega { get; private set; }

    public float TargetCarrierDirection { get; private set; }

    public float TargetCarrierHeight { get; private set; }

    public float TargetSignificantHeight { get; private set; }

    /// <summary>Effective spectrum for GPU detail, blended with the same weight as the CPU components.</summary>
    public SpectrumState GpuSpectrum { get; private set; }

    /// <summary>
    /// Queues a wind change. The spectrum crossfades over <see cref="BlendSeconds"/>; never snaps.
    /// </summary>
    public void SetWind(in SwellParams wind) => SetWind(wind, SplitMix64.Mix(_seed, _generation++), Time);

    /// <summary>
    /// A wind change whose components are drawn from <paramref name="setSeed"/> and whose blend began at
    /// <paramref name="blendStart"/> (s; at most <see cref="Time"/>), so a swell made late agrees with one that saw it.
    /// </summary>
    public void SetWind(in SwellParams wind, ulong setSeed, double blendStart)
    {
        if (!_hasWind)
        {
            _to.Build(wind, setSeed);
            _hasWind = true;
            PrepareTarget();
            Prepare();
            return;
        }

        if (_blending)
        {
            _pending = wind;
            _pendingSeed = setSeed;
            _hasPending = true;
            return;
        }

        StartBlend(wind, setSeed, blendStart);
        Prepare();
    }

    /// <summary>Forgets every wind: the next <see cref="SetWind(in SwellParams)"/> is the first.</summary>
    public void Reset()
    {
        _hasWind = false;
        _blending = false;
        _hasPending = false;
        _spareReady = false;
        _generation = 0;
        _to.Build(new SwellParams { WindSpeed = 0f }, 0UL);
        PrepareTarget();
        Prepare();
    }

    /// <summary>
    /// Builds the set a coming <see cref="SetWind(in SwellParams, ulong, double)"/> with this seed and wind will blend to,
    /// ahead of it (off the tick that takes it up). The seed must stand for the wind.
    /// </summary>
    public void Prebuild(in SwellParams wind, ulong setSeed)
    {
        _spare.Build(wind, setSeed);
        _spareSeed = setSeed;
        _spareReady = true;
    }

    private void StartBlend(in SwellParams wind, ulong setSeed, double blendStart)
    {
        (_from, _to, _spare) = (_to, _spare, _from);

        if (!_spareReady || _spareSeed != setSeed)
            _to.Build(wind, setSeed);

        _spareReady = false;
        _blendStart = blendStart;
        _blending = true;
        PrepareTarget();
        _carrierGap = Gap(_from.CarrierOmega, _to.CarrierOmega);
        _groupGap = Gap(GroupOmega(_from), GroupOmega(_to));
    }

    /// <summary>
    /// What the phase integrated over the blend misses the new set's −ω t by at the blend's end, wrapped to ±π: the
    /// integral runs −ω_from τ − (ω_to − ω_from) B / 2 over the blend's B seconds.
    /// </summary>
    private double Gap(double from, double to)
    {
        var gap = -(to - from) * (_blendStart + 0.5 * BlendSeconds);
        return gap - Math.Round(gap / Math.Tau) * Math.Tau;
    }

    /// <summary>Advances to simulation time and caches per-component phase. O(components), no allocation.</summary>
    public void SetTime(double time)
    {
        Time = time;
        Prepare();
    }

    /// <summary>
    /// The surf carrier's phase (or its set group's) at <paramref name="time"/>, in closed form so it is a function of
    /// the time and the two sets alone: −ω t of the set in force. Across a blend it integrates ω lerped on the components'
    /// smoothstep s (as the integrated phase did), plus s × the gap left at the end (<see cref="Gap"/>), so the phase never
    /// jumps and the crests run at most 1.5 π / <see cref="BlendSeconds"/> rad/s off the lerped ω meanwhile.
    /// </summary>
    private double CarrierPhase(double time, bool group)
    {
        var to = group ? GroupOmega(_to) : _to.CarrierOmega;

        if (!_blending || BlendSeconds <= 0.0 || time >= _blendStart + BlendSeconds)
            return Wrap(-to * time);

        var from = group ? GroupOmega(_from) : _from.CarrierOmega;

        if (time <= _blendStart)
            return Wrap(-from * time);

        var x = (time - _blendStart) / BlendSeconds;
        var s = x * x * (3.0 - 2.0 * x);
        var integral = BlendSeconds * x * x * x * (1.0 - 0.5 * x);
        return Wrap(-from * time - (to - from) * integral + (group ? _groupGap : _carrierGap) * s);
    }

    private static double GroupOmega(WaveSet set) => set.CarrierOmega * SetRatio(set);

    private static double Wrap(double phase) => phase - Math.Floor(phase / Math.Tau) * Math.Tau;

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
                    StartBlend(_pending, _pendingSeed, Time);
                    weight = 0.0;
                }
            }
        }

        weight = Math.Clamp(weight, 0.0, 1.0);
        BlendProgress = (float)weight;

        var smooth = weight * weight * (3.0 - 2.0 * weight);
        var t = _blending ? (float)smooth : 1f;
        UpdateGpuSpectrum(t);
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

        PrepareCarrier(t);
    }

    /// <summary>
    /// The surf carrier this tick: ω, set ratio and direction blended with the components' weight
    /// <paramref name="t"/>, and its breaker height.
    /// </summary>
    private void PrepareCarrier(float t)
    {
        var from = _blending ? _from : _to;
        var omega = float.Lerp((float)from.CarrierOmega, (float)_to.CarrierOmega, t);
        _carrierOmega = omega;
        GroupRatio = float.Lerp(SetRatio(from), SetRatio(_to), t);
        _carrierDirection = from.CarrierDirection + MathF.IEEERemainder(_to.CarrierDirection - from.CarrierDirection, MathF.Tau) * t;

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

    /// <summary>
    /// The surf carrier of the swell being blended to: it moves in steps, when a new wind is taken up, so the coast maps
    /// (<see cref="Coast.CoastRegionSet"/>) are baked for it and cross-fade as the swell does.
    /// </summary>
    private void PrepareTarget()
    {
        var omega = (float)_to.CarrierOmega;
        var energy = 0f;
        var total = 0.0;

        for (var i = 0; i < _to.Count; i++)
        {
            var a = _to.Amplitude[i];
            total += 0.5 * a * a;

            if (omega > 0f)
                energy += a * a * (1f - Smooth((MathF.Abs(MathF.Log(_to.Omega[i] / omega)) - BandInner) / (BandOuter - BandInner)));
        }

        TargetCarrierOmega = omega;
        TargetCarrierDirection = _to.CarrierDirection;
        TargetCarrierHeight = 2f * MathF.Sqrt(CarrierShare * energy);
        TargetSignificantHeight = (float)(4.0 * Math.Sqrt(total));
    }

    private static float SetRatio(WaveSet set) => Math.Clamp(2f * (float)set.CarrierBandwidth, MinGroupRatio, MaxGroupRatio);

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The surf carrier for the GPU at <paramref name="renderTime"/>: (ω, phase, set group ratio, set group phase), its
    /// phases rebased in double so the GPU's rest positions are relative to (<paramref name="originX"/>, <paramref name="originZ"/>).
    /// </summary>
    public System.Numerics.Vector4 GpuCarrier(double renderTime, double originX = 0.0, double originZ = 0.0)
    {
        var (dirZ, dirX) = MathF.SinCos(DominantDirection);
        var spatial = _carrierOmega * _carrierOmega / 9.81f * (dirX * originX + dirZ * originZ);
        var phase = Wrap(CarrierPhase(renderTime, group: false) + spatial);
        var group = Wrap(CarrierPhase(renderTime, group: true) + GroupRatio * spatial);
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
    /// Writes the active components for the GPU (wavevector, amplitude, chop, ω; in the wave plane's axes), and each one's
    /// phase at <paramref name="renderTime"/> rebased by k·origin in double, so the GPU's rest positions are relative to
    /// (<paramref name="originX"/>, <paramref name="originZ"/>) and its low band matches CPU sampling at that time.
    /// Returns the count written.
    /// </summary>
    public int CopyGpuWaves(Span<GpuWave> destination, double renderTime, double originX = 0.0, double originZ = 0.0)
    {
        var count = Math.Min(_count, destination.Length);
        var dt = renderTime - Time;

        for (var i = 0; i < count; i++)
        {
            destination[i] = new GpuWave
            {
                Kx = _kx[i],
                Kz = _kz[i],
                Phase = GpuPhase(i, dt, originX, originZ),
                Amplitude = _a[i],
                Cx = _cx[i],
                Cz = _cz[i],
                Omega = _omega[i],
            };
        }

        return count;
    }

    /// <summary>
    /// The phases alone, for another origin of the same components (a neighbouring sea tile's plane): every
    /// <paramref name="stride"/>th float from <paramref name="offset"/>. Returns the count written.
    /// </summary>
    public int CopyGpuPhases(Span<float> destination, int offset, int stride, double renderTime, double originX, double originZ)
    {
        var count = Math.Min(_count, (destination.Length - offset + stride - 1) / stride);
        var dt = renderTime - Time;

        for (var i = 0; i < count; i++)
            destination[offset + i * stride] = GpuPhase(i, dt, originX, originZ);

        return count;
    }

    private float GpuPhase(int i, double dt, double originX, double originZ)
    {
        var phase = _phase[i] - _omega[i] * dt + _kx[i] * originX + _kz[i] * originZ;
        return (float)(phase - Math.Floor(phase / Math.Tau) * Math.Tau);
    }

    /// <summary>Surface position of the open swell's water particle whose rest position is (x0, z0) in the wave plane.</summary>
    public float3 SurfacePoint(float x0, float z0)
    {
        SumDisplacement(x0, z0, 0f, out var dx, out var dz);
        return new float3(x0 + dx, SumHeight(x0, z0, 0f), z0 + dz);
    }

    /// <summary>The open swell's surface height at (x, z) of the wave plane.</summary>
    public float SampleHeight(float x, float z)
    {
        Invert(x, z, out var x0, out var z0);
        return SumHeight(x0, z0, 0f);
    }

    /// <summary>The open swell's height, normal, orbital velocity and Jacobian at (x, z) of the wave plane (y up).</summary>
    public WaveSample Sample(float x, float z)
    {
        Invert(x, z, out var x0, out var z0);
        Sum(x0, z0, 0f, out var s);
        return s.ToSample(1f);
    }

    /// <summary>The rest position under (x, z).</summary>
    private void Invert(float x, float z, out float x0, out float z0)
    {
        x0 = x;
        z0 = z;

        if (Choppiness <= 0f)
            return;

        for (var i = 0; i < InversionIterations; i++)
        {
            SumDisplacement(x0, z0, 0f, out var dx, out var dz);
            x0 = x - dx;
            z0 = z - dz;
        }
    }

    /// <summary>
    /// Every component's sums at a rest position, <paramref name="lead"/> seconds past <see cref="Time"/>.
    /// </summary>
    // Fully optimised from its first call: few callers sample the whole wave, so tiered compilation left it in its slow
    // early tiers and promoted it on whichever tick crossed the threshold (10 samples took 13 ms on first use, against
    // 0.05 ms once promoted).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Sum(float x0, float z0, float lead, out SwellSums sums)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var back = Vector256.Create(-lead);
        var height = Vector256<float>.Zero;
        var hx = Vector256<float>.Zero;
        var hz = Vector256<float>.Zero;
        var jxx = Vector256<float>.Zero;
        var jzz = Vector256<float>.Zero;
        var jxz = Vector256<float>.Zero;
        var dx = Vector256<float>.Zero;
        var dz = Vector256<float>.Zero;
        var ux = Vector256<float>.Zero;
        var uz = Vector256<float>.Zero;
        var uy = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var kx = Load(_kx, i);
            var kz = Load(_kz, i);
            var phase = Vector256.FusedMultiplyAdd(Load(_omega, i), back, Load(_phase, i));
            var theta = Vector256.FusedMultiplyAdd(kx, vx, Vector256.FusedMultiplyAdd(kz, vz, phase));
            var (sin, cos) = Vector256.SinCos(theta);
            var cx = Load(_cx, i);
            var cz = Load(_cz, i);

            height = Vector256.FusedMultiplyAdd(Load(_a, i), cos, height);
            hx = Vector256.FusedMultiplyAdd(Load(_akx, i), sin, hx);
            hz = Vector256.FusedMultiplyAdd(Load(_akz, i), sin, hz);
            jxx = Vector256.FusedMultiplyAdd(cx * kx, cos, jxx);
            jzz = Vector256.FusedMultiplyAdd(cz * kz, cos, jzz);
            jxz = Vector256.FusedMultiplyAdd(cx * kz, cos, jxz);
            dx = Vector256.FusedMultiplyAdd(cx, sin, dx);
            dz = Vector256.FusedMultiplyAdd(cz, sin, dz);
            ux = Vector256.FusedMultiplyAdd(Load(_awx, i), cos, ux);
            uz = Vector256.FusedMultiplyAdd(Load(_awz, i), cos, uz);
            uy = Vector256.FusedMultiplyAdd(Load(_aw, i), sin, uy);
        }

        sums = new SwellSums
        {
            Height = Vector256.Sum(height),
            Hx = Vector256.Sum(hx),
            Hz = Vector256.Sum(hz),
            Jxx = Vector256.Sum(jxx),
            Jzz = Vector256.Sum(jzz),
            Jxz = Vector256.Sum(jxz),
            Dx = -Vector256.Sum(dx),
            Dz = -Vector256.Sum(dz),
            Ux = Vector256.Sum(ux),
            Uy = Vector256.Sum(uy),
            Uz = Vector256.Sum(uz),
        };
    }

    internal float SumHeight(float x0, float z0, float lead)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var back = Vector256.Create(-lead);
        var height = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var phase = Vector256.FusedMultiplyAdd(Load(_omega, i), back, Load(_phase, i));
            var theta = Vector256.FusedMultiplyAdd(Load(_kx, i), vx, Vector256.FusedMultiplyAdd(Load(_kz, i), vz, phase));
            height = Vector256.FusedMultiplyAdd(Load(_a, i), Vector256.Cos(theta), height);
        }

        return Vector256.Sum(height);
    }

    internal void SumDisplacement(float x0, float z0, float lead, out float dx, out float dz)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var back = Vector256.Create(-lead);
        var sx = Vector256<float>.Zero;
        var sz = Vector256<float>.Zero;

        for (var i = 0; i < _paddedCount; i += Lanes)
        {
            var phase = Vector256.FusedMultiplyAdd(Load(_omega, i), back, Load(_phase, i));
            var theta = Vector256.FusedMultiplyAdd(Load(_kx, i), vx, Vector256.FusedMultiplyAdd(Load(_kz, i), vz, phase));
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

/// <summary>
/// The swell's raw sums at a rest position, in the wave plane's axes (x, z; y up): height, Σ a k sin (the slope is its
/// negative), the chop's Jacobian sums, the chop displacement and the orbital velocity.
/// </summary>
internal struct SwellSums
{
    public float Height;
    public float Hx, Hz;
    public float Jxx, Jzz, Jxz;
    public float Dx, Dz;
    public float Ux, Uy, Uz;

    /// <summary>Adds <paramref name="s"/> weighted by <paramref name="w"/>, its plane vectors turned by the 2 × 2 matrix a (taken as a rotation).</summary>
    public void Add(in SwellSums s, float w, float a11, float a12, float a21, float a22)
    {
        Height += w * s.Height;
        Uy += w * s.Uy;
        Hx += w * (a11 * s.Hx + a12 * s.Hz);
        Hz += w * (a21 * s.Hx + a22 * s.Hz);
        Dx += w * (a11 * s.Dx + a12 * s.Dz);
        Dz += w * (a21 * s.Dx + a22 * s.Dz);
        Ux += w * (a11 * s.Ux + a12 * s.Uz);
        Uz += w * (a21 * s.Ux + a22 * s.Uz);

        // A j Aᵀ for the symmetric j.
        var m11 = a11 * s.Jxx + a12 * s.Jxz;
        var m12 = a11 * s.Jxz + a12 * s.Jzz;
        var m21 = a21 * s.Jxx + a22 * s.Jxz;
        var m22 = a21 * s.Jxz + a22 * s.Jzz;
        Jxx += w * (m11 * a11 + m12 * a12);
        Jxz += w * (m11 * a21 + m12 * a22);
        Jzz += w * (m21 * a21 + m22 * a22);
    }

    /// <summary>The sample in the plane's axes with every component scaled by <paramref name="cap"/>.</summary>
    public readonly WaveSample ToSample(float cap)
    {
        var slopeX = -cap * Hx;
        var slopeZ = -cap * Hz;
        var dxx = 1f - cap * Jxx;
        var dzz = 1f - cap * Jzz;
        var dxz = -cap * Jxz;

        // Tangents Tx = (dxx, slopeX, dxz), Tz = (dxz, slopeZ, dzz); normal = Tz × Tx.
        var normal = new float3(
            slopeZ * dxz - dzz * slopeX,
            dzz * dxx - dxz * dxz,
            dxz * slopeX - slopeZ * dxx
        );

        return new WaveSample
        {
            Height = cap * Height,
            Normal = float3.Normalize(normal),
            Velocity = cap * new float3(Ux, Uy, Uz),
            Jacobian = dxx * dzz - dxz * dxz,
        };
    }
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
