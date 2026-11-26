using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using BrutalGame.Engine.Jobs;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// Streams the focus body's octree of chunks around the camera (planets.md "Streaming at flight speeds"), in the body's
/// rotating frame with its centre at the origin: leaf selection runs on its own job (<see cref="PlanetLeafSelector"/>),
/// chunk jobs generate + replay edits + mesh (+ collision for LOD-0 leaves in a ring round the camera), and the main
/// thread uploads and swaps LODs without holes. Never waits on the job system; one chunk batch is in flight at a time.
/// Collision bodies sit in the physics world's bubble (<see cref="PhysicsBubble"/>), for the one body that has it; their
/// shapes are pooled. Every rocky body's height bounds are built at load; <see cref="SetFocus"/> moves the octree to another
/// body (or none, past the impostor switch) once nothing is in flight, freeing the old body's leaves at once: the switch
/// happens where neither octree is drawn. Each body keeps its own edit replica.
/// </summary>
public sealed class TerrainWorld : IDisposable
{
    public const int MaxJobsPerBatch = 96;

    /// <summary>LOD-0 leaves whose centre lies within this distance (m) of the camera get collision.</summary>
    public const double CollisionRadius = 128.0;

    private const int JobPoolSize = MaxJobsPerBatch;
    private const int ShapePoolSize = 192;

    /// <summary>With the arenas full, at most this many uncovered leaves are freed per frame.</summary>
    private const int MaxForcedRetires = 32;

    /// <summary>Reselect once the camera has moved this share of the finest leaf's size.</summary>
    private const double ReselectShare = 0.25;

    /// <summary>The speed bias steps back to a finer LOD only once 1/this of the speed would allow it.</summary>
    private const double SpeedLodHysteresis = 0.7;

    /// <summary>Horizon pruning keeps nodes this far (m) past the horizon, plus <see cref="HorizonLeadSeconds"/> of travel, so they load before they can show.</summary>
    private const double HorizonMargin = 2000.0;
    private const double HorizonLeadSeconds = 2.0;

    private static readonly long RetireBudget = Stopwatch.Frequency / 2000;

    private readonly TerrainRenderer _renderer;
    private readonly PhysicsWorld _physics;
    private readonly JobScheduler _scheduler;
    private readonly JobScheduler _selectScheduler;
    private readonly Body[] _bodies;
    private readonly SelectJob _selectJob = new();
    private readonly Stack<ChunkJob> _pool = new(JobPoolSize);
    private readonly List<ChunkJob> _inFlight = new(JobPoolSize);
    private readonly List<ChunkJob> _awaitingUpload = new(JobPoolSize);
    private readonly Dictionary<ulong, ChunkRecord> _chunks = new(8192);
    private readonly Stack<ChunkRecord> _recordPool = new(8192);
    private readonly Stack<StaticMeshShape> _shapes = new(ShapePoolSize);
    private readonly List<ChunkCoord> _scratch = new(8192);
    private readonly List<ChunkCoord> _dirty = new(256);
    private readonly List<EditOp> _pendingOps = new(64);
    private readonly HashSet<ulong> _drawable = new(8192);
    private readonly HashSet<ulong> _drawableAncestors = new(16384);
    private readonly int[] _holeLods = new int[PlanetLeafSelector.MaxLeafLod + 1];
    private LeafSet _desired = new();
    private Body? _body;
    private int _focus = -1;
    private int _requestedFocus;
    private int _retireCursor;
    private double3 _camera;
    private double _speed;
    private double3 _selectedAt;
    private ChunkCoord _cameraChunk = new(int.MinValue, 0, 0);
    private bool _selecting;
    private bool _reselect = true;
    private bool _starved;
    private int _minLod;
    private int _batchJobs;
    private long _batchStart;
    private long _frame;

    private sealed class ChunkRecord
    {
        public ChunkCoord Coord;
        public int MeshHandle = -1;
        public BodyHandle Body;
        public StaticMeshShape? Shape;
        public bool Ready;
        public bool InFlight;
        public bool Dirty;
        public bool HasGeometry;
        public long ReadyFrame;

