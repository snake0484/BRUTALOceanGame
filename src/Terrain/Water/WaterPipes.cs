using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// 3D virtual pipe flow with momentum (docs/voxel-water.md "Flow simulation v2"): a signed flux per face, carried from
/// step to step, driven by head differences and limited to the water a cell holds; water falling into space moves at a
/// fall speed carried with it, cell to cell. Same blocks, halos and two-pass order as the automaton (<see cref="WaterFlow"/>):
/// copy Water halos and the fall speeds above (<see cref="WaterHalo.CopyFallAbove"/>), <see cref="UpdateFluxes"/>, copy
/// OutScale halos, <see cref="ApplyFluxes"/>, <see cref="WaterFlow.Swap"/>.
/// <para>
/// A face shared by two blocks is held by both (the −X, −Y, −Z faces in the halo cells' slots) and every update of it
/// comes from the same inputs through <see cref="Horizontal"/>, <see cref="Vertical"/> and <see cref="Limit"/>, so both
/// copies stay bit-identical and mass stays exact across blocks. A face between two dry cells holds nothing, so dry rows
/// are skipped as in the automaton. Scalar only: the in-place sweep reads faces and fall speeds the cells before it have
/// just updated.
/// </para>
/// </summary>
public static class WaterPipes
{
    public const float Gravity = 9.81f;

    /// <summary>Background flux damping (1/s) on every face, so a closed pool stops sloshing (the zone sim's).</summary>
    public const float DampingRate = 0.2f;

    /// <summary>Quadratic bed drag over a solid floor: deceleration Cd u² / h, so films slow and deep water glides (the zone sim's).</summary>
    public const float BedDrag = 0.01f;

    /// <summary>A block may sleep only while every flux it holds is under this (m³/s): a steady stream changes no fill.</summary>
    public const float FluxEpsilon = 1e-4f;

    /// <summary>Falling water falls at most this fast (m/s): free fall from about 45 m.</summary>
    public const float MaxFallSpeed = 30f;

    private const float MinFaceDepth = 0.01f;

    /// <summary>A supported cell's speed down through its floor is measured over at least this much water, so its last drops don't read as a jet.</summary>
    private const float MinFallFill = 0.05f;
    private const float InverseCompression = 1f / WaterFlow.Compression;
    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int A = ChunkConstants.PaddedArea;
    private const byte SolidBit = (byte)WaterFlags.Solid;
    private const byte ReservoirBit = (byte)WaterFlags.Reservoir;

    /// <summary>
    /// A cell's head relative to its centre (m): its free surface while it has room, and over full the pressure its
    /// overfill stands for, <see cref="WaterFlow.Compression"/> per metre of water above (the automaton's and the
    /// reservoirs' equilibrium).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Head(float w) => w <= 1f ? w - 0.5f : 0.5f + (w - 1f) * InverseCompression;

