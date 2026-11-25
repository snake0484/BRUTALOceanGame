namespace BrutalGame.Terrain;

/// <summary>
/// A planet's permanent edits (D32) with a per-region op index in size tiers (planets.md "Detail ladder", review P3), so
/// no chunk scans more than its own region's lists:
/// fine, ops under <see cref="HugeRadius"/> keyed by LOD-0 chunk, read by LOD-0 leaves;
/// coarse, ops from <see cref="CoarseRadius"/> to <see cref="HugeRadius"/> keyed by LOD-5 cell (1 km), read by LOD 1-5;
/// huge, ops of <see cref="HugeRadius"/> and up keyed by LOD-10 cell (32 km), read by every LOD.
/// Each op is filed in every cell its reach at the coarsest LOD it shows at (plus that LOD's apron) meets, so a leaf
/// reads only the cells its own box covers (one for LOD ≤ the tier's cell, 64 for a LOD-12 leaf in the huge tier).
/// Main thread: jobs get their ops copied by <see cref="Gather"/>. Snapshots and compaction come later.
/// </summary>
public sealed class EditStore
{
    /// <summary>Ops this size and up show at every distance and in the huge tier.</summary>
    public const float HugeRadius = 32f;

    /// <summary>2 × the LOD-1 voxel: smaller ops show only at LOD 0.</summary>
    public const float CoarseRadius = 4f;

    /// <summary>An op shows at LOD L ≥ 1 only if its radius is at least this many voxels of L.</summary>
    public const float VisibleVoxels = 2f;

    public const double DefaultEditRadius = 8000.0;
    public const int FineCellLod = 0;
    public const int CoarseCellLod = 5;
    public const int HugeCellLod = 10;
    public const int CoarseMaxLod = 5;

    private EditOp[] _ops = new EditOp[256];
    private int _count;
    private uint _lastSeq;
    private readonly Dictionary<ulong, OpBucket> _fine = new();
    private readonly Dictionary<ulong, OpBucket> _coarse = new();
    private readonly Dictionary<ulong, OpBucket> _huge = new();

    /// <summary>Beyond this distance from the viewer, ops under <see cref="HugeRadius"/> are not applied at LOD ≥ 1.</summary>
    public double EditRadius = DefaultEditRadius;

    private sealed class OpBucket
    {
        public int[] Items = new int[4];
        public int Count;

        public void Add(int op)
        {
            if (Count == Items.Length)
                Array.Resize(ref Items, Count * 2);

            Items[Count++] = op;
        }
    }

    private readonly struct BySeq : IComparer<EditOp>
    {
        public int Compare(EditOp a, EditOp b) => a.Seq.CompareTo(b.Seq);
    }

    public int Count => _count;

    public ReadOnlySpan<EditOp> Ops => _ops.AsSpan(0, _count);

    /// <summary>Cells filed in the three tiers (index size, for the profiler).</summary>
    public int IndexedCells => _fine.Count + _coarse.Count + _huge.Count;

    /// <summary>
    /// The visibility rule: every op applies at LOD 0; at coarser LODs only ops of at least 2 voxels, and only within the
    /// edit radius unless they are huge (craters show from afar).
    /// </summary>
    public static bool Applies(float radius, int lod, double distance, double editRadius) =>
        lod == 0 || (radius >= VisibleVoxels * (1 << lod) && (distance <= editRadius || radius >= HugeRadius));

    /// <summary>Appends an op; Seq 0 is stamped as the next (the server), otherwise it must follow the last (a client replaying).</summary>
    public EditOp Add(EditOp op)
    {
        if (op.Seq == 0)
            op = op with { Seq = _lastSeq + 1 };
        else if (op.Seq <= _lastSeq)
            throw new ArgumentException($"Op seq {op.Seq} after {_lastSeq}", nameof(op));

        _lastSeq = op.Seq;

        if (_count == _ops.Length)
            Array.Resize(ref _ops, _count * 2);

        var index = _count++;
        _ops[index] = op;

        if (op.Radius >= HugeRadius)
        {
            File(_huge, HugeCellLod, index, op, CoarsestVoxel(op.Radius, PlanetGenerator.MaxLod));
            return op;
        }

        File(_fine, FineCellLod, index, op, 1.0);

        if (op.Radius >= CoarseRadius)
            File(_coarse, CoarseCellLod, index, op, CoarsestVoxel(op.Radius, CoarseMaxLod));

        return op;
    }

