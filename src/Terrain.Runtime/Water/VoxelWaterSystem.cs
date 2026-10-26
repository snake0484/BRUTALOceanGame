using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Runtime.Water;

/// <summary>
/// Runs the voxel water flow sim (docs/voxel-water.md) in LOD-0 blocks around edits.
/// Activation (regenerate + edit replay) is asynchronous; stepping runs inside the fixed tick as parallel jobs with a
/// barrier between the two passes. Sleeping blocks cost nothing; a sleeping neighbour is woken before any transfer
/// across its face so mass stays exact.
/// </summary>
public sealed class VoxelWaterSystem : IDisposable
{
    /// <summary>Sim steps per second. Higher fills faster at proportional CPU cost (the cell automaton spreads diffusively).</summary>
    public int StepsPerSecond = 30;
    private const int MaxStepsPerTick = 8;

    /// <summary>Main-thread time per tick the 3D automaton may use; a step already running always finishes.</summary>
    public float StepBudgetMs = 2f;

    /// <summary>Up to this many awake blocks step on the main thread instead of the job system.</summary>
    public int InlineBlocks = 24;
    public const int MaxBlocks = 512;
    public const int EvictRadius = 12;
    private const int QuietStepsToSleep = 60;
    private const float SleepDelta = 1e-4f;
    private const int MaxActivationsPerBatch = 32;

    /// <summary>Metres beyond an edit's radius in which the sea is simulated instead of clamped.</summary>
    public float SeaZoneMargin = 6f;

    private static readonly (int X, int Y, int Z)[] FaceOffsets = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    private readonly TerrainWorld _terrain;
    private readonly JobScheduler _stepScheduler;
    private readonly JobScheduler _activationScheduler;
    private readonly Dictionary<ChunkCoord, Slot> _blocks = new(MaxBlocks);
    private readonly Stack<Slot> _slotPool = new(MaxBlocks);
    private readonly Stack<WaterActivationJob> _activationPool = new(MaxActivationsPerBatch);
    private readonly List<WaterActivationJob> _activationsInFlight = new(MaxActivationsPerBatch);
    private readonly List<(ChunkCoord Coord, bool Refresh)> _activationRequests = new(256);
    private readonly BathymetryGrid _bathymetry;

    /// <summary>Sea zone discs: (world x, world z, radius). Kept forever, like brush ops.</summary>
    private readonly List<Vector3> _zones = new(256);

    /// <summary>Shallow-water sim of the sea inside zone discs (the 3D automaton treats those cells as clamped sea).</summary>
    public readonly ZoneFlowSim Zones = new();
    public int ZoneSubsteps = 4;
    private int _bedVersion = -1;

    public float LastZoneMs { get; private set; }

    /// <summary>Part of <see cref="LastZoneMs"/>: splashes, turbulence and packing the flow for the GPU.</summary>
    public float LastAgitateMs { get; private set; }

    public int ZoneDiscs => _zones.Count;
    private readonly WaterStepJob[] _stepJobs = new WaterStepJob[MaxBlocks];
    private readonly List<Slot> _awake = new(MaxBlocks);

    /// <summary>The blocks the last step ran, so a block stepping again after a pause takes their shared fluxes (pipe model).</summary>
    private readonly List<Slot> _stepped = new(MaxBlocks);
    private readonly List<ChunkCoord> _scratch = new(MaxBlocks);

    // Surface meshing runs in the background: the tick never waits on it. Blocks being meshed are not stepped,
    // refreshed or evicted until their batch is collected.
    private readonly JobScheduler _meshScheduler;
    private readonly WaterStepJob[] _meshJobs = new WaterStepJob[MaxBlocks];
    private readonly List<Slot> _meshing = new(MaxBlocks);
    private double _accumulator;

    private sealed class Slot(bool pipes)
    {
        public readonly WaterBlock Block = new(pipes);

        /// <summary>Ran in the last step.</summary>
        public bool Stepped;

        /// <summary>Drawn mesh, and the one a background mesh job is filling; swapped when the job finishes.</summary>
        public WaterMesh Mesh = new();
        public WaterMesh Building = new();
        public bool Pending;
        public bool MeshDirty;
        public bool Meshing;
        public bool RefreshQueued;

