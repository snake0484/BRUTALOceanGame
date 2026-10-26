using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// Two-pass compressible cell automaton (docs/voxel-water.md "Flow simulation v1").
/// <para>
/// Every pair transfer comes from <see cref="Transfer"/>, called with the pair in canonical order (lo = lower index),
/// so a pair straddling a block face gives a bit-identical result in both blocks and mass stays exact across blocks.
/// </para>
/// <para>
/// Support rule: a horizontal source is supported when the cell below it is solid or holds ≥ <see cref="SupportThreshold"/>.
/// For pairs inside a block, or crossing an X/Z face at y ≥ 1, both cells' "below" lie in the interior or in the face
/// halo, so both blocks see the same data. For pairs crossing an X/Z face in the bottom layer (y = 0), one "below" is an
/// edge halo cell (a diagonal neighbour) that the face halos never carry, so those pairs always count as supported.
/// Cost: falling water in the bottom layer can step sideways across a vertical block seam. Benefit: no edge or corner
/// halo exchange and no ordering between halo copies.
/// </para>
/// Per step: copy Water halos, <see cref="ComputeOutflowScale"/>, copy OutScale halos, <see cref="ApplyFlows"/>, <see cref="Swap"/>.
/// </summary>
public static partial class WaterFlow
{
    public const float Compression = 0.02f;
    public const float VerticalRate = 0.5f;
    public const float HorizontalRate = 0.25f;
    public const float SupportThreshold = 0.99f;
    public const float SnapEpsilon = 1e-5f;
    public const float BorderEpsilon = 1e-4f;

    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int A = ChunkConstants.PaddedArea;
    private const byte SolidBit = (byte)WaterFlags.Solid;
    private const byte ReservoirBit = (byte)WaterFlags.Reservoir;
    private const int Vertical = 0;
    private const int Horizontal = 1;
    private const int HorizontalSeam = 2;

    /// <summary>AVX2 path (bit-identical to the scalar one). Every block of a world must use the same path.</summary>
    internal static bool UseSimd = Avx2.IsSupported;

    /// <summary>
    /// Pass 1: OutScale = min(1, W / Σ positive outgoing transfers) per interior cell. A cell that would keep less than
    /// <see cref="SnapEpsilon"/> sends all it has instead, so the snap in pass 2 never deletes a drained remnant.
    /// Needs fresh Water halos.
    /// </summary>
    public static void ComputeOutflowScale(WaterBlock block)
    {
        BuildWetRows(block);

        if (UseSimd)
            ComputeOutflowScaleSimd(block);
        else
            ComputeOutflowScaleScalar(block);
    }

    /// <summary>Pass 2: Next = W + inflow − outflow (each scaled by its source's OutScale). Needs pass 1 and fresh OutScale halos.</summary>
    public static void ApplyFlows(WaterBlock block)
    {
        if (UseSimd)
            ApplyFlowsSimd(block);
        else
            ApplyFlowsScalar(block);
    }

