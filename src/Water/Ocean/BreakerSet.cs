using System.Diagnostics;
using System.Numerics;

namespace BrutalGame.Water.Ocean;

public enum BreakerStage : byte
{
    Shoaling,
    Curling,
    Plunging,
    Bore,
    Spent,
}

public enum BreakerType : byte
{
    Spilling,
    Plunging,
    Surging,
}

/// <summary>One crest vertex: position, water depth, breaking cap (1 = not breaking), local wave height, stage.</summary>
public struct BreakerVertex
{
    public Vector2 Position;
    public float Depth;
    public float Cap;
    public float Height;
    public BreakerStage Stage;

    /// <summary>
    /// Which way the crest travels here, as the axis of the grid edge it crosses signed by the phase's rise: within 90° of
    /// the local wave direction even where refraction has turned the crest far from the swell's mean direction.
    /// </summary>
    public Vector2 Travel;
}

/// <summary>A crest line in the surf zone (docs/breaking-waves.md). Vertices run along the crest.</summary>
public sealed class Breaker
{
    public readonly BreakerVertex[] Vertices = new BreakerVertex[BreakerSet.MaxVertices];

    /// <summary>The coastal grid edge each vertex crosses (<see cref="BreakerSet.EdgeIndex"/>): the lip base slot it shares with its neighbours.</summary>
    public readonly int[] Edges = new int[BreakerSet.MaxVertices];
    public int Count;

    /// <summary>Crest number: the integer phase cycle this crest sits on. Stable while the crest travels ashore.</summary>
    public long Crest;

    /// <summary>Vertex where breaking begins next to unbroken water (−1 if none), and the side it peels toward (±1 along the vertices).</summary>
    public int BreakIndex;
    public int PeelDirection;

    /// <summary>Break point interpolated to where the cap leaves 1 between the break vertex and its unbroken neighbour.</summary>
    public Vector2 BreakPoint;

    /// <summary>Break point speed along the crest (m/s), smoothed over ticks; 0 until measured.</summary>
    public float PeelSpeed;
    public BreakerType Type;

    internal Vector2 LastBreakPoint;
    internal bool HadBreakPoint;
}

/// <summary>
/// Breaker crests, derived each tick from the layer B coastal maps (v1, docs/breaking-waves.md):
/// the swell's peak frequency is followed as one carrier wave whose phase is k₀ d̂·x + δψ(x) − ω t (lag interpolated at
/// the peak frequency). Crests are its zero-phase contours in the surf zone, extracted by marching squares on the 4 m
/// coastal grid and chained into polylines. Stage per vertex comes from the depth-limited breaking cap, the same cap the
/// surface uses, so breakers sit exactly where the drawn waves lose height. Deterministic from seed, time and terrain.
/// Main thread, allocation-free after construction.
/// </summary>
public sealed class BreakerSet
{
    public const int MaxBreakers = 32;
    public const int MaxVertices = 128;
    private const int N = CoastalField.Resolution;
    private const int MaxSegments = 16384;

    /// <summary>Bound on <see cref="EdgeIndex"/>: every crossed edge of this tick's segments.</summary>
    public const int MaxEdges = 2 * MaxSegments;
    private const float Gravity = 9.81f;

    /// <summary>Surf zone: texels shallower than this many local Hs / γ take part (breaking, or about to); slightly onshore ones too, so crests end at the beach.</summary>
    private const float SurfMargin = 1.3f;
    private const float MinSurfDepth = 3f;
    private const float OnshoreDepth = -1f;
    private const float SpentDepth = 0.3f;
    private const int MinChainPoints = 4;
    private const float PeelSmoothing = 0.05f;
    private const float BreakingCap = 0.999f;

    private const float MaxBreakDistance = 1000f;

    /// <summary>
    /// The break clock runs from this cap (full speed <see cref="ClockRampCap"/> lower): the wave is this far past its
    /// breaking limit, the face is ready. Everything the break then does (the face standing up, the lip's throw and
    /// landing, the collapse: <see cref="BreakerFace.CrestState"/>) runs on this clock, so the sequence has the same
    /// shape on any bed; the cap after that only says when the water has run out. (From 0.97, with the face still on
    /// the cap, a 1:50 shelf had the lip thrown and the crest collapsed before the face had stood up.)
    /// </summary>
    public const float ClockStartCap = 0.92f;
    private const float ClockRampCap = 0.06f;

    /// <summary>
    /// Hysteresis: once the clock has run <see cref="ClockStartedDistance"/> it keeps running until the wave is under its
    /// breaking limit by this much (cap, unclamped, above 1: H under γ h), with the same ramp. Started and stopped at one
    /// cap, a wave hovering at its limit (its height moving with the set envelope) kept starting its break and letting
    /// it decay: it could not decide whether it was crashing.
    /// </summary>
    private const float ClockStopCap = 1.1f;
    private const float ClockStartedDistance = 1f;