        /// <summary>Touches a sea zone disc: some of its sea cells are simulated, and it feeds ocean level offsets.</summary>
        public bool Simulated;
    }

    /// <param name="pipes">Run the 3D pipe model with momentum (<see cref="WaterPipes"/>) instead of the cell automaton.</param>
    public VoxelWaterSystem(TerrainWorld terrain, bool pipes = false)
    {
        Pipes = pipes;
        _terrain = terrain;
        _bathymetry = terrain.Bathymetry;
        var workers = (uint)Math.Max(1, Environment.ProcessorCount - 2);
        // The tick waits on step jobs, so they outrank streaming and activation, which only add latency.
        _stepScheduler = new JobScheduler(workers, "WaterStep", ThreadPriority.AboveNormal);
        _activationScheduler = new JobScheduler(Math.Max(1u, workers / 2), "WaterActivate", ThreadPriority.BelowNormal);
        _meshScheduler = new JobScheduler(Math.Max(1u, workers / 2), "WaterMesh");

        for (var i = 0; i < MaxBlocks; i++)
        {
            _slotPool.Push(new Slot(pipes));
            _stepJobs[i] = new WaterStepJob();
            _meshJobs[i] = new WaterStepJob { Pass = WaterStepJob.Kind.Mesh };
        }

        for (var i = 0; i < MaxActivationsPerBatch; i++)
            _activationPool.Push(new WaterActivationJob());

        terrain.EditApplied += OnEdit;
    }

    public bool Pipes { get; }

    public int Blocks => _blocks.Count;

    public int AwakeBlocks { get; private set; }

    public float LastStepMs { get; private set; }

    public float Mass { get; private set; }

    private void OnEdit(BrushOp op)
    {
        Span<ChunkCoord> affected = stackalloc ChunkCoord[128];
        var count = VoxelEditor.AffectedChunks(op, 0, affected);

        foreach (var coord in affected[..count])
        {
            Request(coord, refresh: true);

            foreach (var (dx, dy, dz) in FaceOffsets)
                Request(new ChunkCoord(coord.X + dx, coord.Y + dy, coord.Z + dz), refresh: false);
        }

        // Let the nearby sea drain into a dig: a disc of sea around it is run by the zone shallow-water sim (and drawn by
        // the ocean at the sim surface). The open sea beyond the disc is its boundary. Placing material only displaces
        // water; existing zones see it through their bed refresh, and no new zone is needed.
        if (op.Mode == BrushMode.Dig)
            AddZone(new Vector3(op.Centre.X, op.Centre.Z, op.Radius + SeaZoneMargin));
    }

    /// <summary>Adds a sea zone disc (world x, world z, radius): grid mask, zone sim domain, and clamped cells for the 3D automaton.</summary>
    private void AddZone(Vector3 disc, bool grown = false)
    {
        _zones.Add(disc);
        _bathymetry.AddSimulatedDisc(disc.X, disc.Y, disc.Z);
        Zones.SeaLevel = _terrain.Params.SeaLevel;
        Zones.AddDisc(disc, _bathymetry, grown);
        SimulateDisc(disc);
    }

    /// <summary>A calm grown disc goes back to the open ocean: drop it from the mask and re-clamp its blocks.</summary>
    private void RemergeZone(Vector3 disc)
    {
        _zones.Remove(disc);
        _bathymetry.RemoveSimulatedDisc(disc);
        SimulateDisc(disc);
    }

    /// <summary>Activates or refreshes the sea blocks under a disc so they re-apply the current zone discs.</summary>
    private void SimulateDisc(Vector3 disc)
    {
        var radius = disc.Z;
        var seaTop = ChunkCoord.FromWorld(new Vector3(0f, _terrain.Params.SeaLevel - 1f, 0f)).Y;
        var min = ChunkCoord.FromWorld(new Vector3(disc.X - radius, 0f, disc.Y - radius));
        var max = ChunkCoord.FromWorld(new Vector3(disc.X + radius, 0f, disc.Y + radius));

        for (var z = min.Z; z <= max.Z; z++)
            for (var x = min.X; x <= max.X; x++)
                for (var y = ChunkConstants.MinChunkY; y <= seaTop; y++)
                    Simulate(new ChunkCoord(x, y, z));
    }

