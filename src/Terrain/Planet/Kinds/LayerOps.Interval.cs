namespace BrutalGame.Terrain;

/// <summary>
/// Interval evaluation of a program over a cap of directions (planet-kinds.md §3): every op maps channel intervals to a
/// channel interval that holds for every point of the cap and every LOD in [minLod, maxLod] (band limits, fades and
/// class weights included). Noise octaves use the lattice noise's Lipschitz bound about the cap's centre.
/// </summary>
internal static partial class LayerOps
{
    private const float BoundsSlack = 0.05f;
    private const float RelativeSlack = 2e-5f;

    /// <param name="rho">Cap radius on the datum sphere (m).</param>
    /// <param name="layers">Run only the first so many layers (the compiler's worst-case sum before the knee).</param>
    public static void Bounds(PlanetKindProgram p, double qx, double qy, double qz, double rho, int minLod, int maxLod, Span<float> lo, Span<float> hi, int layers)
    {
        lo.Clear();
        hi.Clear();

        for (var layer = 0; layer < layers; layer++)
        {
            switch (p.Op[layer])
            {
                case LayerOp.Fbm:
                case LayerOp.Ridged:
                case LayerOp.Billow:
                case LayerOp.Eroded:
                case LayerOp.Warp:
                    NoiseBounds(p, layer, qx, qy, qz, rho, minLod, maxLod, lo, hi);
                    break;
                case LayerOp.Craters:
                case LayerOp.Ergs:
                    Stamps.Bounds(p, layer, qx, qy, qz, rho, minLod, maxLod, lo, hi);
                    break;
                case LayerOp.Curve:
                    CurveBounds(p, layer, lo, hi);
                    break;
                case LayerOp.Terrace:
                    TerraceBounds(p, layer, minLod, maxLod, lo, hi);
                    break;
                case LayerOp.Canyon:
                    CanyonBounds(p, layer, maxLod, lo, hi);
                    break;
                case LayerOp.Mask:
                    MaskBounds(p, layer, qx, qy, qz, rho, lo, hi);
                    break;
                case LayerOp.Combine:
                    CombineBounds(p, layer, lo, hi);
                    break;
                case LayerOp.SeaClamp:
                {
                    var pb = p.ParamBase[layer];
                    var d = p.Dst[layer];
                    lo[d] = MathF.Max(lo[d], SeaFloor(lo[p.SrcA[layer]], p.Params[pb], p.Params[pb + 1]));
                    hi[d] = MathF.Max(hi[d], SeaFloor(hi[p.SrcA[layer]], p.Params[pb], p.Params[pb + 1]));
                    break;
                }
                case LayerOp.Legacy:
                    LegacyBounds(p, layer, lo, hi);
                    break;
            }
        }
    }

    /// <summary>Outward rounding allowance for float sums.</summary>
    public static void Widen(ref float lo, ref float hi)
    {
        lo -= BoundsSlack + RelativeSlack * MathF.Abs(lo);
        hi += BoundsSlack + RelativeSlack * MathF.Abs(hi);
    }

    private static void OctaveInterval(PlanetKindProgram p, int octave, double qx, double qy, double qz, double reach, out float nLo, out float nHi)
    {
        var f = p.Frequency[octave];
        var spread = (float)(Noise.GradientLipschitz * f * reach);
        nLo = -1f;
        nHi = 1f;

        if (spread >= 1f)
            return;

        var n = Math.Clamp(Noise.Gradient3(qx * f + p.OffsetX[octave], qy * f + p.OffsetY[octave], qz * f + p.OffsetZ[octave], p.Seed[octave]), -1f, 1f);
        nLo = MathF.Max(n - spread, -1f);
        nHi = MathF.Min(n + spread, 1f);
    }

    private static void AbsInterval(float nLo, float nHi, out float absLo, out float absHi)
    {
        absLo = nLo <= 0f && nHi >= 0f ? 0f : MathF.Min(MathF.Abs(nLo), MathF.Abs(nHi));
        absHi = MathF.Max(MathF.Abs(nLo), MathF.Abs(nHi));
    }