        public void Reset()
        {
            MeshHandle = -1;
            Body = default;
            Shape = null;
            Ready = false;
            InFlight = false;
            Dirty = false;
            HasGeometry = false;
        }
    }

    private sealed class BoundsJob(PlanetHeightBounds bounds, int face) : IJob
    {
        public void Run() => bounds.BuildFace(face);
    }

    /// <summary>One rocky body the octree can stream: generator, height bounds, this client's edit replica and selector.</summary>
    private sealed class Body
    {
        public readonly PlanetGenerator Generator;
        public readonly PlanetHeightBounds Bounds;

        /// <summary>Written only between selections while this body is the focus.</summary>
        public readonly EditStore Edits = new();
        public readonly bool Collision;

        public Body(PlanetGenerator generator, bool collision)
        {
            Generator = generator;
            Bounds = new PlanetHeightBounds(generator);
            Collision = collision;
        }

        /// <summary>Made once the bounds are built: its horizon occluder is their lowest ground.</summary>
        public PlanetLeafSelector Selector { get; private set; } = null!;

        public void BoundsBuilt() => Selector = new PlanetLeafSelector(Bounds, Edits);
    }

    /// <summary>
    /// Builds every body's height bounds on the streaming workers (blocks, once at load). Only
    /// <paramref name="collisionBody"/> gets collision (the physics bubble is on it); <paramref name="focus"/> streams first.
    /// </summary>
    public TerrainWorld(TerrainRenderer renderer, PhysicsWorld physics, ReadOnlySpan<PlanetGenerator> bodies, int collisionBody, int focus)
    {
        _renderer = renderer;
        _physics = physics;
        _scheduler = new JobScheduler((uint)WorkerBudget.Streaming, "Terrain", ThreadPriority.BelowNormal);
        _selectScheduler = new JobScheduler(1, "TerrainSelect", ThreadPriority.BelowNormal);
        _bodies = new Body[bodies.Length];

        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < bodies.Length; i++)
        {
            _bodies[i] = new Body(bodies[i], i == collisionBody);

            for (var face = 0; face < CubeLattice.Faces; face++)
                _scheduler.Queue(new BoundsJob(_bodies[i].Bounds, face), 1);
        }

        _scheduler.ExecuteJobs();
        _scheduler.Wait();

        foreach (var body in _bodies)
            body.BoundsBuilt();

        BoundsMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _requestedFocus = focus;

        for (var i = 0; i < JobPoolSize; i++)
            _pool.Push(new ChunkJob());