    private void Simulate(ChunkCoord coord)
    {
        Request(coord, refresh: false);

        if (!_blocks.TryGetValue(coord, out var slot))
            return;

        // A new disc must be applied even to blocks that already touch an older one.
        slot.Simulated = true;

        // Re-apply discs with a refresh. A block whose job is already running would miss this disc, so it gets one too;
        // a brand-new block's queued init simply picks the disc up when it is dispatched.
        if (!slot.RefreshQueued)
        {
            slot.RefreshQueued = true;
            _activationRequests.Add((coord, true));
        }
    }

    /// <summary>Creates (or, for an existing block and refresh = true, re-reads solids for) a block.</summary>
    private void Request(ChunkCoord coord, bool refresh)
    {
        if (coord.Y < ChunkConstants.MinChunkY || coord.Y > ChunkConstants.MaxChunkY)
            return;

        if (_blocks.TryGetValue(coord, out var slot))
        {
            if (!refresh || slot.RefreshQueued)
                return;

            slot.RefreshQueued = true;
            _activationRequests.Add((coord, true));
            return;
        }

        if (_blocks.Count >= MaxBlocks || _slotPool.Count == 0)
            return;

        var created = _slotPool.Pop();
        created.Block.Reset(coord);
        created.Pending = true;
        created.Stepped = false;
        created.MeshDirty = false;
        created.RefreshQueued = false;
        created.Simulated = false;
        created.Mesh.Clear();
        created.Building.Clear();
        created.Meshing = false;
        _blocks.Add(coord, created);
        _activationRequests.Add((coord, false));
    }

    /// <summary>Main thread, once per frame: finish activations, start new ones, evict far blocks.</summary>
    public void Update(Vector3 cameraPos)
    {
        if (_activationScheduler.IsExecuting)
            return;

        foreach (var job in _activationsInFlight)
        {
            if (_blocks.TryGetValue(job.Block!.Coord, out var slot) && ReferenceEquals(slot.Block, job.Block))
            {
                slot.Pending = false;
                slot.RefreshQueued = false;
                slot.MeshDirty = true;
                slot.Block.Asleep = AllClamped(slot.Block);
                slot.Block.QuietSteps = 0;
                RefreshFlagHalos(job.Block.Coord);


            }

            job.Block = null;
            _activationPool.Push(job);
        }

        _activationsInFlight.Clear();
        Evict(cameraPos);

        var queued = 0;
        for (var i = 0; i < _activationRequests.Count && _activationPool.Count > 0; i++)
        {
            var (coord, refresh) = _activationRequests[i];
            if (!_blocks.TryGetValue(coord, out var slot) || slot.Meshing)
                continue;

            // A refresh writes into the block, so it must not be stepped meanwhile.
            slot.Pending = true;
            var job = _activationPool.Pop();
            job.Prepare(slot.Block, refresh, _terrain.Params, _terrain.Ops, _zones);
            _activationScheduler.Queue(job, 1);
            _activationsInFlight.Add(job);
            _activationRequests.RemoveAt(i--);
            queued++;
        }

        if (queued > 0)
            _activationScheduler.ExecuteJobs();
    }

    private void RefreshFlagHalos(ChunkCoord coord)
    {
        var block = _blocks[coord].Block;

        for (var f = 0; f < 6; f++)
        {
            var neighbour = Neighbour(coord, f);
            WaterHalo.CopyFlags(block, neighbour, (Face)f);

            if (neighbour is not null)
                WaterHalo.CopyFlags(neighbour, block, Opposite((Face)f));
        }
    }

