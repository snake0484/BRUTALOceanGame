using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// CPU swell: physics-relevant low band of the ocean, deterministic from seed, wind history and time.
/// Call <see cref="SetWind"/> when wind changes and <see cref="SetTime"/> once per tick on the main thread.
/// Sample methods are read-only between SetTime calls and safe to call from jobs.
/// </summary>
public sealed class OceanSwell
{
    public const int MaxComponents = WaveSet.Capacity * 2;
    public const int InversionIterations = 4;

    /// <summary>
    /// Shaped samples (<see cref="Sample{TShape}"/>): Newton steps at most, the longest one (m), halvings of a step that
    /// overshoots, the miss (m) that ends them early, and the Jacobian below which a step is a plain fixed-point one.
    /// </summary>
    private const int ShapedIterations = 16;
    private const float MaxShapedStep = 4f;
    private const int ShapedHalvings = 4;
    private const float ShapedTolerance = 1e-4f;
    private const float MinNewtonJacobian = 0.02f;

    /// <summary>
    /// Metres the rest position may lie from where a shaped sample took its coastal terms before they're retaken, and how
    /// many times at most: near breaking the depth-limited surf wave changes height by centimetres per metre.
    /// </summary>
    private const float RetakeDistance = 0.05f;
    private const int MaxRetakes = 3;

    /// <summary>
    /// Choppiness × rms of the Gerstner trace Σ a·k·cos θ at most: 1 / this standard deviations to a fold (Jacobian 0),
    /// 4 here, 3e-5 of the surface, on the crests that whitecap. The worst case Σ|a·k| is 14× the rms over 128 random
    /// phases: bounding it pinned the chop at a fixed share of the slope, so a high sea peaked no harder than a low one.
    /// </summary>
    private const float MaxRmsSteepness = 0.25f;
    private static readonly int Lanes = Vector256<float>.Count;

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
    private readonly float[] _coth = new float[MaxComponents];
    private readonly float[] _ref = new float[MaxComponents];
    private readonly float[] _refEnergy = new float[CoastalField.RefCount];
    private readonly float[] _refChop = new float[CoastalField.RefCount];
    private int _count;
    private int _paddedCount;

    private const int ScratchArrays = 11;

    // Surf-zone coherence (docs/breaking-waves.md, lip plan B): near shore, components in the peak band hand their energy
    // to one coherent carrier wave, the same wave BreakerSet contours, so the drawn crests are the breaker crests.
    private const float CoherentKh = 0.4f;
    private const float IncoherentKh = 1.2f;
    private const float BandInner = 0.5f;
    private const float BandOuter = 1.0f;

    /// <summary>Share of the peak band the carrier takes at most: the rest keeps its random phases, so surf stays irregular.</summary>
    private const float MaxCoherence = 0.75f;

    /// <summary>
    /// Individual-wave breaking: the coherent surf wave grows as it shoals until its height reaches this × depth, then
    /// breaks and is held there. (The rest of the spectrum keeps the significant-height cap, γ = 0.55.)
    /// </summary>
    public const float WaveBreakingGamma = 0.78f;

    /// <summary>The crests' break clocks (BreakerSet.BreakDistance), set by <see cref="OceanSurface"/>: the drawn carrier holds its breaking height while a lip is in the air (BreakerFace.HeldCap).</summary>
    public BreakerSet? Breaking;

    /// <summary>The coherent surf carrier's deep-water height crest to trough (m), before shoaling and sets.</summary>
    public float CarrierHeight => 2f * MathF.Sqrt(MaxCoherence * _carrierEnergy);

    /// <summary>
    /// Sets: the surf wave's height is modulated by a group envelope that travels with its crests (phase GroupRatio × the
    /// carrier's spatial phase − GroupRatio ω t, so a set every 1 / GroupRatio waves), with mean square 1 so the energy is
    /// unchanged. Set waves stand about 1.5× the mean and break further out; the waves between them are small. A narrow
    /// band beats into long groups, so GroupRatio is twice the dominant band's relative bandwidth (a set every 8 waves for
    /// a wind sea, every 10 for a 5%-wide groundswell), within these bounds.
    /// </summary>
    private const float MinGroupRatio = 1f / 20f;
    private const float MaxGroupRatio = 1f / 4f;
    private const float GroupDepth = 0.7f;

    /// <summary>Carrier plus its bound harmonics (2nd, 3rd), and the crest peaking they give: s from H/h over [from, full].</summary>
    private const int CarrierSlots = 3;
    private const float MaxSharpness = 0.55f;
    private const float SharpFrom = 0.1f;
    private const float SharpFull = 0.45f;
    private readonly float[] _band = new float[MaxComponents];
    private float _carrierRef;
    private float _carrierK0;
    private float _carrierOmega;
    private float _carrierPhase;
    private float _groupPhase;
    private float _carrierEnergy;
    private const float MinCothKh = 0.1f;

    /// <summary>The surf carrier's wavenumber (<see cref="SurfCarrier"/>) is taken at no shallower than this.</summary>
    private const float SpentDepthK = 0.3f;

    /// <summary>Layer B coastal maps; null = open ocean everywhere. Set on the main thread between ticks.</summary>
    public CoastalField? Coastal { get; set; }

    /// <summary>The field <see cref="Coastal"/> replaced, while it fades out; null = fade from the open ocean.</summary>
    public CoastalField? CoastalPrevious { get; set; }

    /// <summary>0..1 weight of <see cref="Coastal"/> against <see cref="CoastalPrevious"/>.</summary>
    public float CoastalFade { get; set; } = 1f;

    /// <summary>
    /// Surf state at (x, z) for breakers: water depth, the depth-limited breaking cap (1 = not breaking) and the local
    /// significant wave height before the cap. False = open ocean.
    /// </summary>
    public bool SampleSurf(float x, float z, out float depth, out float cap, out float hs)
    {
        var s = new CoastalSample();
        if (!SampleCoastal(x, z, ref s))
        {
            (depth, cap, hs) = (CoastalField.DeepDepth, 1f, SignificantWaveHeight);
            return false;
        }

        var variance = 0f;
        for (var j = RefMin; j <= RefMax; j++)
            variance += _refEnergy[j] * s.Amp[j] * s.Amp[j];

        depth = s.Depth;
        hs = 4f * MathF.Sqrt(variance);
        cap = hs > 1e-4f ? Math.Clamp(CoastalField.BreakingGamma * depth / hs, 0f, 1f) : 1f;
        return true;
    }

