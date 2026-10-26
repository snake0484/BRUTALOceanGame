using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Streams an octree of chunks around the camera (docs/terrain-lod.md): fine voxels near, coarse voxels far.
/// Jobs generate + replay edits + mesh (+ collision for LOD 0); the main thread uploads, swaps LODs without holes,
/// and rebuilds bathymetry. Never waits on the job system; one batch is in flight at a time.
/// </summary>
public sealed class TerrainWorld : IDisposable
{
    public const int MaxLod = 4;
    public const float SplitFactor = 3f;
    public const int RootRadius = 2;
    public const float WorldMinY = ChunkConstants.MinChunkY * ChunkConstants.Size;
    public const float WorldMaxY = (ChunkConstants.MaxChunkY + 1) * ChunkConstants.Size;
    public const int MaxJobsPerBatch = 96;
    private const int JobPoolSize = MaxJobsPerBatch;
    private const int MaxLodCount = MaxLod + 1;

    /// <summary>
    /// LOD-0 chunks within this many chunks of the camera get collision. Kept small because the current Jolt package
    /// lacks native MeshShape, and the triangle-compound fallback costs ~300 bytes per triangle.
    /// </summary>
    public int CollisionRadius = 4;

    public readonly BathymetryGrid Bathymetry = new();
    public TerrainParams Params = new();

    private readonly TerrainRenderer _renderer;
    private readonly PhysicsWorld _physics;
    private readonly JobScheduler _scheduler;
    private readonly Stack<ChunkJob> _pool = new(JobPoolSize);
    private readonly List<ChunkJob> _inFlight = new(JobPoolSize);
    private readonly List<ChunkJob> _awaitingUpload = new(JobPoolSize);
    private readonly Dictionary<ChunkCoord, ChunkRecord> _chunks = new(8192);
    private readonly Stack<ChunkRecord> _recordPool = new(8192);
    private readonly HashSet<ChunkCoord> _desired = new(8192);
    private readonly List<ChunkCoord> _desiredOrder = new(8192);
    private readonly List<ChunkCoord> _scratch = new(8192);
    private static readonly long RetireBudget = Stopwatch.Frequency / 2000;
    private int _retireCursor;
    private readonly List<ChunkCoord> _dirty = new(256);
    private readonly List<BrushOp> _ops = new(1024);
    private readonly int[] _leavesPerLod = new int[MaxLodCount];
    private readonly Comparison<ChunkCoord> _byPriority;
    private Vector3 _camera;
    private ChunkCoord _cameraChunk = new(int.MinValue, 0, 0);
    private bool _bathymetryDirty;
    private bool _bathymetryUrgent;

    // Partial bathymetry rebuilds: footprints of leaves added or retired since the last rebuild, and the strips a re-centre
    // moves in. An edit re-merges just the few cells it touched. A rebuild runs in bands of rows under a per-frame budget,
    // so a big one (the first, or many streaming changes) spreads over a few frames instead of stalling one.
    private const int MaxBathymetryRegions = 16;
    private const int BandRows = 16;
    private static readonly long BathymetryBudget = Stopwatch.Frequency / 1000;
    private readonly List<BathymetryGrid.Region> _bathymetryRegions = new(MaxBathymetryRegions);
    private readonly List<BathymetryGrid.Region> _rebuildQueue = new(MaxBathymetryRegions + 4);
    private int _rebuildRow;
    private double _rebuildMs;
    private bool _bathymetryFull = true;
    private long _lastBathymetryRebuild;
    private static readonly long BathymetryInterval = Stopwatch.Frequency / 4;
    private int _batchJobs;
    private long _batchStart;
    private long _frame;

    private sealed class ChunkRecord
    {
        public int MeshHandle = -1;
        public BodyHandle Body;
        public bool Ready;
        public bool InFlight;
        public bool Dirty;
        public bool HasGeometry;
        public bool HasSeaLayer;

        /// <summary>Any column has a surface top; leaves that are all air or all rock add nothing to the bathymetry.</summary>
        public bool HasTops;
        public long ReadyFrame;
        public readonly float[] ColumnTops = new float[ChunkJob.Columns];
        public readonly float[] ColumnBeds = new float[ChunkJob.Columns];
        public readonly bool[] SeaFlags = new bool[ChunkJob.Columns];