    /// <summary>Metres of travel over which a wave that stops breaking (re-forming over deeper water) forgets its break, by 1/e.</summary>
    private const float ReformDistance = 10f;

    /// <summary>Break point easing per tick, and the jump (m) beyond which it snaps to a new breaking section.</summary>
    private const float BreakEase = 0.15f;
    private const float BreakSnap = 25f;
    private const int SmoothPasses = 2;

    private readonly float[] _sin = new float[N * N];
    private readonly float[] _cos = new float[N * N];
    private readonly byte[] _mask = new byte[N * N];
    private readonly float[] _depth = new float[N * N];
    /// <summary>Unbroken height of the coherent surf wave per texel, before the set envelope; cap and height follow it each tick.</summary>
    private readonly float[] _hs = new float[N * N];
    private readonly float[] _height = new float[N * N];
    private readonly float[] _cap = new float[N * N];

    /// <summary>
    /// Break distance (m) per texel: how far the wave here has travelled since it began to break, carried with the waves
    /// at the carrier's celerity each tick: it grows once the lip appears (from cap <see cref="ClockStartCap"/>, full
    /// speed <see cref="ClockRampCap"/> lower, as the curl comes up; the lip lands a set distance after that) and fades by the rest, so
    /// it fades as a wave re-forms over deeper water. A break has memory: a section throws, its lip
    /// lands within a fraction of a wavelength (<see cref="BreakerFace"/>) and it rolls on as whitewater, so the curl is
    /// only ever a pocket where the break begins, peeling along the crest, however the depth runs after. Also kept as
    /// half floats for the GPU (<see cref="BreakDistanceBits"/>).
    /// </summary>
    private float[] _breakDistance = new float[N * N];
    private float[] _breakNext = new float[N * N];
    private readonly ushort[] _breakBits = new ushort[N * N];
    private readonly int[] _breakCells = new int[N * N];
    private int _breakCount;
    private Vector2 _breakOrigin = new(float.NaN);
    private readonly float[] _edgeT = new float[2 * N * N];

    // Texels that can ever be in the surf zone for the current fields and wave height; rebuilt when those change.
    private readonly int[] _candidates = new int[N * N];
    private int _candidateCount;
    private int _candidateField = -1;
    private int _candidatePrevious = -1;
    private float _candidateDepth = -1f;
    private readonly float[] _spatialPhase = new float[N * N];
    private readonly int[] _surfCells = new int[N * N];
    private int _surfCount;
    private int _stateField = -1;
    private float _stateOmega;
    private double _stateTime;
    private bool _stateFading;
    private const double FadeRefresh = 0.1;
    private float _stateEnergy;
    private int _stateCandidates = -1;
    private readonly Vector2[] _edgePoint = new Vector2[2 * N * N];
    private readonly int[] _edgeLinks = new int[2 * N * N * 2];
    private readonly int[] _touchedEdges = new int[MaxSegments * 2];
    private readonly int[] _edgeIndex = new int[2 * N * N];
    private readonly int[] _segEdges = new int[MaxSegments * 2];
    private readonly bool[] _segUsed = new bool[MaxSegments];
    private const int MaxChain = 4 * N;
    private readonly int[] _chain = new int[2 * MaxChain + 2];
    private readonly Breaker[] _breakers = new Breaker[MaxBreakers];
    private readonly Breaker[] _previous = new Breaker[MaxBreakers];
    private int _previousCount;
    private int _touched;
    private int _segments;
    private Vector2 _fieldOrigin;
    private Vector2 _k0Dir;

    public BreakerSet()
    {
        Array.Fill(_edgeLinks, -1);

        for (var i = 0; i < MaxBreakers; i++)
        {
            _breakers[i] = new Breaker();
            _previous[i] = new Breaker();
        }
    }

    public int Count { get; private set; }

    public Breaker this[int index] => _breakers[index];

    public float LastUpdateMs { get; private set; }

    public int SurfTexels { get; private set; }

    /// <summary>World xz of the break distance grid's texel (0, 0) corner (the coastal field's origin); NaN before the first.</summary>
    public Vector2 BreakOrigin => _breakOrigin;

    /// <summary>Break distances as half floats, N² row-major on the coastal grid, for the shaders.</summary>
    public ReadOnlySpan<ushort> BreakDistanceBits => _breakBits;

    /// <summary>
    /// How far (m) the wave at world (x, z) has travelled since it began to break, bilinear between texel centres (as the
    /// shaders sample it); 0 where it isn't breaking or outside the surf zone. Read-only, so safe from jobs between ticks.
    /// </summary>
    public float BreakDistance(float x, float z)
    {
        var fx = (x - _breakOrigin.X) / CoastalField.CellSize - 0.5f;
        var fz = (z - _breakOrigin.Y) / CoastalField.CellSize - 0.5f;
        if (!(fx >= 0f && fz >= 0f && fx < N - 1 && fz < N - 1))
            return 0f;

        var ix = (int)fx;
        var iz = (int)fz;
        var c = ix + iz * N;
        var tx = fx - ix;
        var tz = fz - iz;
        return float.Lerp(float.Lerp(_breakDistance[c], _breakDistance[c + 1], tx), float.Lerp(_breakDistance[c + N], _breakDistance[c + N + 1], tx), tz);
    }