    /// <summary>
    /// Copies the ops that apply to <paramref name="node"/> (rule above, reach touching its padded box) into
    /// <paramref name="output"/>, in Seq order, growing it if needed. <paramref name="distance"/> is the node's distance
    /// from the viewer. Returns how many.
    /// </summary>
    public int Gather(in ChunkCoord node, double distance, ref EditOp[] output)
    {
        var count = 0;

        if (node.Lod == 0)
            Collect(_fine, FineCellLod, node, distance, ref output, ref count);
        else if (node.Lod <= CoarseMaxLod)
            Collect(_coarse, CoarseCellLod, node, distance, ref output, ref count);

        Collect(_huge, HugeCellLod, node, distance, ref output, ref count);

        if (count < 2)
            return count;

        var ops = output.AsSpan(0, count);
        ops.Sort(new BySeq());
        var unique = 1;

        for (var i = 1; i < count; i++)
        {
            if (ops[i].Seq != ops[unique - 1].Seq)
                ops[unique++] = ops[i];
        }

        return unique;
    }

    /// <summary>True if any op applies to <paramref name="node"/>: a shell-uniform node must then be generated.</summary>
    public bool Reaches(in ChunkCoord node, double distance)
    {
        if (node.Lod == 0 && Any(_fine, FineCellLod, node, distance))
            return true;

        if (node.Lod is > 0 and <= CoarseMaxLod && Any(_coarse, CoarseCellLod, node, distance))
            return true;

        return Any(_huge, HugeCellLod, node, distance);
    }

    /// <summary>The largest voxel (m) an op of this radius shows at, up to <paramref name="maxLod"/>; LOD 0 always.</summary>
    private static double CoarsestVoxel(float radius, int maxLod)
    {
        var lod = 0;

        while (lod < maxLod && radius >= VisibleVoxels * (1 << (lod + 1)))
            lod++;

        return 1 << lod;
    }

    private void File(Dictionary<ulong, OpBucket> tier, int cellLod, int index, in EditOp op, double voxel)
    {
        op.Centre(out var cx, out var cy, out var cz);
        var reach = op.ReachAt(voxel) + voxel;
        var cell = ChunkConstants.Size * (double)(1 << cellLod);
        var x0 = (int)Math.Floor((cx - reach) / cell);
        var y0 = (int)Math.Floor((cy - reach) / cell);
        var z0 = (int)Math.Floor((cz - reach) / cell);
        var x1 = (int)Math.Floor((cx + reach) / cell);
        var y1 = (int)Math.Floor((cy + reach) / cell);
        var z1 = (int)Math.Floor((cz + reach) / cell);

        for (var z = z0; z <= z1; z++)
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var key = new ChunkCoord(x, y, z, cellLod).Key;

            if (!tier.TryGetValue(key, out var bucket))
            {
                bucket = new OpBucket();
                tier.Add(key, bucket);
            }

            bucket.Add(index);
        }
    }

    /// <summary>Cells of <paramref name="cellLod"/> under the node's own box: one, or 2^(L − cellLod) per axis.</summary>
    private static void CellRange(in ChunkCoord node, int cellLod, out int x0, out int y0, out int z0, out int span)
    {
        if (node.Lod <= cellLod)
        {
            var shift = cellLod - node.Lod;
            x0 = node.X >> shift;
            y0 = node.Y >> shift;
            z0 = node.Z >> shift;
            span = 1;
            return;
        }

        var scale = node.Lod - cellLod;
        x0 = node.X << scale;
        y0 = node.Y << scale;
        z0 = node.Z << scale;
        span = 1 << scale;
    }

    private void Collect(Dictionary<ulong, OpBucket> tier, int cellLod, in ChunkCoord node, double distance, ref EditOp[] output, ref int count)
    {
        if (tier.Count == 0)
            return;

        CellRange(node, cellLod, out var x0, out var y0, out var z0, out var span);

        for (var z = z0; z < z0 + span; z++)
        for (var y = y0; y < y0 + span; y++)
        for (var x = x0; x < x0 + span; x++)
        {
            if (!tier.TryGetValue(new ChunkCoord(x, y, z, cellLod).Key, out var bucket))
                continue;

            for (var i = 0; i < bucket.Count; i++)
            {
                ref readonly var op = ref _ops[bucket.Items[i]];

                if (!Applies(op.Radius, node.Lod, distance, EditRadius) || !PlanetEditor.Intersects(op, node))
                    continue;

                if (count == output.Length)
                    Array.Resize(ref output, Math.Max(16, count * 2));

                output[count++] = op;
            }
        }
    }

    private bool Any(Dictionary<ulong, OpBucket> tier, int cellLod, in ChunkCoord node, double distance)
    {
        if (tier.Count == 0)
            return false;

        CellRange(node, cellLod, out var x0, out var y0, out var z0, out var span);

        for (var z = z0; z < z0 + span; z++)
        for (var y = y0; y < y0 + span; y++)
        for (var x = x0; x < x0 + span; x++)
        {
            if (!tier.TryGetValue(new ChunkCoord(x, y, z, cellLod).Key, out var bucket))
                continue;

            for (var i = 0; i < bucket.Count; i++)
            {
                ref readonly var op = ref _ops[bucket.Items[i]];

                if (Applies(op.Radius, node.Lod, distance, EditRadius) && PlanetEditor.Intersects(op, node))
                    return true;
            }
        }

        return false;
    }
}
