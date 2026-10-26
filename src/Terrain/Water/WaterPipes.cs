using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// 3D virtual pipe flow with momentum (docs/voxel-water.md "Flow simulation v2"): a signed flux per face, carried from
/// step to step, driven by head differences and limited to the water a cell holds; water falling into space moves at a
/// fall speed carried with it, cell to cell. Same blocks, halos and two-pass order as the automaton (<see cref="WaterFlow"/>):
/// copy Water halos and the falling water beside and above (<see cref="WaterHalo.CopyFalling"/>), <see cref="UpdateFluxes"/>, copy
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

    /// <summary>
    /// Damping (1/s) of a vertical face over full water. Water is incompressible: the overfill that stands for its pressure
    /// should settle, not ring as a pressure wave (about 4 Hz in a 3 m pond) that the background damping let run for good.
    /// </summary>
    public const float PressureDampingRate = 6f;

    /// <summary>
    /// Manning roughness n (s/m^⅓) of the bed under sideways flow: deceleration g n² u² / h^⁴⁄₃, so films slow and deep
    /// water glides. 0.1 is natural ground under overland flow (grass runs 0.1–0.4, channels 0.01–0.05). With the zone
    /// sim's sand-bed drag (Cd 0.01) a sheet on a 20 % slope ran at Froude 4, and its roll waves grew down the voxel stairs
    /// until it emptied between surges; at 0.06 they still grew past 15 m.
    /// </summary>
    public const float BedRoughness = 0.1f;
    private const float Friction = Gravity * BedRoughness * BedRoughness;

    /// <summary>Quadratic drag (Cd: deceleration Cd u² / h) on the bottom layer of water deeper than a cell, the zone sim's sand bed.</summary>
    public const float DeepBedDrag = 0.01f;

    /// <summary>Falling water falls at most this fast (m/s): free fall from about 45 m.</summary>
    public const float MaxFallSpeed = 30f;

    private const float MinFaceDepth = 0.01f;

    /// <summary>A supported cell's speed down through its floor is measured over at least this much water, so its last drops don't read as a jet.</summary>
    private const float MinFallFill = 0.05f;

    /// <summary>Falling water's sideways speed is at most this (m/s); water flowing off a thin film reads its speed over at least <see cref="MinFallFill"/>.</summary>
    private const float MaxDrift = 10f;
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
        var settling = MathF.Exp(-PressureDampingRate * dt);

        fixed (float* w = block.Water)
        fixed (float* os = block.OutScale)
        fixed (byte* fl = block.Flags)
        fixed (sbyte* bed = block.Bed)
        fixed (byte* wet = block.WetRows)
        fixed (float* fx = block.FluxX)
        fixed (float* fy = block.FluxY)
        fixed (float* fz = block.FluxZ)
        fixed (float* v = block.FallSpeed)
        fixed (float* dx = block.DriftX)
        fixed (float* dz = block.DriftZ)
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

                    // Landing water is looked for only under rows that hold falling water (the X faces' cells and the Z
                    // faces' cells on either side). A face across an X or Z block face in the top row has edge halo cells
                    // over it, which no block carries: none there.
                    var top = y == S - 1;
                    var fallsBack = z == 0 && Falling(v, row + P - A);
                    var fallsHere = Falling(v, row + P);
                    var fallsFront = Falling(v, row + P + A);

                    for (var x = 0; x < S; x++)
                    {
                        var i = row + x;

                        if (x == 0)
                            fx[i - 1] = Horizontal(w, fl, bed, v, dx, fx[i - 1], i - 1, 1, seam, !top, fallsHere, pipe, damping, dt);

                        if (y == 0)
                            fy[i - P] = Vertical(w, fl, v, fy[i - P], i - P, pipe, settling, dt);

                        if (z == 0)
                            fz[i - A] = Horizontal(w, fl, bed, v, dz, fz[i - A], i - A, A, seam, !top, fallsBack || fallsHere, pipe, damping, dt);

                        fx[i] = Horizontal(w, fl, bed, v, dx, fx[i], i, 1, seam && x == S - 1, !(top && x == S - 1), fallsHere, pipe, damping, dt);
                        fy[i] = Vertical(w, fl, v, fy[i], i, pipe, settling, dt);
                        fz[i] = Horizontal(w, fl, bed, v, dz, fz[i], i, A, seam && z == S - 1, !(top && z == S - 1), fallsHere || fallsFront, pipe, damping, dt);

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
    /// above, mixed by volume, gaining g dt while nothing holds it up; and its drift, the same water and the water that
    /// flowed in sideways, mixed by volume. Needs pass 1 and fresh OutScale halos.
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
        fixed (float* dx = block.DriftX)
        fixed (float* dz = block.DriftZ)
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
                        var left = Moved(fx[i - 1], dt);
                        var right = Moved(fx[i], dt);
                        var floor = Moved(fy[i - P], dt);
                        var top = Moved(fy[i], dt);
                        var back = Moved(fz[i - A], dt);
                        var front = Moved(fz[i], dt);
                        var n = wc + dt * (left - right + floor - top + back - front);
                        var speed = 0f;
                        var driftX = 0f;
                        var driftZ = 0f;

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

                                var inLeft = dt * MathF.Max(left, 0f);
                                var inRight = -dt * MathF.Min(right, 0f);
                                var inBack = dt * MathF.Max(back, 0f);
                                var inFront = -dt * MathF.Min(front, 0f);
                                var mixed = falling + inLeft + inRight + inBack + inFront;

                                if (mixed > 0f)
                                {
                                    driftX = (kept * dx[i] + fell * dx[i + P] + inLeft * Drift(left, w[i - 1]) + inRight * Drift(right, w[i + 1])) / mixed;
                                    driftZ = (kept * dz[i] + fell * dz[i + P] + inBack * Drift(back, w[i - A]) + inFront * Drift(front, w[i + A])) / mixed;
                                }
                            }

                            speed = MathF.Max(speed, -MaxFallSpeed);
                        }

                        next[i] = n;
                        v[i] = speed;
                        dx[i] = driftX;
                        dz[i] = driftZ;
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
    /// Sideways face lo → lo + stride: the head difference (<see cref="SideHead"/>) over the upwind cell's water, which
    /// pushes only while that cell stands on something (water in free fall has no pressure to spread with), with bed drag
    /// where it stands on solid. A seam face (bottom layer, across an X or Z block face) has an edge halo cell no block
    /// carries for its "below": it counts as supported, and as standing on a bed by the cells' <see cref="WaterBlock.Bed"/>.
    /// <paramref name="over"/>: the cells over the face are carried (false across an X or Z block face in the top row);
    /// <paramref name="falling"/>: the rows over it hold falling water.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Horizontal(float* w, byte* fl, sbyte* bd, float* v, float* drift, float f, int lo, int stride, bool seam, bool over, bool falling, float pipe, float damping, float dt)
    {
        var hi = lo + stride;
        var wl = w[lo];
        var wh = w[hi];

        if (((fl[lo] | fl[hi]) & SolidBit) != 0 || (wl == 0f && wh == 0f))
            return 0f;

        var drive = SideHead(w, fl, bd, lo, seam) - SideHead(w, fl, bd, hi, seam);
        var up = drive > 0f ? lo : hi;
        var below = up - P;
        var bed = seam ? bd[up] != WaterBlock.NotBed : (fl[below] & SolidBit) != 0;
        var face = seam || bed || w[below] >= WaterFlow.SupportThreshold ? MathF.Min(w[up], 1f) : 0f;

        if (bed)
        {
            // Manning for a film. Under more water the layer is the bottom of a deeper flow, and braking it alone as a 1 m
            // film let the layers over it race ahead (a lidded pit filling from the sea sloshed for 80 s): blend to the
            // deep bed drag as the cell above fills.
            var depth = MathF.Max(face, MinFaceDepth);
            var film = Friction / Cbrt(depth);
            var drag = over ? film + (DeepBedDrag - film) * MathF.Min(w[up + P], 1f) : film;
            damping /= 1f + dt * drag * MathF.Abs(f) / (depth * depth);
        }

        f = f * damping + pipe * face * drive;

        // Water landing in either cell from above brings its sideways momentum (r u, per second) onto this face if it
        // moves toward it.
        return over && falling ? f + dt * (Landing(w, fl, v, drift, lo, seam, 1f) + Landing(w, fl, v, drift, hi, seam, -1f)) : f;
    }

    /// <summary>Any falling water in the padded row starting at <paramref name="row"/> (its 34 cells, halos included).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool Falling(float* v, int row)
    {
        var start = v + row - 1;
        var any = Vector<float>.Zero;
        var i = 0;

        for (; i <= P - Vector<float>.Count; i += Vector<float>.Count)
            any = Vector.Min(any, Vector.Load(start + i));

        var low = Vector.Dot(Vector<float>.One, Vector.Min(any, Vector<float>.Zero)) < 0f;

        for (; i < P; i++)
            low |= start[i] < 0f;

        return low;
    }

    /// <summary>Cube root for depths in (0, 1]: an exponent estimate and one Newton step, about 0.2 % off and far cheaper than <see cref="MathF.Cbrt"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Cbrt(float x)
    {
        var y = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(x) / 3 + 709921077);
        return y - (y * y * y - x) / (3f * y * y);
    }

    /// <summary>
    /// The sideways momentum (m³/s per second) of the water falling into cell i from above this step, toward the face on
    /// i's + side (side 1) or − side (−1): its fall rate times its drift. Only into water that is held up (a bed, a pool;
    /// a seam's bottom layer counts as held up): water landing in water still falling mixes into its drift instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float Landing(float* w, byte* fl, float* v, float* drift, int i, bool seam, float side)
    {
        var above = i + P;
        var u = drift[above];

        if (v[above] >= 0f || u * side <= 0f)
            return 0f;

        if (!seam && (fl[i - P] & SolidBit) == 0 && w[i - P] < WaterFlow.SupportThreshold)
            return 0f;

        return -v[above] * MathF.Min(w[above], 1f) * u;
    }

    /// <summary>The sideways speed of water a face moved: its flux over the water of the cell it left, at most <see cref="MaxDrift"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Drift(float f, float source) => Math.Clamp(f / MathF.Max(MathF.Min(source, 1f), MinFallFill), -MaxDrift, MaxDrift);

    /// <summary>
    /// A cell's head for sideways flow. Water on a bed stands on the terrain's smooth surface (<see cref="WaterBlock.BedRise"/>),
    /// so a film on a voxel staircase feels the slope the stairs stand for, not flat terraces. Water in a cell with room
    /// over a bed cell with room is falling and has no pressure of its own: it reads as the surface it lands on one layer
    /// down, so a film runs over a stair's edge instead of damming behind it until it is deep enough to spill. Over a
    /// deeper drop (a cliff, a pit) it keeps its own head. A seam cell's "below" is an edge halo cell: it counts as supported.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe float SideHead(float* w, byte* fl, sbyte* bd, int i, bool seam)
    {
        var wi = w[i];
        var below = i - P;

        if (seam || (fl[below] & SolidBit) != 0 || w[below] >= WaterFlow.SupportThreshold)
            return Head(wi) + WaterBlock.BedRise(bd[i], wi);

        var under = bd[below];
        return under == WaterBlock.NotBed ? Head(wi) : Head(w[below]) + WaterBlock.BedRise(under, w[below]) - 1f + wi;
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