    [SkipLocalsInit]
    private static unsafe void ComputeOutflowScaleScalar(WaterBlock block)
    {
        var yCache = stackalloc float[S];
        var zCache = stackalloc float[S * S];

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
                    var left = Transfer(w, fl, row - 1, 1, seam ? HorizontalSeam : Horizontal);

                    for (var x = 0; x < S; x++)
                    {
                        var i = row + x;
                        var right = Transfer(w, fl, i, 1, seam && x == S - 1 ? HorizontalSeam : Horizontal);
                        var down = y == 0 ? Transfer(w, fl, i - P, P, Vertical) : yCache[x];
                        var up = Transfer(w, fl, i, P, Vertical);
                        var back = z == 0 ? Transfer(w, fl, i - A, A, seam ? HorizontalSeam : Horizontal) : zRow[x];
                        var front = Transfer(w, fl, i, A, seam && z == S - 1 ? HorizontalSeam : Horizontal);
                        yCache[x] = up;
                        zRow[x] = front;

                        var outflow = MathF.Max(-left, 0f) + MathF.Max(right, 0f) + MathF.Max(-down, 0f)
                            + MathF.Max(up, 0f) + MathF.Max(-back, 0f) + MathF.Max(front, 0f);
                        var wc = w[i];
                        os[i] = (fl[i] & ReservoirBit) == 0 && outflow > 0f && wc - outflow < SnapEpsilon ? wc / outflow : 1f;
                        left = right;
                    }
                }
            }
        }
    }

    [SkipLocalsInit]
    private static unsafe void ApplyFlowsScalar(WaterBlock block)
    {
        var yCache = stackalloc float[S];
        var zCache = stackalloc float[S * S];
        var maxDelta = 0f;
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
                    var reservoirFill = block.ReservoirFill(y);
                    var borderRow = y == 0 || y == S - 1 || z == 0 || z == S - 1;
                    var left = Actual(w, fl, os, row - 1, 1, seam ? HorizontalSeam : Horizontal);

                    for (var x = 0; x < S; x++)
                    {
                        var i = row + x;
                        var right = Actual(w, fl, os, i, 1, seam && x == S - 1 ? HorizontalSeam : Horizontal);
                        var down = y == 0 ? Actual(w, fl, os, i - P, P, Vertical) : yCache[x];
                        var up = Actual(w, fl, os, i, P, Vertical);
                        var back = z == 0 ? Actual(w, fl, os, i - A, A, seam ? HorizontalSeam : Horizontal) : zRow[x];
                        var front = Actual(w, fl, os, i, A, seam && z == S - 1 ? HorizontalSeam : Horizontal);
                        yCache[x] = up;
                        zRow[x] = front;

                        var wc = w[i];
                        var flags = fl[i];
                        var n = wc + (left + down + back - right - up - front);

                        if ((flags & ReservoirBit) != 0)
                            n = reservoirFill;
                        else if (n < SnapEpsilon || (flags & SolidBit) != 0)
                            n = 0f;

                        next[i] = n;
                        var delta = MathF.Abs(n - wc);
                        maxDelta = MathF.Max(maxDelta, delta);

                        if (delta > BorderEpsilon && (borderRow || x == 0 || x == S - 1))
                            border = true;

                        left = right;
                    }
                }
            }
        }

        block.MaxDelta = maxDelta;
        block.BorderActive = border;
    }

    /// <summary>Makes Next the current state. The halo planes are carried over so meshing and face queries see them.</summary>
    public static void Swap(WaterBlock block)
    {
        WaterHalo.CopyHaloPlanes(block.Water, block.Next);
        (block.Water, block.Next) = (block.Next, block.Water);
    }

    /// <summary>
    /// Desired signed transfer lo → hi for the pair (lo, lo + stride), from the previous state only.
    /// The single source of every pair value: both passes, both cells of a pair and both blocks of a seam call this.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Transfer(float* w, byte* fl, int lo, int stride, int kind)
    {
        var hi = lo + stride;

        if (((fl[lo] | fl[hi]) & SolidBit) != 0)
            return 0f;

        var wl = w[lo];
        var wh = w[hi];

        if (kind == Vertical)
        {
            // hi sits above lo; positive = up (pressure), negative = falling.
            var total = wl + wh;
            var lower = total <= 1f ? total
                : total < 2f + Compression ? (1f + total * Compression) / (1f + Compression)
                : (total + Compression) * 0.5f;
            return (wl - lower) * VerticalRate;
        }

        // Spreads smaller than the snap would only be destroyed by it on arrival.
        var t = (wl - wh) * HorizontalRate;

        if (MathF.Abs(t) < SnapEpsilon)
            return 0f;

        return kind == HorizontalSeam || Supported(w, fl, t > 0f ? lo : hi) ? t : 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Actual(float* w, byte* fl, float* os, int lo, int stride, int kind)
    {
        var t = Transfer(w, fl, lo, stride, kind);
        return t > 0f ? t * os[lo] : t < 0f ? t * os[lo + stride] : 0f;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool Supported(float* w, byte* fl, int i) => (fl[i - P] & SolidBit) != 0 || w[i - P] >= SupportThreshold;

    /// <summary>A row whose cells and 4 neighbouring rows are all dry exchanges nothing.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool RowQuiet(byte* wet, int y, int z)
    {
        var r = (y + 1) + (z + 1) * P;
        return (wet[r] | wet[r - 1] | wet[r + 1] | wet[r - P] | wet[r + P]) == 0;
    }

    /// <summary>Any non-zero bits in each padded row of 34 cells (4 vectors + 2 scalars).</summary>
    internal static unsafe void BuildWetRows(WaterBlock block)
    {
        fixed (float* water = block.Water)
        fixed (byte* wet = block.WetRows)
        {
            for (var r = 0; r < A; r++)
            {
                var row = (uint*)(water + r * P);
                var bits = Vector256.Load(row) | Vector256.Load(row + 8) | Vector256.Load(row + 16) | Vector256.Load(row + 24);
                wet[r] = bits != Vector256<uint>.Zero || (row[32] | row[33]) != 0 ? (byte)1 : (byte)0;
            }
        }
    }
}