    /// <summary>Fixed tick: advances the sim at <see cref="StepsPerSecond"/>.</summary>
    public void Tick(double dt)
    {
        _accumulator += dt;
        var stepTime = 1.0 / StepsPerSecond;
        var start = Stopwatch.GetTimestamp();
        var steps = 0;
        CollectMeshes();

        // Budgeted: after an edit wakes many blocks a step can take tens of ms; stop once the budget is spent and drop
        // the backlog, so water slows down for a moment instead of the frame rate collapsing (and the clock spiralling).
        var budget = (long)(StepBudgetMs * Stopwatch.Frequency / 1000.0);
        while (_meshing.Count == 0 && _accumulator >= stepTime && steps < MaxStepsPerTick && Stopwatch.GetTimestamp() - start < budget)
        {
            _accumulator -= stepTime;
            Step();
            steps++;
        }

        if (_accumulator >= stepTime)
            _accumulator = 0.0;

        StartMeshing();
        StepZones(dt);

        if (steps > 0)
            LastStepMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds / steps;
    }

    private void Step()
    {
        WakeNeighboursOfAwake();

        _awake.Clear();
        foreach (var (_, slot) in _blocks)
            if (!slot.Pending && !slot.Block.Asleep)
                _awake.Add(slot);

        AwakeBlocks = _awake.Count;
        if (_awake.Count == 0)
        {
            MarkStepped();
            return;
        }

        if (Pipes)
            SyncResumedFluxes();

        foreach (var slot in _awake)
        {
            for (var f = 0; f < 6; f++)
            {
                // A missing or mid-refresh neighbour is a wall for this step, so nothing drains into it.
                var neighbour = Neighbour(slot.Block.Coord, f);
                if (neighbour is null)
                    WaterHalo.CopyFlags(slot.Block, null, (Face)f);

                WaterHalo.CopyWater(slot.Block, neighbour, (Face)f);
            }

            if (Pipes)
                WaterHalo.CopyFallAbove(slot.Block, Neighbour(slot.Block.Coord, (int)Face.PosY));
        }
        RunPass(Pipes ? WaterStepJob.Kind.PipeFluxes : WaterStepJob.Kind.OutflowScale);

        foreach (var slot in _awake)
            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(slot.Block, Neighbour(slot.Block.Coord, f), (Face)f);
        RunPass(Pipes ? WaterStepJob.Kind.PipeApply : WaterStepJob.Kind.ApplyFlows);

        var mass = 0f;
        foreach (var slot in _awake)
        {
            var block = slot.Block;
            WaterFlow.Swap(block);
            mass += block.Mass();

            // With momentum a steady stream changes no fill, so the pipe model also waits for its fluxes to die down.
            if (block.MaxDelta > SleepDelta || block.MaxFlux > WaterPipes.FluxEpsilon)
            {
                block.QuietSteps = 0;
                slot.MeshDirty = true;
            }
            else if (++block.QuietSteps >= QuietStepsToSleep)
            {
                block.Asleep = true;
                block.ClearFluxes();
            }

            if (block.BorderActive)
                GrowAcrossFaces(block);
        }

        Mass = mass;
        MarkStepped();
    }

    /// <summary>
    /// Pipe model: a block stepping again after sleeping, activating or refreshing takes the fluxes of the faces it shares
    /// with blocks that kept stepping, and zeroes those it shares with blocks that didn't (as they hold none), so both
    /// copies of each shared face agree again.
    /// </summary>
    private void SyncResumedFluxes()
    {
        foreach (var slot in _awake)
        {
            if (slot.Stepped)
                continue;

            var c = slot.Block.Coord;

            for (var f = 0; f < 6; f++)
            {
                var (dx, dy, dz) = FaceOffsets[f];
                var kept = _blocks.TryGetValue(new ChunkCoord(c.X + dx, c.Y + dy, c.Z + dz), out var neighbour) && !neighbour.Pending && neighbour.Stepped;
                WaterHalo.SyncFluxes(slot.Block, kept ? neighbour!.Block : null, (Face)f);
            }
        }
    }

    private void MarkStepped()
    {
        foreach (var slot in _stepped)
            slot.Stepped = false;

        _stepped.Clear();

        foreach (var slot in _awake)
        {
            slot.Stepped = true;
            _stepped.Add(slot);
        }
    }