    /// <summary>
    /// The surf carrier at (x, z) in the current coastal field (ocean_common.glsl surfCarrierAt): phase (0 on a crest),
    /// local wavevector (direction of k₀ d̂ + ∇lag, magnitude k(h)), unbroken height crest to trough (sets included, as the
    /// surface draws it) and the water depth; in the field fading out instead when <paramref name="previous"/>. False where
    /// the field has nothing or there is no carrier.
    /// </summary>
    public bool SurfCarrier(float x, float z, out SurfCarrierPoint carrier, bool previous = false)
    {
        carrier = default;
        var field = previous ? CoastalPrevious : Coastal;
        var s = new CoastalSample();
        if (field is null || !CoherenceEnabled || _carrierEnergy <= 0f || !field.Sample(x, z, ref s))
            return false;

        var (dirZ, dirX) = MathF.SinCos(field.Direction);
        var jc = (int)_carrierRef;
        var tc = _carrierRef - jc;
        var lag = float.Lerp(s.Lag[jc], s.Lag[jc + 1], tc);
        var kx = _carrierK0 * dirX + float.Lerp(s.LagX[jc], s.LagX[jc + 1], tc);
        var kz = _carrierK0 * dirZ + float.Lerp(s.LagZ[jc], s.LagZ[jc + 1], tc);
        var shoal = float.Lerp(s.Amp[jc], s.Amp[jc + 1], tc);
        var depth = MathF.Max(s.Depth, 0f);
        var spatial = _carrierK0 * (dirX * x + dirZ * z) + lag;
        var (unbroken, _) = CarrierBreaking(shoal * GroupEnvelope(spatial, _groupPhase), Coherence(Jonswap.WaveNumberApprox(_carrierOmega, depth) * depth), depth);

        // Direction from the lag map; magnitude k(h), continuous in depth where the map's gradient steps at texel lines
        // (the eikonal makes |∇phase| = k(h) anyway).
        var k = Jonswap.WaveNumberApprox(_carrierOmega, MathF.Max(depth, SpentDepthK)) / MathF.Max(MathF.Sqrt(kx * kx + kz * kz), 1e-6f);
        kx *= k;
        kz *= k;

        carrier = new SurfCarrierPoint { Phase = spatial + _carrierPhase, Kx = kx, Kz = kz, Unbroken = unbroken, Depth = s.Depth };
        return true;
    }

    /// <summary>
    /// The surf carrier's direction of travel at (x, z), from central differences of its lag a cell apart
    /// (ocean_common.glsl carrierDirection): continuous, where the bilinear map's own gradient steps at every texel line
    /// and, where refraction is strong, swings by tens of degrees between cells. In the field fading out when
    /// <paramref name="previous"/>. False where the field runs out.
    /// </summary>
    public bool CarrierDirection(float x, float z, out float dirX, out float dirZ, bool previous = false)
    {
        (dirX, dirZ) = (0f, 0f);
        var field = previous ? CoastalPrevious : Coastal;
        if (field is null)
            return false;

        const float h = CoastalField.CellSize;
        if (!CarrierLag(field, x + h, z, out var east) || !CarrierLag(field, x - h, z, out var west)
            || !CarrierLag(field, x, z + h, out var north) || !CarrierLag(field, x, z - h, out var south))
            return false;

        var (sin, cos) = MathF.SinCos(field.Direction);
        var gx = _carrierK0 * cos + (east - west) / (2f * h);
        var gz = _carrierK0 * sin + (north - south) / (2f * h);
        var length = MathF.Max(MathF.Sqrt(gx * gx + gz * gz), 1e-6f);
        (dirX, dirZ) = (gx / length, gz / length);
        return true;
    }

    /// <summary>
    /// Water depth at (x, z) as the carrier reads it (ocean_common.glsl carrierDepthAt): from the current field, or the
    /// one fading out when <paramref name="previous"/>; <see cref="CoastalField.DeepDepth"/> where a field has nothing.
    /// </summary>
    public float CarrierDepth(float x, float z, bool previous = false)
    {
        var field = previous ? CoastalPrevious : Coastal;
        return field is not null && field.Depth(x, z, out var depth) ? depth : CoastalField.DeepDepth;
    }

    private bool CarrierLag(CoastalField field, float x, float z, out float lag)
    {
        var s = new CoastalSample();
        lag = 0f;
        if (!field.Sample(x, z, ref s))
            return false;

        var jc = (int)_carrierRef;
        lag = float.Lerp(s.Lag[jc], s.Lag[jc + 1], _carrierRef - jc);
        return true;
    }

    /// <summary>
    /// Peak angular frequency of the target spectrum's dominant band (rad/s): the groundswell's when it dominates, else the
    /// wind sea's; 0 when calm. The surf carrier and the breakers follow it.
    /// </summary>
    public float PeakOmega => (float)_to.CarrierOmega;

    /// <summary>Direction the dominant band travels toward (radians): the one the coastal maps are solved for.</summary>
    public float DominantDirection => _to.CarrierDirection;

    /// <summary>Sets: one every 1 / this waves (see <see cref="GroupEnvelope"/>), from the dominant band's bandwidth.</summary>
    public float GroupRatio { get; private set; } = 2f * (float)WaveSet.WindSeaBandwidth;

    /// <summary>
    /// Coastal terms of each field at (x, z): the current one and, while a rebuild fades in, the one it replaces (open
    /// ocean where a field has nothing). Returns 0 for open ocean, 1 for the current field only, 2 when the surface must
    /// be evaluated with both and blended by <see cref="CoastalFade"/>. Blending the fields' phase lags instead sweeps the
    /// phase by their difference (tens of radians after a big seabed change) over the fade, and the waves run backwards.
    /// </summary>
    private int SampleFields(float x, float z, ref CoastalSample current, ref CoastalSample previous)
    {
        if (Coastal is null)
            return 0;

        var hasCurrent = Coastal.Sample(x, z, ref current);
        if (CoastalFade >= 1f)
            return hasCurrent ? 1 : 0;

        var hasPrevious = CoastalPrevious?.Sample(x, z, ref previous) ?? false;
        if (!hasCurrent && !hasPrevious)
            return 0;

        if (!hasCurrent)
            current.SetDeep();

        if (!hasPrevious)
            previous.SetDeep();

        return 2;
    }

