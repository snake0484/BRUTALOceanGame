using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain;

/// <summary>
/// A planet's water depth for the ocean (planets.md "Ocean and shore wash on a sphere", review C7): sea level minus the
/// ground along a radial, the ground being the generated surface H(dir) raised by placed fill. The sea mask is the
/// generated one (H &lt; sea level) and edits never change it: digging can only take placed fill back down to the
/// generated ground (shores and seabeds are unminable, D30; an inland pit dug below sea level holds no sea), so depth
/// only ever gets shallower. This is the pit rule for the CPU and the GPU alike (the GPU's depth maps are filled from
/// here).
/// <para>
/// Edits that reach down to the sea's band are binned by their place on the sea sphere, so a query reads only the ops
/// over its own column: two hash lookups, no scan (README carry-over: 3.3 us with 1000 edits before). One writer
/// (<see cref="Add"/>, the main thread at a tick), any number of readers on any thread, no locks: lists only grow, each
/// link is published after its node is written, and growing swaps in a copy.
/// </para>
/// </summary>
public sealed class PlanetShore
{
    /// <summary>Ops whose lowest point is above sea level by more than this (m) can't change what the sea sees.</summary>
    public const float BandTop = 8f;

    /// <summary>Placed fill counts as ground where it reaches down to within this (m) of the ground under it.</summary>
    public const float Contact = 0.5f;

    private const double FineCell = 16.0;
    private const double CoarseCell = 128.0;
    private const float FineRadius = 16f;
    private const long CoordBias = 1L << 20;
    private const long CoarseBit = 1L << 63;

    private readonly PlanetSurface _surface;
    private readonly double _radius;
    private readonly double _seaRadius;
    private readonly float _sea;
    private State _state = new(new Op[64], 256, 256);
    private int _ops;
    private int _nodes;
    private int _cells;

    private struct Op
    {
        public double X, Y, Z;
        public float Radius;
        public bool Place;
    }

    /// <summary>Open-addressing table of cell key to its op list (head, tail as node + 1), the list nodes and the ops.</summary>
    private sealed class State(Op[] ops, int nodes, int slots)
    {
        public readonly Op[] Ops = ops;
        public readonly int[] NodeOp = new int[nodes];
        public readonly int[] NodeNext = new int[nodes];
        public readonly long[] Keys = new long[slots];
        public readonly int[] Heads = new int[slots];
        public readonly int[] Tails = new int[slots];
    }

    public PlanetShore(PlanetSurface surface)
    {
        _surface = surface;
        _radius = surface.Radius;
        _sea = surface.SeaHeight;
        _seaRadius = surface.Radius + surface.SeaHeight;
    }

    public PlanetSurface Surface => _surface;

    /// <summary>Ops filed (those reaching the sea's band).</summary>
    public int IndexedOps => _ops;

    /// <summary>Sea mask: generated ground below sea level; edits never change it.</summary>
    public bool IsSea(double dx, double dy, double dz) => _surface.IsSea(dx, dy, dz);

    /// <summary>Depth from the generated ground alone, with the octaves LOD <paramref name="lod"/> evaluates (the far depth map).</summary>
    public float GeneratedDepth(double dx, double dy, double dz, int lod) => _sea - _surface.Height(dx, dy, dz, lod);

    /// <summary>Radial depth below sea level (m, negative on land) along unit direction d.</summary>
    public float Depth(double dx, double dy, double dz)
    {
        var generated = _surface.Height(dx, dy, dz);
        var state = Volatile.Read(ref _state);

        if (Volatile.Read(ref _ops) == 0)
            return _sea - generated;

        var px = dx * _seaRadius;
        var py = dy * _seaRadius;
        var pz = dz * _seaRadius;
        var fine = Head(state, Key(px, py, pz, FineCell, 0L));
        var coarse = Head(state, Key(px, py, pz, CoarseCell, CoarseBit));
        var ground = generated;

        // Both lists are in op order: merge them so fill and digs replay as they happened.
        while (fine != 0 || coarse != 0)
        {
            int node;

            if (coarse == 0 || (fine != 0 && state.NodeOp[fine - 1] < state.NodeOp[coarse - 1]))
            {
                node = fine - 1;
                fine = Volatile.Read(ref state.NodeNext[node]);
            }
            else
            {
                node = coarse - 1;
                coarse = Volatile.Read(ref state.NodeNext[node]);
            }

            ref readonly var op = ref state.Ops[state.NodeOp[node]];
            var along = op.X * dx + op.Y * dy + op.Z * dz;
            var lateral = op.X * op.X + op.Y * op.Y + op.Z * op.Z - along * along;
            var reach = (double)op.Radius * op.Radius - lateral;

            if (reach <= 0.0)
                continue;

            var half = (float)Math.Sqrt(reach);
            var centre = (float)(along - _radius);
            var bottom = centre - half;
            var top = centre + half;

            if (op.Place)
            {
                if (bottom <= ground + Contact && top > ground)
                    ground = top;
            }
            else if (top >= ground && bottom < ground)
            {
                ground = MathF.Max(generated, bottom);
            }
        }

        return _sea - ground;
    }

