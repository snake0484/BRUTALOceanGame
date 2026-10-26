using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// AVX2 version of both passes, 8 cells of a row at a time. <see cref="TransferSimd"/> performs exactly the scalar
/// <see cref="Transfer"/> operations per lane (same IEEE ops, same order, no FMA), so both paths give the same bits.
/// </summary>
public static partial class WaterFlow
{
    private const int Lanes = 8;
    private const int Chunks = S / Lanes;

    [SkipLocalsInit]
    private static unsafe void ComputeOutflowScaleSimd(WaterBlock block)
    {
        var yCache = stackalloc float[S];
        var zCache = stackalloc float[S * S];
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        var eps = Vector256.Create(SnapEpsilon);
        var reservoirBit = Vector256.Create((int)ReservoirBit);

        fixed (float* w = block.Water)
        fixed (float* os = block.OutScale)
        fixed (byte* fl = block.Flags)
        fixed (byte* wet = block.WetRows)
        {
            for (var z = 0; z < S; z++)
            {
                for (var y = 0; y < S; y++)
                {
                    var row = ChunkConstants.Index(0, y, z);
                    var zRow = zCache + y * S;

                    if (RowQuiet(wet, y, z))
                    {
                        new Span<float>(os + row, S).Fill(1f);
                        new Span<float>(yCache, S).Clear();
                        new Span<float>(zRow, S).Clear();
                        continue;
                    }

                    var seam = y == 0;
                    var seamBack = seam && z == 0 ? AllLanes() : zero;
                    var seamFront = seam && z == S - 1 ? AllLanes() : zero;

                    for (var c = 0; c < Chunks; c++)
                    {
                        var x = c * Lanes;
                        var i = row + x;
                        var left = TransferSimd(w, fl, i - 1, 1, seam && c == 0 ? FirstLane() : zero, false);
                        var right = TransferSimd(w, fl, i, 1, seam && c == Chunks - 1 ? LastLane() : zero, false);
                        var down = y == 0 ? TransferSimd(w, fl, i - P, P, zero, true) : Vector256.Load(yCache + x);
                        var up = TransferSimd(w, fl, i, P, zero, true);
                        var back = z == 0 ? TransferSimd(w, fl, i - A, A, seamBack, false) : Vector256.Load(zRow + x);
                        var front = TransferSimd(w, fl, i, A, seamFront, false);
                        up.Store(yCache + x);
                        front.Store(zRow + x);

                        var outflow = Vector256.MaxNative(-left, zero) + Vector256.MaxNative(right, zero) + Vector256.MaxNative(-down, zero)
                            + Vector256.MaxNative(up, zero) + Vector256.MaxNative(-back, zero) + Vector256.MaxNative(front, zero);
                        var wc = Vector256.Load(w + i);
                        var reservoir = Vector256.Equals(Avx2.ConvertToVector256Int32(fl + i) & reservoirBit, reservoirBit).AsSingle();
                        var limit = Vector256.AndNot(
                            Vector256.GreaterThan(outflow, zero) & Vector256.LessThan(wc - outflow, eps), reservoir);
                        Vector256.ConditionalSelect(limit, wc / outflow, one).Store(os + i);
                    }
                }
            }
        }
    }