    /// <summary>Direction the fading-out field was solved for (the current one's when fading in from the open ocean).</summary>
    private float PreviousDirection => CoastalPrevious?.Direction ?? Coastal!.Direction;

    /// <summary>Depth and amplitudes crossfaded between the fields (no phase): enough for the surf state.</summary>
    private bool SampleCoastal(float x, float z, ref CoastalSample s)
    {
        var current = Coastal;
        if (current is null)
            return false;

        var hasCurrent = current.Sample(x, z, ref s);

        if (CoastalFade >= 1f)
            return hasCurrent;

        // No previous field (the first build) fades in from the open ocean.
        var old = new CoastalSample();
        var hasOld = CoastalPrevious?.Sample(x, z, ref old) ?? false;

        if (!hasCurrent && !hasOld)
            return false;

        if (!hasCurrent)
            s.SetDeep();

        if (!hasOld)
            old.SetDeep();

        s.LerpFrom(old, CoastalFade);
        return true;
    }

    /// <summary>Swell variance (m²) per coastal reference frequency, for the per-sample breaking cap.</summary>
    public ReadOnlySpan<float> RefEnergy => _refEnergy;

    /// <summary>Variance of the Gerstner trace, Σ ½(|c|·k)², per coastal reference frequency (deep water, after the choppiness clamp).</summary>
    public ReadOnlySpan<float> RefChop => _refChop;

    /// <summary>Lowest and highest coastal reference any component interpolates between this tick.</summary>
    public int RefMin { get; private set; }

    public int RefMax { get; private set; } = 1;

    public OceanSwell(ulong seed)
    {
        _seed = seed;
    }

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

    private double _spreadNormFor = double.NaN;
    private double _spreadNorm;

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
        {
            _kx[i] = _kz[i] = _phase[i] = 0f;
            _a[i] = _akx[i] = _akz[i] = _cx[i] = _cz[i] = _awx[i] = _awz[i] = _aw[i] = _omega[i] = _ref[i] = 0f;
            _coth[i] = 1f;
        }

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