        for (var i = 0; i < ShapePoolSize; i++)
            _shapes.Push(new StaticMeshShape());
    }

    /// <summary>Wall time every body's height bounds took to build at load.</summary>
    public float BoundsMs { get; }

    /// <summary>The body the octree streams (index into the constructor's list), −1 for none.</summary>
    public int Focus => _focus;

    /// <summary>A focus change waits for the selection and batch in flight.</summary>
    public bool Switching => _requestedFocus != _focus;

    /// <summary>The focus body's generator; null with no focus.</summary>
    public PlanetGenerator? Generator => _body?.Generator;

    /// <summary>Focus switches made, since start.</summary>
    public int FocusSwitches { get; private set; }

    /// <summary>Streams <paramref name="body"/> (−1: none, every leaf freed) from the next <see cref="Update"/> that finds nothing in flight.</summary>
    public void SetFocus(int body)
    {
        if ((uint)(body + 1) > (uint)_bodies.Length)
            throw new ArgumentOutOfRangeException(nameof(body));

        _requestedFocus = body;
    }

    public int LoadedChunks => _chunks.Count;

    /// <summary>Loaded leaves with geometry (renderer slots in use).</summary>
    public int MeshedChunks => _renderer.ChunkCount;

    /// <summary>Vertex and index arena bytes in use.</summary>
    public long MeshBytes => _renderer.VertexBytesUsed + _renderer.IndexBytesUsed;

    /// <summary>The last <see cref="CountHoles"/>'s holes per LOD.</summary>
    public ReadOnlySpan<int> HoleLods => _holeLods;

    public int DesiredLeaves => _desired.Count;

    public ReadOnlySpan<int> LeavesPerLod => _desired.PerLod;

    /// <summary>The speed bias's finest LOD in use.</summary>
    public int MinLod => _minLod;

    public int PendingJobs => _inFlight.Count + _awaitingUpload.Count;

    public int DirtyChunks => _dirty.Count;

    /// <summary>The focus body's edits, accepted ones included.</summary>
    public int EditCount => _focus < 0 ? 0 : EditCountOf(_focus);

    /// <summary>Edits this client holds for <paramref name="body"/>, accepted ones included.</summary>
    public int EditCountOf(int body) => _bodies[body].Edits.Count + (body == _focus ? _pendingOps.Count : 0);

    public int CollisionBodies { get; private set; }

    /// <summary>Leaves loaded for the first time (not re-meshes), since start.</summary>
    public long LeavesLoaded { get; private set; }

    /// <summary>Chunk jobs finished, since start.</summary>
    public long JobsCompleted { get; private set; }

    /// <summary>Wall time of the last completed batch, and how many chunks it held.</summary>
    public float LastBatchMs { get; private set; }

    public int LastBatchChunks { get; private set; }

    /// <summary>Worker time of the last leaf selection, and the octree nodes it visited.</summary>
    public float LastSelectMs => _desired.Ms;

    public int LastSelectVisits => _desired.Visits;

    /// <summary>Leaf selections swapped in so far.</summary>
    public int Selects { get; private set; }

    /// <summary>Leaves freed before their volume was covered because the renderer's arenas were full (may open a hole).</summary>
    public long ForcedRetires { get; private set; }

    /// <summary>Milliseconds the last retire pass took (part of the stream time in <see cref="LastUpdate"/>).</summary>
    public float LastRetireMs { get; private set; }

    /// <summary>Milliseconds of the last <see cref="Update"/>: (selection swap + collision set, uploads, retire + queue).</summary>
    public (float Select, float Uploads, float Stream) LastUpdate { get; private set; }

    /// <summary>Nothing left to do: the selection is current and every desired leaf is loaded and ready.</summary>
    public bool Settled { get; private set; }

    /// <summary>Records an edit the sim accepted on <paramref name="body"/>; on the focus body, every loaded leaf it shows on regenerates.</summary>
    public void ApplyEdit(int body, in EditOp op)
    {
        if (body != _focus)
        {
            _bodies[body].Edits.Add(op);
            return;
        }

        if (_selecting)
        {
            _pendingOps.Add(op);
            return;
        }

        AddEdit(op);
    }

    private void AddEdit(in EditOp op)
    {
        var edits = _body!.Edits;
        edits.Add(op);
        _reselect = true;

        foreach (var record in _chunks.Values)
        {
            var coord = record.Coord;

            if (record.Dirty || !EditStore.Applies(op.Radius, coord.Lod, NodeDistance(coord), edits.EditRadius) || !PlanetEditor.Intersects(op, coord))
                continue;

            record.Dirty = true;
            _dirty.Add(coord);
        }
    }

    /// <param name="camera">The camera in the focus body's frame (m from its centre).</param>
    /// <param name="speed">The camera's speed (m/s) for the speed LOD bias.</param>
    public void Update(double3 camera, double speed)
    {
        _camera = camera;
        _speed = speed;
        _frame++;

        var mark = Stopwatch.GetTimestamp();

        if (_requestedFocus != _focus && !_selectScheduler.IsExecuting && !_scheduler.IsExecuting)
            SwitchBody();

        if (_body is null)
        {
            Settled = false;
            LastUpdate = default;
            return;
        }

        if (_selecting && !_selectScheduler.IsExecuting)
            CompleteSelection();

        UpdateMinLod(speed);

        if (!_selecting && NeedsSelection())
            StartSelection();

        var cameraChunk = ChunkCoord.FromPlanet(camera.X, camera.Y, camera.Z, 0);

        if (cameraChunk != _cameraChunk)
        {
            _cameraChunk = cameraChunk;
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
            Settled = !_selecting && !_reselect && _batchJobs == 0 && _awaitingUpload.Count == 0 && _desired.Count > 0;
        }
        else
        {
            Settled = false;
        }

        LastUpdate = (select, uploads, stream);
    }

    private static float Lap(ref long mark)
    {
        var now = Stopwatch.GetTimestamp();
        var ms = (float)Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
        mark = now;
        return ms;
    }

    private void UpdateMinLod(double speed)
    {
        var target = PlanetLeafSelector.SpeedLod(speed);

        if (target > _minLod)
            _minLod = target;
        else
            _minLod = Math.Min(_minLod, PlanetLeafSelector.SpeedLod(speed / SpeedLodHysteresis));
    }

    private bool NeedsSelection()
    {
        if (_reselect || _minLod != _desired.MinLod)
            return true;

        var threshold = ReselectShare * ChunkConstants.Size * (double)(1 << _desired.FinestLod);
        var moved = _camera - _selectedAt;
        return double3.Dot(moved, moved) > threshold * threshold;
    }

    /// <summary>
    /// Moves the octree to the requested body with nothing in flight: the finished selection and batch are dropped, every
    /// leaf is freed, and edits accepted during the last selection go into the old body's store.
    /// </summary>
    private void SwitchBody()
    {
        if (_selecting)
            CompleteSelection();

        foreach (var job in _inFlight)
            Recycle(job);

        foreach (var job in _awaitingUpload)
            Recycle(job);

        foreach (var record in _chunks.Values)
        {
            if (record.MeshHandle >= 0)
                _renderer.Free(record.MeshHandle);

            RemoveBody(record);
            _recordPool.Push(record);
        }

        _inFlight.Clear();
        _awaitingUpload.Clear();
        _chunks.Clear();
        _dirty.Clear();
        _desired.Clear();
        (_batchJobs, _retireCursor) = (0, 0);
        _focus = _requestedFocus;
        _body = _focus >= 0 ? _bodies[_focus] : null;
        _selectJob.Selector = _body?.Selector!;
        _cameraChunk = new ChunkCoord(int.MinValue, 0, 0);
        _reselect = true;
        Settled = false;
        FocusSwitches++;
    }

    private void StartSelection()
    {
        _selectJob.Camera = _camera;
        _selectJob.MinLod = _minLod;
        _selectJob.HorizonMargin = HorizonMargin + _speed * HorizonLeadSeconds;
        _selectScheduler.Queue(_selectJob, 1);
        _selectScheduler.ExecuteJobs();
        _selectedAt = _camera;
        _reselect = false;
        _selecting = true;
    }

    /// <summary>Swaps the finished selection in, then applies the edits that arrived while it ran.</summary>
    private void CompleteSelection()
    {
        (_desired, _selectJob.Output) = (_selectJob.Output, _desired);
        _selecting = false;
        _retireCursor = 0;
        Selects++;

        foreach (var op in _pendingOps)
            AddEdit(op);

        _pendingOps.Clear();
    }

    private void CollectBatch()
    {
        if (_inFlight.Count == 0)
            return;

        LastBatchMs = (float)Stopwatch.GetElapsedTime(_batchStart).TotalMilliseconds;
        LastBatchChunks = _batchJobs;
        JobsCompleted += _inFlight.Count;

        foreach (var job in _inFlight)
        {
            if (_chunks.TryGetValue(job.Chunk.Coord.Key, out var record))
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
            var coord = job.Chunk.Coord;

            if (!_chunks.TryGetValue(coord.Key, out var record))
            {
                _awaitingUpload.RemoveAt(i);
                Recycle(job);
                continue;
            }

            // Leaves with no surface take no renderer slot: most of the shell's conservative band is empty.
            var empty = job.Mesh.IndexCount == 0;
            var handle = empty ? -1 : _renderer.Upload(coord, job.Mesh);

            if (!empty && handle < 0)
            {
                _starved |= handle == TerrainRenderer.UploadNoRoom;
                continue;
            }

            if (record.MeshHandle >= 0)
                _renderer.Free(record.MeshHandle);

            RemoveBody(record);
            record.MeshHandle = handle;
            record.HasGeometry = !empty;

            if (job.Shape is { IsBuilt: true } shape)
            {
                record.Body = _physics.AddStatic(shape, _physics.Bubble.ToLocal(coord.Origin));
                record.Shape = shape;
                CollisionBodies++;
            }
            else if (job.Shape is not null)
            {
                ReturnShape(job.Shape);
            }

            job.Shape = null;

            if (!record.Ready)
                LeavesLoaded++;

            record.Ready = true;
            record.ReadyFrame = _frame;

            if (record.Dirty && !_dirty.Contains(coord))
                _dirty.Add(coord);

            _awaitingUpload.RemoveAt(i);
            _pool.Push(job);
        }
    }

    private void RemoveBody(ChunkRecord record)
    {
        if (!record.Body.IsValid)
            return;

        _physics.Remove(record.Body);
        record.Body = default;
        CollisionBodies--;

        if (record.Shape is null)
            return;

        ReturnShape(record.Shape);
        record.Shape = null;
    }

    private StaticMeshShape RentShape() => _shapes.Count > 0 ? _shapes.Pop() : new StaticMeshShape();

    private void ReturnShape(StaticMeshShape shape)
    {
        shape.Release();
        _shapes.Push(shape);
    }

    private void Recycle(ChunkJob job)
    {
        if (job.Shape is not null)
            ReturnShape(job.Shape);

        job.Shape = null;
        _pool.Push(job);
    }

    /// <summary>
    /// Frees loaded leaves that are no longer desired once whatever replaces their volume is ready, so LOD swaps
    /// never open holes. Checking that a coarse leaf is covered walks the selection's split nodes, so the checks run under a time
    /// budget and resume where they stopped; a leaf retiring a few frames late just draws a little longer. If an upload
    /// found the arenas full, a few uncovered ones are freed too: a brief hole beats a deadlock.
    /// </summary>
    private void Retire()
    {
        var force = _starved;
        _starved = false;
        _scratch.Clear();

        foreach (var record in _chunks.Values)
        {
            if (!_desired.Leaves.Contains(record.Coord.Key) && !record.InFlight && record.Ready)
                _scratch.Add(record.Coord);
        }

        var start = Stopwatch.GetTimestamp();
        var candidates = _scratch.Count;
        var checkedCount = 0;
        var forced = 0;

        for (; checkedCount < candidates && Stopwatch.GetTimestamp() - start < RetireBudget; checkedCount++)
        {
            var coord = _scratch[(_retireCursor + checkedCount) % candidates];

            if (!Covered(coord))
            {
                if (!force || forced == MaxForcedRetires)
                    continue;

                forced++;
                ForcedRetires++;
            }

            _chunks.Remove(coord.Key, out var record);
            _renderer.Free(record!.MeshHandle);
            RemoveBody(record);

            if (record.Dirty)
                _dirty.Remove(coord);

            _recordPool.Push(record);
        }

        _retireCursor = candidates == 0 ? 0 : (_retireCursor + checkedCount) % candidates;
    }

    private bool Covered(ChunkCoord coord)
    {
        for (var ancestor = coord.Parent; ancestor.Lod <= PlanetLeafSelector.MaxLeafLod; ancestor = ancestor.Parent)
        {
            if (_desired.Leaves.Contains(ancestor.Key))
                return IsReady(ancestor);
        }

        return DescendantsReady(coord);
    }

    /// <summary>
    /// Every desired leaf inside the node's volume is ready: a walk down the selection's split nodes. A node that is
    /// neither a leaf nor split is uniform or below the horizon, so it needs nothing.
    /// </summary>
    private bool DescendantsReady(ChunkCoord node)
    {
        if (_desired.Leaves.Contains(node.Key))
            return IsReady(node);

        if (!_desired.Internal.Contains(node.Key))
            return true;

        for (var i = 0; i < 8; i++)
        {
            if (!DescendantsReady(new ChunkCoord(node.X * 2 + (i & 1), node.Y * 2 + ((i >> 1) & 1), node.Z * 2 + ((i >> 2) & 1), node.Lod - 1)))
                return false;
        }

        return true;
    }

    /// <summary>Ready and already drawable: the renderer draws an upload from the frame after it was made.</summary>
    private bool IsReady(ChunkCoord coord) => _chunks.TryGetValue(coord.Key, out var record) && record.Ready && record.ReadyFrame < _frame;

    private void QueueBatch()
    {
        _batchJobs = 0;

        // Edits first: they are what the player is looking at.
        for (var i = _dirty.Count - 1; i >= 0 && _pool.Count > 0 && _batchJobs < MaxJobsPerBatch; i--)
        {
            var coord = _dirty[i];
            var record = _chunks[coord.Key];

            if (record.InFlight || !record.Ready)
                continue;

            record.Dirty = false;
            record.InFlight = true;
            _dirty.RemoveAt(i);
            Queue(coord);
        }

        foreach (var coord in _desired.Order)
        {
            if (_pool.Count == 0 || _batchJobs == MaxJobsPerBatch)
                break;

            if (_chunks.ContainsKey(coord.Key))
                continue;

            var record = _recordPool.Count > 0 ? _recordPool.Pop() : new ChunkRecord();
            record.Reset();
            record.Coord = coord;
            record.InFlight = true;
            _chunks.Add(coord.Key, record);
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
        job.Generator = _body!.Generator;
        job.GatherOps(_body.Edits, NodeDistance(coord));
        job.Shape = InCollisionRange(coord) ? RentShape() : null;
        job.BubbleRotation = _physics.Bubble.PlanetToLocal;
        _scheduler.Queue(job, 1 + coord.Lod / 4);
        _inFlight.Add(job);
        _batchJobs++;
    }

    private double NodeDistance(ChunkCoord coord)
    {
        var size = coord.SizeMetres;
        var origin = coord.Origin;
        var dx = Outside(_camera.X, origin.X, origin.X + size);
        var dy = Outside(_camera.Y, origin.Y, origin.Y + size);
        var dz = Outside(_camera.Z, origin.Z, origin.Z + size);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Outside(double v, double lo, double hi) => v < lo ? lo - v : v > hi ? v - hi : 0.0;

    private bool InCollisionRange(ChunkCoord coord)
    {
        if (coord.Lod != 0 || !_body!.Collision)
            return false;

        var centre = coord.Origin + new double3(0.5 * ChunkConstants.Size);
        var d = centre - _camera;
        return double3.Dot(d, d) <= CollisionRadius * CollisionRadius;
    }

    /// <summary>
    /// Drop bodies that left the collision ring, and re-run LOD-0 leaves that entered it
    /// (regenerate + mesh is cheaper than keeping voxels around).
    /// </summary>
    private void UpdateCollisionSet()
    {
        foreach (var record in _chunks.Values)
        {
            if (!record.Ready)
                continue;

            var coord = record.Coord;
            var inRange = InCollisionRange(coord);

            if (!inRange && record.Body.IsValid)
            {
                RemoveBody(record);
            }
            else if (inRange && !record.Body.IsValid && record.HasGeometry && !record.Dirty)
            {
                record.Dirty = true;
                _dirty.Add(coord);
            }
        }
    }

    /// <summary>
    /// Desired leaves that a camera-relative frustum (planet axes, planes as (n, d) with n·p + d ≥ 0 inside) sees, that may
    /// show something (<see cref="PlanetLeafSelector.ProvablyUnseen"/>), but that loaded leaves do not cover: not ready, no
    /// ready ancestor, and ready descendants over less than all of it (<see cref="DescendantsCover"/>). LOD seams are not
    /// leaves: the pixel probe (--seam-test) counts those. A test metric, not per-frame game work.
    /// </summary>
    public int CountHoles(ReadOnlySpan<Vector4> planes)
    {
        if (_body is null)
            return 0;

        Array.Clear(_holeLods);
        var holes = 0;
        var built = false;

        foreach (var leaf in _desired.Order)
        {
            if (IsReady(leaf) || !InFrustum(leaf, planes) || AncestorReady(leaf) || _body.Selector.ProvablyUnseen(leaf, _camera))
                continue;

            if (!built)
            {
                BuildDrawable();
                built = true;
            }

            if (DescendantsCover(leaf, _drawable, _drawableAncestors, _body.Selector, _camera))
                continue;

            holes++;
            _holeLods[leaf.Lod]++;
        }

        return holes;
    }

    /// <summary>
    /// The leaves the renderer drew this frame, and every node above one. An upload is in the frame it was made in (its
    /// table record and mesh copy go out together, a re-mesh swapping whole), so every ready leaf counts.
    /// </summary>
    private void BuildDrawable()
    {
        _drawable.Clear();
        _drawableAncestors.Clear();

        foreach (var record in _chunks.Values)
        {
            if (!record.Ready)
                continue;

            _drawable.Add(record.Coord.Key);

            for (var a = record.Coord.Parent; a.Lod <= PlanetLeafSelector.MaxLeafLod && _drawableAncestors.Add(a.Key); a = a.Parent)
            {
            }
        }
    }

    /// <summary>
    /// Drawn leaves at or below <paramref name="node"/> cover all of it that could show: every branch down from it ends
    /// in a drawn leaf or in a node that provably shows nothing. One drawn descendant is not enough.
    /// </summary>
    /// <param name="drawable">Keys of the drawn leaves.</param>
    /// <param name="ancestors">Keys of every node above a drawn leaf.</param>
    internal static bool DescendantsCover(ChunkCoord node, HashSet<ulong> drawable, HashSet<ulong> ancestors, PlanetLeafSelector selector, double3 camera)
    {
        if (drawable.Contains(node.Key))
            return true;

        if (node.Lod == 0 || !ancestors.Contains(node.Key))
            return selector.ProvablyUnseen(node, camera);

        for (var i = 0; i < 8; i++)
        {
            if (!DescendantsCover(new ChunkCoord(node.X * 2 + (i & 1), node.Y * 2 + ((i >> 1) & 1), node.Z * 2 + ((i >> 2) & 1), node.Lod - 1), drawable, ancestors, selector, camera))
                return false;
        }

        return true;
    }

    private bool AncestorReady(ChunkCoord leaf)
    {
        for (var a = leaf.Parent; a.Lod <= PlanetLeafSelector.MaxLeafLod; a = a.Parent)
        {
            if (IsReady(a))
                return true;
        }

        return false;
    }

    private bool InFrustum(ChunkCoord leaf, ReadOnlySpan<Vector4> planes)
    {
        var size = leaf.SizeMetres;
        var margin = SurfaceNetsMesher.MarginVoxels * leaf.VoxelSize;
        var lo = leaf.Origin - _camera - new double3(margin);
        var hi = lo + new double3(size + 2.0 * margin);

        foreach (var p in planes)
        {
            var x = p.X >= 0f ? hi.X : lo.X;
            var y = p.Y >= 0f ? hi.Y : lo.Y;
            var z = p.Z >= 0f ? hi.Z : lo.Z;

            if (p.X * x + p.Y * y + p.Z * z + p.W < 0.0)
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        _selectScheduler.AbortJobs(waitForCompletion: true);
        _scheduler.AbortJobs(waitForCompletion: true);

        foreach (var job in _inFlight)
            Recycle(job);

        foreach (var job in _awaitingUpload)
            Recycle(job);

        foreach (var record in _chunks.Values)
            RemoveBody(record);

        while (_shapes.Count > 0)
            _shapes.Pop().Dispose();
    }
}
