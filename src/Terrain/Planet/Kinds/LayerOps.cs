using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// A set of lattice points the evaluator runs over, rows of channels at a time: datum positions (direction × radius) in
/// double, one float row per channel plus <see cref="PlanetKindProgram.TempRows"/> temporaries. Points form a W × H grid
/// of lattice indices from (I0, J0) so octaves of 256 voxels and longer can come from the coarse grid's slots; a Direct
/// set (a single query point, or the coarse grid itself) evaluates every kept octave where it stands.
/// </summary>
internal ref struct RowSet
{
    public ReadOnlySpan<double> X, Y, Z;
    public Span<float> Ch;
    public Span<float> Slots;
    public int Count, Stride, Channels;
    public int W, H, I0, J0;
    public int CW, CH, CI0, CJ0, SlotStride;
    public bool Direct;

    /// <summary>Per column and per row of the window: the two coarse indices either side and the fraction between them.</summary>
    public ReadOnlySpan<int> ColA, ColB, RowA, RowB;
    public ReadOnlySpan<float> ColF, RowF;

    /// <summary>
    /// A single point's octave cache (<see cref="PlanetKindProgram.OctaveCache"/> floats, or empty): the layer being
    /// evaluated has its octaves' values, then their three derivative components, from table entry OctaveFirst.
    /// </summary>
    public Span<float> Octaves;
    public int OctaveFirst, OctaveCount;

    /// <summary>A lattice window's face, cells per side and job scratch (stamp list); null scratch for point queries.</summary>
    public PlanetChunkScratch? Scratch;
    public int Face, N;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<float> Row(int channel) => Ch.Slice(channel * Stride, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<float> Tmp(int k) => Ch.Slice((Channels + k) * Stride, Count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Span<float> Slot(int slot) => Slots.Slice(slot * SlotStride, SlotStride);
}

/// <summary>
/// The generic layer evaluator (planet-kinds.md §1.2): runs a <see cref="PlanetKindProgram"/>'s layers in order over a
/// <see cref="RowSet"/>. Deterministic maths only (add, sub, mul, div, floor, sqrt, min, max); a lattice point's value
/// depends only on its position, the LOD and the program, and a gated term adds exactly 0, so a chunk that skips a
/// layer agrees with a neighbour that evaluates it.
/// </summary>
internal static partial class LayerOps
{
    public const int CoarseStep = PlanetGenerator.CoarseStep;

    private const float FloorDrop = 1e5f;
    private const byte None = PlanetKindProgram.None;

    /// <summary>Fills the coarse grid's slots (and its warp channels) for every noise layer with coarse octaves at this LOD.</summary>
    public static void Coarse(PlanetKindProgram p, int lod, ref RowSet r)
    {
        for (var layer = 0; layer < p.LayerCount; layer++)
        {
            if (p.IsNoise(layer))
                CoarseLayer(p, layer, lod, ref r);
        }
    }

    public static void Evaluate(PlanetKindProgram p, int lod, ref RowSet r) => Evaluate(p, lod, ref r, p.LayerCount);

    /// <param name="layers">Run only the first so many layers (the compiler's calibration of a layer's input).</param>
    public static void Evaluate(PlanetKindProgram p, int lod, ref RowSet r, int layers)
    {
        for (var layer = 0; layer < layers; layer++)
        {
            switch (p.Op[layer])
            {
                case LayerOp.Fbm:
                case LayerOp.Ridged:
                case LayerOp.Billow:
                case LayerOp.Eroded:
                case LayerOp.Warp:
                    NoiseLayer(p, layer, lod, ref r);
                    break;
                case LayerOp.Craters:
                case LayerOp.Ergs:
                    Stamps.Evaluate(p, layer, lod, ref r);
                    break;
                case LayerOp.Curve:
                    Curve(p, layer, ref r);
                    break;
                case LayerOp.Terrace:
                    Terrace(p, layer, lod, ref r);
                    break;
                case LayerOp.Canyon:
                    Canyon(p, layer, lod, ref r);
                    break;
                case LayerOp.Mask:
                    Mask(p, layer, ref r);
                    break;
                case LayerOp.Combine:
                    Combine(p, layer, ref r);
                    break;
                case LayerOp.SeaClamp:
                    SeaClamp(p, layer, ref r);
                    break;
                case LayerOp.Legacy:
                    Legacy(p, layer, ref r);
                    break;
            }
        }
    }

    private static void CoarseLayer(PlanetKindProgram p, int layer, int lod, ref RowSet r)
    {
        var at = p.At(lod, layer);
        int end = p.OctaveEnd[at], coarse = p.CoarseEnd[at], slot = p.CoarseSlot[at], first = p.FirstOctave[layer];
        var n = r.Tmp(0);

        if (p.Op[layer] == LayerOp.Warp)
        {
            for (var c = 0; c < 3; c++)
                r.Row(p.Dst[layer] + c).Clear();

            for (var o = 0; o < end; o++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var row = r.Row(p.Dst[layer] + c);
                    NoiseRow(p, first + 3 * o + c, ref r, None, default, n);
                    AddScaled(row, n, p.Amplitude[first + 3 * o + c]);

                    if (o == coarse - 1)
                        row.CopyTo(r.Slot(slot + c));
                }
            }

            return;
        }

        if (coarse == 0)
            return;

        var warp = p.Warp[layer];
        var mode = p.Mode[layer];

        switch (p.Op[layer])
        {
            case LayerOp.Fbm:
            case LayerOp.Ridged when (mode & LayerMode.ShapedV1) != 0:
            {
                var sum = r.Slot(slot);
                sum.Clear();

                if ((mode & LayerMode.Gradient) != 0)
                {
                    Span<float> gx = r.Tmp(1), gy = r.Tmp(2), gz = r.Tmp(3);
                    Span<float> sx = r.Slot(slot + 1), sy = r.Slot(slot + 2), sz = r.Slot(slot + 3);
                    sx.Clear();
                    sy.Clear();
                    sz.Clear();

                    for (var o = 0; o < coarse; o++)
                    {
                        NoiseRowD(p, first + o, ref r, warp, default, n, gx, gy, gz);
                        var a = p.Amplitude[first + o];
                        var af = (float)(a * p.Frequency[first + o]);
                        AddScaled(sum, n, a);
                        AddScaled(sx, gx, af);
                        AddScaled(sy, gy, af);
                        AddScaled(sz, gz, af);
                    }

                    break;
                }

                for (var o = 0; o < coarse; o++)
                {
                    NoiseRow(p, first + o, ref r, warp, default, n);

                    if (p.Op[layer] == LayerOp.Ridged)
                        AddRidgeV1(sum, n, p.Amplitude[first + o]);
                    else
                        AddScaled(sum, n, p.Amplitude[first + o]);
                }

                break;
            }
            case LayerOp.Ridged:
            case LayerOp.Billow:
            {
                for (var o = 0; o < coarse; o++)
                    NoiseRow(p, first + o, ref r, warp, default, r.Slot(slot + o));

                break;
            }
            case LayerOp.Eroded:
            {
                Span<float> sum = r.Slot(slot), sx = r.Slot(slot + 1), sy = r.Slot(slot + 2), sz = r.Slot(slot + 3);
                sum.Clear();
                sx.Clear();
                sy.Clear();
                sz.Clear();
                ErodedOctaves(p, layer, ref r, 0, coarse, default, sum, sx, sy, sz);
                break;
            }
        }
    }

    /// <summary>
    /// Eroded octaves [from, to): each is damped by 1 / (1 + κ·|G|²), G the running tangential slope of the octaves so
    /// far (its own included). The sum and G carry on from what the rows already hold.
    /// </summary>
    private static void ErodedOctaves(PlanetKindProgram p, int layer, ref RowSet r, int from, int to, ReadOnlySpan<float> gate, Span<float> dst, Span<float> sx, Span<float> sy, Span<float> sz)
    {
        Span<float> n = r.Tmp(0), gx = r.Tmp(1), gy = r.Tmp(2), gz = r.Tmp(3);
        var first = p.FirstOctave[layer];
        var kappa = p.Params[p.ParamBase[layer]];
        var invRadius = 1.0 / p.Radius;

        for (var o = from; o < to; o++)
        {
            NoiseRowD(p, first + o, ref r, p.Warp[layer], gate, n, gx, gy, gz);
            var a = p.Amplitude[first + o];
            var af = (float)(a * p.Frequency[first + o]);

            for (var i = 0; i < dst.Length; i++)
            {
                sx[i] += af * gx[i];
                sy[i] += af * gy[i];
                sz[i] += af * gz[i];
                var radial = (float)((sx[i] * r.X[i] + sy[i] * r.Y[i] + sz[i] * r.Z[i]) * invRadius);
                var slope2 = MathF.Max(sx[i] * sx[i] + sy[i] * sy[i] + sz[i] * sz[i] - radial * radial, 0f);
                dst[i] += a * n[i] / (1f + kappa * slope2);
            }
        }
    }

    private static void NoiseLayer(PlanetKindProgram p, int layer, int lod, ref RowSet r)
    {
        var at = p.At(lod, layer);
        int end = p.OctaveEnd[at], coarse = r.Direct ? 0 : p.CoarseEnd[at], slot = p.CoarseSlot[at], first = p.FirstOctave[layer];
        var mode = p.Mode[layer];
        var warp = p.Warp[layer];
        var dst = r.Row(p.Dst[layer]);
        var gate = p.Gate[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.Gate[layer]);
        var gradient = p.Op[layer] == LayerOp.Fbm && (mode & LayerMode.Gradient) != 0;
        var n = r.Tmp(0);

        if (!gate.IsEmpty && AllZero(gate))
        {
            for (var c = 0; c < (gradient ? 4 : p.Fields(layer)); c++)
                r.Row(p.Dst[layer] + c).Clear();

            return;
        }

        Prefetch(p, layer, end, gradient || p.Op[layer] == LayerOp.Eroded, ref r);

        switch (p.Op[layer])
        {
            case LayerOp.Warp:
            {
                for (var c = 0; c < 3; c++)
                    Start(ref r, r.Row(p.Dst[layer] + c), coarse > 0, slot + c);

                for (var o = coarse; o < end; o++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        NoiseRow(p, first + 3 * o + c, ref r, None, default, n);
                        AddScaled(r.Row(p.Dst[layer] + c), n, p.Amplitude[first + 3 * o + c]);
                    }
                }

                break;
            }
            case LayerOp.Fbm when gradient:
            {
                Span<float> gx = r.Tmp(1), gy = r.Tmp(2), gz = r.Tmp(3);
                Span<float> sx = r.Row(p.Dst[layer] + 1), sy = r.Row(p.Dst[layer] + 2), sz = r.Row(p.Dst[layer] + 3);
                Start(ref r, dst, coarse > 0, slot);
                Start(ref r, sx, coarse > 0, slot + 1);
                Start(ref r, sy, coarse > 0, slot + 2);
                Start(ref r, sz, coarse > 0, slot + 3);

                for (var o = coarse; o < end; o++)
                {
                    NoiseRowD(p, first + o, ref r, warp, gate, n, gx, gy, gz);
                    var a = p.Amplitude[first + o];
                    var af = (float)(a * p.Frequency[first + o]);
                    AddScaled(dst, n, a);
                    AddScaled(sx, gx, af);
                    AddScaled(sy, gy, af);
                    AddScaled(sz, gz, af);
                }

                break;
            }
            case LayerOp.Fbm:
            case LayerOp.Ridged when (mode & LayerMode.ShapedV1) != 0:
            {
                Start(ref r, dst, coarse > 0, slot);

                for (var o = coarse; o < end; o++)
                {
                    NoiseRow(p, first + o, ref r, warp, gate, n);

                    if (p.Op[layer] == LayerOp.Ridged)
                        AddRidgeV1(dst, n, p.Amplitude[first + o]);
                    else
                        AddScaled(dst, n, p.Amplitude[first + o]);
                }

                break;
            }
            case LayerOp.Ridged:
            {
                var weight = r.Tmp(4);
                var gain = p.Params[p.ParamBase[layer]];
                weight.Fill(1f);
                dst.Clear();

                for (var o = 0; o < end; o++)
                {
                    if (o < coarse)
                        Interpolate(ref r, slot + o, n);
                    else
                        NoiseRow(p, first + o, ref r, warp, gate, n);

                    var a = p.Amplitude[first + o];

                    for (var i = 0; i < dst.Length; i++)
                    {
                        var k = 1f - MathF.Abs(n[i]);
                        var s = k * k * weight[i];
                        dst[i] += a * s;
                        weight[i] = MathF.Min(s * gain, 1f);
                    }
                }

                AddConstant(dst, p.RidgeMean[at]);
                break;
            }
            case LayerOp.Billow:
            {
                dst.Clear();

                for (var o = 0; o < end; o++)
                {
                    if (o < coarse)
                        Interpolate(ref r, slot + o, n);
                    else
                        NoiseRow(p, first + o, ref r, warp, gate, n);

                    var a = p.Amplitude[first + o];

                    for (var i = 0; i < dst.Length; i++)
                        dst[i] += a * (2f * MathF.Abs(n[i]) - 1f);
                }

                AddConstant(dst, p.RidgeMean[at]);
                break;
            }
            case LayerOp.Eroded:
            {
                Span<float> sx = r.Tmp(4), sy = r.Tmp(5), sz = r.Tmp(6);
                Start(ref r, dst, coarse > 0, slot);
                Start(ref r, sx, coarse > 0, slot + 1);
                Start(ref r, sy, coarse > 0, slot + 2);
                Start(ref r, sz, coarse > 0, slot + 3);
                ErodedOctaves(p, layer, ref r, coarse, end, gate, dst, sx, sy, sz);
                break;
            }
        }
    }

    /// <summary>A lone point with an octave cache gets the layer's octaves eight at a time; <see cref="NoiseRows"/> then reads them.</summary>
    private static void Prefetch(PlanetKindProgram p, int layer, int octaves, bool derivative, ref RowSet r)
    {
        r.OctaveCount = 0;
        var count = octaves * p.Fields(layer);

        if (r.Count != 1 || count == 0 || r.Octaves.Length < 4 * count)
            return;

        var first = p.FirstOctave[layer];
        var warp = p.Warp[layer];
        double x = r.X[0], y = r.Y[0], z = r.Z[0];

        if (warp != None)
            (x, y, z) = (x + r.Row(warp)[0], y + r.Row(warp + 1)[0], z + r.Row(warp + 2)[0]);

        var o = r.Octaves;

        if (!NoiseOctavesSimd(x, y, z, p.Frequency.AsSpan(first, count), p.OffsetX.AsSpan(first, count), p.OffsetY.AsSpan(first, count), p.OffsetZ.AsSpan(first, count), p.Seed.AsSpan(first, count),
                o[..count], derivative ? o.Slice(count, count) : default, derivative ? o.Slice(2 * count, count) : default, derivative ? o.Slice(3 * count, count) : default))
            return;

        (r.OctaveFirst, r.OctaveCount) = (first, count);
    }

    private static void Start(ref RowSet r, Span<float> dst, bool fromCoarse, int slot)
    {
        if (fromCoarse)
            Interpolate(ref r, slot, dst);
        else
            dst.Clear();
    }

    private static bool AllZero(ReadOnlySpan<float> row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            if (row[i] != 0f)
                return false;
        }

        return true;
    }

    private static void AddScaled(Span<float> dst, ReadOnlySpan<float> n, float a)
    {
        for (var i = 0; i < dst.Length; i++)
            dst[i] += a * n[i];
    }

    private static void AddRidgeV1(Span<float> dst, ReadOnlySpan<float> n, float a)
    {
        for (var i = 0; i < dst.Length; i++)
        {
            var k = 1f - MathF.Abs(n[i]);
            dst[i] += a * k * k;
        }
    }

    private static void AddConstant(Span<float> dst, float c)
    {
        if (c == 0f)
            return;

        for (var i = 0; i < dst.Length; i++)
            dst[i] += c;
    }

    /// <summary>
    /// One octave over the rows, clamped to [−1, 1]. Points whose gate is exactly 0 need no value (their term is multiplied
    /// by it): a row that is mostly gated off skips them one by one, any other runs eight points at a time.
    /// </summary>
    private static void NoiseRow(PlanetKindProgram p, int octave, ref RowSet r, byte warp, ReadOnlySpan<float> gate, Span<float> n) =>
        NoiseRows(p, octave, ref r, warp, gate, n, default, default, default);

    /// <summary>As <see cref="NoiseRow"/> with the analytic derivative in lattice units (the warp's Jacobian is left out).</summary>
    private static void NoiseRowD(PlanetKindProgram p, int octave, ref RowSet r, byte warp, ReadOnlySpan<float> gate, Span<float> n, Span<float> gx, Span<float> gy, Span<float> gz) =>
        NoiseRows(p, octave, ref r, warp, gate, n, gx, gy, gz);

    private static void NoiseRows(PlanetKindProgram p, int octave, ref RowSet r, byte warp, ReadOnlySpan<float> gate, Span<float> n, Span<float> gx, Span<float> gy, Span<float> gz)
    {
        var f = p.Frequency[octave];
        var ox = p.OffsetX[octave];
        var oy = p.OffsetY[octave];
        var oz = p.OffsetZ[octave];
        var seed = p.Seed[octave];
        var warped = warp != None;
        var derivative = !gx.IsEmpty;
        var cached = octave - r.OctaveFirst;

        if ((uint)cached < (uint)r.OctaveCount)
        {
            n[0] = r.Octaves[cached];

            if (derivative)
                (gx[0], gy[0], gz[0]) = (r.Octaves[r.OctaveCount + cached], r.Octaves[2 * r.OctaveCount + cached], r.Octaves[3 * r.OctaveCount + cached]);

            return;
        }

        ReadOnlySpan<float> wx = warped ? r.Row(warp) : default, wy = warped ? r.Row(warp + 1) : default, wz = warped ? r.Row(warp + 2) : default;
        var start = gate.IsEmpty || !MostlyZero(gate) ? NoiseRowSimd(r.X, r.Y, r.Z, wx, wy, wz, f, ox, oy, oz, seed, n, gx, gy, gz) : 0;

        for (var i = start; i < n.Length; i++)
        {
            if (!gate.IsEmpty && gate[i] == 0f)
            {
                n[i] = 0f;

                if (derivative)
                    gx[i] = gy[i] = gz[i] = 0f;

                continue;
            }

            var x = warped ? r.X[i] + wx[i] : r.X[i];
            var y = warped ? r.Y[i] + wy[i] : r.Y[i];
            var z = warped ? r.Z[i] + wz[i] : r.Z[i];

            n[i] = derivative
                ? Math.Clamp(Noise.Gradient3(x * f + ox, y * f + oy, z * f + oz, seed, out gx[i], out gy[i], out gz[i]), -1f, 1f)
                : Math.Clamp(Noise.Gradient3(x * f + ox, y * f + oy, z * f + oz, seed), -1f, 1f);
        }
    }

    private static bool MostlyZero(ReadOnlySpan<float> gate)
    {
        var zeros = 0;

        for (var i = 0; i < gate.Length; i++)
            zeros += gate[i] == 0f ? 1 : 0;

        return zeros * 2 > gate.Length;
    }

    /// <summary>Bilinear from the coarse grid (every 8th lattice point); a(1−f) + b·f form, exact at coarse points.</summary>
    private static void Interpolate(ref RowSet r, int slot, Span<float> dst)
    {
        ReadOnlySpan<float> s = r.Slot(slot);
        ReadOnlySpan<int> colA = r.ColA, colB = r.ColB;
        ReadOnlySpan<float> colF = r.ColF;
        var k = 0;

        for (var j = 0; j < r.H; j++)
        {
            var top = s.Slice(r.RowA[j] * r.CW, r.CW);
            var bottom = s.Slice(r.RowB[j] * r.CW, r.CW);
            var fj = r.RowF[j];

            for (var i = 0; i < colA.Length; i++, k++)
                dst[k] = Bilinear(top[colA[i]], top[colB[i]], bottom[colA[i]], bottom[colB[i]], colF[i], fj);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Bilinear(float h00, float h10, float h01, float h11, float fu, float fv)
    {
        var gu = 1f - fu;
        return (h00 * gu + h10 * fu) * (1f - fv) + (h01 * gu + h11 * fu) * fv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Smooth01(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        return v * v * (3f - 2f * v);
    }

    /// <summary>Piecewise remap through ascending knots, linear or eased per segment (monotone on each), constant outside.</summary>
    internal static float CurveAt(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, bool ease, float v)
    {
        var last = xs.Length - 1;

        if (v <= xs[0])
            return ys[0];

        if (v >= xs[last])
            return ys[last];

        var k = 0;

        while (v >= xs[k + 1])
            k++;

        var t = (v - xs[k]) / (xs[k + 1] - xs[k]);

        if (ease)
            t = t * t * (3f - 2f * t);

        return ys[k] + (ys[k + 1] - ys[k]) * t;
    }

    /// <summary>
    /// The relief knee (§1.3): identity inside [kneeLo, kneeHi]; past a knee the worst-case sum maps onto the relief bound
    /// by s·t / (1 + (s−1)·t), which leaves the knee with slope 1 and stays strictly increasing.
    /// </summary>
    internal static float KneeAt(ReadOnlySpan<float> k, float v)
    {
        if (v > k[0])
        {
            var t = MathF.Min((v - k[0]) / (k[1] - k[0]), 1f);
            v = k[0] + (k[2] - k[0]) * (k[3] * t / (1f + (k[3] - 1f) * t));
        }
        else if (v < k[4])
        {
            var t = MathF.Min((k[4] - v) / (k[4] - k[5]), 1f);
            v = k[4] - (k[4] - k[6]) * (k[7] * t / (1f + (k[7] - 1f) * t));
        }

        return v;
    }

    private static void Curve(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var mode = p.Mode[layer];
        ReadOnlySpan<float> src = r.Row(p.SrcA[layer]);
        var dst = r.Row(p.Dst[layer]);

        if ((mode & LayerMode.CurveKnee) != 0)
        {
            ReadOnlySpan<float> knee = p.Params.AsSpan(pb, 8);

            for (var i = 0; i < dst.Length; i++)
                dst[i] = KneeAt(knee, src[i]);

            return;
        }

        var count = (int)p.Params[pb];
        ReadOnlySpan<float> xs = p.Params.AsSpan(pb + 1, count);
        ReadOnlySpan<float> ys = p.Params.AsSpan(pb + 1 + count, count);
        var ease = (mode & LayerMode.CurveEase) != 0;

        if ((mode & LayerMode.CurveAdd) != 0)
        {
            for (var i = 0; i < dst.Length; i++)
                dst[i] += CurveAt(xs, ys, ease, src[i]);

            return;
        }

        for (var i = 0; i < dst.Length; i++)
            dst[i] = CurveAt(xs, ys, ease, src[i]);
    }

    /// <summary>
    /// Terrace offset T(u) − u at input height u: within a step, gentle benches (slope β) either side of a riser centred on
    /// the step's middle, so the offset is zero-mean and a riser band as wide as the step is the identity.
    /// </summary>
    internal static float TerraceAt(ReadOnlySpan<float> levels, float beta, float band, float u)
    {
        var last = levels.Length - 1;

        if (u <= levels[0] || u >= levels[last])
            return 0f;

        var k = 0;

        while (u >= levels[k + 1])
            k++;

        var step = levels[k + 1] - levels[k];
        var t = (u - levels[k]) / step;
        var share = Math.Clamp(band / step, 1e-4f, 1f);
        var a = 0.5f - 0.5f * share;
        float shaped;

        if (t < a)
            shaped = beta * t;
        else if (t > 1f - a)
            shaped = 1f - beta * (1f - t);
        else
            shaped = beta * a + (1f - 2f * beta * a) * ((t - a) / share);

        return (shaped - t) * step;
    }

    private static void Terrace(PlanetKindProgram p, int layer, int lod, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var count = (int)p.Params[pb];
        var beta = p.Params[pb + 1];
        ReadOnlySpan<float> levels = p.Params.AsSpan(pb + 2, count + 1);
        var band = p.RiserBand[p.At(lod, layer)];
        var h = r.Row(p.Dst[layer]);
        var strength = p.Gate[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.Gate[layer]);
        var strata = p.SrcB[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.SrcB[layer]);

        for (var i = 0; i < h.Length; i++)
        {
            var m = strength.IsEmpty ? 1f : strength[i];

            if (m == 0f)
                continue;

            var u = strata.IsEmpty ? h[i] : h[i] + strata[i];
            h[i] += m * TerraceAt(levels, beta, band, u);
        }
    }

    /// <summary>Canyon wall profile over x = distance / half-width: 1 on the floor, stepping down through benches to exactly 0 at the rim.</summary>
    internal static float CanyonProfile(float x, float floor, float benches, float riser)
    {
        if (x >= 1f)
            return 0f;

        if (x <= floor)
            return 1f;

        var u = (x - floor) / (1f - floor);

        if (benches < 1f)
            return 1f - Smooth01(u);

        var q = u * benches;
        var k = MathF.Min(MathF.Floor(q), benches - 1f);
        return 1f - (k + Smooth01((q - k) / riser)) / benches;
    }

    private static void Canyon(PlanetKindProgram p, int layer, int lod, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var halfWidth = p.Params[pb];
        var depth = p.Params[pb + 1];
        var floor = p.Params[pb + 2];
        var benches = p.Params[pb + 3];
        var gMin = p.Params[pb + 4];
        var mean = p.Params[pb + 5];
        var at = p.At(lod, layer);
        var riser = p.RiserBand[at];
        var weight = p.LodWeight[at];
        var src = p.SrcA[layer];
        ReadOnlySpan<float> n = r.Row(src), gx = r.Row(src + 1), gy = r.Row(src + 2), gz = r.Row(src + 3);
        var gate = p.Gate[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.Gate[layer]);
        var h = r.Row(p.Dst[layer]);
        var invRadius = 1.0 / p.Radius;

        for (var i = 0; i < h.Length; i++)
        {
            var m = gate.IsEmpty ? 1f : gate[i];

            if (m == 0f)
                continue;

            var radial = (float)((gx[i] * r.X[i] + gy[i] * r.Y[i] + gz[i] * r.Z[i]) * invRadius);
            var g = MathF.Max(MathF.Sqrt(MathF.Max(gx[i] * gx[i] + gy[i] * gy[i] + gz[i] * gz[i] - radial * radial, 0f)), gMin);
            var profile = CanyonProfile(MathF.Abs(n[i]) / g / halfWidth, floor, benches, riser);
            h[i] -= depth * m * (weight * profile + (1f - weight) * mean);
        }
    }

    private static void Mask(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var from = p.Params[pb];
        var inv = p.Params[pb + 1];
        var dst = r.Row(p.Dst[layer]);

        if (p.Mode[layer] == LayerMode.MaskLat)
        {
            var invRadius = 1.0 / p.Radius;

            for (var i = 0; i < dst.Length; i++)
            {
                var sinLat = (float)Math.Abs((r.X[i] * p.AxisX + r.Y[i] * p.AxisY + r.Z[i] * p.AxisZ) * invRadius);
                dst[i] = Smooth01((sinLat - from) * inv);
            }

            return;
        }

        ReadOnlySpan<float> src = r.Row(p.SrcA[layer]);

        for (var i = 0; i < dst.Length; i++)
            dst[i] = Smooth01((src[i] - from) * inv);
    }

    private static void Combine(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var dst = r.Row(p.Dst[layer]);
        var a = p.SrcA[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.SrcA[layer]);
        var b = p.SrcB[layer] == None ? default : (ReadOnlySpan<float>)r.Row(p.SrcB[layer]);

        switch (p.Mode[layer])
        {
            case LayerMode.Mul:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = a[i] * b[i];

                break;
            case LayerMode.Min:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = MathF.Min(a[i], b[i]);

                break;
            case LayerMode.Max:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = MathF.Max(a[i], b[i]);

                break;
            case LayerMode.Invert:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] = 1f - a[i];

                break;
            case LayerMode.AddMul when b.IsEmpty:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] += a[i];

                break;
            case LayerMode.AddMul:
                for (var i = 0; i < dst.Length; i++)
                    dst[i] += a[i] * b[i];

                break;
            case LayerMode.AddConst:
                AddConstant(dst, p.Params[p.ParamBase[layer]]);
                break;
            case LayerMode.Copy:
                a.CopyTo(dst);
                break;
        }
    }

    /// <summary>The inland floor (D30): past the coastal ramp the ground stands above the protected band; before it the floor drops away.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float SeaFloor(float c, float floor, float inlandStart) => floor - MathF.Max(0f, inlandStart - c) * FloorDrop;

    private static void SeaClamp(PlanetKindProgram p, int layer, ref RowSet r)
    {
        var pb = p.ParamBase[layer];
        var floor = p.Params[pb];
        var inlandStart = p.Params[pb + 1];
        ReadOnlySpan<float> c = r.Row(p.SrcA[layer]);
        var h = r.Row(p.Dst[layer]);

        for (var i = 0; i < h.Length; i++)
            h[i] = MathF.Max(h[i], SeaFloor(c[i], floor, inlandStart));
    }

    private const float ShelfDepth = 150f;
    private const float ShelfWidth = 0.03f;
    private const float AbyssWidth = 0.3f;
    private const float LandRise = 3000f;
    private const float MountainStart = 0.1f;
    private const float MountainRamp = 0.3f;
    private const float CoastDetail = 0.15f;
    private const float DetailRamp = 0.15f;
    internal const float LegacyInlandStart = 0.06f;

    /// <summary>The v1 compose (compat "v1"): continent shape, ridges on the interiors, detail, the inland clamp and a hard relief clamp.</summary>
    private static void Legacy(PlanetKindProgram p, int layer, ref RowSet r)
    {
        ReadOnlySpan<float> k = p.Params.AsSpan(p.ParamBase[layer], LegacyParams);
        ReadOnlySpan<float> continent = r.Row(p.SrcA[layer]), ridge = r.Row(p.SrcB[layer]), detail = r.Row(p.Scale[layer]);
        var h = r.Row(p.Dst[layer]);

        for (var i = 0; i < h.Length; i++)
        {
            var c = continent[i] + k[0];
            var v = LegacyShape(c, k) + LegacyMountains(c, k) * ridge[i] + LegacyDetail(c) * detail[i];
            v = MathF.Max(v, SeaFloor(c, k[4], LegacyInlandStart));
            h[i] = Math.Clamp(v, k[5], k[6]);
        }
    }

    /// <summary>Legacy params: land bias, mountain height, sea, abyss, floor, min, max.</summary>
    internal const int LegacyParams = 7;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LegacyShape(float c, ReadOnlySpan<float> k)
    {
        if (c >= 0f)
            return k[2] + LandRise * c;

        var x = -c;
        return k[2] - ShelfDepth * Smooth01(x / ShelfWidth) - (k[2] - ShelfDepth - k[3]) * Smooth01((x - ShelfWidth) / AbyssWidth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LegacyMountains(float c, ReadOnlySpan<float> k) => k[1] * Smooth01((c - MountainStart) / MountainRamp);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LegacyDetail(float c) => CoastDetail + (1f - CoastDetail) * Smooth01(c / DetailRamp);
}