        PrepareCoastalRefs();
    }

    /// <summary>Per-reference swell variance and Gerstner trace variance, and the reference range the components use.</summary>
    private void PrepareCoastalRefs()
    {
        Array.Clear(_refEnergy);
        Array.Clear(_refChop);
        RefMin = CoastalField.RefCount - 2;
        RefMax = 0;

        for (var i = 0; i < _count; i++)
        {
            var j = (int)_ref[i];
            var t = _ref[i] - j;
            var variance = 0.5f * _a[i] * _a[i];
            var steepness = 0.5f * (_cx[i] * _cx[i] + _cz[i] * _cz[i]) * (_kx[i] * _kx[i] + _kz[i] * _kz[i]);

            _refEnergy[j] += (1f - t) * variance;
            _refEnergy[j + 1] += t * variance;
            _refChop[j] += (1f - t) * steepness;
            _refChop[j + 1] += t * steepness;
            RefMin = Math.Min(RefMin, j);
            RefMax = Math.Max(RefMax, j + 1);
        }

        if (RefMax < RefMin)
            (RefMin, RefMax) = (0, 1);

        // Carrier: the peak frequency, phase −ω t (crest where k₀ d̂·x + δψ − ω t ≡ 0, as BreakerSet extracts it).
        var omega = PeakOmega;
        _carrierOmega = omega;
        _carrierRef = CoastalField.RefCoord(Math.Max(omega, 1e-3f));
        _carrierK0 = omega * omega / 9.81f;
        var phase = -(double)omega * Time;
        _carrierPhase = (float)(phase - Math.Floor(phase / Math.Tau) * Math.Tau);
        GroupRatio = Math.Clamp(2f * (float)_to.CarrierBandwidth, MinGroupRatio, MaxGroupRatio);
        var group = phase * GroupRatio;
        _groupPhase = (float)(group - Math.Floor(group / Math.Tau) * Math.Tau);
        _carrierEnergy = 0f;

        for (var i = 0; i < _paddedCount; i++)
        {
            _band[i] = i < _count && omega > 0f ? 1f - Smooth((MathF.Abs(_ref[i] - _carrierRef) - BandInner) / (BandOuter - BandInner)) : 0f;
            _carrierEnergy += i < _count ? _a[i] * _a[i] * _band[i] : 0f;
        }
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Share of the peak band carried coherently at local carrier k·h: 0 offshore (k h ≥ 1.2), <see cref="MaxCoherence"/>
    /// in the surf (≤ 0.4).
    /// </summary>
    public static float Coherence(float kh) => MaxCoherence * (1f - Smooth((kh - CoherentKh) / (IncoherentKh - CoherentKh)));

    /// <summary>
    /// The carrier for the GPU: (k₀, reference coordinate, phase at <paramref name="renderTime"/>, peak-band energy Σ a²)
    /// and (choppiness, ω, group phase at <paramref name="renderTime"/>, group ratio). Mirrors <see cref="CoastalWaves"/>.
    /// </summary>
    public (System.Numerics.Vector4 Carrier, System.Numerics.Vector4 Chop) GpuCarrier(double renderTime)
    {
        var phase = _carrierPhase - _carrierOmega * (renderTime - Time);
        phase -= Math.Floor(phase / Math.Tau) * Math.Tau;
        var group = _groupPhase - _carrierOmega * GroupRatio * (renderTime - Time);
        group -= Math.Floor(group / Math.Tau) * Math.Tau;
        return (new System.Numerics.Vector4(_carrierK0, _carrierRef, (float)phase, _carrierEnergy), new System.Numerics.Vector4(Choppiness, _carrierOmega, (float)group, GroupRatio));
    }

    /// <summary>The set envelope's time phase this tick (−GroupRatio ω t); see <see cref="GroupEnvelope"/>.</summary>
    public float GroupPhase => _groupPhase;

    /// <summary>
    /// Set envelope at a point whose carrier spatial phase (k₀ d̂·x + δψ) is <paramref name="spatial"/>, for the group
    /// time phase <paramref name="group"/>: (1 + m cos φ) / √(1 + m²/2), mean square 1.
    /// </summary>
    public float GroupEnvelope(float spatial, float group) =>
        (1f + GroupDepth * MathF.Cos(GroupRatio * spatial + group)) / MathF.Sqrt(1f + 0.5f * GroupDepth * GroupDepth);

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
            _coth[i] = set.Coth[j];
            _ref[i] = CoastalField.RefCoord(omega);
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
                RefCoord = _ref[i],
            };
        }

        return count;
    }

    /// <summary>
    /// Surface position of the water particle whose rest position is (x0, z0).
    /// </summary>
    public float3 SurfacePoint(float x0, float z0)
    {
        var current = new CoastalSample();
        var previous = new CoastalSample();
        var fields = SampleFields(x0, z0, ref current, ref previous);
        if (fields == 0)
            return PointOf(DeepWaves(), x0, z0);

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var point = PointOf(CoastalWaves(Coastal!.Direction, x0, z0, current, scratch), x0, z0);
        return fields == 2 ? float3.Lerp(PointOf(CoastalWaves(PreviousDirection, x0, z0, previous, scratch), x0, z0), point, CoastalFade) : point;
    }

    private float3 PointOf(in Waves waves, float x0, float z0)
    {
        SumDisplacement(waves, x0, z0, out var dx, out var dz);
        return new float3(x0 + dx, SumHeight(waves, x0, z0), z0 + dz);
    }

    /// <summary>Water surface height at world (x, z).</summary>
    public float SampleHeight(float x, float z)
    {
        var current = new CoastalSample();
        var previous = new CoastalSample();
        var fields = SampleFields(x, z, ref current, ref previous);
        if (fields == 0)
            return HeightOf(DeepWaves(), x, z);

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var height = HeightOf(CoastalWaves(Coastal!.Direction, x, z, current, scratch), x, z);
        return fields == 2 ? float.Lerp(HeightOf(CoastalWaves(PreviousDirection, x, z, previous, scratch), x, z), height, CoastalFade) : height;
    }

    private float HeightOf(in Waves waves, float x, float z)
    {
        Invert(waves, x, z, out var x0, out var z0);
        return SumHeight(waves, x0, z0);
    }

    public float3 SampleNormal(float x, float z) => Sample(x, z).Normal;

    public float3 SampleVelocity(float x, float z) => Sample(x, z).Velocity;

    public void SampleHeights(ReadOnlySpan<float2> positions, Span<float> heights)
    {
        for (var i = 0; i < positions.Length; i++)
            heights[i] = SampleHeight(positions[i].X, positions[i].Y);
    }

    /// <summary>Height, normal, orbital velocity and Jacobian at world (x, z).</summary>
    public WaveSample Sample(float x, float z)
    {
        var current = new CoastalSample();
        var previous = new CoastalSample();
        var fields = SampleFields(x, z, ref current, ref previous);
        if (fields == 0)
            return SampleOf(DeepWaves(), x, z);

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var now = SampleOf(CoastalWaves(Coastal!.Direction, x, z, current, scratch), x, z);
        if (fields == 1)
            return now;

        var before = SampleOf(CoastalWaves(PreviousDirection, x, z, previous, scratch), x, z);
        var t = CoastalFade;
        return new WaveSample
        {
            Height = float.Lerp(before.Height, now.Height, t),
            Normal = float3.Normalize(float3.Lerp(before.Normal, now.Normal, t)),
            Velocity = float3.Lerp(before.Velocity, now.Velocity, t),
            Jacobian = float.Lerp(before.Jacobian, now.Jacobian, t),
        };
    }

    // Fully optimised from its first call: few callers sample the whole wave (a sea zone seeding the cells it grows over),
    // so tiered compilation left it in its slow early tiers and promoted it on whichever tick crossed the threshold
    // (10 samples took 13 ms on a zone's first growth, against 0.05 ms once promoted).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private WaveSample SampleOf(in Waves w, float x, float z)
    {
        Invert(w, x, z, out var x0, out var z0);

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

        for (var i = 0; i < w.Padded; i += Lanes)
        {
            var kx = Load(w.Kx, i);
            var kz = Load(w.Kz, i);
            var theta = Vector256.FusedMultiplyAdd(kx, vx, Vector256.FusedMultiplyAdd(kz, vz, Load(w.Phase, i)));
            var (sin, cos) = Vector256.SinCos(theta);
            var cx = Load(w.Cx, i);
            var cz = Load(w.Cz, i);

            height = Vector256.FusedMultiplyAdd(Load(w.A, i), cos, height);
            hx = Vector256.FusedMultiplyAdd(Load(w.Akx, i), sin, hx);
            hz = Vector256.FusedMultiplyAdd(Load(w.Akz, i), sin, hz);
            jxx = Vector256.FusedMultiplyAdd(cx * kx, cos, jxx);
            jzz = Vector256.FusedMultiplyAdd(cz * kz, cos, jzz);
            jxz = Vector256.FusedMultiplyAdd(cx * kz, cos, jxz);
            ux = Vector256.FusedMultiplyAdd(Load(w.Awx, i), cos, ux);
            uz = Vector256.FusedMultiplyAdd(Load(w.Awz, i), cos, uz);
            uy = Vector256.FusedMultiplyAdd(Load(w.Aw, i), sin, uy);
        }

        var slopeX = -Vector256.Sum(hx);
        var slopeZ = -Vector256.Sum(hz);
        var dxx = 1f - Vector256.Sum(jxx);
        var dzz = 1f - Vector256.Sum(jzz);
        var dxz = -Vector256.Sum(jxz);

        // Tangents Tx = (dxx, slopeX, dxz), Tz = (dxz, slopeZ, dzz); normal = Tz × Tx.
        var normal = new float3(
            slopeZ * dxz - dzz * slopeX,
            dzz * dxx - dxz * dxz,
            dxz * slopeX - slopeZ * dxx
        );

        return new WaveSample
        {
            Height = Vector256.Sum(height),
            Normal = float3.Normalize(normal),
            Velocity = new float3(Vector256.Sum(ux), Vector256.Sum(uy), Vector256.Sum(uz)),
            Jacobian = dxx * dzz - dxz * dxz,
        };
    }

    /// <summary>
    /// <see cref="Sample(float, float)"/> with a shape drawn on the swell, such as the breaker faces
    /// (<see cref="OceanSurface"/>). A shape can shear the surface far harder than the swell's chop (a pitched breaker's
    /// wall is squeezed to an eighth), where the fixed-point inversion stalls, so this inverts with Newton steps on the
    /// full Jacobian, halving any that overshoot.
    /// </summary>
    public WaveSample Sample<TShape>(float x, float z, ref TShape shape) where TShape : struct, IRestShape
    {
        // Coastal terms are linearised about where they're taken, and a shape can put the rest position metres from
        // (x, z) (a pitched crest's lies about 6 m back), where the depth-limited surf wave is another height. Past
        // RetakeDistance they're taken again there and the inversion finished from it, as the GPU evaluates them at each
        // rest position.
        var px = x;
        var pz = z;
        var sample = ShapedSample(x, z, px, pz, ref shape, out var x0, out var z0);

        for (var i = 0; i < MaxRetakes && Coastal is not null; i++)
        {
            var dx = x0 - px;
            var dz = z0 - pz;
            if (dx * dx + dz * dz < RetakeDistance * RetakeDistance)
                break;

            px = x0;
            pz = z0;
            sample = ShapedSample(x, z, px, pz, ref shape, out x0, out z0);
        }

        return sample;
    }

    /// <summary>A shaped sample at world (x, z) with the coastal terms taken at (px, pz), where the inversion starts.</summary>
    private WaveSample ShapedSample<TShape>(float x, float z, float px, float pz, ref TShape shape, out float x0, out float z0)
        where TShape : struct, IRestShape
    {
        var current = new CoastalSample();
        var previous = new CoastalSample();
        var fields = SampleFields(px, pz, ref current, ref previous);
        if (fields == 0)
            return ShapedOf(DeepWaves(), x, z, px, pz, false, ref shape, out x0, out z0);

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var now = ShapedOf(CoastalWaves(Coastal!.Direction, px, pz, current, scratch), x, z, px, pz, false, ref shape, out x0, out z0);
        if (fields == 1)
            return now;

        var before = ShapedOf(CoastalWaves(PreviousDirection, px, pz, previous, scratch), x, z, px, pz, true, ref shape, out _, out _);
        var t = CoastalFade;
        return new WaveSample
        {
            Height = float.Lerp(before.Height, now.Height, t),
            Normal = float3.Normalize(float3.Lerp(before.Normal, now.Normal, t)),
            Velocity = float3.Lerp(before.Velocity, now.Velocity, t),
            Jacobian = float.Lerp(before.Jacobian, now.Jacobian, t),
            Whitewater = float.Lerp(before.Whitewater, now.Whitewater, t),
        };
    }

    /// <summary>The swell with the shape on it at rest position (x0, z0): height, displacement and their partials.</summary>
    public RestPoint SampleRest<TShape>(float x0, float z0, ref TShape shape) where TShape : struct, IRestShape
    {
        var current = new CoastalSample();
        var previous = new CoastalSample();
        var fields = SampleFields(x0, z0, ref current, ref previous);
        if (fields == 0)
            return ShapedRest(DeepWaves(), x0, z0, false, ref shape);

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var now = ShapedRest(CoastalWaves(Coastal!.Direction, x0, z0, current, scratch), x0, z0, false, ref shape);
        return fields == 2 ? RestPoint.Lerp(ShapedRest(CoastalWaves(PreviousDirection, x0, z0, previous, scratch), x0, z0, true, ref shape), now, CoastalFade) : now;
    }

    private WaveSample ShapedOf<TShape>(in Waves w, float x, float z, float startX, float startZ, bool previous, ref TShape shape, out float x0, out float z0)
        where TShape : struct, IRestShape
    {
        x0 = startX;
        z0 = startZ;
        var p = ShapedRest(w, x0, z0, previous, ref shape);
        var rx = x0 + p.Dx - x;
        var rz = z0 + p.Dz - z;
        var miss = rx * rx + rz * rz;

        for (var i = 0; i < ShapedIterations && miss > ShapedTolerance * ShapedTolerance; i++)
        {
            var dxx = 1f + p.DxX;
            var dzz = 1f + p.DzZ;
            var det = dxx * dzz - p.DxZ * p.DxZ;
            var sx = rx;
            var sz = rz;

            if (det > MinNewtonJacobian)
            {
                sx = (dzz * rx - p.DxZ * rz) / det;
                sz = (dxx * rz - p.DxZ * rx) / det;
            }

            // On a pitched wall the Jacobian is about a tenth, so a Newton step from beside it leaps across the crest: cap it,
            // halve it until the miss shrinks, and failing that take the plain fixed-point step (also halved), which shrinks
            // the miss wherever the Jacobian lies in (0, 2), as the faces keep it.
            var length = MathF.Sqrt(sx * sx + sz * sz);
            if (length > MaxShapedStep)
            {
                sx *= MaxShapedStep / length;
                sz *= MaxShapedStep / length;
            }

            var improved = false;
            for (var h = 0; h <= 2 * ShapedHalvings + 1 && !improved; h++)
            {
                if (h == ShapedHalvings + 1)
                {
                    sx = rx;
                    sz = rz;
                }

                var q = ShapedRest(w, x0 - sx, z0 - sz, previous, ref shape);
                var qx = x0 - sx + q.Dx - x;
                var qz = z0 - sz + q.Dz - z;
                var qMiss = qx * qx + qz * qz;

                if (qMiss < miss)
                {
                    x0 -= sx;
                    z0 -= sz;
                    p = q;
                    rx = qx;
                    rz = qz;
                    miss = qMiss;
                    improved = true;
                }
                else
                {
                    sx *= 0.5f;
                    sz *= 0.5f;
                }
            }

            if (!improved)
                break;
        }

        return SampleOf(p);
    }

    /// <summary>The swell through one field at (x0, z0) with the shape on it; previous = the field fading out (or open ocean).</summary>
    private RestPoint ShapedRest<TShape>(in Waves w, float x0, float z0, bool previous, ref TShape shape) where TShape : struct, IRestShape
    {
        var p = RestOf(w, x0, z0);
        shape.Apply(x0, z0, previous, ref p);
        return p;
    }

    private RestPoint RestOf(in Waves w, float x0, float z0)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var height = Vector256<float>.Zero;
        var hx = Vector256<float>.Zero;
        var hz = Vector256<float>.Zero;
        var sx = Vector256<float>.Zero;
        var sz = Vector256<float>.Zero;
        var jxx = Vector256<float>.Zero;
        var jzz = Vector256<float>.Zero;
        var jxz = Vector256<float>.Zero;
        var ux = Vector256<float>.Zero;
        var uz = Vector256<float>.Zero;
        var uy = Vector256<float>.Zero;

        for (var i = 0; i < w.Padded; i += Lanes)
        {
            var kx = Load(w.Kx, i);
            var kz = Load(w.Kz, i);
            var theta = Vector256.FusedMultiplyAdd(kx, vx, Vector256.FusedMultiplyAdd(kz, vz, Load(w.Phase, i)));
            var (sin, cos) = Vector256.SinCos(theta);
            var cx = Load(w.Cx, i);
            var cz = Load(w.Cz, i);

            height = Vector256.FusedMultiplyAdd(Load(w.A, i), cos, height);
            hx = Vector256.FusedMultiplyAdd(Load(w.Akx, i), sin, hx);
            hz = Vector256.FusedMultiplyAdd(Load(w.Akz, i), sin, hz);
            sx = Vector256.FusedMultiplyAdd(cx, sin, sx);
            sz = Vector256.FusedMultiplyAdd(cz, sin, sz);
            jxx = Vector256.FusedMultiplyAdd(cx * kx, cos, jxx);
            jzz = Vector256.FusedMultiplyAdd(cz * kz, cos, jzz);
            jxz = Vector256.FusedMultiplyAdd(cx * kz, cos, jxz);
            ux = Vector256.FusedMultiplyAdd(Load(w.Awx, i), cos, ux);
            uz = Vector256.FusedMultiplyAdd(Load(w.Awz, i), cos, uz);
            uy = Vector256.FusedMultiplyAdd(Load(w.Aw, i), sin, uy);
        }

        return new RestPoint
        {
            Height = Vector256.Sum(height),
            SlopeX = -Vector256.Sum(hx),
            SlopeZ = -Vector256.Sum(hz),
            Dx = -Vector256.Sum(sx),
            Dz = -Vector256.Sum(sz),
            DxX = -Vector256.Sum(jxx),
            DzZ = -Vector256.Sum(jzz),
            DxZ = -Vector256.Sum(jxz),
            Velocity = new float3(Vector256.Sum(ux), Vector256.Sum(uy), Vector256.Sum(uz)),
        };
    }

    private static WaveSample SampleOf(in RestPoint p)
    {
        var dxx = 1f + p.DxX;
        var dzz = 1f + p.DzZ;
        var dxz = p.DxZ;

        // Tangents Tx = (dxx, slopeX, dxz), Tz = (dxz, slopeZ, dzz); normal = Tz x Tx.
        var normal = new float3(
            p.SlopeZ * dxz - dzz * p.SlopeX,
            dzz * dxx - dxz * dxz,
            dxz * p.SlopeX - p.SlopeZ * dxx
        );

        return new WaveSample
        {
            Height = p.Height,
            Normal = float3.Normalize(normal),
            Velocity = p.Velocity,
            Jacobian = dxx * dzz - dxz * dxz,
            Whitewater = p.Whitewater,
        };
    }

    /// <summary>Per-component terms a sample sums over: the cached open-ocean ones, or coastal ones for one point.</summary>
    private ref struct Waves
    {
        /// <summary>Loop length: the components padded to whole SIMD lanes (coastal sets add the carrier).</summary>
        public int Padded;
        public Span<float> Kx;
        public Span<float> Kz;
        public Span<float> Phase;
        public Span<float> A;
        public Span<float> Akx;
        public Span<float> Akz;
        public Span<float> Cx;
        public Span<float> Cz;
        public Span<float> Aw;
        public Span<float> Awx;
        public Span<float> Awz;
    }

    private Waves DeepWaves() => new()
    {
        Padded = _paddedCount,
        Kx = _kx,
        Kz = _kz,
        Phase = _phase,
        A = _a,
        Akx = _akx,
        Akz = _akz,
        Cx = _cx,
        Cz = _cz,
        Aw = _aw,
        Awx = _awx,
        Awz = _awz,
    };

    /// <summary>
    /// Coastal terms at (px, pz): phase lag and its gradient (local wavevector), amplitude factor, breaking cap,
    /// displacement along the local direction, and a per-sample steepness clamp. The lag is linearised about (px, pz) so
    /// the Gerstner inversion can reuse it; the GPU evaluates it exactly at each rest position.
    /// </summary>
    private Waves CoastalWaves(float direction, float px, float pz, in CoastalSample s, Span<float> scratch)
    {
        var n = CoastalPadded;
        scratch.Clear();
        var w = new Waves
        {
            Padded = n,
            Kx = scratch.Slice(0 * n, n),
            Kz = scratch.Slice(1 * n, n),
            Phase = scratch.Slice(2 * n, n),
            A = scratch.Slice(3 * n, n),
            Akx = scratch.Slice(4 * n, n),
            Akz = scratch.Slice(5 * n, n),
            Cx = scratch.Slice(6 * n, n),
            Cz = scratch.Slice(7 * n, n),
            Aw = scratch.Slice(8 * n, n),
            Awx = scratch.Slice(9 * n, n),
            Awz = scratch.Slice(10 * n, n),
        };

        var (cap, chopScale) = CoastalLimits(s, direction);
        var depth = MathF.Max(s.Depth, 0f);

        // Carrier at its reference frequency; coherence from its local wavenumber (|k₀ d̂ + ∇δψ|, the eikonal's k(h)) × depth.
        var (dirZ, dirX) = MathF.SinCos(direction);
        var jc = (int)_carrierRef;
        var tc = _carrierRef - jc;
        var lagC = float.Lerp(s.Lag[jc], s.Lag[jc + 1], tc);
        var lxC = float.Lerp(s.LagX[jc], s.LagX[jc + 1], tc);
        var lzC = float.Lerp(s.LagZ[jc], s.LagZ[jc + 1], tc);
        var shoalC = float.Lerp(s.Amp[jc], s.Amp[jc + 1], tc);
        var kxC = _carrierK0 * dirX + lxC;
        var kzC = _carrierK0 * dirZ + lzC;
        var kC = MathF.Max(MathF.Sqrt(kxC * kxC + kzC * kzC), 1e-6f);

        // Coherence from k(h) h, continuous in depth: the lag map's gradient steps at every texel line (SurfCarrier).
        var coherence = CoherenceEnabled && _carrierEnergy > 0f ? Coherence(Jonswap.WaveNumberApprox(_carrierOmega, depth) * depth) : 0f;

        for (var i = 0; i < _count; i++)
        {
            var j = (int)_ref[i];
            var t = _ref[i] - j;
            var lag = float.Lerp(s.Lag[j], s.Lag[j + 1], t);
            var lx = float.Lerp(s.LagX[j], s.LagX[j + 1], t);
            var lz = float.Lerp(s.LagZ[j], s.LagZ[j + 1], t);
            var amp = float.Lerp(s.Amp[j], s.Amp[j + 1], t) * cap * MathF.Sqrt(1f - coherence * _band[i]);

            var kx = _kx[i] + lx;
            var kz = _kz[i] + lz;
            var k = MathF.Max(MathF.Sqrt(kx * kx + kz * kz), 1e-6f);
            var a = _a[i] * amp;
            var chop = MathF.Sqrt(_cx[i] * _cx[i] + _cz[i] * _cz[i]) * amp * chopScale;
            var aw = a * _omega[i];
            var coth = 1f / MathF.Tanh(MathF.Max(k * depth, MinCothKh));

            w.Kx[i] = kx;
            w.Kz[i] = kz;
            w.Phase[i] = _phase[i] + lag - lx * px - lz * pz;
            w.A[i] = a;
            w.Akx[i] = a * kx;
            w.Akz[i] = a * kz;
            w.Cx[i] = chop * kx / k;
            w.Cz[i] = chop * kz / k;
            w.Aw[i] = aw;
            w.Awx[i] = aw * coth * kx / k;
            w.Awz[i] = aw * coth * kz / k;
        }

        if (coherence > 0f)
        {
            var envelope = GroupEnvelope(_carrierK0 * (dirX * px + dirZ * pz) + lagC, _groupPhase);
            var (unbroken, capC) = CarrierBreaking(shoalC * envelope, coherence, depth);
            var phase = _carrierPhase + lagC - lxC * px - lzC * pz;

            // The held height is the wave's own, taken at its crest (the break clock is carried per position, and a
            // wave's crest and the trough ahead of it hold different values): the crest behind's over most of the
            // wavelength, handing over to the crest ahead's around the trough (BreakerFace.HoldBlend), so the crest keeps
            // its amplitude across its whole peak and stays on the carrier crest, and only the trough's depth varies
            // (ocean_common.glsl swellShare).
            var theta = kxC * px + kzC * pz + phase;
            var u = theta / MathF.Tau - MathF.Floor(theta / MathF.Tau);
            var lambda = MathF.Tau / kC;
            var hold = 0f;
            if (Breaking is not null)
            {
                var behind = BreakerFace.BreakHold(Breaking.BreakDistance(px - kxC / kC * u * lambda, pz - kzC / kC * u * lambda), lambda);
                var ahead = BreakerFace.BreakHold(Breaking.BreakDistance(px + kxC / kC * (1f - u) * lambda, pz + kzC / kC * (1f - u) * lambda), lambda);
                hold = float.Lerp(behind, ahead, BreakerFace.HoldBlend(u));
            }

            var a = 0.5f * unbroken * BreakerFace.HeldCap(capC, depth, unbroken, hold);
            var chop = a * Choppiness * chopScale;

            // Shoaling peaks the carrier: bound harmonics (1, s, s²), normalised to its energy, all in phase at the crest.
            var sharp = Sharpness(2f * a, depth);
            var norm = a / MathF.Sqrt(1f + sharp * sharp * (1f + sharp * sharp));
            var an = norm;

            for (var h = 1; h <= CarrierSlots; h++, an *= sharp)
            {
                var c = _count + h - 1;
                var kx = h * kxC;
                var kz = h * kzC;
                var aw = an * h * _carrierOmega;
                var coth = 1f / MathF.Tanh(MathF.Max(h * kC * depth, MinCothKh));

                w.Kx[c] = kx;
                w.Kz[c] = kz;
                w.Phase[c] = h * phase;
                w.A[c] = an;
                w.Akx[c] = an * kx;
                w.Akz[c] = an * kz;
                w.Cx[c] = h == 1 ? chop * kxC / kC : 0f;
                w.Cz[c] = h == 1 ? chop * kzC / kC : 0f;
                w.Aw[c] = aw;
                w.Awx[c] = aw * coth * kxC / kC;
                w.Awz[c] = aw * coth * kzC / kC;
            }
        }

        return w;
    }

    /// <summary>
    /// The coherent surf wave's unbroken height (crest to trough, from its share of the peak band and the shoaling factor
    /// at the carrier frequency, times the set envelope when given) and its breaking cap min(1, γ_wave h / H). The surface draws H × cap and the breakers
    /// stage from the cap, so the wave grows until it breaks exactly where its crash is drawn.
    /// </summary>
    public (float Height, float Cap) CarrierBreaking(float shoaling, float coherence, float depth)
    {
        var height = 2f * MathF.Sqrt(coherence * _carrierEnergy) * shoaling;
        return (height, height > 1e-4f ? Math.Clamp(WaveBreakingGamma * MathF.Max(depth, 0f) / height, 0f, 1f) : 1f);
    }

    /// <summary>
    /// Crest peaking s of the surf carrier from its height over the depth: 0 while it is small for the water, rising to
    /// <see cref="MaxSharpness"/> as it nears breaking (H/h toward γ), so crests stand taller and troughs flatten as it shoals.
    /// </summary>
    public static float Sharpness(float height, float depth) =>
        MaxSharpness * Smooth((height / MathF.Max(depth, 0.1f) - SharpFrom) / (SharpFull - SharpFrom));

    /// <summary>Tests: switch surf coherence off on the CPU (the GPU always applies it).</summary>
    internal bool CoherenceEnabled = true;

    /// <summary>Tests: Σ A² over the coastal wave set at (x, z), i.e. twice the local variance the surface carries.</summary>
    internal float SurfVariance(float x, float z)
    {
        var sample = new CoastalSample();
        var previous = new CoastalSample();
        if (SampleFields(x, z, ref sample, ref previous) == 0)
            return float.NaN;

        Span<float> scratch = stackalloc float[ScratchArrays * CoastalPadded];
        var w = CoastalWaves(Coastal!.Direction, x, z, sample, scratch);
        var sum = 0f;
        for (var i = 0; i < w.Padded; i++)
            sum += w.A[i] * w.A[i];

        return sum;
    }

    /// <summary>Component slots of a coastal wave set: the components plus the carrier and its harmonics, padded to whole SIMD lanes.</summary>
    private int CoastalPadded => (_count + CarrierSlots + Lanes - 1) / Lanes * Lanes;

    /// <summary>
    /// Over the references in use: the breaking cap min(1, γ h / Hs) from the shoaled spectrum, and the choppiness scale
    /// that keeps the rms of the shoaled, shortened Gerstner trace under <see cref="MaxRmsSteepness"/>, as the open sea's
    /// clamp does, so the two agree where a field fades out. Mirrored in ocean_common.glsl.
    /// </summary>
    private (float Cap, float ChopScale) CoastalLimits(in CoastalSample s, float direction)
    {
        var (dirZ, dirX) = MathF.SinCos(direction);
        var variance = 0f;
        var steepness = 0f;

        for (var j = RefMin; j <= RefMax; j++)
        {
            var k0 = CoastalField.RefK0(j);
            var kx = k0 * dirX + s.LagX[j];
            var kz = k0 * dirZ + s.LagZ[j];
            var amp2 = s.Amp[j] * s.Amp[j];
            variance += _refEnergy[j] * amp2;
            steepness += _refChop[j] * amp2 * (kx * kx + kz * kz) / (k0 * k0);
        }

        var hs = 4f * MathF.Sqrt(variance);
        var cap = hs > 1e-4f ? Math.Clamp(CoastalField.BreakingGamma * s.Depth / hs, 0f, 1f) : 1f;
        steepness = MathF.Sqrt(steepness) * cap;
        return (cap, steepness > MaxRmsSteepness ? MaxRmsSteepness / steepness : 1f);
    }

    private void Invert(in Waves waves, float x, float z, out float x0, out float z0)
    {
        x0 = x;
        z0 = z;

        if (Choppiness <= 0f)
            return;

        for (var i = 0; i < InversionIterations; i++)
        {
            SumDisplacement(waves, x0, z0, out var dx, out var dz);
            x0 = x - dx;
            z0 = z - dz;
        }
    }

    private float SumHeight(in Waves w, float x0, float z0)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var height = Vector256<float>.Zero;

        for (var i = 0; i < w.Padded; i += Lanes)
        {
            var theta = Vector256.FusedMultiplyAdd(Load(w.Kx, i), vx, Vector256.FusedMultiplyAdd(Load(w.Kz, i), vz, Load(w.Phase, i)));
            height = Vector256.FusedMultiplyAdd(Load(w.A, i), Vector256.Cos(theta), height);
        }

        return Vector256.Sum(height);
    }

    private void SumDisplacement(in Waves w, float x0, float z0, out float dx, out float dz)
    {
        var vx = Vector256.Create(x0);
        var vz = Vector256.Create(z0);
        var sx = Vector256<float>.Zero;
        var sz = Vector256<float>.Zero;

        for (var i = 0; i < w.Padded; i += Lanes)
        {
            var theta = Vector256.FusedMultiplyAdd(Load(w.Kx, i), vx, Vector256.FusedMultiplyAdd(Load(w.Kz, i), vz, Load(w.Phase, i)));
            var sin = Vector256.Sin(theta);
            sx = Vector256.FusedMultiplyAdd(Load(w.Cx, i), sin, sx);
            sz = Vector256.FusedMultiplyAdd(Load(w.Cz, i), sin, sz);
        }

        dx = -Vector256.Sum(sx);
        dz = -Vector256.Sum(sz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Load(Span<float> span, int index) =>
        Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(span), (nuint)index);
}