        public void Reset()
        {
            MeshHandle = -1;
            Body = default;
            Ready = false;
            InFlight = false;
            Dirty = false;
            HasGeometry = false;
            HasSeaLayer = false;
        }
    }

    public TerrainWorld(TerrainRenderer renderer, PhysicsWorld physics)
    {
        _renderer = renderer;
        _physics = physics;
        _scheduler = new JobScheduler((uint)Math.Max(1, Environment.ProcessorCount - 2), "Terrain", ThreadPriority.BelowNormal);
        _byPriority = ComparePriority;

        for (var i = 0; i < JobPoolSize; i++)
            _pool.Push(new ChunkJob());
    }

    public int LoadedChunks => _chunks.Count;

    public int DesiredLeaves => _desired.Count;

    public ReadOnlySpan<int> LeavesPerLod => _leavesPerLod;

    public int PendingJobs => _inFlight.Count + _awaitingUpload.Count;

    public int DirtyChunks => _dirty.Count;

    public int EditCount => _ops.Count;

    /// <summary>Every brush op so far, in order. Read-only for other systems (voxel water replays them).</summary>
    public IReadOnlyList<BrushOp> Ops => _ops;

    /// <summary>Raised after an edit is recorded. Main thread.</summary>
    public event Action<BrushOp>? EditApplied;

    /// <summary>Wall time of the last completed batch, and how many chunks it held.</summary>
    public float LastBatchMs { get; private set; }

    public int LastBatchChunks { get; private set; }

    public float LastSelectMs { get; private set; }

    /// <summary>Leaf selections run so far (one per camera chunk change).</summary>
    public int Selects { get; private set; }

    /// <summary>Milliseconds the last retire pass took (part of the stream time in <see cref="LastUpdate"/>).</summary>
    public float LastRetireMs { get; private set; }

    /// <summary>The last bathymetry rebuild covered the whole grid.</summary>
    public bool LastRebuildFull { get; private set; }

    /// <summary>Milliseconds of the last <see cref="Update"/>: (select + collision set, uploads, retire + queue, bathymetry).</summary>
    public (float Select, float Uploads, float Stream, float Bathymetry) LastUpdate { get; private set; }

    /// <summary>
    /// Records a brush edit and schedules every loaded leaf it touches (any LOD, apron included) for regeneration.
    /// </summary>
    public void ApplyBrush(in BrushOp op)
    {
        _ops.Add(op);
        EditApplied?.Invoke(op);

        foreach (var (coord, record) in _chunks)
        {
            if (record.Dirty || !VoxelEditor.Intersects(op, coord))
                continue;

            record.Dirty = true;
            _dirty.Add(coord);
        }
    }

    public void Update(Vector3 cameraPos)
    {
        _camera = cameraPos;
        _frame++;

        if (Bathymetry.Recenter(cameraPos, out var dx, out var dz))
            Recentred(dx, dz);

        var mark = Stopwatch.GetTimestamp();
        var cameraChunk = ChunkCoord.FromWorld(cameraPos);
        if (cameraChunk != _cameraChunk)
        {
            _cameraChunk = cameraChunk;
            SelectLeaves();
            UpdateCollisionSet();
        }

        var select = Lap(ref mark);
        RetryUploads();
        var uploads = Lap(ref mark);
        var stream = 0f;

        if (!_scheduler.IsExecuting)
        {
            CollectBatch();
            uploads += Lap(ref mark);
            Retire();
            LastRetireMs = Lap(ref mark);
            QueueBatch();
            stream = LastRetireMs + Lap(ref mark);
        }

        // Streaming changes are batched at 4 Hz; edits start at once. A started rebuild continues every frame until done.
        if (_rebuildQueue.Count == 0 && _bathymetryDirty && (_bathymetryUrgent || Stopwatch.GetTimestamp() - _lastBathymetryRebuild > BathymetryInterval))
            StartRebuild();

        if (_rebuildQueue.Count > 0)
            ContinueRebuild();

        LastUpdate = (select, uploads, stream, Lap(ref mark));
    }

    private static float Lap(ref long mark)
    {
        var now = Stopwatch.GetTimestamp();
        var ms = (float)Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
        mark = now;
        return ms;
    }

