using System.Runtime.CompilerServices;

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
    private const double SlopeSlack = 1e-4;
    private const int VectorOctaves = 3;

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
                    CanyonBounds(p, layer, minLod, maxLod, lo, hi);
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

    /// <summary>
    /// An octave over the reach about q: its value there ± the least of the noise's steepest slope × the distance and
    /// its slope there × the distance + half the slope's own Lipschitz bound × the distance squared.
    /// </summary>
    /// <param name="centre">The layer's octave values at q from <paramref name="first"/>, then their three derivative components; or empty to evaluate this one.</param>
    private static void OctaveInterval(PlanetKindProgram p, int octave, ReadOnlySpan<float> centre, int first, double qx, double qy, double qz, double reach, out float nLo, out float nHi)
    {
        var f = p.Frequency[octave];
        var across = f * reach;
        nLo = -1f;
        nHi = 1f;

        if (Noise.GradientLipschitz * across >= 1.0)
            return;

        float n, gx, gy, gz;

        if (centre.IsEmpty)
        {
            n = Math.Clamp(Noise.Gradient3(qx * f + p.OffsetX[octave], qy * f + p.OffsetY[octave], qz * f + p.OffsetZ[octave], p.Seed[octave], out gx, out gy, out gz), -1f, 1f);
        }
        else
        {
            var count = centre.Length / 4;
            var k = octave - first;
            (n, gx, gy, gz) = (centre[k], centre[count + k], centre[2 * count + k], centre[3 * count + k]);
        }

        var spread = (float)Spread(across, gx, gy, gz);
        nLo = MathF.Max(n - spread, -1f);
        nHi = MathF.Min(n + spread, 1f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Spread(double across, float gx, float gy, float gz)
    {
        var slope = Math.Sqrt((double)gx * gx + (double)gy * gy + (double)gz * gz);
        return Math.Min(Noise.GradientLipschitz * across, (slope + 0.5 * Noise.SlopeLipschitz * across) * across * (1.0 + SlopeSlack));
    }

    /// <summary>The layer's first octaves and their derivatives at one point, eight at a time; empty without AVX2.</summary>
    private static ReadOnlySpan<float> Centres(PlanetKindProgram p, int first, int count, double qx, double qy, double qz, Span<float> buffer)
    {
        var all = buffer[..(4 * count)];

        return count > 0 && NoiseOctavesSimd(qx, qy, qz, p.Frequency.AsSpan(first, count), p.OffsetX.AsSpan(first, count), p.OffsetY.AsSpan(first, count), p.OffsetZ.AsSpan(first, count), p.Seed.AsSpan(first, count),
            all[..count], all.Slice(count, count), all.Slice(2 * count, count), all.Slice(3 * count, count))
            ? all
            : default;
    }

    private static void AbsInterval(float nLo, float nHi, out float absLo, out float absHi)
    {
        absLo = nLo <= 0f && nHi >= 0f ? 0f : MathF.Min(MathF.Abs(nLo), MathF.Abs(nHi));
        absHi = MathF.Max(MathF.Abs(nLo), MathF.Abs(nHi));
    }

    [SkipLocalsInit]
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
        double upX = qx, upY = qy, upZ = qz;

        if (warp != None)
        {
            double hx = 0.5 * ((double)hi[warp] - lo[warp]), hy = 0.5 * ((double)hi[warp + 1] - lo[warp + 1]), hz = 0.5 * ((double)hi[warp + 2] - lo[warp + 2]);
            qx += 0.5 * ((double)hi[warp] + lo[warp]);
            qy += 0.5 * ((double)hi[warp + 1] + lo[warp + 1]);
            qz += 0.5 * ((double)hi[warp + 2] + lo[warp + 2]);
            reach += Math.Sqrt(hx * hx + hy * hy + hz * hz) * 1.0001 + 1e-3;
        }

        float sumLo = 0f, sumHi = 0f;
        Span<float> buffer = stackalloc float[PlanetKindProgram.OctaveCache];
        var near = 0;

        while (near < endMin && Noise.GradientLipschitz * p.Frequency[first + near * p.Fields(layer)] * reach < 1.0)
            near++;

        // Octaves too fine to tell across the reach need no value; a couple are as quick one by one.
        var centre = fields == 4 || near < VectorOctaves ? default : Centres(p, first, near * p.Fields(layer), qx, qy, qz, buffer);

        switch (op)
        {
            case LayerOp.Warp:
            {
                for (var c = 0; c < 3; c++)
                {
                    sumLo = sumHi = 0f;

                    for (var o = 0; o < endMin; o++)
                    {
                        OctaveInterval(p, first + 3 * o + c, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
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
                    OctaveInterval(p, first + o, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
                    AbsInterval(nLo, nHi, out var absLo, out var absHi);
                    var a = p.Amplitude[first + o];
                    Accumulate(a * (1f - absHi) * (1f - absHi), a * (1f - absLo) * (1f - absLo), o >= endMax, 0f, ref sumLo, ref sumHi);
                }

                break;
            }
            case LayerOp.Fbm when fields == 4:
            {
                SlopeBounds(p, layer, qx, qy, qz, upX, upY, upZ, reach, rho, endMin, endMax, out sumLo, out sumHi, out lo[dst + 1], out hi[dst + 1]);
                break;
            }
            case LayerOp.Fbm:
            {
                for (var o = 0; o < endMin; o++)
                {
                    OctaveInterval(p, first + o, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
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

                    OctaveInterval(p, first + o, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
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

                    OctaveInterval(p, first + o, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
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
                    OctaveInterval(p, first + o, centre, first, qx, qy, qz, reach, out var nLo, out var nHi);
                    var a = p.Amplitude[first + o];
                    var least = p.OctaveMean[first + o];
                    Accumulate(a * MathF.Min(nLo * least, nLo), a * MathF.Max(nHi * least, nHi), o >= endMax, 0f, ref sumLo, ref sumHi);
                }

                break;
            }
        }

        lo[dst] = sumLo;
        hi[dst] = sumHi;
    }

    /// <summary>
    /// A gradient field over the cap: its value's interval and that of its tangential slope |G|. An octave every LOD
    /// keeps, whose gradient cannot turn far across the reach, counts at the centre ± its slope's Lipschitz bound; any
    /// other may add up to its steepest slope. The tangent plane tilts by ρ / R across the cap.
    /// </summary>
    private static void SlopeBounds(PlanetKindProgram p, int layer, double qx, double qy, double qz, double upX, double upY, double upZ, double reach, double rho, int endMin, int endMax,
        out float sumLo, out float sumHi, out float slopeLo, out float slopeHi)
    {
        var first = p.FirstOctave[layer];
        double gx = 0.0, gy = 0.0, gz = 0.0, turn = 0.0, steepest = 0.0;
        sumLo = sumHi = 0f;

        for (var o = 0; o < endMin; o++)
        {
            var f = p.Frequency[first + o];
            var a = p.Amplitude[first + o];
            var bend = Noise.SlopeLipschitz * f * reach;
            steepest += a * f * Noise.GradientLipschitz;
            float nLo = -1f, nHi = 1f;

            if (Noise.GradientLipschitz * f * reach < 1.0)
            {
                var n = Math.Clamp(Noise.Gradient3(qx * f + p.OffsetX[first + o], qy * f + p.OffsetY[first + o], qz * f + p.OffsetZ[first + o], p.Seed[first + o], out var dx, out var dy, out var dz), -1f, 1f);
                var spread = (float)Spread(f * reach, dx, dy, dz);
                nLo = MathF.Max(n - spread, -1f);
                nHi = MathF.Min(n + spread, 1f);

                if (o < endMax && bend < Noise.GradientLipschitz)
                {
                    gx += a * f * dx;
                    gy += a * f * dy;
                    gz += a * f * dz;
                    turn += a * f * bend;
                    Accumulate(a * nLo, a * nHi, false, 0f, ref sumLo, ref sumHi);
                    continue;
                }
            }

            turn += a * f * Noise.GradientLipschitz;
            Accumulate(a * nLo, a * nHi, o >= endMax, 0f, ref sumLo, ref sumHi);
        }

        var whole = Math.Sqrt(gx * gx + gy * gy + gz * gz);
        var radial = (gx * upX + gy * upY + gz * upZ) / Math.Sqrt(upX * upX + upY * upY + upZ * upZ);
        var tangential = Math.Sqrt(Math.Max(whole * whole - radial * radial, 0.0));
        turn += 2.0 * whole * rho / p.Radius;
        slopeLo = (float)(Math.Max(tangential - turn, 0.0) * (1.0 - SlopeSlack));
        slopeHi = (float)(Math.Min(tangential + turn, steepest) * (1.0 + SlopeSlack));
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

        // h + m·(T(u) − u) with u = h + s is (1 − m)·h + m·(T(u) − s), T monotone in u and in the band; the offset is also within half a step.
        float sLo = lo[p.SrcB[layer]], sHi = hi[p.SrcB[layer]];
        float uLo = vLo + sLo, uHi = vHi + sHi;
        var maxStep = 0f;

        for (var k = 0; k < count; k++)
            maxStep = MathF.Max(maxStep, levels[k + 1] - levels[k]);

        var yLo = MathF.Min(uLo + TerraceAt(levels, beta, bandA, uLo), uLo + TerraceAt(levels, beta, bandB, uLo)) - sHi;
        var yHi = MathF.Max(uHi + TerraceAt(levels, beta, bandA, uHi), uHi + TerraceAt(levels, beta, bandB, uHi)) - sLo;
        var least = MathF.Min(vLo + mLo * (yLo - vLo), vLo + mHi * (yLo - vLo));
        var most = MathF.Max(vHi + mLo * (yHi - vHi), vHi + mHi * (yHi - vHi));
        lo[d] = MathF.Max(least, vLo - 0.5f * maxStep * mHi);
        hi[d] = MathF.Min(most, vHi + 0.5f * maxStep * mHi);
    }

    /// <summary>Canyon params: half-width, depth, floor, benches, gMin, mean profile, the field's gradient bound.</summary>
    internal const int CanyonParams = 7;

    /// <summary>
    /// The wall profile falls with |n| / (g·halfWidth) and rises with the riser share, and the cut is linear in the
    /// LOD's fade weight: the deepest cut takes the least |n| over the steepest slope, the shallowest the reverse.
    /// </summary>
    private static void CanyonBounds(PlanetKindProgram p, int layer, int minLod, int maxLod, Span<float> lo, Span<float> hi)
    {
        var pb = p.ParamBase[layer];
        var src = p.SrcA[layer];
        var dst = p.Dst[layer];
        float halfWidth = p.Params[pb], depth = p.Params[pb + 1], floor = p.Params[pb + 2], benches = p.Params[pb + 3], gMin = p.Params[pb + 4], mean = p.Params[pb + 5];
        float mLo = 1f, mHi = 1f;

        if (p.Gate[layer] != None)
        {
            mLo = lo[p.Gate[layer]];
            mHi = hi[p.Gate[layer]];
        }

        if (mHi <= 0f)
            return;

        int fine = p.At(minLod, layer), coarse = p.At(maxLod, layer);
        AbsInterval(lo[src], hi[src], out var absLo, out var absHi);
        float gLo = MathF.Max(lo[src + 1], gMin), gHi = MathF.Max(hi[src + 1], gMin);
        var most = CanyonProfile(absLo / gHi / halfWidth * (1f - RelativeSlack), floor, benches, MathF.Max(p.RiserBand[fine], p.RiserBand[coarse]));
        var least = CanyonProfile(absHi / gLo / halfWidth * (1f + RelativeSlack), floor, benches, MathF.Min(p.RiserBand[fine], p.RiserBand[coarse]));
        float wA = p.LodWeight[fine], wB = p.LodWeight[coarse];
        var deepest = MathF.Max(wA * most + (1f - wA) * mean, wB * most + (1f - wB) * mean);
        var shallowest = MathF.Min(wA * least + (1f - wA) * mean, wB * least + (1f - wB) * mean);
        lo[dst] -= depth * mHi * deepest;
        hi[dst] -= depth * MathF.Max(mLo, 0f) * shallowest;
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