    /// <summary>Fixed-rate shallow-water substeps for sea zones, then publish to the ocean's bathymetry map.</summary>
    private void StepZones(double dt)
    {
        if (Zones.Cells == 0)
            return;

        var start = Stopwatch.GetTimestamp();

        // Edits land asynchronously; re-read the bed whenever the bathymetry was rebuilt.
        if (_bathymetry.Version != _bedVersion)
        {
            _bedVersion = _bathymetry.Version;
            Zones.RefreshBed(_bathymetry);
        }

        Zones.UpdateBoundary();
        var substep = (float)(dt / ZoneSubsteps);

        for (var i = 0; i < ZoneSubsteps; i++)
            Zones.Step(substep);

        var agitate = Stopwatch.GetTimestamp();
        Zones.Agitate((float)dt);
        LastAgitateMs = (float)Stopwatch.GetElapsedTime(agitate).TotalMilliseconds;

        // Violent flow or a surge past the edge grows the zone (rate-limited, reach-limited and capped inside the sim);
        // grown discs that have settled remerge into the ocean.
        foreach (var disc in Zones.Growth)
            AddZone(disc, grown: true);

        Zones.Growth.Clear();
        Zones.CheckRemerge(_bathymetry, (float)dt);

        foreach (var disc in Zones.Remerged)
            RemergeZone(disc);

        Zones.Remerged.Clear();

        var pack = Stopwatch.GetTimestamp();
        Zones.PackFlow();
        LastAgitateMs += (float)Stopwatch.GetElapsedTime(pack).TotalMilliseconds;
        Zones.Publish(_bathymetry);
        LastZoneMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>
    /// Small passes run inline: waking parked workers while other schedulers are busy cost 10-70 ms spikes, far more than
    /// a handful of blocks take to step.
    /// </summary>
    private void RunPass(WaterStepJob.Kind pass)
    {
        var inline = _awake.Count <= InlineBlocks;
        var dt = 1f / StepsPerSecond;

        for (var i = 0; i < _awake.Count; i++)
        {
            var job = _stepJobs[i];
            job.Block = _awake[i].Block;
            job.Pass = pass;
            job.Dt = dt;

            if (inline)
                job.Run();
            else
                _stepScheduler.Queue(job, 1);
        }

        if (inline)
            return;

        _stepScheduler.ExecuteJobs();
        _stepScheduler.Wait();
    }

    /// <summary>Starts background meshing of dirty blocks, unless a batch is still running.</summary>
    private void StartMeshing()
    {
        if (_meshing.Count > 0)
            return;

        foreach (var (_, slot) in _blocks)
        {
            if (!slot.MeshDirty || slot.Pending)
                continue;

            slot.MeshDirty = false;
            slot.Meshing = true;
            var job = _meshJobs[_meshing.Count];
            job.Block = slot.Block;
            job.Mesh = slot.Building;
            _meshScheduler.Queue(job, 1);
            _meshing.Add(slot);
        }

        if (_meshing.Count > 0)
            _meshScheduler.ExecuteJobs();
    }

    /// <summary>Swaps in finished meshes once the whole batch is done.</summary>
    private void CollectMeshes()
    {
        if (_meshing.Count == 0 || _meshScheduler.IsExecuting)
            return;

        foreach (var slot in _meshing)
        {
            (slot.Mesh, slot.Building) = (slot.Building, slot.Mesh);
            slot.Meshing = false;
        }

        _meshing.Clear();
    }

    /// <summary>Sleeping neighbours that could exchange water with an awake block must step too, or mass would leak.</summary>
    private void WakeNeighboursOfAwake()
    {
        foreach (var (coord, slot) in _blocks)
        {
            if (slot.Pending || slot.Block.Asleep)
                continue;

            for (var f = 0; f < 6; f++)
            {
                var neighbour = Neighbour(coord, f);
                if (neighbour is null || !neighbour.Asleep)
                    continue;

                // Sea and solid cells never change, so a neighbour whose shared face is all clamped can stay asleep.
                if (FaceClamped(neighbour, Opposite((Face)f)))
                    continue;

                if (slot.Block.AnyWaterOnFace((Face)f) || neighbour.AnyWaterOnFace(Opposite((Face)f)))
                {
                    neighbour.Asleep = false;
                    neighbour.QuietSteps = 0;
                }
            }
        }
    }

    /// <summary>Water reached a block face that leads into open space with no block yet: create that block.</summary>
    private void GrowAcrossFaces(WaterBlock block)
    {
        for (var f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceOffsets[f];
            var coord = new ChunkCoord(block.Coord.X + dx, block.Coord.Y + dy, block.Coord.Z + dz);

            if (!_blocks.ContainsKey(coord) && block.AnyWaterOnFace((Face)f) && block.FaceOpen((Face)f))
                Request(coord, refresh: false);
        }
    }

    private void Evict(Vector3 cameraPos)
    {
        var centre = ChunkCoord.FromWorld(cameraPos);
        _scratch.Clear();

        foreach (var (coord, slot) in _blocks)
        {
            var dx = coord.X - centre.X;
            var dz = coord.Z - centre.Z;

            if (!slot.Pending && !slot.Meshing && dx * dx + dz * dz > EvictRadius * EvictRadius)
                _scratch.Add(coord);
        }

        foreach (var coord in _scratch)
        {
            _blocks.Remove(coord, out var slot);
            _slotPool.Push(slot!);


            for (var f = 0; f < 6; f++)
            {
                var neighbour = Neighbour(coord, f);
                if (neighbour is not null)
                    WaterHalo.CopyFlags(neighbour, null, Opposite((Face)f));
            }
        }
    }

    /// <summary>Per frame: hand every non-empty surface mesh to the renderer.</summary>
    public void SubmitMeshes(VoxelWaterRenderer renderer)
    {
        foreach (var (coord, slot) in _blocks)
            if (!slot.Pending && slot.Mesh.IndexCount > 0)
                renderer.Add(coord, slot.Mesh);
    }

    private WaterBlock? Neighbour(ChunkCoord coord, int face)
    {
        var (dx, dy, dz) = FaceOffsets[face];
        return _blocks.TryGetValue(new ChunkCoord(coord.X + dx, coord.Y + dy, coord.Z + dz), out var slot) && !slot.Pending
            ? slot.Block
            : null;
    }

    private static Face Opposite(Face face) => (Face)((int)face ^ 1);

    private const byte Clamped = (byte)(WaterFlags.Solid | WaterFlags.Reservoir);

    /// <summary>True when every interior cell is solid or sea: nothing in the block can ever change.</summary>
    private static bool AllClamped(WaterBlock block)
    {
        var flags = block.Flags;
        for (var z = 0; z < ChunkConstants.Size; z++)
            for (var y = 0; y < ChunkConstants.Size; y++)
                for (var x = 0; x < ChunkConstants.Size; x++)
                    if ((flags[ChunkConstants.Index(x, y, z)] & Clamped) == 0)
                        return false;

        return true;
    }

    private static bool FaceClamped(WaterBlock block, Face face)
    {
        const int last = ChunkConstants.Size - 1;
        var flags = block.Flags;

        for (var a = 0; a < ChunkConstants.Size; a++)
        {
            for (var b = 0; b < ChunkConstants.Size; b++)
            {
                var index = face switch
                {
                    Face.NegX => ChunkConstants.Index(0, a, b),
                    Face.PosX => ChunkConstants.Index(last, a, b),
                    Face.NegY => ChunkConstants.Index(a, 0, b),
                    Face.PosY => ChunkConstants.Index(a, last, b),
                    Face.NegZ => ChunkConstants.Index(a, b, 0),
                    _ => ChunkConstants.Index(a, b, last),
                };

                if ((flags[index] & Clamped) == 0)
                    return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _terrain.EditApplied -= OnEdit;
        _activationScheduler.AbortJobs(waitForCompletion: true);
        _stepScheduler.AbortJobs(waitForCompletion: true);
        _meshScheduler.AbortJobs(waitForCompletion: true);
    }
}