    private void SelectLeaves()
    {
        var start = Stopwatch.GetTimestamp();
        _desired.Clear();
        _desiredOrder.Clear();
        Array.Clear(_leavesPerLod);

        var root = ChunkCoord.FromWorld(_camera, MaxLod);
        var rootSize = ChunkConstants.Size * (1 << MaxLod);
        var yMin = (int)MathF.Floor(WorldMinY / rootSize);
        var yMax = (int)MathF.Floor((WorldMaxY - 1f) / rootSize);

        for (var z = -RootRadius; z <= RootRadius; z++)
            for (var y = yMin; y <= yMax; y++)
                for (var x = -RootRadius; x <= RootRadius; x++)
                    Select(new ChunkCoord(root.X + x, y, root.Z + z, MaxLod));

        _desiredOrder.Sort(_byPriority);
        LastSelectMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Selects++;
    }

    private void Select(ChunkCoord node)
    {
        var size = ChunkConstants.Size * node.VoxelScale;
        var origin = node.WorldOrigin;

        if (origin.Y >= WorldMaxY || origin.Y + size <= WorldMinY)
            return;

        if (node.Lod > 0 && DistanceToNode(node) < SplitFactor * size)
        {
            var lod = node.Lod - 1;
            for (var i = 0; i < 8; i++)
                Select(new ChunkCoord(node.X * 2 + (i & 1), node.Y * 2 + ((i >> 1) & 1), node.Z * 2 + ((i >> 2) & 1), lod));

            return;
        }

        _desired.Add(node);
        _desiredOrder.Add(node);
        _leavesPerLod[node.Lod]++;
    }

    private float DistanceToNode(ChunkCoord node)
    {
        var size = ChunkConstants.Size * node.VoxelScale;
        var min = node.WorldOrigin;
        var closest = Vector3.Clamp(_camera, min, min + new Vector3(size));
        return Vector3.Distance(_camera, closest);
    }

    private int ComparePriority(ChunkCoord a, ChunkCoord b) =>
        (DistanceToNode(a) / a.VoxelScale).CompareTo(DistanceToNode(b) / b.VoxelScale);

    private void CollectBatch()
    {
        if (_inFlight.Count == 0)
            return;

        LastBatchMs = (float)Stopwatch.GetElapsedTime(_batchStart).TotalMilliseconds;
        LastBatchChunks = _batchJobs;

        foreach (var job in _inFlight)
        {
            if (_chunks.TryGetValue(job.Chunk.Coord, out var record))
            {
                record.InFlight = false;
                _awaitingUpload.Add(job);
            }
            else
            {
                Recycle(job);
            }
        }

        _inFlight.Clear();
        RetryUploads();
    }

    private void RetryUploads()
    {
        for (var i = _awaitingUpload.Count - 1; i >= 0; i--)
        {
            var job = _awaitingUpload[i];

            if (!_chunks.TryGetValue(job.Chunk.Coord, out var record))
            {
                _awaitingUpload.RemoveAt(i);
                Recycle(job);
                continue;
            }

            var handle = _renderer.Upload(job.Chunk.Coord, job.Mesh);
            if (handle < 0)
                continue;

            if (record.MeshHandle >= 0)
                _renderer.Free(record.MeshHandle);

            if (record.Body.IsValid)
                _physics.Remove(record.Body);

            record.MeshHandle = handle;
            record.HasGeometry = job.Mesh.IndexCount > 0;
            record.Body = job.Shape is null ? default : _physics.AddStatic(job.Shape, job.Chunk.Coord.WorldOrigin);
            job.Shape = null;

            // A re-meshed leaf only dirties the columns that changed: edits remesh coarse leaves too, whose whole
            // footprint (up to 512 m) would otherwise force a near-full bathymetry rebuild.
            if (record.Ready && record.HasSeaLayer == job.HasSeaLayer)
                MarkBathymetry(job.Chunk.Coord, ChangedColumns(record, job));
            else
                MarkBathymetry(job.Chunk.Coord);

            job.ColumnTops.CopyTo(record.ColumnTops, 0);
            job.ColumnBeds.CopyTo(record.ColumnBeds, 0);
            job.SeaFlags.CopyTo(record.SeaFlags, 0);
            record.HasSeaLayer = job.HasSeaLayer;
            record.HasTops = false;
            foreach (var top in record.ColumnTops)
            {
                if (float.IsNaN(top))
                    continue;

                record.HasTops = true;
                break;
            }
            record.Ready = true;
            record.ReadyFrame = _frame;

            if (job.HadEdits)
                _bathymetryUrgent = true;

            if (record.Dirty && !_dirty.Contains(job.Chunk.Coord))
                _dirty.Add(job.Chunk.Coord);

            _awaitingUpload.RemoveAt(i);
            _pool.Push(job);
        }
    }