    /// <summary>Files an accepted edit. Main thread; returns false if it can't reach the sea's band.</summary>
    public bool Add(in EditOp edit)
    {
        if (!_surface.HasSea)
            return false;

        edit.Centre(out var x, out var y, out var z);
        var r = Math.Sqrt(x * x + y * y + z * z);

        if (r - edit.Radius - _radius > _sea + BandTop || r < 1.0)
            return false;

        var state = _state;

        if (_ops == state.Ops.Length)
        {
            var ops = new Op[_ops * 2];
            Array.Copy(state.Ops, ops, _ops);
            state = Grow(state, ops, state.NodeOp.Length, state.Keys.Length);
        }

        var index = _ops;
        state.Ops[index] = new Op { X = x, Y = y, Z = z, Radius = edit.Radius, Place = edit.Mode == BrushMode.Place };

        // Filed round its place on the sea sphere, where the columns it covers are looked up.
        var scale = _seaRadius / r;
        var sx = x * scale;
        var sy = y * scale;
        var sz = z * scale;
        var coarse = edit.Radius >= FineRadius;
        var cell = coarse ? CoarseCell : FineCell;
        var bit = coarse ? CoarseBit : 0L;
        var reach = edit.Radius * Math.Max(1.0, scale) + 1.0;
        var x0 = (long)Math.Floor((sx - reach) / cell);
        var y0 = (long)Math.Floor((sy - reach) / cell);
        var z0 = (long)Math.Floor((sz - reach) / cell);
        var x1 = (long)Math.Floor((sx + reach) / cell);
        var y1 = (long)Math.Floor((sy + reach) / cell);
        var z1 = (long)Math.Floor((sz + reach) / cell);

        for (var cz = z0; cz <= z1; cz++)
        for (var cy = y0; cy <= y1; cy++)
        for (var cx = x0; cx <= x1; cx++)
        {
            // Only cells the sea sphere passes through hold columns.
            var mx = (cx + 0.5) * cell;
            var my = (cy + 0.5) * cell;
            var mz = (cz + 0.5) * cell;

            if (Math.Abs(Math.Sqrt(mx * mx + my * my + mz * mz) - _seaRadius) > cell)
                continue;

            state = File(state, Pack(cx, cy, cz, bit), index);
        }

        Volatile.Write(ref _ops, index + 1);
        return true;
    }

    private State File(State state, long key, int op)
    {
        if (_nodes == state.NodeOp.Length)
            state = Grow(state, state.Ops, _nodes * 2, state.Keys.Length);

        if ((_cells + 1) * 2 > state.Keys.Length)
            state = Grow(state, state.Ops, state.NodeOp.Length, state.Keys.Length * 2);

        var node = _nodes++;
        state.NodeOp[node] = op;
        state.NodeNext[node] = 0;
        var slot = Slot(state, key);

        if (state.Keys[slot] == key)
        {
            Volatile.Write(ref state.NodeNext[state.Tails[slot] - 1], node + 1);
            state.Tails[slot] = node + 1;
            return state;
        }

        state.Heads[slot] = node + 1;
        state.Tails[slot] = node + 1;
        Volatile.Write(ref state.Keys[slot], key);
        _cells++;
        return state;
    }

    /// <summary>A copy with room for more nodes and cells, rehashed and swapped in; readers keep the one they hold.</summary>
    private State Grow(State old, Op[] ops, int nodes, int slots)
    {
        var grown = new State(ops, nodes, slots);
        Array.Copy(old.NodeOp, grown.NodeOp, _nodes);
        Array.Copy(old.NodeNext, grown.NodeNext, _nodes);

        for (var i = 0; i < old.Keys.Length; i++)
        {
            var key = old.Keys[i];

            if (key == 0L)
                continue;

            var slot = Slot(grown, key);
            grown.Keys[slot] = key;
            grown.Heads[slot] = old.Heads[i];
            grown.Tails[slot] = old.Tails[i];
        }

        Volatile.Write(ref _state, grown);
        return grown;
    }

    private static int Head(State state, long key)
    {
        var slot = Slot(state, key);
        return Volatile.Read(ref state.Keys[slot]) == key ? state.Heads[slot] : 0;
    }

    /// <summary>The key's slot, or the empty one it would take.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Slot(State state, long key)
    {
        var mask = state.Keys.Length - 1;
        var slot = (int)((ulong)(key * unchecked((long)0x9E3779B97F4A7C15UL)) >> 40) & mask;

        while (true)
        {
            var k = Volatile.Read(ref state.Keys[slot]);

            if (k == key || k == 0L)
                return slot;

            slot = (slot + 1) & mask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Key(double x, double y, double z, double cell, long bit) =>
        Pack((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell), bit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Pack(long x, long y, long z, long bit) => (x + CoordBias) | ((y + CoordBias) << 21) | ((z + CoordBias) << 42) | bit;
}