    /// <summary>Fixed tick, after <see cref="OceanSwell.SetTime"/>.</summary>
    public void Update(OceanSwell swell, float dt)
    {
        var start = Stopwatch.GetTimestamp();
        Count = 0;
        _segments = 0;

        var field = swell.Coastal;
        var omega = swell.PeakOmega;

        if (field is not null && omega > 0f)
        {
            var (dirZ, dirX) = MathF.SinCos(field.Direction);
            _fieldOrigin = field.Origin;
            _k0Dir = new Vector2(dirX, dirZ) * (omega * omega / Gravity);
            CarrierPhase(swell, field, omega);
            CarryBreakDistance(field, omega, dt);
            ExtractSegments();
            BuildBreakers(swell, field, omega, dt);
        }

        LastUpdateMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>
    /// Per surf-zone texel centre: depth, local Hs, breaking cap and the spatial part of the carrier phase are cached
    /// (they change only with the fields, the spectrum or a crossfade); each tick then costs one sincos per surf texel.
    /// Texel centres need no interpolation; a fading-out field is read at the same world texel (re-centring moves origins
    /// by whole texels) and its depth and height blended in. Only texels breaking or close to it (depth under
    /// <see cref="SurfMargin"/> · Hs / γ) count.
    /// </summary>
    private void CarrierPhase(OceanSwell swell, CoastalField field, float omega)
    {
        var maxDepth = MathF.Max(MinSurfDepth, SurfMargin * swell.SignificantWaveHeight * CoastalField.MaxAmplitudeFactor / CoastalField.BreakingGamma);
        var previous = swell.CoastalFade < 1f ? swell.CoastalPrevious : null;
        var shift = previous is null ? default : (field.Origin - previous.Origin) / CoastalField.CellSize;
        var phaseT = (float)((omega * swell.Time) % (2.0 * Math.PI));
        UpdateCandidates(field, previous, shift, maxDepth);
        UpdateSurfState(swell, field, previous, shift, omega);

        // Sets move through the surf every tick: the cap follows the set envelope's height (OceanSwell.GroupEnvelope).
        var group = swell.GroupPhase;
        for (var i = 0; i < _surfCount; i++)
        {
            var c = _surfCells[i];
            (_sin[c], _cos[c]) = MathF.SinCos(_spatialPhase[c] - phaseT);
            var height = _hs[c] * swell.GroupEnvelope(_spatialPhase[c], group);
            _height[c] = height;
            _cap[c] = height > 1e-4f ? Math.Clamp(OceanSwell.WaveBreakingGamma * MathF.Max(_depth[c], 0f) / height, 0f, 1f) : 1f;
        }

        SurfTexels = _surfCount;
    }

    /// <summary>Recomputes the cached per-texel surf state when the fields, peak frequency or spectrum change, or every tick during a crossfade.</summary>
    private void UpdateSurfState(OceanSwell swell, CoastalField field, CoastalField? previous, Vector2 shift, float omega)
    {
        var energy = swell.RefEnergy;
        var energyKey = 0f;
        for (var k = 0; k < energy.Length; k++)
            energyKey += energy[k] * (k + 1);

        // During a crossfade the blended depth and height change slowly, so 10 Hz is plenty.
        var fade = swell.CoastalFade;
        var same = field.Version == _stateField && omega == _stateOmega && energyKey == _stateEnergy && _candidateField == _stateCandidates;
        if ((fade >= 1f && same) || (fade < 1f && swell.Time - _stateTime < FadeRefresh && _stateFading))
            return;

        _stateTime = swell.Time;
        _stateFading = fade < 1f;

        _stateField = fade >= 1f ? field.Version : -1;
        _stateOmega = omega;
        _stateEnergy = energyKey;
        _stateCandidates = _candidateField;

        var r = CoastalField.RefCoord(omega);
        var j = (int)r;
        var t = r - j;
        var k0 = omega * omega / Gravity;
        var (dirZ, dirX) = MathF.SinCos(field.Direction);
        var (refMin, refMax) = (swell.RefMin, swell.RefMax);
        var texels = field.Texels;
        _surfCount = 0;

        for (var i = 0; i < _candidateCount; i++)
        {
            var c = _candidates[i];
            var x = c % N;
            var z = c / N;
            var o = c * CoastalField.TexelFloats;
            var po = -1;

            if (previous is not null && (uint)(x + (int)shift.X) < N && (uint)(z + (int)shift.Y) < N)
                po = (x + (int)shift.X + (z + (int)shift.Y) * N) * CoastalField.TexelFloats;

            var depth = Blend(texels[o], po >= 0 ? previous!.Texels[po] : CoastalField.DeepDepth, fade);
            var variance = 0f;

            for (var k = refMin; k <= refMax; k++)
            {
                var amp = Blend(texels[o + 5 + 2 * k], po >= 0 ? previous!.Texels[po + 5 + 2 * k] : 1f, fade);
                variance += energy[k] * amp * amp;
            }

            var hs = 4f * MathF.Sqrt(variance);
            _mask[c] = depth >= OnshoreDepth && depth <= SurfMargin * hs / CoastalField.BreakingGamma ? (byte)1 : (byte)0;

            if (_mask[c] == 0)
                continue;

            _surfCells[_surfCount++] = c;

            // Phase from the current field only: blending two fields' lags sweeps the phase across the fade and the crests
            // run backwards (OceanSwell.SampleFields). Depth and height above do blend.
            var lag = float.Lerp(texels[o + 4 + 2 * j], texels[o + 6 + 2 * j], t);
            var px = field.Origin.X + (x + 0.5f) * CoastalField.CellSize;
            var pz = field.Origin.Y + (z + 0.5f) * CoastalField.CellSize;

            // Stage from the coherent surf wave's own breaking (OceanSwell.CarrierBreaking), the wave the surface draws here.
            var shoal = Blend(float.Lerp(texels[o + 5 + 2 * j], texels[o + 7 + 2 * j], t),
                po >= 0 ? float.Lerp(previous!.Texels[po + 5 + 2 * j], previous.Texels[po + 7 + 2 * j], t) : 1f, fade);
            var kh = Jonswap.WaveNumberApprox(omega, MathF.Max(depth, 0.05f)) * MathF.Max(depth, 0f);
            var (height, _) = swell.CarrierBreaking(shoal, OceanSwell.Coherence(kh), depth);

            _spatialPhase[c] = k0 * (dirX * px + dirZ * pz) + lag;
            _depth[c] = depth;
            _hs[c] = height;
        }
    }

    /// <summary>
    /// Candidate texels: within [<see cref="OnshoreDepth"/>, maxDepth] in either field. Rebuilt only when a field is
    /// swapped or the wave height moves the bound by more than 5%, so a tick touches the surf zone, not the whole map.
    /// </summary>
    private void UpdateCandidates(CoastalField field, CoastalField? previous, Vector2 shift, float maxDepth)
    {
        var previousVersion = previous?.Version ?? 0;
        if (field.Version == _candidateField && previousVersion == _candidatePrevious && MathF.Abs(maxDepth - _candidateDepth) < 0.05f * _candidateDepth)
            return;

        _candidateField = field.Version;
        _candidatePrevious = previousVersion;
        _candidateDepth = maxDepth;
        _candidateCount = 0;
        Array.Clear(_mask);
        var texels = field.Texels;

        for (var c = 0; c < N * N; c++)
        {
            var depth = texels[c * CoastalField.TexelFloats];
            var px = c % N + (int)shift.X;
            var pz = c / N + (int)shift.Y;

            if (previous is not null && (uint)px < N && (uint)pz < N)
                depth = MathF.Min(depth, MathF.Max(previous.Texels[(px + pz * N) * CoastalField.TexelFloats], OnshoreDepth));

            if (depth <= maxDepth && depth >= OnshoreDepth)
                _candidates[_candidateCount++] = c;
        }
    }

    private static float Blend(float current, float previous, float fade) => fade < 1f ? float.Lerp(previous, current, fade) : current;

    /// <summary>
    /// Carries the break distance one tick: each surf texel takes the distance upwind of it, a tick's travel back along
    /// the carrier's local direction (its phase gradient), plus that travel times how far the face has stood up, and faded
    /// over <see cref="ReformDistance"/> by the rest. It is a property of the water, so a wave that is not breaking brings
    /// its 0 in from upwind at its celerity: zeroing it where the cap rose past a threshold flipped a collapsed wave back
    /// to a fresh one in a tick as a set went by, and starting it at a threshold ran the crash clock before the lip had
    /// formed. A re-centred field moves its origin by whole texels, and the distances stay on the world.
    /// </summary>
    private void CarryBreakDistance(CoastalField field, float omega, float dt)
    {
        if (field.Origin != _breakOrigin)
            ShiftBreakDistance(field.Origin);

        Array.Clear(_breakNext);

        for (var i = 0; i < _surfCount; i++)
        {
            var c = _surfCells[i];
            var g = PhaseGradient(c);
            var k = MathF.Max(g.Length(), 1e-4f);
            var travel = omega / k * dt;
            var back = travel / (k * CoastalField.CellSize);
            var upwind = Sample(_breakDistance, c % N - g.X * back, c / N - g.Y * back);
            var raw = _height[c] > 1e-4f ? OceanSwell.WaveBreakingGamma * MathF.Max(_depth[c], 0f) / _height[c] : 2f;
            var rate = BreakerFaces.Smooth(((upwind > ClockStartedDistance ? ClockStopCap : ClockStartCap) - raw) / ClockRampCap);
            _breakNext[c] = MathF.Min(upwind * MathF.Exp(-travel * (1f - rate) / ReformDistance) + travel * rate, MaxBreakDistance);
        }

        (_breakDistance, _breakNext) = (_breakNext, _breakDistance);

        for (var i = 0; i < _breakCount; i++)
            _breakBits[_breakCells[i]] = 0;

        _breakCount = 0;
        for (var i = 0; i < _surfCount; i++)
        {
            var c = _surfCells[i];
            if (_breakDistance[c] <= 0f)
                continue;

            _breakBits[c] = BitConverter.HalfToUInt16Bits((Half)_breakDistance[c]);
            _breakCells[_breakCount++] = c;
        }
    }

    /// <summary>The carrier's spatial phase gradient at texel c, its local wavevector, from surf neighbours (k₀ d̂ where there are none).</summary>
    private Vector2 PhaseGradient(int c)
    {
        var x = c % N;
        var z = c / N;
        return new Vector2(PhaseSlope(c, 1, x > 0, x < N - 1, _k0Dir.X), PhaseSlope(c, N, z > 0, z < N - 1, _k0Dir.Y));
    }

    private float PhaseSlope(int c, int step, bool hasLow, bool hasHigh, float fallback)
    {
        var low = hasLow && _mask[c - step] != 0;
        var high = hasHigh && _mask[c + step] != 0;
        return low && high ? (_spatialPhase[c + step] - _spatialPhase[c - step]) / (2f * CoastalField.CellSize)
            : high ? (_spatialPhase[c + step] - _spatialPhase[c]) / CoastalField.CellSize
            : low ? (_spatialPhase[c] - _spatialPhase[c - step]) / CoastalField.CellSize
            : fallback;
    }

    /// <summary>Bilinear at texel coordinates (x, z) = centres at integers; 0 outside the grid.</summary>
    private static float Sample(float[] grid, float x, float z)
    {
        if (!(x >= 0f && z >= 0f && x < N - 1 && z < N - 1))
            return 0f;

        var ix = (int)x;
        var iz = (int)z;
        var c = ix + iz * N;
        var tx = x - ix;
        var tz = z - iz;
        return float.Lerp(float.Lerp(grid[c], grid[c + 1], tx), float.Lerp(grid[c + N], grid[c + N + 1], tx), tz);
    }

    private void ShiftBreakDistance(Vector2 origin)
    {
        Array.Clear(_breakNext);

        if (!float.IsNaN(_breakOrigin.X))
        {
            var sx = (int)MathF.Round((origin.X - _breakOrigin.X) / CoastalField.CellSize);
            var sz = (int)MathF.Round((origin.Y - _breakOrigin.Y) / CoastalField.CellSize);

            for (var z = 0; z < N; z++)
            {
                var oz = z + sz;
                if ((uint)oz >= N)
                    continue;

                for (var x = 0; x < N; x++)
                {
                    var ox = x + sx;
                    if ((uint)ox < N)
                        _breakNext[x + z * N] = _breakDistance[ox + oz * N];
                }
            }
        }

        (_breakDistance, _breakNext) = (_breakNext, _breakDistance);
        _breakOrigin = origin;
        Array.Clear(_breakBits);
        _breakCount = 0;
    }


    /// <summary>Marching squares on sin θ = 0 where cos θ &gt; 0 (crests, not troughs); one segment per crossed cell.</summary>
    private void ExtractSegments()
    {
        for (var i = 0; i < _touched; i++)
        {
            _edgeLinks[_touchedEdges[i] * 2] = -1;
            _edgeLinks[_touchedEdges[i] * 2 + 1] = -1;
        }

        _touched = 0;
        _segments = 0;
        Span<int> crossed = stackalloc int[4];

        for (var i = 0; i < _surfCount; i++)
        {
            var c = _surfCells[i];
            if (c % N == N - 1 || c / N == N - 1 || (_mask[c] & _mask[c + 1] & _mask[c + N] & _mask[c + N + 1]) == 0)
                continue;

            var n = 0;
            TryEdge(c, c + 1, 2 * c, crossed, ref n);
            TryEdge(c + N, c + N + 1, 2 * (c + N), crossed, ref n);
            TryEdge(c, c + N, 2 * c + 1, crossed, ref n);
            TryEdge(c + 1, c + N + 1, 2 * (c + 1) + 1, crossed, ref n);

            if (n == 2)
                AddSegment(crossed[0], crossed[1]);
            else if (n == 4)
                AddSaddle(c, crossed);
        }
    }

    /// <summary>
    /// All four edges crossed (bottom, top, left, right in <paramref name="crossed"/>): the two contour pieces pair by the
    /// sign of sin θ at the cell's centre, so the pairing never flips as the saddle drifts. Pairing opposite edges drew
    /// two crossing pieces.
    /// </summary>
    private void AddSaddle(int c, Span<int> crossed)
    {
        var centre = _sin[c] + _sin[c + 1] + _sin[c + N] + _sin[c + N + 1];

        if ((centre < 0f) == (_sin[c] < 0f))
        {
            AddSegment(crossed[0], crossed[3]);
            AddSegment(crossed[2], crossed[1]);
        }
        else
        {
            AddSegment(crossed[0], crossed[2]);
            AddSegment(crossed[1], crossed[3]);
        }
    }

    private void TryEdge(int a, int b, int edge, Span<int> crossed, ref int n)
    {
        var sa = _sin[a];
        var sb = _sin[b];
        if ((sa < 0f) == (sb < 0f))
            return;

        var t = sa / (sa - sb);
        if (float.Lerp(_cos[a], _cos[b], t) <= 0f)
            return;

        var pa = TexelCentre(a);
        _edgePoint[edge] = pa + (TexelCentre(b) - pa) * t;
        _edgeT[edge] = t;
        crossed[n++] = edge;
    }

    private static Vector2 TexelCentre(int c) => new(c % N + 0.5f, c / N + 0.5f);

    private void AddSegment(int e0, int e1)
    {
        if (_segments == MaxSegments)
            return;

        var s = _segments++;
        _segEdges[s * 2] = e0;
        _segEdges[s * 2 + 1] = e1;
        _segUsed[s] = false;
        Link(e0, s);
        Link(e1, s);
    }

    private void Link(int edge, int segment)
    {
        if (_edgeLinks[edge * 2] < 0)
        {
            _edgeLinks[edge * 2] = segment;

            if (_touched < _touchedEdges.Length)
            {
                _edgeIndex[edge] = _touched;
                _touchedEdges[_touched++] = edge;
            }
        }
        else
        {
            _edgeLinks[edge * 2 + 1] = segment;
        }
    }

    /// <summary>Chains segments through shared edges into crest polylines, then classifies each vertex.</summary>
    private void BuildBreakers(OceanSwell swell, CoastalField field, float omega, float dt)
    {
        var k0 = omega * omega / Gravity;
        var (dirZ, dirX) = MathF.SinCos(field.Direction);

        for (var s = 0; s < _segments && Count < MaxBreakers; s++)
        {
            if (_segUsed[s])
                continue;

            var length = Chain(s);
            if (length < MinChainPoints)
                continue;

            var b = _breakers[Count];
            b.Count = 0;
            var breaking = false;
            var stride = (length + MaxVertices - 1) / MaxVertices;

            for (var i = 0; i < length; i += stride)
            {
                var vertex = EdgeVertex(_chain[i]);
                breaking |= vertex.Stage is BreakerStage.Curling or BreakerStage.Plunging or BreakerStage.Bore;
                b.Edges[b.Count] = _chain[i];
                b.Vertices[b.Count++] = vertex;
            }

            if (!breaking)
                continue;

            Smooth(b);

            var first = b.Vertices[0].Position;
            var theta = k0 * (dirX * first.X + dirZ * first.Y) + Lag(field, first, omega) - omega * swell.Time;
            b.Crest = (long)Math.Round(theta / (2.0 * Math.PI));
            FindBreakPoint(b);
            b.Type = Classify(b, field, omega);
            MeasurePeel(b, dt);
            Count++;
        }

        RememberCrests();
    }

    /// <summary>
    /// Laplacian passes over vertex positions (ends fixed): marching squares on a 4 m grid leaves a staircase that the
    /// face and lip would otherwise show. Shifts are a fraction of a cell, so stage, depth and cap stay valid.
    /// </summary>
    private static void Smooth(Breaker b)
    {
        for (var pass = 0; pass < SmoothPasses; pass++)
        {
            var previous = b.Vertices[0].Position;

            for (var v = 1; v + 1 < b.Count; v++)
            {
                var here = b.Vertices[v].Position;
                b.Vertices[v].Position = 0.5f * here + 0.25f * (previous + b.Vertices[v + 1].Position);
                previous = here;
            }
        }
    }

    /// <summary>
    /// The crest where it crosses grid edge e, which joins texel e &gt;&gt; 1 to its +x (even e) or +z (odd e) neighbour:
    /// surf state interpolated along the edge from its two texels. Travel: across a crest sin θ rises the way the wave
    /// travels (cos θ &gt; 0 there), so the edge's axis signed by that rise points within 90° of the travel direction.
    /// </summary>
    public BreakerVertex EdgeVertex(int edge)
    {
        var a = edge >> 1;
        var b = (edge & 1) == 0 ? a + 1 : a + N;
        var t = _edgeT[edge];
        var depth = float.Lerp(_depth[a], _depth[b], t);
        var cap = float.Lerp(_cap[a], _cap[b], t);
        var axis = (edge & 1) == 0 ? Vector2.UnitX : Vector2.UnitY;

        var stage = depth < SpentDepth ? BreakerStage.Spent
            : cap >= BreakingCap ? BreakerStage.Shoaling
            : cap >= 0.85f ? BreakerStage.Curling
            : cap >= 0.6f ? BreakerStage.Plunging
            : BreakerStage.Bore;

        return new BreakerVertex
        {
            Position = _fieldOrigin + _edgePoint[edge] * CoastalField.CellSize,
            Depth = depth,
            Cap = cap,
            Height = float.Lerp(_height[a], _height[b], t) * cap,
            Stage = stage,
            Travel = _sin[b] >= _sin[a] ? axis : -axis,
        };
    }

    /// <summary>
    /// The crest's direction of travel where it crosses edge e: the carrier's wavevector at the edge's two texels,
    /// interpolated along it, so both segments meeting at the edge agree. Unit length.
    /// </summary>
    public Vector2 EdgeNormal(int edge)
    {
        var a = edge >> 1;
        var b = (edge & 1) == 0 ? a + 1 : a + N;
        var k = Vector2.Lerp(PhaseGradient(a), PhaseGradient(b), _edgeT[edge]);
        var length = k.Length();
        return length > 1e-6f ? k / length : Vector2.Normalize(_k0Dir);
    }

    /// <summary>Marching-squares segments this tick, before chaining: the lips' seeds (<see cref="BreakerFaces"/>).</summary>
    public int RawSegmentCount => _segments;

    /// <summary>The two edges segment s joins.</summary>
    public (int Edge0, int Edge1) RawSegment(int segment) => (_segEdges[segment * 2], _segEdges[segment * 2 + 1]);

    /// <summary>Compact index of an edge crossed this tick, under <see cref="MaxEdges"/>; shared by the segments meeting there.</summary>
    public int EdgeIndex(int edge) => _edgeIndex[edge];

    /// <summary>Walks both ways from segment s; the chain is the ordered list of edge indices (points).</summary>
    private int Chain(int s)
    {
        var mid = MaxChain;
        var lo = mid;
        var hi = mid;
        _segUsed[s] = true;
        _chain[hi++] = _segEdges[s * 2];
        _chain[hi++] = _segEdges[s * 2 + 1];

        Walk(s, _segEdges[s * 2 + 1], ref hi, 1);
        Walk(s, _segEdges[s * 2], ref lo, -1);

        var length = hi - lo;
        if (lo != 0)
            Array.Copy(_chain, lo, _chain, 0, length);

        return length;
    }

    private void Walk(int segment, int edge, ref int cursor, int step)
    {
        while (true)
        {
            var next = _edgeLinks[edge * 2] == segment ? _edgeLinks[edge * 2 + 1] : _edgeLinks[edge * 2];
            if (next < 0 || _segUsed[next])
                return;

            _segUsed[next] = true;
            var far = _segEdges[next * 2] == edge ? _segEdges[next * 2 + 1] : _segEdges[next * 2];

            if (step > 0 ? cursor >= _chain.Length : cursor <= 0)
                return;

            if (step > 0)
                _chain[cursor++] = far;
            else
                _chain[--cursor] = far;

            segment = next;
            edge = far;
        }
    }

    private static float Lag(CoastalField field, Vector2 p, float omega)
    {
        var s = new CoastalSample();
        if (!field.Sample(p.X, p.Y, ref s))
            return 0f;

        var r = CoastalField.RefCoord(omega);
        var j = (int)r;
        return float.Lerp(s.Lag[j], s.Lag[j + 1], r - j);
    }

    /// <summary>Break point: the breaking vertex next to unbroken (shoaling) water; peel runs toward the unbroken side.</summary>
    private void FindBreakPoint(Breaker b)
    {
        b.BreakIndex = -1;
        b.PeelDirection = 0;

        // A crest can have several breaking sections, and its chain may run either way from one tick to the next, so
        // "the first transition" can flip between them. Take the one nearest this crest's break point last tick.
        var remembered = RememberedBreakPoint(b.Crest, b.Vertices[b.Count / 2].Position, out var found);
        var nearest = float.MaxValue;

        for (var i = 0; i + 1 < b.Count; i++)
        {
            var here = b.Vertices[i].Stage;
            var next = b.Vertices[i + 1].Stage;
            int index;
            int direction;
            Vector2 point;

            if (here == BreakerStage.Shoaling && next is BreakerStage.Curling or BreakerStage.Plunging)
                (index, direction, point) = (i + 1, -1, CapCrossing(b.Vertices[i], b.Vertices[i + 1]));
            else if (next == BreakerStage.Shoaling && here is BreakerStage.Curling or BreakerStage.Plunging)
                (index, direction, point) = (i, 1, CapCrossing(b.Vertices[i + 1], b.Vertices[i]));
            else
                continue;

            var distance = found ? Vector2.DistanceSquared(point, remembered) : i;
            if (distance >= nearest)
                continue;

            nearest = distance;
            (b.BreakIndex, b.PeelDirection, b.BreakPoint) = (index, direction, point);
        }

        // Where the cap is nearly flat along the crest the threshold crossing is ill-conditioned and wanders; ease toward
        // it, snapping only to a section well away (a new one breaking).
        if (b.BreakIndex >= 0 && found && Vector2.Distance(b.BreakPoint, remembered) < BreakSnap)
            b.BreakPoint = Vector2.Lerp(remembered, b.BreakPoint, BreakEase);
    }

    /// <summary>The break point remembered from last tick for this crest number nearest to <paramref name="near"/>.</summary>
    private Vector2 RememberedBreakPoint(long crest, Vector2 near, out bool found)
    {
        var best = float.MaxValue;
        var point = Vector2.Zero;
        found = false;

        for (var i = 0; i < _previousCount; i++)
        {
            var old = _previous[i];
            if (old.Crest != crest || !old.HadBreakPoint)
                continue;

            var distance = Vector2.DistanceSquared(old.LastBreakPoint, near);
            if (distance >= best)
                continue;

            (best, point, found) = (distance, old.LastBreakPoint, true);
        }

        return point;
    }

    /// <summary>Where the cap leaves 1 between an unbroken and a breaking vertex: continuous as the crest moves, unlike vertex indices.</summary>
    private static Vector2 CapCrossing(in BreakerVertex unbroken, in BreakerVertex breaking)
    {
        var span = unbroken.Cap - breaking.Cap;
        var t = span > 1e-5f ? Math.Clamp((unbroken.Cap - BreakingCap) / span, 0f, 1f) : 1f;
        return Vector2.Lerp(unbroken.Position, breaking.Position, t);
    }

    /// <summary>Iribarren number ξ = tan β / √(H / L₀) at the break point (or the shallowest vertex) picks the breaker type.</summary>
    private static BreakerType Classify(Breaker b, CoastalField field, float omega)
    {
        var i = b.BreakIndex >= 0 ? b.BreakIndex : b.Count / 2;
        var v = b.Vertices[i];
        var s = new CoastalSample();
        var e = new CoastalSample();
        const float d = CoastalField.CellSize;

        if (!field.Sample(v.Position.X - d, v.Position.Y, ref s) || !field.Sample(v.Position.X + d, v.Position.Y, ref e))
            return BreakerType.Spilling;

        var slopeX = (e.Depth - s.Depth) / (2f * d);
        field.Sample(v.Position.X, v.Position.Y - d, ref s);
        field.Sample(v.Position.X, v.Position.Y + d, ref e);
        var slopeZ = (e.Depth - s.Depth) / (2f * d);
        var tanBeta = MathF.Sqrt(slopeX * slopeX + slopeZ * slopeZ);
        var l0 = 2f * MathF.PI * Gravity / (omega * omega);
        var xi = tanBeta / MathF.Sqrt(MathF.Max(v.Height, 0.05f) / l0);

        return xi < 0.5f ? BreakerType.Spilling : xi < 3.3f ? BreakerType.Plunging : BreakerType.Surging;
    }

    private void MeasurePeel(Breaker b, float dt)
    {
        b.HadBreakPoint = b.BreakIndex >= 0;
        b.PeelSpeed = 0f;

        if (!b.HadBreakPoint)
            return;

        b.LastBreakPoint = b.BreakPoint;

        // Peel is motion along the crest only: the crest's own shoreward travel is removed by projecting on its tangent.
        var i0 = Math.Max(0, b.BreakIndex - 1);
        var i1 = Math.Min(b.Count - 1, b.BreakIndex + 1);
        var tangent = b.Vertices[i1].Position - b.Vertices[i0].Position;
        tangent = tangent.LengthSquared() > 1e-6f ? Vector2.Normalize(tangent) : Vector2.Zero;

        // Against this crest's nearest remembered break point: a crest split into pieces shares its number.
        var best = -1;
        var nearest = float.MaxValue;
        for (var i = 0; i < _previousCount; i++)
        {
            var old = _previous[i];
            var distance = Vector2.DistanceSquared(b.LastBreakPoint, old.LastBreakPoint);
            if (old.Crest != b.Crest || !old.HadBreakPoint || distance >= nearest)
                continue;

            (best, nearest) = (i, distance);
        }

        if (best >= 0 && dt > 0f)
        {
            var old = _previous[best];
            var speed = MathF.Abs(Vector2.Dot(b.LastBreakPoint - old.LastBreakPoint, tangent)) / dt;
            b.PeelSpeed = old.PeelSpeed > 0f ? float.Lerp(old.PeelSpeed, speed, PeelSmoothing) : speed;
        }
    }

    /// <summary>Keeps this tick's crest numbers, break points and peel speeds for the next tick's peel measurement.</summary>
    private void RememberCrests()
    {
        _previousCount = Count;

        for (var i = 0; i < Count; i++)
        {
            var b = _breakers[i];
            var old = _previous[i];
            old.Crest = b.Crest;
            old.HadBreakPoint = b.HadBreakPoint;
            old.LastBreakPoint = b.LastBreakPoint;
            old.PeelSpeed = b.PeelSpeed;
        }
    }
}