    private void Recycle(ChunkJob job)
    {
        job.Shape?.Dispose();
        job.Shape = null;
        _pool.Push(job);
    }

    /// <summary>
    /// Frees loaded leaves that are no longer desired once whatever replaces their volume is ready, so LOD swaps
    /// never open holes. Checking that a coarse leaf is covered walks its descendants, so the checks run under a time
    /// budget and resume where they stopped; a leaf retiring a few frames late just draws a little longer.
    /// </summary>
    private void Retire()
    {
        _scratch.Clear();

        foreach (var (coord, record) in _chunks)
        {
            if (!_desired.Contains(coord) && !record.InFlight && record.Ready)
                _scratch.Add(coord);
        }

        var start = Stopwatch.GetTimestamp();
        var candidates = _scratch.Count;
        var checkedCount = 0;

        for (; checkedCount < candidates && Stopwatch.GetTimestamp() - start < RetireBudget; checkedCount++)
        {
            var coord = _scratch[(_retireCursor + checkedCount) % candidates];
            if (!Covered(coord))
                continue;

            _chunks.Remove(coord, out var record);
            _renderer.Free(record!.MeshHandle);

            if (record.Body.IsValid)
                _physics.Remove(record.Body);

            if (record.Dirty)
                _dirty.Remove(coord);

            _recordPool.Push(record);
            MarkBathymetry(coord);
        }

        _retireCursor = candidates == 0 ? 0 : (_retireCursor + checkedCount) % candidates;
    }

    private bool Covered(ChunkCoord coord)
    {
        for (var ancestor = coord.Parent; ancestor.Lod <= MaxLod; ancestor = ancestor.Parent)
        {
            if (_desired.Contains(ancestor))
                return IsReady(ancestor);
        }

        return DescendantsReady(coord);
    }

    /// <summary>
    /// Every desired leaf inside the node's volume is ready. The octree is aligned, so a finer leaf is inside exactly when
    /// its origin is: one pass over the desired leaves, rather than walking up to 8^lod descendants of a coarse node.
    /// </summary>
    private bool DescendantsReady(ChunkCoord node)
    {
        if (_desired.Contains(node))
            return IsReady(node);

        var min = node.WorldOrigin;
        var max = min + new Vector3(ChunkConstants.Size * node.VoxelScale);

        foreach (var leaf in _desiredOrder)
        {
            if (leaf.Lod >= node.Lod)
                continue;

            var origin = leaf.WorldOrigin;
            if (origin.X < min.X || origin.X >= max.X || origin.Y < min.Y || origin.Y >= max.Y || origin.Z < min.Z || origin.Z >= max.Z)
                continue;

            if (!IsReady(leaf))
                return false;
        }

        return true;
    }

    /// <summary>Ready and already drawable: the renderer draws an upload from the frame after it was made.</summary>
    private bool IsReady(ChunkCoord coord) => _chunks.TryGetValue(coord, out var record) && record.Ready && record.ReadyFrame < _frame;

    private void QueueBatch()
    {
        _batchJobs = 0;

        // Edits first: they are what the player is looking at.
        for (var i = _dirty.Count - 1; i >= 0 && _pool.Count > 0 && _batchJobs < MaxJobsPerBatch; i--)
        {
            var coord = _dirty[i];
            var record = _chunks[coord];

            if (record.InFlight || !record.Ready)
                continue;

            record.Dirty = false;
            record.InFlight = true;
            _dirty.RemoveAt(i);
            Queue(coord);
        }

        foreach (var coord in _desiredOrder)
        {
            if (_pool.Count == 0 || _batchJobs == MaxJobsPerBatch)
                break;

            if (_chunks.ContainsKey(coord))
                continue;

            var record = _recordPool.Count > 0 ? _recordPool.Pop() : new ChunkRecord();
            record.Reset();
            record.InFlight = true;
            _chunks.Add(coord, record);
            Queue(coord);
        }

        if (_batchJobs == 0)
            return;

        _batchStart = Stopwatch.GetTimestamp();
        _scheduler.ExecuteJobs();
    }

    private void Queue(ChunkCoord coord)
    {
        var job = _pool.Pop();
        job.Chunk.Coord = coord;
        job.Params = Params;
        job.SetOps(_ops);
        job.BuildCollision = InCollisionRange(coord);
        _scheduler.Queue(job, 1);
        _inFlight.Add(job);
        _batchJobs++;
    }