    private static void NoiseBounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double rho, int minLod, int maxLod, Span<float> lo, Span<float> hi)
    {
        var dst = p.Dst[layer];
        var mode = p.Mode[layer];
        var op = p.Op[layer];
        var fields = op == LayerOp.Fbm && (mode & LayerMode.Gradient) != 0 ? 4 : p.Fields(layer);

        for (var c = 0; c < fields; c++)
            lo[dst + c] = hi[dst + c] = 0f;

        if (p.Gate[layer] != None && hi[p.Gate[layer]] <= 0f)
            return;

        int endMin = p.OctaveEnd[p.At(minLod, layer)], endMax = p.OctaveEnd[p.At(maxLod, layer)];
        int first = p.FirstOctave[layer], count = p.OctaveCount[layer];
        var warp = p.Warp[layer];
        var reach = rho;

        if (warp != None)
        {
            double hx = 0.5 * ((double)hi[warp] - lo[warp]), hy = 0.5 * ((double)hi[warp + 1] - lo[warp + 1]), hz = 0.5 * ((double)hi[warp + 2] - lo[warp + 2]);
            qx += 0.5 * ((double)hi[warp] + lo[warp]);
            qy += 0.5 * ((double)hi[warp + 1] + lo[warp + 1]);
            qz += 0.5 * ((double)hi[warp + 2] + lo[warp + 2]);
            reach += Math.Sqrt(hx * hx + hy * hy + hz * hz) * 1.0001 + 1e-3;
        }

        float sumLo = 0f, sumHi = 0f;

        switch (op)
        {
            case LayerOp.Warp:
            {
                for (var c = 0; c < 3; c++)
                {
                    sumLo = sumHi = 0f;

                    for (var o = 0; o < endMin; o++)
                    {
                        OctaveInterval(p, first + 3 * o + c, qx, qy, qz, reach, out var nLo, out var nHi);
                        Accumulate(p.Amplitude[first + 3 * o + c] * nLo, p.Amplitude[first + 3 * o + c] * nHi, o >= endMax, 0f, ref sumLo, ref sumHi);
                    }

                    lo[dst + c] = sumLo;
                    hi[dst + c] = sumHi;
                }

                return;
            }
            case LayerOp.Ridged when (mode & LayerMode.ShapedV1) != 0:
            {
                for (var o = 0; o < endMin; o++)
                {
                    OctaveInterval(p, first + o, qx, qy, qz, reach, out var nLo, out var nHi);
                    AbsInterval(nLo, nHi, out var absLo, out var absHi);
                    var a = p.Amplitude[first + o];
                    Accumulate(a * (1f - absHi) * (1f - absHi), a * (1f - absLo) * (1f - absLo), o >= endMax, 0f, ref sumLo, ref sumHi);
                }

                break;
            }
            case LayerOp.Fbm:
            {
                for (var o = 0; o < endMin; o++)
                {
                    OctaveInterval(p, first + o, qx, qy, qz, reach, out var nLo, out var nHi);
                    Accumulate(p.Amplitude[first + o] * nLo, p.Amplitude[first + o] * nHi, o >= endMax, 0f, ref sumLo, ref sumHi);
                }

                break;
            }
            case LayerOp.Ridged:
            {
                var gain = p.Params[p.ParamBase[layer]];
                float wLo = 1f, wHi = 1f;

                for (var o = 0; o < count; o++)
                {
                    var mean = p.OctaveMean[first + o];

                    if (o >= endMin)
                    {
                        sumLo += mean;
                        sumHi += mean;
                        continue;
                    }

                    OctaveInterval(p, first + o, qx, qy, qz, reach, out var nLo, out var nHi);
                    AbsInterval(nLo, nHi, out var absLo, out var absHi);
                    var sLo = (1f - absHi) * (1f - absHi) * wLo;
                    var sHi = (1f - absLo) * (1f - absLo) * wHi;
                    var a = p.Amplitude[first + o];
                    Accumulate(a * sLo, a * sHi, o >= endMax, mean, ref sumLo, ref sumHi);
                    wLo = MathF.Min(sLo * gain, 1f);
                    wHi = MathF.Min(sHi * gain, 1f);
                }

                break;
            }
            case LayerOp.Billow:
            {
                for (var o = 0; o < count; o++)
                {
                    var mean = p.OctaveMean[first + o];

                    if (o >= endMin)
                    {
                        sumLo += mean;
                        sumHi += mean;
                        continue;
                    }

                    OctaveInterval(p, first + o, qx, qy, qz, reach, out var nLo, out var nHi);
                    AbsInterval(nLo, nHi, out var absLo, out var absHi);
                    var a = p.Amplitude[first + o];
                    Accumulate(a * (2f * absLo - 1f), a * (2f * absHi - 1f), o >= endMax, mean, ref sumLo, ref sumHi);
                }

                break;
            }
            case LayerOp.Eroded:
            {
                for (var o = 0; o < endMin; o++)
                {
                    OctaveInterval(p, first + o, qx, qy, qz, reach, out var nLo, out var nHi);
                    var a = p.Amplitude[first + o];
                    sumLo += a * MathF.Min(nLo, 0f);
                    sumHi += a * MathF.Max(nHi, 0f);
                }

                break;
            }
        }

        lo[dst] = sumLo;
        hi[dst] = sumHi;
    }

    /// <summary>Adds an octave's interval; one a LOD in range may skip also takes the value it leaves behind (0, or the shaped mean).</summary>
    private static void Accumulate(float cLo, float cHi, bool maySkip, float skipped, ref float sumLo, ref float sumHi)
    {
        if (maySkip)
        {
            cLo = MathF.Min(cLo, skipped);
            cHi = MathF.Max(cHi, skipped);
        }

        sumLo += cLo;
        sumHi += cHi;
    }

    private static void CurveBounds(PlanetKindProgram p, int layer, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var mode = p.Mode[layer];
        var src = p.SrcA[layer];
        var dst = p.Dst[layer];
        float sLo = lo[src], sHi = hi[src];

        if ((mode & LayerMode.CurveKnee) != 0)
        {
            ReadOnlySpan<float> knee = p.Params.AsSpan(pb, 8);
            lo[dst] = KneeAt(knee, sLo);
            hi[dst] = KneeAt(knee, sHi);
            return;
        }

        var count = (int)p.Params[pb];
        ReadOnlySpan<float> xs = p.Params.AsSpan(pb + 1, count);
        ReadOnlySpan<float> ys = p.Params.AsSpan(pb + 1 + count, count);
        var ease = (mode & LayerMode.CurveEase) != 0;
        var a = CurveAt(xs, ys, ease, sLo);
        var b = CurveAt(xs, ys, ease, sHi);
        float cLo = MathF.Min(a, b), cHi = MathF.Max(a, b);

        for (var k = 0; k < count; k++)
        {
            if (xs[k] <= sLo || xs[k] >= sHi)
                continue;

            cLo = MathF.Min(cLo, ys[k]);
            cHi = MathF.Max(cHi, ys[k]);
        }

        if ((mode & LayerMode.CurveAdd) != 0)
        {
            lo[dst] += cLo;
            hi[dst] += cHi;
            return;
        }

        lo[dst] = cLo;
        hi[dst] = cHi;
    }

    private static void TerraceBounds(PlanetKindProgram p, int layer, int minLod, int maxLod, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var count = (int)p.Params[pb];
        var beta = p.Params[pb + 1];
        ReadOnlySpan<float> levels = p.Params.AsSpan(pb + 2, count + 1);
        var d = p.Dst[layer];
        float bandA = p.RiserBand[p.At(minLod, layer)], bandB = p.RiserBand[p.At(maxLod, layer)];
        float mLo = 1f, mHi = 1f;

        if (p.Gate[layer] != None)
        {
            mLo = lo[p.Gate[layer]];
            mHi = hi[p.Gate[layer]];
        }

        if (mHi <= 0f)
            return;

        float vLo = lo[d], vHi = hi[d];

        if (p.SrcB[layer] == None)
        {
            // (1 − m)·v + m·T(v) is monotone in v, linear in m, and T is monotone in the band at a fixed v.
            float a = vLo + TerraceAt(levels, beta, bandA, vLo), b = vLo + TerraceAt(levels, beta, bandB, vLo);
            float c = vHi + TerraceAt(levels, beta, bandA, vHi), e = vHi + TerraceAt(levels, beta, bandB, vHi);
            var tLo = MathF.Min(a, b);
            var tHi = MathF.Max(c, e);
            lo[d] = MathF.Min(vLo + mLo * (tLo - vLo), vLo + mHi * (tLo - vLo));
            hi[d] = MathF.Max(vHi + mLo * (tHi - vHi), vHi + mHi * (tHi - vHi));
            return;
        }

        float uLo = vLo + lo[p.SrcB[layer]], uHi = vHi + hi[p.SrcB[layer]];
        var maxStep = 0f;

        for (var k = 0; k < count; k++)
            maxStep = MathF.Max(maxStep, levels[k + 1] - levels[k]);

        var dLo = MathF.Min(uLo + TerraceAt(levels, beta, bandA, uLo), uLo + TerraceAt(levels, beta, bandB, uLo)) - uHi;
        var dHi = MathF.Max(uHi + TerraceAt(levels, beta, bandA, uHi), uHi + TerraceAt(levels, beta, bandB, uHi)) - uLo;
        dLo = Math.Clamp(dLo, -0.5f * maxStep, 0f);
        dHi = Math.Clamp(dHi, 0f, 0.5f * maxStep);
        lo[d] = vLo + mHi * dLo;
        hi[d] = vHi + mHi * dHi;
    }

    /// <summary>Canyon params: half-width, depth, floor, benches, gMin, mean profile, the field's gradient bound.</summary>
    internal const int CanyonParams = 7;

    private static void CanyonBounds(PlanetKindProgram p, int layer, int maxLod, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var src = p.SrcA[layer];
        AbsInterval(lo[src], hi[src], out var absLo, out _);
        var mHi = p.Gate[layer] == None ? 1f : hi[p.Gate[layer]];
        var reach = absLo < p.Params[pb] * p.Params[pb + 6] || p.LodWeight[p.At(maxLod, layer)] < 1f;

        if (reach && mHi > 0f)
            lo[p.Dst[layer]] -= p.Params[pb + 1] * mHi;
    }

    private static void MaskBounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double rho, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var from = p.Params[pb];
        var inv = p.Params[pb + 1];
        var dst = p.Dst[layer];
        float sLo, sHi;

        if (p.Mode[layer] == LayerMode.MaskLat)
        {
            var s = (float)((qx * p.AxisX + qy * p.AxisY + qz * p.AxisZ) / p.Radius);
            var spread = (float)(rho / p.Radius) + 1e-6f;
            AbsInterval(MathF.Max(s - spread, -1f), MathF.Min(s + spread, 1f), out sLo, out sHi);
        }
        else
        {
            sLo = lo[p.SrcA[layer]];
            sHi = hi[p.SrcA[layer]];
        }

        var a = Smooth01((sLo - from) * inv);
        var b = Smooth01((sHi - from) * inv);
        lo[dst] = MathF.Min(a, b);
        hi[dst] = MathF.Max(a, b);
    }

    private static void Product(float aLo, float aHi, float bLo, float bHi, out float lo, out float hi)
    {
        float p0 = aLo * bLo, p1 = aLo * bHi, p2 = aHi * bLo, p3 = aHi * bHi;
        lo = MathF.Min(MathF.Min(p0, p1), MathF.Min(p2, p3));
        hi = MathF.Max(MathF.Max(p0, p1), MathF.Max(p2, p3));
    }

    private static void CombineBounds(PlanetKindProgram p, int layer, Span<float> lo, Span<float> hi)
    {
        var d = p.Dst[layer];
        int a = p.SrcA[layer], b = p.SrcB[layer];

        switch (p.Mode[layer])
        {
            case LayerMode.Mul:
                Product(lo[a], hi[a], lo[b], hi[b], out lo[d], out hi[d]);
                break;
            case LayerMode.Min:
                lo[d] = MathF.Min(lo[a], lo[b]);
                hi[d] = MathF.Min(hi[a], hi[b]);
                break;
            case LayerMode.Max:
                lo[d] = MathF.Max(lo[a], lo[b]);
                hi[d] = MathF.Max(hi[a], hi[b]);
                break;
            case LayerMode.Invert:
            {
                float l = 1f - hi[a], h = 1f - lo[a];
                lo[d] = l;
                hi[d] = h;
                break;
            }
            case LayerMode.AddMul when b == None:
                lo[d] += lo[a];
                hi[d] += hi[a];
                break;
            case LayerMode.AddMul:
            {
                Product(lo[a], hi[a], lo[b], hi[b], out var l, out var h);
                lo[d] += l;
                hi[d] += h;
                break;
            }
            case LayerMode.AddConst:
                lo[d] += p.Params[p.ParamBase[layer]];
                hi[d] += p.Params[p.ParamBase[layer]];
                break;
            case LayerMode.Copy:
                lo[d] = lo[a];
                hi[d] = hi[a];
                break;
        }
    }

    private static void LegacyBounds(PlanetKindProgram p, int layer, Span<float> lo, Span<float> hi)
    {
        ReadOnlySpan<float> k = p.Params.AsSpan(p.ParamBase[layer], LegacyParams);
        int c = p.SrcA[layer], r = p.SrcB[layer], t = p.Scale[layer], d = p.Dst[layer];
        float cLo = lo[c] + k[0], cHi = hi[c] + k[0];
        float sLo = LegacyDetail(cLo), sHi = LegacyDetail(cHi);
        var detailLo = MathF.Min(sLo * lo[t], sHi * lo[t]);
        var detailHi = MathF.Max(sLo * hi[t], sHi * hi[t]);
        var l = LegacyShape(cLo, k) + LegacyMountains(cLo, k) * lo[r] + detailLo;
        var h = LegacyShape(cHi, k) + LegacyMountains(cHi, k) * hi[r] + detailHi;
        l = MathF.Max(l, SeaFloor(cLo, k[4], LegacyInlandStart));
        h = MathF.Max(h, SeaFloor(cHi, k[4], LegacyInlandStart));
        lo[d] = Math.Clamp(l, k[5], k[6]);
        hi[d] = Math.Clamp(h, k[5], k[6]);
    }
}