    [SkipLocalsInit]
    private static unsafe void ApplyFlowsSimd(WaterBlock block)
    {
        var yCache = stackalloc float[S];
        var zCache = stackalloc float[S * S];
        var zero = Vector256<float>.Zero;
        var eps = Vector256.Create(SnapEpsilon);
        var borderEps = Vector256.Create(BorderEpsilon);
        var solidBit = Vector256.Create((int)SolidBit);
        var reservoirBit = Vector256.Create((int)ReservoirBit);
        var maxDelta = zero;
        var border = false;

        fixed (float* w = block.Water)
        fixed (float* next = block.Next)
        fixed (float* os = block.OutScale)
        fixed (byte* fl = block.Flags)
        fixed (byte* wet = block.WetRows)
        {
            for (var z = 0; z < S; z++)
            {
                for (var y = 0; y < S; y++)
                {
                    var row = ChunkConstants.Index(0, y, z);
                    var zRow = zCache + y * S;

                    if (RowQuiet(wet, y, z))
                    {
                        new Span<float>(next + row, S).Clear();
                        new Span<float>(yCache, S).Clear();
                        new Span<float>(zRow, S).Clear();
                        continue;
                    }

                    var seam = y == 0;
                    var seamBack = seam && z == 0 ? AllLanes() : zero;
                    var seamFront = seam && z == S - 1 ? AllLanes() : zero;
                    var borderRow = y == 0 || y == S - 1 || z == 0 || z == S - 1;
                    var reservoirFill = Vector256.Create(block.ReservoirFill(y));

                    for (var c = 0; c < Chunks; c++)
                    {
                        var x = c * Lanes;
                        var i = row + x;
                        var left = ActualSimd(w, fl, os, i - 1, 1, seam && c == 0 ? FirstLane() : zero, false);
                        var right = ActualSimd(w, fl, os, i, 1, seam && c == Chunks - 1 ? LastLane() : zero, false);
                        var down = y == 0 ? ActualSimd(w, fl, os, i - P, P, zero, true) : Vector256.Load(yCache + x);
                        var up = ActualSimd(w, fl, os, i, P, zero, true);
                        var back = z == 0 ? ActualSimd(w, fl, os, i - A, A, seamBack, false) : Vector256.Load(zRow + x);
                        var front = ActualSimd(w, fl, os, i, A, seamFront, false);
                        up.Store(yCache + x);
                        front.Store(zRow + x);

                        var wc = Vector256.Load(w + i);
                        var flags = Avx2.ConvertToVector256Int32(fl + i);
                        var reservoir = Vector256.Equals(flags & reservoirBit, reservoirBit).AsSingle();
                        var solid = Vector256.Equals(flags & solidBit, solidBit).AsSingle();
                        var n = wc + (left + down + back - right - up - front);
                        n = Vector256.AndNot(n, Vector256.LessThan(n, eps) | solid);
                        n = Vector256.ConditionalSelect(reservoir, reservoirFill, n);
                        n.Store(next + i);

                        var delta = Vector256.Abs(n - wc);
                        maxDelta = Vector256.MaxNative(maxDelta, delta);

                        if (!border)
                        {
                            var edges = borderRow ? AllLanes() : (c == 0 ? FirstLane() : zero) | (c == Chunks - 1 ? LastLane() : zero);
                            border = Vector256.ExtractMostSignificantBits(Vector256.GreaterThan(delta, borderEps) & edges) != 0;
                        }
                    }
                }
            }
        }

        var max = 0f;

        for (var l = 0; l < Lanes; l++)
            max = MathF.Max(max, maxDelta.GetElement(l));

        block.MaxDelta = max;
        block.BorderActive = border;
    }

    /// <summary>Lane-wise <see cref="Transfer"/>. <c>seam</c> marks lanes whose horizontal pair counts as supported.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> TransferSimd(float* w, byte* fl, int lo, int stride, Vector256<float> seam, bool vertical)
    {
        var hi = lo + stride;
        var blocked = SolidLanes(fl, lo) | SolidLanes(fl, hi);
        var wl = Vector256.Load(w + lo);
        var wh = Vector256.Load(w + hi);

        if (vertical)
        {
            var total = wl + wh;
            var squeezed = (Vector256.Create(1f) + total * Vector256.Create(Compression)) / Vector256.Create(1f + Compression);
            var stacked = (total + Vector256.Create(Compression)) * Vector256.Create(0.5f);
            var lower = Vector256.ConditionalSelect(Vector256.LessThanOrEqual(total, Vector256.Create(1f)), total,
                Vector256.ConditionalSelect(Vector256.LessThan(total, Vector256.Create(2f + Compression)), squeezed, stacked));
            return Vector256.AndNot((wl - lower) * Vector256.Create(VerticalRate), blocked);
        }

        var t = (wl - wh) * Vector256.Create(HorizontalRate);
        var threshold = Vector256.Create(SupportThreshold);
        var supportLo = SolidLanes(fl, lo - P) | Vector256.GreaterThanOrEqual(Vector256.Load(w + lo - P), threshold);
        var supportHi = SolidLanes(fl, hi - P) | Vector256.GreaterThanOrEqual(Vector256.Load(w + hi - P), threshold);
        var supported = Vector256.ConditionalSelect(Vector256.GreaterThan(t, Vector256<float>.Zero), supportLo, supportHi) | seam;
        var keep = supported & Vector256.GreaterThanOrEqual(Vector256.Abs(t), Vector256.Create(SnapEpsilon));
        return Vector256.AndNot(t & keep, blocked);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> ActualSimd(float* w, byte* fl, float* os, int lo, int stride, Vector256<float> seam, bool vertical)
    {
        var t = TransferSimd(w, fl, lo, stride, seam, vertical);
        var scale = Vector256.ConditionalSelect(Vector256.GreaterThan(t, Vector256<float>.Zero), Vector256.Load(os + lo), Vector256.Load(os + lo + stride));
        return t * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> SolidLanes(byte* fl, int i)
    {
        var bit = Vector256.Create((int)SolidBit);
        return Vector256.Equals(Avx2.ConvertToVector256Int32(fl + i) & bit, bit).AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> AllLanes() => Vector256<float>.AllBitsSet;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> FirstLane() => Vector256.Create(-1, 0, 0, 0, 0, 0, 0, 0).AsSingle();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> LastLane() => Vector256.Create(0, 0, 0, 0, 0, 0, 0, -1).AsSingle();
}