    private bool InCollisionRange(ChunkCoord coord)
    {
        if (coord.Lod != 0)
            return false;

        var dx = coord.X - _cameraChunk.X;
        var dz = coord.Z - _cameraChunk.Z;
        return dx * dx + dz * dz <= CollisionRadius * CollisionRadius;
    }

    /// <summary>
    /// Drop bodies that left the collision radius, and re-run LOD-0 chunks that entered it
    /// (regenerate + mesh is cheaper than keeping voxels around).
    /// </summary>
    private void UpdateCollisionSet()
    {
        foreach (var (coord, record) in _chunks)
        {
            if (!record.Ready)
                continue;

            var inRange = InCollisionRange(coord);

            if (!inRange && record.Body.IsValid)
            {
                _physics.Remove(record.Body);
                record.Body = default;
            }
            else if (inRange && !record.Body.IsValid && record.HasGeometry && !record.Dirty)
            {
                record.Dirty = true;
                _dirty.Add(coord);
            }
        }
    }

    private static bool Same(float a, float b) => a == b || (float.IsNaN(a) && float.IsNaN(b));

    /// <summary>Voxel-column bounds [x0, x1) × [z0, z1) where a re-meshed leaf's tops, beds or sea flags differ from its last ones.</summary>
    private static (int X0, int Z0, int X1, int Z1) ChangedColumns(ChunkRecord record, ChunkJob job)
    {
        const int size = ChunkConstants.Size;
        var (x0, z0, x1, z1) = (size, size, 0, 0);

        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = x + z * size;
                var same = Same(record.ColumnTops[i], job.ColumnTops[i]) && Same(record.ColumnBeds[i], job.ColumnBeds[i]) && record.SeaFlags[i] == job.SeaFlags[i];
                if (same)
                    continue;

                (x0, z0, x1, z1) = (Math.Min(x0, x), Math.Min(z0, z), Math.Max(x1, x + 1), Math.Max(z1, z + 1));
            }
        }

        return (x0, z0, x1, z1);
    }

    /// <summary>Dirties the cells under voxel columns [x0, x1) × [z0, z1) of a leaf.</summary>
    private void MarkBathymetry(ChunkCoord coord, (int X0, int Z0, int X1, int Z1) columns)
    {
        if (columns.X1 <= columns.X0 || columns.Z1 <= columns.Z0)
            return;

        var scale = coord.VoxelScale;
        var origin = coord.WorldOrigin;
        var o = Bathymetry.Origin;
        const float cell = BathymetryGrid.CellSize;
        var region = new BathymetryGrid.Region(
            Math.Max(0, (int)MathF.Floor((origin.X + columns.X0 * scale - o.X) / cell)),
            Math.Max(0, (int)MathF.Floor((origin.Z + columns.Z0 * scale - o.Y) / cell)),
            Math.Min(BathymetryGrid.Resolution, (int)MathF.Ceiling((origin.X + columns.X1 * scale - o.X) / cell)),
            Math.Min(BathymetryGrid.Resolution, (int)MathF.Ceiling((origin.Z + columns.Z1 * scale - o.Y) / cell)));

        AddBathymetryRegion(region);
    }

    private void MarkBathymetry(ChunkCoord coord) => AddBathymetryRegion(Bathymetry.CellsOf(coord));

    private void AddBathymetryRegion(BathymetryGrid.Region cells)
    {
        _bathymetryDirty = true;
        if (cells.Empty || _bathymetryFull)
            return;

        // Merge into a region it overlaps, unless the bounding box would cover far more than the two do (two strips
        // meeting at a corner would otherwise become the whole grid).
        for (var i = 0; i < _bathymetryRegions.Count; i++)
        {
            var union = _bathymetryRegions[i].Union(cells);
            if (!_bathymetryRegions[i].Intersects(cells) || union.Area > 5 * (_bathymetryRegions[i].Area + cells.Area) / 4)
                continue;

            _bathymetryRegions[i] = union;
            return;
        }

        if (_bathymetryRegions.Count < MaxBathymetryRegions)
        {
            _bathymetryRegions.Add(cells);
            return;
        }

        // Too many separate changes: fold them into one bounding region; only half the grid or more is worth a full rebuild.
        var bounds = cells;
        foreach (var region in _bathymetryRegions)
            bounds = bounds.Union(region);

        _bathymetryRegions.Clear();
        var area = (bounds.X1 - bounds.X0) * (bounds.Z1 - bounds.Z0);

        if (area * 2 >= BathymetryGrid.Resolution * BathymetryGrid.Resolution)
            _bathymetryFull = true;
        else
            _bathymetryRegions.Add(bounds);
    }

    /// <summary>
    /// The bathymetry grid moved by (dx, dz) cells with its data: pending and in-progress rebuild regions move with it,
    /// and the strips that came in from outside are rebuilt. A jump of a whole grid or more rebuilds everything.
    /// </summary>
    private void Recentred(int dx, int dz)
    {
        const int n = BathymetryGrid.Resolution;
        _bathymetryDirty = true;

        if (Math.Abs(dx) >= n || Math.Abs(dz) >= n)
        {
            _bathymetryFull = true;
            _bathymetryRegions.Clear();
            _rebuildQueue.Clear();
            return;
        }

        // Unfinished work goes back to pending, moved with the data.
        if (_rebuildQueue.Count > 0)
        {
            var current = _rebuildQueue[0];
            _rebuildQueue[0] = current with { Z0 = _rebuildRow };
            foreach (var region in _rebuildQueue)
                _bathymetryRegions.Add(region);

            _rebuildQueue.Clear();
        }

        for (var i = _bathymetryRegions.Count - 1; i >= 0; i--)
        {
            var moved = _bathymetryRegions[i].Shifted(dx, dz);
            if (moved.Empty)
                _bathymetryRegions.RemoveAt(i);
            else
                _bathymetryRegions[i] = moved;
        }

        Span<BathymetryGrid.Region> strips = stackalloc BathymetryGrid.Region[2];
        var count = BathymetryGrid.Exposed(dx, dz, strips);
        for (var i = 0; i < count; i++)
            AddBathymetryRegion(strips[i]);
    }

    private void StartRebuild()
    {
        _bathymetryDirty = false;
        _bathymetryUrgent = false;
        _lastBathymetryRebuild = Stopwatch.GetTimestamp();
        _rebuildMs = 0.0;
        LastRebuildFull = _bathymetryFull;

        if (_bathymetryFull)
            _rebuildQueue.Add(BathymetryGrid.Region.All);
        else
            _rebuildQueue.AddRange(_bathymetryRegions);

        _bathymetryFull = false;
        _bathymetryRegions.Clear();
        _rebuildRow = _rebuildQueue.Count > 0 ? _rebuildQueue[0].Z0 : 0;
    }

    /// <summary>Re-merges bands of rows until the frame's budget is spent (at least one band); the last band ends the rebuild.</summary>
    private void ContinueRebuild()
    {
        var start = Stopwatch.GetTimestamp();

        do
        {
            var region = _rebuildQueue[0];
            var band = region with { Z0 = _rebuildRow, Z1 = Math.Min(_rebuildRow + BandRows, region.Z1) };
            Bathymetry.BeginRegion(band);
            MergeLeaves(band);
            Bathymetry.EndRegion(band);
            _rebuildRow = band.Z1;

            if (_rebuildRow >= region.Z1)
            {
                _rebuildQueue.RemoveAt(0);
                _rebuildRow = _rebuildQueue.Count > 0 ? _rebuildQueue[0].Z0 : 0;
            }
        }
        while (_rebuildQueue.Count > 0 && Stopwatch.GetTimestamp() - start < BathymetryBudget);

        _rebuildMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (_rebuildQueue.Count == 0)
            Bathymetry.EndRebuild(_rebuildMs);
    }

    private void MergeLeaves(in BathymetryGrid.Region region)
    {
        foreach (var (coord, record) in _chunks)
        {
            if (!record.Ready || (!record.HasTops && !record.HasSeaLayer))
                continue;

            var cells = Bathymetry.CellsOf(coord);
            if (cells.Intersects(region))
                Bathymetry.MergeLeaf(coord, record.ColumnTops, record.ColumnBeds, record.SeaFlags, record.HasSeaLayer, region);
        }
    }

    public void Dispose()
    {
        _scheduler.AbortJobs(waitForCompletion: true);

        foreach (var job in _inFlight)
            Recycle(job);

        foreach (var job in _awaitingUpload)
            Recycle(job);
    }
}