public struct WaveSample
{
    public float Height;
    public float3 Normal;
    public float3 Velocity;

    /// <summary>Surface area ratio; below ~0.3 the surface is folding (feeds foam).</summary>
    public float Jacobian;

    /// <summary>0..1 whitewater a shape adds (a throwing crest or a broken roller); 0 for the bare swell.</summary>
    public float Whitewater;
}

/// <summary>The surf carrier at a point (<see cref="OceanSwell.SurfCarrier"/>).</summary>
public struct SurfCarrierPoint
{
    /// <summary>Phase (rad), 0 on a crest, increasing the way the wave travels.</summary>
    public float Phase;
    public float Kx;
    public float Kz;

    /// <summary>Unbroken height crest to trough (m), sets included.</summary>
    public float Unbroken;
    public float Depth;
}

/// <summary>
/// The surface at a rest position: height and its slope, the horizontal displacement (world = rest + D) and its partials,
/// so d(world)/d(rest) = I + [[DxX, DxZ], [DxZ, DzZ]], and the water velocity.
/// </summary>
public struct RestPoint
{
    public float Height;
    public float SlopeX;
    public float SlopeZ;
    public float Dx;
    public float Dz;
    public float DxX;
    public float DzZ;
    public float DxZ;
    public float3 Velocity;
    public float Whitewater;