    /// <summary>
    /// Pass 1: every face touching the interior once, then each interior cell's outflow limit. Needs fresh Water halos and
    /// fall speeds above. A face reads only its own last flux and the cells' state, so the sweep's order doesn't matter.
    /// </summary>
    public static unsafe void UpdateFluxes(WaterBlock block, float dt)
    {
        WaterFlow.BuildWetRows(block);
        var pipe = dt * Gravity;
        var damping = MathF.Exp(-DampingRate * dt);

        fixed (float* w = block.Water)
        fixed (float* os = block.OutScale)
        fixed (byte* fl = block.Flags)
        fixed (byte* wet = block.WetRows)
        fixed (float* fx = block.FluxX)
        fixed (float* fy = block.FluxY)
        fixed (float* fz = block.FluxZ)
        fixed (float* v = block.FallSpeed)
        {
            for (var z = 0; z < S; z++)
            {
                for (var y = 0; y < S; y++)
                {
                    var row = ChunkConstants.Index(0, y, z);

                    // Every face of a row that is dry with its four neighbouring rows joins two dry cells: it holds nothing.
                    if (WaterFlow.RowQuiet(wet, y, z))
                    {
                        new Span<float>(os + row, S).Fill(1f);
                        new Span<float>(fx + row - 1, S + 1).Clear();
                        new Span<float>(fy + row, S).Clear();
                        new Span<float>(fz + row, S).Clear();

                        if (y == 0)
                            new Span<float>(fy + row - P, S).Clear();

                        if (z == 0)
                            new Span<float>(fz + row - A, S).Clear();

                        continue;
                    }

                    var seam = y == 0;

                    for (var x = 0; x < S; x++)
                    {
                        var i = row + x;

                        if (x == 0)
                            fx[i - 1] = Horizontal(w, fl, fx[i - 1], i - 1, 1, seam, pipe, damping, dt);

                        if (y == 0)
                            fy[i - P] = Vertical(w, fl, v, fy[i - P], i - P, pipe, damping, dt);

                        if (z == 0)
                            fz[i - A] = Horizontal(w, fl, fz[i - A], i - A, A, seam, pipe, damping, dt);

                        fx[i] = Horizontal(w, fl, fx[i], i, 1, seam && x == S - 1, pipe, damping, dt);
                        fy[i] = Vertical(w, fl, v, fy[i], i, pipe, damping, dt);
                        fz[i] = Horizontal(w, fl, fz[i], i, A, seam && z == S - 1, pipe, damping, dt);

                        var sent = dt * (MathF.Max(fx[i], 0f) + MathF.Max(-fx[i - 1], 0f) + MathF.Max(fy[i], 0f)
                            + MathF.Max(-fy[i - P], 0f) + MathF.Max(fz[i], 0f) + MathF.Max(-fz[i - A], 0f));
                        var wc = w[i];
                        os[i] = (fl[i] & ReservoirBit) == 0 && sent > 0f && wc - sent < WaterFlow.SnapEpsilon ? wc / sent : 1f;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pass 2: each face scaled once by its source cell's limit and stored back scaled (the limited flux is the momentum
    /// carried on), W += dt (in − out), then each cell's fall speed: the water left in it and the water that fell in from
    /// above, mixed by volume, gaining g dt while nothing holds it up. Needs pass 1 and fresh OutScale halos.
    /// </summary>
    public static unsafe void ApplyFluxes(WaterBlock block, float dt)
    {
        var maxDelta = 0f;
        var maxFlux = 0f;
        var border = false;
        var gain = Gravity * dt;

        fixed (float* w = block.Water)
        fixed (float* next = block.Next)
        fixed (float* os = block.OutScale)
        fixed (byte* fl = block.Flags)
        fixed (byte* wet = block.WetRows)
        fixed (float* fx = block.FluxX)
        fixed (float* fy = block.FluxY)
        fixed (float* fz = block.FluxZ)
        fixed (float* v = block.FallSpeed)
        {
            for (var z = 0; z < S; z++)
            {
                for (var y = 0; y < S; y++)
                {
                    var row = ChunkConstants.Index(0, y, z);

                    if (WaterFlow.RowQuiet(wet, y, z))
                    {
                        new Span<float>(next + row, S).Clear();
                        new Span<float>(v + row, S).Clear();
                        continue;
                    }

                    var reservoirFill = block.ReservoirFill(y);
                    var borderRow = y == 0 || y == S - 1 || z == 0 || z == S - 1;

                    for (var x = 0; x < S; x++)
                    {
                        var i = row + x;

                        if (x == 0)
                            fx[i - 1] = Limit(fx[i - 1], os, i - 1, 1);

                        if (y == 0)
                            fy[i - P] = Limit(fy[i - P], os, i - P, P);

                        if (z == 0)
                            fz[i - A] = Limit(fz[i - A], os, i - A, A);

                        fx[i] = Limit(fx[i], os, i, 1);
                        fy[i] = Limit(fy[i], os, i, P);
                        fz[i] = Limit(fz[i], os, i, A);

                        var wc = w[i];
                        var flags = fl[i];
                        var right = Moved(fx[i], dt);
                        var floor = Moved(fy[i - P], dt);
                        var top = Moved(fy[i], dt);
                        var front = Moved(fz[i], dt);
                        var n = wc + dt * (Moved(fx[i - 1], dt) - right + floor - top + Moved(fz[i - A], dt) - front);
                        var speed = 0f;

                        // No snap: a speck left behind can't spread (its transfers are under the minimum) and drops whole when
                        // it falls, so deleting it would only lose mass.
                        if ((flags & ReservoirBit) != 0)
                        {
                            n = reservoirFill;
                        }
                        else if (n <= 0f || (flags & SolidBit) != 0)
                        {
                            n = 0f;
                        }
                        else if ((fl[i - P] & SolidBit) == 0)
                        {
                            if (w[i - P] >= WaterFlow.SupportThreshold)
                            {
                                // Held up by full water: its speed down is whatever its floor carries, so a column that starts to
                                // fall keeps its speed when what holds it drains away.
                                speed = MathF.Min(floor, 0f) / MathF.Max(n, MinFallFill);
                            }
                            else
                            {
                                var kept = MathF.Max(wc + dt * MathF.Min(floor, 0f), 0f);
                                var fell = -dt * MathF.Min(top, 0f);
                                var falling = kept + fell;
                                speed = falling > 0f ? (kept * v[i] + fell * v[i + P]) / falling - gain : 0f;
                            }

                            speed = MathF.Max(speed, -MaxFallSpeed);
                        }

                        next[i] = n;
                        v[i] = speed;
                        var delta = MathF.Abs(n - wc);
                        maxDelta = MathF.Max(maxDelta, delta);
                        maxFlux = MathF.Max(maxFlux, MathF.Max(MathF.Max(MathF.Abs(right), MathF.Abs(top)), MathF.Abs(front)));

                        if (delta > WaterFlow.BorderEpsilon && (borderRow || x == 0 || x == S - 1))
                            border = true;
                    }
                }
            }
        }

        block.MaxDelta = maxDelta;
        block.MaxFlux = maxFlux;
        block.BorderActive = border;
    }

    /// <summary>
    /// Sideways face lo → lo + stride: the head difference over the upwind cell's water, which pushes only while that cell
    /// stands on something (water in free fall has no pressure to spread with), with bed drag where it stands on solid.
    /// A seam face (bottom layer, across an X or Z block face) counts as supported without a bed: its "below" is an edge
    /// halo cell no block carries.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Horizontal(float* w, byte* fl, float f, int lo, int stride, bool seam, float pipe, float damping, float dt)
    {
        var hi = lo + stride;
        var wl = w[lo];
        var wh = w[hi];

        if (((fl[lo] | fl[hi]) & SolidBit) != 0 || (wl == 0f && wh == 0f))
            return 0f;

        var drive = Head(wl) - Head(wh);
        var up = drive > 0f ? lo : hi;
        var below = up - P;
        var bed = !seam && (fl[below] & SolidBit) != 0;
        var face = seam || bed || w[below] >= WaterFlow.SupportThreshold ? MathF.Min(w[up], 1f) : 0f;

        if (bed)
        {
            var depth = MathF.Max(face, MinFaceDepth);
            damping /= 1f + dt * BedDrag * MathF.Abs(f) / (depth * depth);
        }

        return f * damping + pipe * face * drive;
    }

    /// <summary>
    /// Face between lo and the cell above it. Over full water it carries momentum driven by the head difference (pressure,
    /// rising and sinking water). Over a cell with room, the water above is falling into space: it crosses at its own fall
    /// speed (<see cref="WaterBlock.FallSpeed"/>, carried with it from cell to cell), so a front moves at the water's speed;
    /// a falling speck too small to move at that speed drops whole, or it would hang in the air.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Vertical(float* w, byte* fl, float* v, float f, int lo, float pipe, float damping, float dt)
    {
        var hi = lo + P;
        var wl = w[lo];
        var wh = w[hi];

        if (((fl[lo] | fl[hi]) & SolidBit) != 0 || (wl == 0f && wh == 0f))
            return 0f;

        if (wl >= WaterFlow.SupportThreshold)
            return f * damping + pipe * (Head(wl) - 1f - Head(wh));

        var fall = v[hi] * MathF.Min(wh, 1f);
        return fall < 0f && -fall * dt < WaterFlow.SnapEpsilon ? -wh / dt : fall;
    }

    /// <summary>A face's flux scaled by its source's limit: the momentum carried on, whether or not it moves water this step (<see cref="Moved"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Limit(float f, float* os, int lo, int stride) => f > 0f ? f * os[lo] : f < 0f ? f * os[lo + stride] : 0f;

    /// <summary>
    /// The flux a face moves water with this step: none when it would move less than <see cref="WaterFlow.SnapEpsilon"/>,
    /// so no cell is left holding a speck the snap would delete. The momentum stays on the face and builds until it moves
    /// water, so a slow flow is held back by a minimum rate, not by a minimum head difference.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Moved(float f, float dt) => MathF.Abs(f) * dt < WaterFlow.SnapEpsilon ? 0f : f;
}