    public static RestPoint Lerp(in RestPoint a, in RestPoint b, float t) => new()
    {
        Height = float.Lerp(a.Height, b.Height, t),
        SlopeX = float.Lerp(a.SlopeX, b.SlopeX, t),
        SlopeZ = float.Lerp(a.SlopeZ, b.SlopeZ, t),
        Dx = float.Lerp(a.Dx, b.Dx, t),
        Dz = float.Lerp(a.Dz, b.Dz, t),
        DxX = float.Lerp(a.DxX, b.DxX, t),
        DzZ = float.Lerp(a.DzZ, b.DzZ, t),
        DxZ = float.Lerp(a.DxZ, b.DxZ, t),
        Velocity = float3.Lerp(a.Velocity, b.Velocity, t),
        Whitewater = float.Lerp(a.Whitewater, b.Whitewater, t),
    };
}

/// <summary>
/// Something drawn on the swell and defined at rest positions, like the GPU's breaker faces: it adds to a
/// <see cref="RestPoint"/>'s height, displacement and their partials. While a coastal rebuild crossfades in, it is applied
/// for each field (previous = the one fading out, or the open ocean where it has nothing) and the results are blended as
/// the swell is. A struct, so shaped samples don't box it.
/// </summary>
public interface IRestShape
{
    void Apply(float x0, float z0, bool previous, ref RestPoint p);
}

/// <summary>
/// std140-compatible wave component: (kx, kz, phase, amplitude), (cx, cz, omega, coastal ref coord).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuWave
{
    public float Kx;
    public float Kz;
    public float Phase;
    public float Amplitude;
    public float Cx;
    public float Cz;
    public float Omega;

    /// <summary>Fractional coastal reference index (<see cref="CoastalField.RefCoord"/>).</summary>
    public float RefCoord;
}

public struct SpectrumState
{
    public SwellParams Params;
    public float Alpha;
    public float PeakOmega;
    public float SpreadNorm;
}
