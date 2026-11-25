using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Runtime.Water;

/// <summary>
/// Runs the voxel water flow sim (docs/voxel-water.md) in LOD-0 blocks around edits.
/// Activation (regenerate + edit replay) is asynchronous; stepping runs inside the fixed tick as parallel jobs with a
/// barrier between the two passes. Sleeping blocks cost nothing; a sleeping neighbour is woken before any transfer
/// across its face so mass stays exact. Still water is held by bodies (<see cref="WaterBodies"/>): the sea, the
/// generator's lakes, and any pool the sim leaves at rest.
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

    /// <summary>Metres beyond an edit's radius in which the sea is simulated instead of held, and a body's water is freed.</summary>
    public float SeaZoneMargin = 6f;

    private static readonly (int X, int Y, int Z)[] FaceOffsets = [(-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 1, 0), (0, 0, -1), (0, 0, 1)];

    private readonly TerrainWorld _terrain;
    private readonly JobScheduler _stepScheduler;
    private readonly JobScheduler _activationScheduler;
    private readonly Dictionary<ChunkCoord, Slot> _blocks = new(MaxBlocks);

    /// <summary>Bodies of still water (docs/voxel-water.md "Bodies of water"): the sea, the generator's, and those born of still free water.</summary>
    public readonly WaterBodies Bodies;

    /// <summary>The generator's bodies, with their ids, and every chunk their water (and their flows') reaches into.</summary>
    public readonly WaterBodyInfo[] Generated;
    private readonly ChunkCoord[] _generatedBlocks;

    /// <summary>
    /// Flows of running water (docs/voxel-water.md "Flows"): the generator's rivers, by id. A reach the camera is near
    /// and whose blocks are loaded runs in the 3D sim; the rest are routed (<see cref="WaterFlows.Route"/>) and their
    /// loaded blocks frozen, so a river flows on where it is far or unloaded, and where only part of it is simulated.
    /// </summary>
    public readonly WaterFlows Flows = new();
    public readonly WaterFlowInfo[] GeneratedFlows;

    /// <summary>
    /// A flow's reach runs in the 3D sim while the camera is within this (m, across) of its chunk column; past it, it is
    /// routed. Measured on the river (--river-test): 32 m runs 3–5 reaches, 7–11 blocks awake at 0.8–1.3 ms a step; 64 m
    /// ran 7, 11–16 blocks at 1.4–3.2 ms.
    /// </summary>
    public float FlowSimRadius = 32f;

    /// <summary>An awake block's face onto a frozen or unloaded reach of a flow this step: what crosses it is booked to that reach.</summary>
    private readonly record struct FlowEdge(WaterBlock Block, Face Face, WaterFlows.Flow Flow, int Reach);

    private readonly List<FlowEdge> _flowEdges = new(32);
    private float _flowEdgeBooked;

    /// <summary>
    /// Discs around digs into bodies (docs/voxel-water.md "Bodies of water"): inside one the body's water is free to
    /// flow, as the sea is inside a sea zone, so it pours into the hole or out through a cut and the held water beyond the
    /// disc refills it. A disc grows while the body keeps pouring into it, ends once every block it touches has slept for
    /// <see cref="DiscSettleSeconds"/> (the water at the body's level there rejoins it), and becomes an outlet when the
    /// flow through it holds steady: then it drains the body at that rate even with its blocks evicted.
    /// </summary>
    private sealed class Disc
    {
        /// <summary>World x, world z, radius, body id.</summary>
        public Vector4 Shape;
        public ushort Body => (ushort)Shape.W;

        /// <summary>Seconds every block under it has been asleep.</summary>
        public float Quiet;

        /// <summary>The body's flow into the disc's blocks (m³/s, positive out of the body), averaged over <see cref="FlowSmoothing"/>; and how long it has held steady.</summary>
        public float Flow;
        public float Steady;
        public float Cooldown;

        /// <summary>An outlet: the steady drain (m³/s) recorded, and the body's level then. 0 while not one.</summary>
        public float OutletRate;
        public float OutletLevel;
        public bool IsOutlet => OutletRate > 0f;
    }

    private readonly List<Disc> _discs = new(16);
    private readonly List<Vector4> _discShapes = new(16);
    private readonly List<Vector4> _remaining = new(8);
    public float DiscSettleSeconds = 3f;

    /// <summary>
    /// A block is calm while its fills change under this a step and no face moves more than this (m³/s): the pour is
    /// over. Calm for <see cref="CalmSeconds"/> it sleeps, as a quiet one does, and settles into bodies. A pond at rest
    /// sloshes at millimetres and a pit's free water against held cells trickles about 0.01 m³/s for good, which kept
    /// their blocks awake for whole runs and the pond from ever being born a body; a stream over a tenth of a cubic
    /// metre a second is never calm, so it keeps running.
    /// </summary>
    public float CalmDelta = 1e-3f;
    public float CalmFlux = 0.1f;
    public float CalmSeconds = 3f;

    /// <summary>Flow into a disc above this (m³/s per metre of its edge) for <see cref="GrowCooldown"/> grows it by <see cref="GrowStep"/>, up to <see cref="MaxDiscRadius"/>.</summary>
    public float GrowFlux = 0.05f;
    public float GrowStep = 4f;
    public float GrowCooldown = 0.5f;

    /// <summary>A disc frees the body's water to its full depth, so unlike a sea zone (a heightfield) it costs 3D cells: 24 m is about 18,000 cells over 10 m of water.</summary>
    public float MaxDiscRadius = 24f;
    private const float FlowSmoothing = 2f;

    /// <summary>A disc's flow at least this (m³/s), within <see cref="OutletSteadiness"/> of its average for <see cref="OutletSeconds"/>, is an outlet.</summary>
    public float MinOutletRate = 0.02f;
    public float OutletSteadiness = 0.3f;
    public float OutletSeconds = 5f;

    /// <summary>Rain intensity 0..1 at a world x, z (the weather), which bodies gain over their area at <see cref="WaterBodies.RainRate"/>.</summary>
    public Func<float, float, float>? RainAt;

    /// <summary>
    /// The open sea's height over its rest level at a world x, z (the swell), which the sea's held cells take within
    /// <see cref="SwellRadius"/> of the camera (docs/voxel-water.md "Swell on voxel water"), pipe model only; null for a flat sea.
    /// </summary>
    public Func<float, float, float>? SeaSurface;

    /// <summary>The sea holds the swell within this distance (m) of the camera, fading out over the last <see cref="SwellFade"/>; past it free water against the sea can sleep.</summary>
    public float SwellRadius = 128f;
    public float SwellFade = 32f;

    /// <summary>
    /// The swell is taken on a lattice of this spacing (m), bilinear between: a power of two, so every block interpolates
    /// it exactly alike. A sample costs about 4.5 µs near the shore; at 4 m a block's 121 cost 0.55 ms a tick. Nearshore
    /// swell runs 50–90 m long, so 8 m interpolates it to a few centimetres, far under a cell.
    /// </summary>
    private const float SwellSpacing = 8f;

    /// <summary>
    /// Each lattice point is sampled again every this many ticks (20 times a second at 60), a share of the points each tick,
    /// and runs on between, unbroken (<see cref="RefreshSwell"/>). Measured in the pond scene: 0.16–0.20 ms a tick (0.41–0.55
    /// sampling every tick) every 3 ticks, straying 0.5 cm on average from the fresh samples (2.6 cm at most; in a storm 2.9,
    /// peaks of 16). Every 6 ticks cost 0.09–0.12 ms but strayed 2.3 cm (6 in a storm): taken for the frame budget, a
    /// stray under a tenth of a cell and the heights never jump (a point re-aims its rate, it is not snapped).
    /// </summary>
    private const int SwellRefreshTicks = 6;

    /// <summary>A lattice point no block has read for this long (s) is forgotten.</summary>
    private const double SwellForget = 2.0;

    /// <summary>Lattice points along a block's padded columns, from one before them to one past.</summary>
    private const int SwellPoints = ChunkConstants.Size / 8 + 3;

    /// <summary>A sleeping block whose free water meets the sea wakes once the swell at its centre has moved this far (m) from where it slept.</summary>
    private const float SwellWake = 0.02f;

    /// <summary>
    /// A lattice point: the swell it runs on from (Height at Sampled, rising at Rate m/s), the fresh sample taken then
    /// (Fresh), and when a block last read it.
    /// </summary>
    private struct SwellSample
    {
        public long Key;
        public float Height;
        public float Rate;
        public float Fresh;
        public double Sampled;
        public double Read;
    }

    private SwellSample[] _swellSamples = new SwellSample[1024];
    private int _swellCount;
    private readonly Dictionary<long, int> _swellIndex = new(1024);
    private double _swellClock;
    private int _swellTick;
    private Vector3 _camera;

    private readonly Stack<Slot> _slotPool = new(MaxBlocks);
    private readonly Stack<WaterActivationJob> _activationPool = new(MaxActivationsPerBatch);
    private readonly List<WaterActivationJob> _activationsInFlight = new(MaxActivationsPerBatch);
    private readonly List<(ChunkCoord Coord, bool Refresh)> _activationRequests = new(256);
    private readonly BathymetryGrid _bathymetry;

    /// <summary>Sea zone discs: (world x, world z, radius). Kept forever, like brush ops.</summary>
    private readonly List<Vector3> _zones = new(256);
    private readonly List<Vector3> _fadedOut = new(16);

    /// <summary>Shallow-water sim of the sea inside zone discs (the 3D automaton treats those cells as the sea's).</summary>
    public readonly ZoneFlowSim Zones = new();
    public int ZoneSubsteps = 4;
    private int _bedVersion = -1;

    public float LastZoneMs { get; private set; }

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

        /// <summary>Audit (<see cref="Audit"/>): its free water when its activation or refresh was queued (m³).</summary>
        public double AuditFree;

        /// <summary>Asleep with free water that meets the sea, under the swell at its centre then (<see cref="WakeOnSwell"/>).</summary>
        public bool SeaFree;
        public float SleptSwell;
    }

    /// <param name="pipes">Run the 3D pipe model with momentum (<see cref="WaterPipes"/>) instead of the cell automaton.</param>
    public VoxelWaterSystem(TerrainWorld terrain, bool pipes = false)
    {
        Pipes = pipes;
        _terrain = terrain;
        _bathymetry = terrain.Bathymetry;
        Bodies = new WaterBodies(terrain.Params.SeaLevel);
        Generated = TerrainGenerator.Bodies(terrain.Params);

        for (var i = 0; i < Generated.Length; i++)
        {
            var info = Generated[i];
            Generated[i] = info with { Id = Bodies.Add(info.Level, info.Area, info.Centre, info.Radius, info.Bottom) };
        }

        GeneratedFlows = TerrainGenerator.Flows(terrain.Params);

        for (var i = 0; i < GeneratedFlows.Length; i++)
        {
            var info = GeneratedFlows[i];
            var source = info.SourceIndex >= 0 && info.SourceIndex < Generated.Length ? Generated[info.SourceIndex] : default;
            GeneratedFlows[i] = info with { Id = Flows.Add(info.Path, source.Id, new Vector3(source.Centre, source.Radius), terrain.Params.SeaLevel) };
        }

        _generatedBlocks = GeneratedBlocks(Generated, Flows);
        _planeColumns = PlaneColumns(Generated);
        // The tick waits on step jobs, so they outrank streaming and activation, which only add latency.
        _stepScheduler = new JobScheduler((uint)WorkerBudget.Tick, "WaterStep", ThreadPriority.AboveNormal);
        _activationScheduler = new JobScheduler((uint)WorkerBudget.Side, "WaterActivate", ThreadPriority.BelowNormal);
        _meshScheduler = new JobScheduler((uint)WorkerBudget.Side, "WaterMesh");

        for (var i = 0; i < MaxBlocks; i++)
        {
            _slotPool.Push(new Slot(pipes));
            _stepJobs[i] = new WaterStepJob();
            _meshJobs[i] = new WaterStepJob { Pass = WaterStepJob.Kind.Mesh };
        }

        for (var i = 0; i < MaxActivationsPerBatch; i++)
            _activationPool.Push(new WaterActivationJob());

        terrain.EditApplied += OnEdit;

        if (pipes)
            new Thread(WarmUp) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "WaterWarmUp" }.Start();
    }

    private const int WarmUpSteps = 400;

    /// <summary>
    /// Steps a scratch block (a column of water falling into a walled box) while the game loads, so the step's code reaches
    /// its final tier before a dig wakes water: at tier 0 the pipe passes inline none of their helpers, and a dig's first
    /// step took 9 ms for four blocks, with another 10 ms tick a second later as they tiered up.
    /// </summary>
    private static void WarmUp()
    {
        var block = new WaterBlock(pipes: true) { Bodies = new WaterBodies(0f) };
        block.Reset(default);

        for (var z = 4; z < 28; z++)
            for (var y = 1; y < 28; y++)
                for (var x = 4; x < 28; x++)
                    block.Flags[ChunkConstants.Index(x, y, z)] = 0;

        for (var f = 0; f < 6; f++)
            WaterHalo.CopyFlags(block, null, (Face)f);

        block.DeriveBeds();
        var dt = 1f / 60f;

        for (var step = 0; step < WarmUpSteps; step++)
        {
            if (step % 100 == 0)
                for (var z = 8; z < 16; z++)
                    for (var y = 12; y < 24; y++)
                        for (var x = 8; x < 16; x++)
                            block.Water[ChunkConstants.Index(x, y, z)] = 1f;

            for (var f = 0; f < 6; f++)
            {
                WaterHalo.CopyFlags(block, null, (Face)f);
                WaterHalo.CopyWater(block, null, (Face)f);
                WaterHalo.CopyColour(block, null, (Face)f);
                WaterHalo.CopyFroth(block, null, (Face)f);

                if (f != (int)Face.NegY)
                    WaterHalo.CopyFalling(block, null, (Face)f);
            }

            WaterPipes.UpdateFluxes(block, dt);

            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(block, null, (Face)f);

            WaterPipes.ApplyFluxes(block, dt);
            WaterFlow.Swap(block);
            Thread.Sleep(1);
        }
    }

    public bool Pipes { get; }

    private bool _nanReported;

    /// <summary>Diagnostics: the first cell whose water went NaN, with its faces and neighbours, to stderr once; the block is then put to sleep.</summary>
    private void ReportNaN(WaterBlock block)
    {
        _nanReported = true;
        const int P = ChunkConstants.Padded;
        const int A = ChunkConstants.PaddedArea;

        for (var z = 0; z < ChunkConstants.Size; z++)
            for (var y = 0; y < ChunkConstants.Size; y++)
                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    if (!float.IsNaN(block.Water[i]) && !float.IsInfinity(block.Water[i]))
                        continue;

                    var o = block.Coord.WorldOrigin;
                    Console.Error.WriteLine($"NaN water at world ({o.X + x}, {o.Y + y}, {o.Z + z}) flags {block.Flags[i]} body {block.Body[i]} bed {block.Bed[i]}");
                    Console.Error.WriteLine($"  next {block.Next[i]} os {block.OutScale[i]} fall {block.FallSpeed?[i]} drift {block.DriftX?[i]},{block.DriftZ?[i]}");
                    Console.Error.WriteLine($"  fx- {block.FluxX?[i - 1]} fx+ {block.FluxX?[i]} fy- {block.FluxY?[i - P]} fy+ {block.FluxY?[i]} fz- {block.FluxZ?[i - A]} fz+ {block.FluxZ?[i]}");
                    Console.Error.WriteLine($"  w -x {block.Next[i - 1]} +x {block.Next[i + 1]} -y {block.Next[i - P]} +y {block.Next[i + P]} -z {block.Next[i - A]} +z {block.Next[i + A]}; flags -y {block.Flags[i - P]} +y {block.Flags[i + P]}");
                    Console.Error.WriteLine($"  fall +y {block.FallSpeed?[i + P]} os -x {block.OutScale[i - 1]} +x {block.OutScale[i + 1]} -y {block.OutScale[i - P]} +y {block.OutScale[i + P]}");
                    block.Asleep = true;
                    return;
                }
    }

    /// <summary>
    /// Diagnostics: the body's held cells over loaded blocks: all of them, those farther than <paramref name="radius"/>
    /// from <paramref name="centre"/> (world xz), and those floating (the cell under them open and neither held nor full),
    /// with the highest floating cell's world y.
    /// </summary>
    private (int Held, int Outside, int Floating, float FloatTop) HeldCells(ushort body, Vector2 centre, float radius)
    {
        const int P = ChunkConstants.Padded;
        var held = 0;
        var outside = 0;
        var floating = 0;
        var top = float.NegativeInfinity;
        var r2 = radius * radius;

        foreach (var (_, slot) in _blocks)
        {
            if (slot.Pending)
                continue;

            var block = slot.Block;
            var origin = block.Coord.WorldOrigin;

            for (var z = 0; z < ChunkConstants.Size; z++)
                for (var y = 0; y < ChunkConstants.Size; y++)
                    for (var x = 0; x < ChunkConstants.Size; x++)
                    {
                        var i = ChunkConstants.Index(x, y, z);

                        if ((block.Flags[i] & (byte)WaterFlags.Reservoir) == 0 || block.Body[i] != body)
                            continue;

                        held++;

                        if (Vector2.DistanceSquared(new Vector2(origin.X + x, origin.Z + z), centre) > r2)
                            outside++;

                        var below = i - P;
                        if ((block.Flags[below] & (byte)(WaterFlags.Solid | WaterFlags.Reservoir)) == 0 && block.Water[below] < WaterFlow.SupportThreshold)
                        {
                            floating++;
                            top = MathF.Max(top, origin.Y + y);
                        }
                    }
        }

        return (held, outside, floating, top);
    }

    /// <summary>
    /// Diagnostics (the terrain panel's water probe): the column of cells at world (x, z) from y − 2 up
    /// <paramref name="up"/> metres, one line for each cell that holds water or is held, top first: world y, fill, kind
    /// (H held by its body, Z sea zone, F free), body, fall speed (pipe model), and whether its block sleeps.
    /// Allocates; not for the tick.
    /// </summary>
    public string DescribeColumn(Vector3 at, int up = 48)
    {
        var (cx, cz) = ((int)MathF.Round(at.X), (int)MathF.Round(at.Z));
        var text = new System.Text.StringBuilder();
        var bottom = (int)MathF.Floor(at.Y) - 2;

        for (var y = bottom + up; y >= bottom; y--)
        {
            var coord = ChunkCoord.FromWorld(new Vector3(cx, y, cz));
            if (!_blocks.TryGetValue(coord, out var slot) || slot.Pending)
                continue;

            var block = slot.Block;
            var o = coord.WorldOrigin;
            var i = ChunkConstants.Index(cx - (int)o.X, y - (int)o.Y, cz - (int)o.Z);
            var flags = (WaterFlags)block.Flags[i];
            var held = (flags & WaterFlags.Reservoir) != 0;

            if ((flags & WaterFlags.Solid) != 0 || (!held && block.Water[i] <= 0.001f))
                continue;

            var kind = held ? "H" : (flags & WaterFlags.SeaZone) != 0 ? "Z" : "F";
            var fall = block.FallSpeed is null ? 0f : -block.FallSpeed[i];
            text.AppendLine($"y {y,4}  fill {block.Water[i],6:F3}  {kind}  body {block.Body[i]}  fall {fall,5:F1}{(block.Asleep ? "  asleep" : "")}");
        }

        return text.Length == 0 ? "(no water in this column)" : text.ToString();
    }

    /// <summary>Diagnostics (the terrain panel): every live body and every disc, one line each. Allocates; not for the tick.</summary>
    public string DescribeBodiesAndDiscs()
    {
        var text = new System.Text.StringBuilder();

        for (var id = WaterBodies.Sea + 1; id < Bodies.Count; id++)
        {
            ref var body = ref Bodies[(ushort)id];
            if (!body.Alive)
                continue;

            var (held, outside, floating, top) = HeldCells((ushort)id, body.Centre, body.Radius);
            text.AppendLine($"{(body.Born ? "born " : "")}body #{id}: level {body.Level:F2} bottom {body.Bottom:F1} area {body.Area:F0} m2, centre {body.Centre} r {body.Radius:F0}, ledger {body.Ledger:F2} m3, gave {body.Given:F1} m3; loaded held cells {held}, {outside} outside r, {floating} floating (top y {top:F0})");
        }

        foreach (var disc in _discs)
            text.AppendLine($"disc of body #{disc.Body}: centre ({disc.Shape.X:F1}, {disc.Shape.Y:F1}) r {disc.Shape.Z:F1}, flow {disc.Flow:F2} m3/s (steady {disc.Steady:F1} s), quiet {disc.Quiet:F1} s{(disc.IsOutlet ? $", outlet {disc.OutletRate:F2} m3/s at level {disc.OutletLevel:F2}" : "")}");

        text.Append(Flows.Describe(Bodies));
        return text.ToString();
    }

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

        if (op.Mode != BrushMode.Dig)
            return;

        // Let the nearby water drain into a dig. At sea: a disc of sea around it is run by the zone shallow-water sim
        // (and drawn by the ocean at the sim surface), the open sea beyond the disc its boundary. In a body of still
        // water: a disc of it goes free for the voxel sim, the held water beyond the disc its boundary. Placing material
        // only displaces water. A dig that reaches neither stays as it is.
        if (op.Centre.Y - op.Radius < _terrain.Params.SeaLevel)
            AddZone(new Vector3(op.Centre.X, op.Centre.Z, op.Radius + SeaZoneMargin));
        else
            _digs.Add(op);
    }

    /// <summary>Digs above the sea not yet tested for the body they reach: their blocks must have finished regenerating first.</summary>
    private readonly List<BrushOp> _digs = new(16);

    /// <summary>
    /// Once a dig's blocks are no longer mid-refresh, the body whose held cells its sphere reaches gets a disc around it.
    /// (Testing at the dig, while the blocks regenerated, read stale cells; a geometric fallback to the generator's record
    /// made a disc for every dig of a tunnel running under the lake bed in rock and freed a hundred metres of lake.)
    /// </summary>
    private void TestDigs()
    {
        for (var d = _digs.Count - 1; d >= 0; d--)
        {
            var op = _digs[d];

            if (AnyPending(op))
                continue;

            _digs.RemoveAt(d);
            var body = TouchedBody(op);

            if (body > WaterBodies.Sea)
                AddDisc(new Vector4(op.Centre.X, op.Centre.Z, op.Radius + SeaZoneMargin, body));
        }
    }

    private bool AnyPending(in BrushOp op)
    {
        var min = ChunkCoord.FromWorld(op.Centre - new Vector3(op.Radius));
        var max = ChunkCoord.FromWorld(op.Centre + new Vector3(op.Radius));

        for (var cz = min.Z; cz <= max.Z; cz++)
            for (var cy = min.Y; cy <= max.Y; cy++)
                for (var cx = min.X; cx <= max.X; cx++)
                    if (_blocks.TryGetValue(new ChunkCoord(cx, cy, cz), out var slot) && (slot.Pending || slot.RefreshQueued))
                        return true;

        return false;
    }

    /// <summary>The body whose held cells the brush's sphere reaches in loaded blocks, or none.</summary>
    private ushort TouchedBody(in BrushOp op)
    {

        var min = ChunkCoord.FromWorld(op.Centre - new Vector3(op.Radius));
        var max = ChunkCoord.FromWorld(op.Centre + new Vector3(op.Radius));
        var r2 = op.Radius * op.Radius;

        for (var cz = min.Z; cz <= max.Z; cz++)
            for (var cy = min.Y; cy <= max.Y; cy++)
                for (var cx = min.X; cx <= max.X; cx++)
                {
                    if (!_blocks.TryGetValue(new ChunkCoord(cx, cy, cz), out var slot) || slot.Pending)
                        continue;

                    var block = slot.Block;
                    var origin = block.Coord.WorldOrigin;
                    var lo = Vector3.Max(op.Centre - new Vector3(op.Radius) - origin, Vector3.Zero);
                    var hi = Vector3.Min(op.Centre + new Vector3(op.Radius) - origin, new Vector3(ChunkConstants.Size - 1));

                    for (var z = (int)lo.Z; z <= (int)hi.Z; z++)
                        for (var y = (int)lo.Y; y <= (int)hi.Y; y++)
                            for (var x = (int)lo.X; x <= (int)hi.X; x++)
                            {
                                var i = ChunkConstants.Index(x, y, z);

                                if (block.Body[i] > WaterBodies.Sea && (block.Flags[i] & (byte)WaterFlags.Reservoir) != 0
                                    && Vector3.DistanceSquared(origin + new Vector3(x, y, z), op.Centre) <= r2)
                                    return block.Body[i];
                            }
                }

        return WaterBodies.None;
    }

    /// <summary>Adds a sea zone disc (world x, world z, radius): grid mask, zone sim domain, and held cells for the 3D automaton.</summary>
    private void AddZone(Vector3 disc, bool grown = false)
    {
        _zones.Add(disc);
        _bathymetry.AddSimulatedDisc(disc.X, disc.Y, disc.Z);
        Zones.SeaLevel = _terrain.Params.SeaLevel;
        Zones.AddDisc(disc, _bathymetry, grown);
        SimulateDisc(disc);
    }

    /// <summary>A calm grown disc goes back to the open ocean once the ocean has faded it out (<see cref="EndZone"/>).</summary>
    private void RemergeZone(Vector3 disc) => _bathymetry.FadeOutDisc(disc);

    /// <summary>A faded-out disc leaves the sim and the mask, and its blocks are re-held.</summary>
    private void EndZone(Vector3 disc)
    {
        _zones.Remove(disc);
        Zones.RemoveDisc(disc, _bathymetry);
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

    /// <summary>Frees the body's water inside the disc: every block it touches is read again (or created) with it.</summary>
    private void AddDisc(Vector4 shape)
    {
        _discs.Add(new Disc { Shape = shape });
        _discShapes.Add(shape);
        ForDiscBlocks(shape, (coord, _) => Request(coord, refresh: true));
    }

    private void RemoveDisc(int d)
    {
        _discs.RemoveAt(d);
        _discShapes.RemoveAt(d);
    }

    /// <summary>The disc's footprint covers the chunk column.</summary>
    private static bool Covers(Vector4 disc, ChunkCoord coord)
    {
        var origin = coord.WorldOrigin;
        var nx = Math.Clamp(disc.X, origin.X, origin.X + ChunkConstants.Size);
        var nz = Math.Clamp(disc.Y, origin.Z, origin.Z + ChunkConstants.Size);
        return (nx - disc.X) * (nx - disc.X) + (nz - disc.Y) * (nz - disc.Y) <= disc.Z * disc.Z;
    }

    /// <summary>Every chunk in the disc's footprint between its body's bottom and level: the loaded slot, or null.</summary>
    private void ForDiscBlocks(Vector4 disc, Action<ChunkCoord, Slot?> act)
    {
        ref var body = ref Bodies[(ushort)disc.W];
        var min = ChunkCoord.FromWorld(new Vector3(disc.X - disc.Z, body.Bottom, disc.Y - disc.Z));
        var max = ChunkCoord.FromWorld(new Vector3(disc.X + disc.Z, body.Level + 1f, disc.Y + disc.Z));

        for (var z = min.Z; z <= max.Z; z++)
            for (var x = min.X; x <= max.X; x++)
                for (var y = min.Y; y <= max.Y; y++)
                {
                    var coord = new ChunkCoord(x, y, z);
                    act(coord, _blocks.TryGetValue(coord, out var slot) ? slot : null);
                }
    }

    /// <summary>
    /// Once per tick, every disc (docs/voxel-water.md "Bodies of water"):
    /// <list type="bullet">
    /// <item>its blocks all asleep for <see cref="DiscSettleSeconds"/>: it is done. It is dropped, and its blocks, still
    /// asleep, join the free water at the body's level back into it (<see cref="WaterBlock.JoinBodies"/>), so the hole's
    /// water is the body's and the blocks go back to costing nothing;</item>
    /// <item>the body pouring into it harder than <see cref="GrowFlux"/> along its edge: it grows, so a breach that runs a
    /// stream simulates a widening area with real currents toward it;</item>
    /// <item>its flow steady for <see cref="OutletSeconds"/>: it is an outlet. With its blocks evicted it keeps draining
    /// the body at the recorded rate, fading as the level drops under where it was recorded, instead of being dropped.</item>
    /// </list>
    /// </summary>
    private void SettleDiscs(float dt)
    {
        for (var d = _discs.Count - 1; d >= 0; d--)
        {
            var disc = _discs[d];
            var calm = true;
            var loaded = 0;
            ForDiscBlocks(disc.Shape, (_, slot) =>
            {
                if (slot is null)
                    return;

                loaded++;
                var block = slot.Block;
                calm &= !slot.Pending && !slot.RefreshQueued && !slot.Meshing && (block.Asleep || (block.MaxDelta < CalmDelta && block.MaxFlux < CalmFlux));
            });

            if (loaded == 0)
            {
                if (!disc.IsOutlet || !DrainOutlet(disc, dt))
                    RemoveDisc(d);

                continue;
            }

            disc.Quiet = calm ? disc.Quiet + dt : 0f;

            if (disc.Quiet >= DiscSettleSeconds)
            {
                RemoveDisc(d);
                ForDiscBlocks(disc.Shape, (coord, slot) =>
                {
                    if (slot is null)
                        return;

                    // A calm block is put to sleep: the join takes its level water, and what is left wakes it if it must.
                    if (!slot.Block.Asleep)
                    {
                        slot.Block.Asleep = true;
                        slot.Block.ClearFluxes();
                    }

                    slot.Block.MarkDiscs(RemainingDiscs(coord));
                    Settle(slot);
                    NoteSleep(slot);

                    // Free water still in the disc's columns that didn't join stops being the body's: it leaves it.
                    Bodies.Exchange(disc.Body, -(float)slot.Block.FreeWaterLeft(disc.Shape));
                });

                continue;
            }

            if (calm)
                continue;

            Grow(d, disc, dt);
        }
    }

    /// <summary>Grows a disc the body keeps pouring into, and records it as an outlet once its flow holds steady.</summary>
    private void Grow(int d, Disc disc, float dt)
    {
        disc.Cooldown -= dt;
        var edge = MathF.Tau * disc.Shape.Z;

        if (disc.Flow > GrowFlux * edge && disc.Cooldown <= 0f && disc.Shape.Z < MaxDiscRadius)
        {
            disc.Shape.Z = MathF.Min(disc.Shape.Z + GrowStep, MaxDiscRadius);
            disc.Cooldown = GrowCooldown;
            _discShapes[d] = disc.Shape;
            ForDiscBlocks(disc.Shape, (coord, _) => Request(coord, refresh: true));
        }

        if (disc.Steady >= OutletSeconds)
        {
            disc.OutletRate = disc.Flow;
            disc.OutletLevel = Bodies[disc.Body].Level;
        }
    }

    /// <summary>
    /// An outlet whose blocks are evicted: drains the body at its recorded rate, halving for every metre the level has
    /// dropped past the first since it was recorded (a weir slackens as its head falls). False once that is next to nothing.
    /// </summary>
    private bool DrainOutlet(Disc disc, float dt)
    {
        ref var body = ref Bodies[disc.Body];
        var drop = MathF.Max(0f, disc.OutletLevel - body.Level - 1f);
        var rate = disc.OutletRate * MathF.Pow(0.5f, drop);

        if (rate < 0.5f * MinOutletRate || body.Level <= body.Bottom)
            return false;

        Bodies.Exchange(disc.Body, -rate * dt);
        return true;
    }

    private readonly float[] _discGave = new float[64];

    /// <summary>
    /// After a step: each disc's flow this step, smoothed, and how long it has held steady. A block's exchange with the
    /// body counts once, for the nearest of the discs over it, so the discs a run of digs leaves overlapping don't each
    /// claim the whole flow (and each drain it again as outlets).
    /// </summary>
    private void MeasureDiscs(float stepTime)
    {
        var gave = _discGave.Length >= _discs.Count ? _discGave : new float[_discs.Count * 2];
        gave.AsSpan(0, _discs.Count).Clear();

        foreach (var slot in _awake)
        {
            var block = slot.Block;
            var origin = block.Coord.WorldOrigin;
            var centre = new Vector2(origin.X + ChunkConstants.Size / 2f, origin.Z + ChunkConstants.Size / 2f);
            var nearest = -1;
            var best = float.MaxValue;

            for (var d = 0; d < _discs.Count; d++)
            {
                var disc = _discs[d];

                if (block.BodyExchange[disc.Body] == 0f || !Covers(disc.Shape, block.Coord))
                    continue;

                var distance = Vector2.DistanceSquared(centre, new Vector2(disc.Shape.X, disc.Shape.Y));

                if (distance < best)
                {
                    best = distance;
                    nearest = d;
                }
            }

            if (nearest >= 0)
                gave[nearest] -= block.BodyExchange[_discs[nearest].Body];
        }

        for (var d = 0; d < _discs.Count; d++)
        {
            var disc = _discs[d];
            var rate = gave[d] / stepTime;
            var k = MathF.Min(1f, stepTime / FlowSmoothing);
            disc.Flow += (rate - disc.Flow) * k;
            var steady = disc.Flow >= MinOutletRate && MathF.Abs(rate - disc.Flow) <= OutletSteadiness * disc.Flow;
            disc.Steady = steady ? disc.Steady + stepTime : 0f;
        }
    }

    /// <summary>Each body gains the rain falling on its area at the weather's intensity over its centre.</summary>
    private void RainOnBodies(float dt)
    {
        if (RainAt is null)
            return;

        for (var id = WaterBodies.Sea + 1; id < Bodies.Count; id++)
        {
            ref var body = ref Bodies[(ushort)id];

            if (!body.Alive || body.Area <= 0f)
                continue;

            var rain = RainAt(body.Centre.X, body.Centre.Y);

            if (rain > 0f)
                Bodies.Exchange((ushort)id, rain * WaterBodies.RainRate * body.Area * dt);
        }
    }

    /// <summary>Each generated body gains what its spring feeds it (<see cref="WaterBodyInfo.Inflow"/>): what its river carries away.</summary>
    private void FeedBodies(float dt)
    {
        foreach (var info in Generated)
            if (info.Inflow > 0f && Bodies[info.Id].Alive)
                Bodies.Exchange(info.Id, info.Inflow * dt);
    }

    /// <summary>
    /// Main thread, once a frame: a routed reach the camera has come within <see cref="FlowSimRadius"/> of, with every
    /// block of it loaded, is taken over by the sim (its blocks wake; its routed storage is reconciled with their water);
    /// an active reach the camera has left is routed again, its blocks' water its storage and the blocks frozen as they
    /// stand, still drawing their flow. Pipe model only: the automaton has no flow to carry across a reach's edge.
    /// </summary>
    private void UpdateFlows(Vector3 camera)
    {
        foreach (var flow in Flows.All)
        {
            for (var r = 0; r < flow.Reaches.Length; r++)
            {
                var reach = flow.Reaches[r];
                var near = Pipes && Near(reach, camera);

                // Its blocks first loaded, all straight from the generator: their water is its design storage.
                if (!reach.Calibrated && !reach.Active && ReachLoaded(reach))
                    WaterFlows.Calibrate(flow, r, ReachWater(flow, r, wake: false));

                if (reach.Active && !near)
                    WaterFlows.Freeze(flow, r, ReachWater(flow, r, wake: false));
                else if (!reach.Active && near && ReachLoaded(reach))
                    WaterFlows.Activate(flow, r, ReachWater(flow, r, wake: true));
            }
        }
    }

    private bool Near(WaterFlows.Reach reach, Vector3 camera)
    {
        const int s = ChunkConstants.Size;
        var dx = MathF.Max(MathF.Max(reach.X * s - camera.X, camera.X - (reach.X + 1) * s), 0f);
        var dz = MathF.Max(MathF.Max(reach.Z * s - camera.Z, camera.Z - (reach.Z + 1) * s), 0f);
        return dx * dx + dz * dz <= FlowSimRadius * FlowSimRadius;
    }

    private bool ReachLoaded(WaterFlows.Reach reach)
    {
        foreach (var coord in reach.Blocks)
            if (!_blocks.TryGetValue(coord, out var slot) || slot.Pending)
                return false;

        return true;
    }

    /// <summary>The flow's water in the loaded blocks of a reach's columns (m³), as they are woken or frozen.</summary>
    private float ReachWater(WaterFlows.Flow flow, int reach, bool wake)
    {
        var water = 0f;

        foreach (var (coord, slot) in _blocks)
        {
            if (slot.Pending || !Flows.ReachAt(coord.X, coord.Z, out var at, out var r) || at != flow || r != reach)
                continue;

            var block = slot.Block;
            water += block.FlowWater();
            block.Frozen = !wake;
            block.Asleep = !wake;
            block.QuietSteps = 0;
            block.CalmSteps = 0;
            slot.SeaFree = false;
        }

        return water;
    }

    /// <summary>
    /// Before a step, after the halos are copied: a face of an awake block onto a frozen block, or onto an unloaded routed
    /// reach of a flow, is that reach's edge. Toward an unloaded one the halo's flow columns open onto the chunk's apron
    /// (<see cref="WaterBlock.OpenFlowEdge"/>); either way their water is scaled to the reach's stage, so the block feels
    /// the head the routed river stands at there. What the step moves across the face is booked to the reach.
    /// </summary>
    private void PrepareFlowEdges(WaterBlock block)
    {
        var c = block.Coord;

        for (var f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceOffsets[f];
            var neighbour = Neighbour(c, f);

            if (neighbour is { Frozen: false } || !Flows.ReachAt(c.X + dx, c.Z + dz, out var flow, out var reach))
                continue;

            if (neighbour is null)
            {
                if (dy != 0 || !block.HasFlow || flow.Reaches[reach].Active)
                    continue;

                block.OpenFlowEdge((Face)f);
            }

            block.ScaleFlowEdge((Face)f, flow.Reaches[reach].Stage);
            _flowEdges.Add(new FlowEdge(block, (Face)f, flow, reach));
        }
    }

    /// <summary>After a step's apply pass: what crossed each flow edge goes into (or came out of) its routed reach.</summary>
    private void BookFlowEdges(float dt)
    {
        _flowEdgeBooked = 0f;

        foreach (var edge in _flowEdges)
        {
            var moved = edge.Block.EdgeOutflow(edge.Face, dt);
            WaterFlows.Book(edge.Flow, edge.Reach, moved);
            _flowEdgeBooked += moved;
        }
    }

    /// <summary>Bodies whose ledger holds a level step move; their blocks refill and wake where they must.</summary>
    private void SettleBodies()
    {
        Bodies.SettleLevels();

        foreach (var id in Bodies.Moved)
            MoveBody(id);

        foreach (var id in Bodies.Released)
            ReleaseBody(id);
    }

    /// <summary>A released body (<see cref="WaterBodies.Released"/>): its held cells go back to free water and wake, and its discs end.</summary>
    private void ReleaseBody(ushort id)
    {
        foreach (var (coord, slot) in _blocks)
        {
            if (slot.Pending || slot.Block.Frozen || !slot.Block.Holds(id) || !slot.Block.ReleaseDead())
                continue;

            slot.Block.Asleep = false;
            slot.Block.QuietSteps = 0;
            slot.MeshDirty = true;
            RefreshFlagHalos(coord);
        }

        for (var d = _discs.Count - 1; d >= 0; d--)
            if (_discs[d].Body == id)
                RemoveDisc(d);
    }

    /// <summary>The discs still live that touch the chunk's footprint.</summary>
    private ReadOnlySpan<Vector4> RemainingDiscs(ChunkCoord coord)
    {
        _remaining.Clear();

        foreach (var disc in _discs)
            if (Covers(disc.Shape, coord))
                _remaining.Add(disc.Shape);

        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_remaining);
    }

    /// <summary>
    /// A block has come to rest (asleep, its halos fresh): free water at a body's level joins that body, and free water
    /// standing level on its own becomes a body. What the bodies paid and the surface they gained is booked. Sleeping
    /// neighbours of a block that took water in settle again, so their water at its level joins it now, not when they
    /// next wake and sleep.
    /// </summary>
    private void Settle(Slot slot)
    {
        SettleOne(slot);

        for (var q = 0; q < _resettle.Count; q++)
        {
            var next = _blocks[_resettle[q]];
            next.Block.MarkDiscs(RemainingDiscs(next.Block.Coord));
            SettleOne(next);
            NoteSleep(next);
        }

        _resettle.Clear();
    }

    private readonly List<ChunkCoord> _resettle = new(8);
    private Func<ChunkCoord, WaterBlock?>? _asleepBlock;

    private WaterBlock? AsleepBlock(ChunkCoord coord) =>
        _blocks.TryGetValue(coord, out var slot) && !slot.Pending && slot.Block.Asleep && !slot.Block.Frozen ? slot.Block : null;

    private void SettleOne(Slot slot)
    {
        var block = slot.Block;
        var free = Audit ? block.FreeMass() : 0.0;
        block.JoinBodies();
        var joined = block.Joined.Count;
        block.BornBodies();

        foreach (var pool in block.EdgePools)
            block.BornAcross(pool, _asleepBlock ??= AsleepBlock);

        block.Revision++;

        foreach (var (id, paid, surface) in block.Joined)
            Bodies.Took(id, paid, surface);

        if (block.Joined.Count > 0)
            block.RaiseTops();

        // Free water that joined a body leaves the free water (the body books only what it paid to take it in at its fill).
        if (Audit)
            AuditEvent("settle", block, block.FreeMass() - free);

        if (block.Joined.Count == 0)
            return;

        RefreshFlagHalos(block.Coord);
        slot.MeshDirty = true;

        // A new body: the neighbours' water may stand at its level too.
        if (block.Joined.Count > joined)
            DirtyNeighbourMeshes(block.Coord);

        for (var f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceOffsets[f];
            var at = new ChunkCoord(block.Coord.X + dx, block.Coord.Y + dy, block.Coord.Z + dz);

            if (AsleepBlock(at) is { } neighbour && neighbour.HasFreeWater() && !_resettle.Contains(at) && RemainingDiscs(at).IsEmpty)
                _resettle.Add(at);
        }
    }

    /// <summary>
    /// Before a step, a block at the sea's height takes the swell over its padded columns (<see cref="WaterBlock.Swell"/>),
    /// bilinear from the tick's lattice. Blocks nowhere near the sea, touching none of its cells, or with no swell over them, hold a flat sea.
    /// </summary>
    private void PrepareSwell(WaterBlock block)
    {
        const int p = ChunkConstants.Padded;
        var seaTop = Bodies.SeaTop;
        var bottom = block.Coord.Y * ChunkConstants.Size - 1;
        var top = bottom + ChunkConstants.Size + 1;
        var any = false;
        var origin = block.Coord.WorldOrigin;
        var lx = (int)MathF.Floor((origin.X - 1f) / SwellSpacing);
        var lz = (int)MathF.Floor((origin.Z - 1f) / SwellSpacing);
        Span<float> points = stackalloc float[SwellPoints * SwellPoints];

        // A block sharing no cell with the sea (inland, a lake's) takes no swell, or samples it for nothing.
        if (SeaSurface is not null && top >= seaTop - WaterBodies.SwellDepth && bottom <= seaTop + WaterBlock.ZoneRunUp && block.TouchesSea())
        {
            for (var j = 0; j < SwellPoints; j++)
            {
                for (var i = 0; i < SwellPoints; i++)
                {
                    var h = SwellPoint(lx + i, lz + j);
                    points[i + j * SwellPoints] = h;
                    any |= h != 0f;
                }
            }
        }

        if (!any)
        {
            if (block.Swelling)
            {
                block.Swell.AsSpan().Clear();
                block.Swelling = false;
            }

            return;
        }

        for (var z = 0; z < p; z++)
        {
            var fz = (origin.Z + z - 1f) / SwellSpacing - lz;
            var iz = (int)fz;
            var tz = fz - iz;

            for (var x = 0; x < p; x++)
            {
                var fx = (origin.X + x - 1f) / SwellSpacing - lx;
                var ix = (int)fx;
                var k = ix + iz * SwellPoints;
                var near = float.Lerp(points[k], points[k + 1], fx - ix);
                var far = float.Lerp(points[k + SwellPoints], points[k + SwellPoints + 1], fx - ix);
                block.Swell[x + z * p] = float.Lerp(near, far, tz);
            }
        }

        block.Swelling = true;
    }

    /// <summary>
    /// The swell at a lattice point this tick, faded out past <see cref="SwellRadius"/>: run on from its last sample
    /// (<see cref="RefreshSwell"/>), the same for every block. A point first read is sampled now.
    /// </summary>
    private float SwellPoint(int ix, int iz)
    {
        var x = ix * SwellSpacing;
        var z = iz * SwellSpacing;
        var dx = x - _camera.X;
        var dz = z - _camera.Z;
        var fade = Math.Clamp((SwellRadius - MathF.Sqrt(dx * dx + dz * dz)) / SwellFade, 0f, 1f);

        if (fade <= 0f)
            return 0f;

        var key = (long)ix << 32 | (uint)iz;

        if (!_swellIndex.TryGetValue(key, out var k))
        {
            if (_swellCount == _swellSamples.Length)
                Array.Resize(ref _swellSamples, _swellCount * 2);

            k = _swellCount++;
            var h = SeaSurface!(x, z);
            _swellSamples[k] = new SwellSample { Key = key, Height = h, Fresh = h, Sampled = _swellClock };
            _swellIndex.Add(key, k);
        }

        ref var point = ref _swellSamples[k];
        point.Read = _swellClock;
        return fade * (point.Height + point.Rate * (float)(_swellClock - point.Sampled));
    }

    /// <summary>
    /// Once a tick: samples a share of the lattice again (every point once in <see cref="SwellRefreshTicks"/>). A point runs
    /// on from where it stands now toward where its last two samples put the swell one refresh on, so it never jumps:
    /// jumping to each fresh sample stepped the sea's held cells by 1–3 cm at every refresh (16 cm in a storm). Once a
    /// second it forgets the points no block has read for <see cref="SwellForget"/>.
    /// </summary>
    private void RefreshSwell(double dt)
    {
        var interval = SwellRefreshTicks * dt;

        for (var k = _swellTick % SwellRefreshTicks; k < _swellCount; k += SwellRefreshTicks)
        {
            ref var point = ref _swellSamples[k];
            var h = SeaSurface!((int)(point.Key >> 32) * SwellSpacing, (int)point.Key * SwellSpacing);
            var since = _swellClock - point.Sampled;
            var now = point.Height + point.Rate * (float)since;
            var ahead = since > 0.0 ? h + (float)((h - point.Fresh) / since * interval) : h;
            point.Height = now;
            point.Rate = (float)((ahead - now) / interval);
            point.Fresh = h;
            point.Sampled = _swellClock;
        }

        if (_swellTick % (SwellRefreshTicks * 10) != 0)
            return;

        var kept = 0;

        for (var k = 0; k < _swellCount; k++)
        {
            var point = _swellSamples[k];

            if (_swellClock - point.Read > SwellForget)
            {
                _swellIndex.Remove(point.Key);
                continue;
            }

            _swellSamples[kept] = point;
            _swellIndex[point.Key] = kept++;
        }

        _swellCount = kept;
    }

    /// <summary>The swell at a block's centre this tick, bilinear from the lattice.</summary>
    private float SwellAtCentre(ChunkCoord coord)
    {
        var origin = coord.WorldOrigin;
        var fx = (origin.X + ChunkConstants.Size * 0.5f) / SwellSpacing;
        var fz = (origin.Z + ChunkConstants.Size * 0.5f) / SwellSpacing;
        var ix = (int)MathF.Floor(fx);
        var iz = (int)MathF.Floor(fz);
        var near = float.Lerp(SwellPoint(ix, iz), SwellPoint(ix + 1, iz), fx - ix);
        var far = float.Lerp(SwellPoint(ix, iz + 1), SwellPoint(ix + 1, iz + 1), fx - ix);
        return float.Lerp(near, far, fz - iz);
    }

    /// <summary>A block asleep: whether its free water meets the sea, and the swell it slept under.</summary>
    private void NoteSleep(Slot slot)
    {
        var block = slot.Block;
        slot.SeaFree = Pipes && SeaSurface is not null && block.Asleep && !block.Frozen && block.TouchesSea() && block.HasFreeWater();

        if (slot.SeaFree)
            slot.SleptSwell = SwellAtCentre(block.Coord);
    }

    /// <summary>
    /// Once a tick: a sleeping block whose free water meets the sea wakes when the swell over it has moved, so water
    /// against the sea rises and falls with it near the camera. Past <see cref="SwellRadius"/> the swell is flat and it sleeps on.
    /// </summary>
    private void WakeOnSwell()
    {
        foreach (var (_, slot) in _blocks)
        {
            var block = slot.Block;

            if (!slot.SeaFree || !block.Asleep || slot.Pending || MathF.Abs(SwellAtCentre(block.Coord) - slot.SleptSwell) <= SwellWake)
                continue;

            slot.SeaFree = false;
            block.Asleep = false;
            block.QuietSteps = 0;
            block.CalmSteps = 0;
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
        created.SeaFree = false;
        created.Mesh.Clear();
        created.Building.Clear();
        created.Meshing = false;
        _blocks.Add(coord, created);
        _activationRequests.Add((coord, false));
    }

    /// <summary>Main thread, once per frame: finish activations, start new ones, evict far blocks.</summary>
    public void Update(Vector3 cameraPos)
    {
        _camera = cameraPos;

        if (_activationScheduler.IsExecuting)
            return;

        foreach (var job in _activationsInFlight)
        {
            if (_blocks.TryGetValue(job.Block!.Coord, out var slot) && ReferenceEquals(slot.Block, job.Block))
            {
                slot.Pending = false;
                slot.RefreshQueued = false;
                slot.MeshDirty = true;

                // A body's level may have moved while it was activating.
                // A body released while it was activating leaves its cells free.
                slot.Block.ReleaseDead();
                slot.Block.RefillHeld();
                slot.Block.RaiseTops();
                slot.Block.BookOpened();

                if (Audit)
                    AuditEvent(job.Refresh ? "refresh" : "activation", slot.Block, slot.Block.FreeMass() - slot.AuditFree);
                slot.Block.Asleep = slot.Block.AllHeld() || job.AtRest;
                slot.Block.QuietSteps = 0;

                if (Flows.ReachAt(job.Block.Coord.X, job.Block.Coord.Z, out var flow, out var reach) && !flow.Reaches[reach].Active)
                {
                    slot.Block.Frozen = true;
                    slot.Block.Asleep = true;
                }

                RefreshFlagHalos(job.Block.Coord);
                NoteSleep(slot);
                DirtyNeighbourMeshes(job.Block.Coord);
            }

            job.Block = null;
            _activationPool.Push(job);
        }

        _activationsInFlight.Clear();

        if (_digs.Count > 0)
            TestDigs();

        // Before eviction: a reach the camera has left is measured while its blocks are still loaded.
        UpdateFlows(cameraPos);
        Evict(cameraPos);
        RequestGenerated(cameraPos);

        var queued = 0;
        for (var i = 0; i < _activationRequests.Count && _activationPool.Count > 0; i++)
        {
            var (coord, refresh) = _activationRequests[i];
            if (!_blocks.TryGetValue(coord, out var slot) || slot.Meshing)
                continue;

            // A refresh writes into the block, so it must not be stepped meanwhile.
            slot.Pending = true;
            slot.AuditFree = refresh ? slot.Block.FreeMass() : 0.0;
            var job = _activationPool.Pop();
            job.Prepare(slot.Block, refresh, _terrain.Params, _terrain.Ops, _zones, _discShapes, Generated, Bodies, GeneratedFlows);
            _activationScheduler.Queue(job, 1);
            _activationsInFlight.Add(job);
            _activationRequests.RemoveAt(i--);
            queued++;
        }

        if (queued > 0)
            _activationScheduler.ExecuteJobs();
    }

    /// <summary>
    /// The generator's bodies' blocks within reach of the camera (a chunk inside the eviction radius, so they don't
    /// thrash at its edge): their water is generated, so it exists in the sim only where a block holds it. They start
    /// asleep, at rest.
    /// </summary>
    private void RequestGenerated(Vector3 cameraPos)
    {
        var centre = ChunkCoord.FromWorld(cameraPos);
        const int reach = (EvictRadius - 1) * (EvictRadius - 1);

        foreach (var coord in _generatedBlocks)
        {
            var dx = coord.X - centre.X;
            var dz = coord.Z - centre.Z;

            if (dx * dx + dz * dz <= reach && !_blocks.ContainsKey(coord))
                Request(coord, refresh: false);
        }
    }

    /// <summary>Every chunk the generator's bodies' water reaches into, and every block of its flows' reaches.</summary>
    private static ChunkCoord[] GeneratedBlocks(WaterBodyInfo[] generated, WaterFlows flows)
    {
        var coords = new HashSet<ChunkCoord>();

        foreach (var flow in flows.All)
            foreach (var reach in flow.Reaches)
                coords.UnionWith(reach.Blocks);

        foreach (var info in generated)
        {
            var low = ChunkCoord.FromWorld(new Vector3(info.Centre.X - info.Radius, info.Bottom, info.Centre.Y - info.Radius));
            var high = ChunkCoord.FromWorld(new Vector3(info.Centre.X + info.Radius, info.Level, info.Centre.Y + info.Radius));

            for (var z = low.Z; z <= high.Z; z++)
            {
                for (var x = low.X; x <= high.X; x++)
                {
                    if (!DiscReaches(info, x, z))
                        continue;

                    for (var y = low.Y; y <= high.Y; y++)
                        coords.Add(new ChunkCoord(x, y, z));
                }
            }
        }

        return [.. coords];
    }

    /// <summary>The body's disc reaches into chunk column (x, z).</summary>
    private static bool DiscReaches(in WaterBodyInfo info, int x, int z)
    {
        var origin = new ChunkCoord(x, 0, z).WorldOrigin;
        var nx = Math.Clamp(info.Centre.X, origin.X, origin.X + ChunkConstants.Size) - info.Centre.X;
        var nz = Math.Clamp(info.Centre.Y, origin.Z, origin.Z + ChunkConstants.Size) - info.Centre.Y;
        return nx * nx + nz * nz <= info.Radius * info.Radius;
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

        if (Pipes && SeaSurface is not null)
        {
            _swellClock += dt;
            _swellTick++;
            RefreshSwell(dt);
            WakeOnSwell();
        }

        while (_meshing.Count == 0 && _accumulator >= stepTime && steps < MaxStepsPerTick && Stopwatch.GetTimestamp() - start < budget)
        {
            _accumulator -= stepTime;
            Step();
            steps++;
        }

        if (_accumulator >= stepTime)
            _accumulator = 0.0;

        // The voxel steps alone: the zone sim below has its own timing (LastZoneMs).
        if (steps > 0)
            LastStepMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds / steps;

        StartMeshing();
        StepZones(dt);
        RainOnBodies((float)dt);
        FeedBodies((float)dt);
        Flows.Route((float)dt, Bodies);

        if (_discs.Count > 0)
            SettleDiscs((float)dt);

        SettleBodies();
    }

    /// <summary>
    /// Diagnostics: audit every step's water. The free water no body counts as its own (WaterBlock.FreeMass) in the awake
    /// blocks after the step, less before, plus what the bodies took in (the sea's too), is water the step made (positive)
    /// or lost: 0 for a step that conserves. Costs a pass over each awake block's cells twice a step, so it is off unless asked for.
    /// </summary>
    public bool Audit;

    /// <summary>Water the audited steps made, summed (m³), and the steps audited.</summary>
    public double AuditMade { get; private set; }
    public long AuditSteps { get; private set; }

    /// <summary>The worst audited step: water it made (m³), its block that changed most against its bodies' exchange, and that block's largest fill.</summary>
    public (float Made, ChunkCoord Block, float BlockMade, float MaxFill, long Step) AuditWorst { get; private set; }

    /// <summary>A step making or losing more than this (m³) is logged, its three worst blocks with it, at most AuditLogLimit times.</summary>
    public float AuditLogThreshold = 0.5f;
    public int AuditLogLimit = 200;
    private int _auditLogged;
    private readonly double[] _auditBefore = new double[MaxBlocks];
    private readonly double[] _auditMade = new double[MaxBlocks];

    private void Step()
    {
        WakeNeighboursOfAwake();

        _awake.Clear();
        _flowEdges.Clear();
        _flowEdgeBooked = 0f;
        foreach (var (_, slot) in _blocks)
            if (!slot.Pending && !slot.Block.Asleep && !slot.Block.Frozen)
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
                WaterHalo.CopyFlags(slot.Block, neighbour, (Face)f);
                WaterHalo.CopyWater(slot.Block, neighbour, (Face)f);

                if (Pipes && f != (int)Face.NegY)
                    WaterHalo.CopyFalling(slot.Block, neighbour, (Face)f);

                if (Pipes)
                {
                    WaterHalo.CopyColour(slot.Block, neighbour, (Face)f);
                    WaterHalo.CopyFroth(slot.Block, neighbour, (Face)f);
                }
            }

            if (Pipes)
            {
                PrepareSwell(slot.Block);

                if (Flows.Count > 0)
                    PrepareFlowEdges(slot.Block);
            }
        }
        if (Audit)
            for (var k = 0; k < _awake.Count; k++)
                _auditBefore[k] = _awake[k].Block.FreeMass();

        RunPass(Pipes ? WaterStepJob.Kind.PipeFluxes : WaterStepJob.Kind.OutflowScale);

        foreach (var slot in _awake)
            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(slot.Block, Neighbour(slot.Block.Coord, f), (Face)f);
        RunPass(Pipes ? WaterStepJob.Kind.PipeApply : WaterStepJob.Kind.ApplyFlows);

        var mass = 0f;
        var stepTime = 1f / StepsPerSecond;
        BookFlowEdges(stepTime);

        // What crossed a flow's edge went to its routed reach: the steps' own water lost it.
        var made = (double)_flowEdgeBooked;
        for (var k = 0; k < _awake.Count; k++)
        {
            var slot = _awake[k];
            var block = slot.Block;
            WaterFlow.Swap(block);
            var blockMass = block.Mass();
            mass += blockMass;

            if (Audit)
            {
                // Held cells are refilled and the bodies' own free water (disc columns) is booked to them: what the
                // bodies took is what the rest of the water lost. (The flows' edges are counted above, for the step.)
                var taken = 0.0;
                foreach (var id in block.Touched)
                    taken += block.BodyExchange[id];

                _auditMade[k] = block.FreeMass() - _auditBefore[k] + taken;
                made += _auditMade[k];
            }

            if (float.IsNaN(mass) && !_nanReported)
                ReportNaN(block);

            // What each body's held cells gave or took this step.
            foreach (var id in block.Touched)
                Bodies.Exchange(id, block.BodyExchange[id]);

            // With momentum a steady stream changes no fill, so the pipe model also waits until no face moves more than
            // SleepDelta of a cell in a step. (A fixed 1e-4 m³/s kept a pond awake for over a minute on millimetre sloshing,
            // and its blocks, remeshed every tick, starved the refresh a breach needed.)
            var noisy = block.MaxDelta > SleepDelta || block.MaxFlux * stepTime > SleepDelta;
            block.QuietSteps = noisy ? 0 : block.QuietSteps + 1;
            block.CalmSteps = block.MaxDelta < CalmDelta && block.MaxFlux < CalmFlux ? block.CalmSteps + 1 : 0;

            if (noisy)
                slot.MeshDirty = true;

            var resting = block.QuietSteps >= QuietStepsToSleep || block.CalmSteps >= CalmSeconds * StepsPerSecond;

            // Free water against a swelling sea moves with it: asleep, it would freeze, or settle into a body of its own
            // that no longer follows the sea. Checked again after another quiet spell.
            if (resting && block.Swelling && block.TouchesSea() && block.HasFreeWater())
            {
                resting = false;
                block.QuietSteps = 0;
                block.CalmSteps = 0;
            }

            if (resting)
            {
                block.Asleep = true;
                block.ClearFluxes();
                block.MarkDiscs(RemainingDiscs(block.Coord));
                Settle(slot);
                NoteSleep(slot);
            }

            if (block.BorderActive)
                GrowAcrossFaces(block);
        }

        Mass = mass;

        if (Audit)
            AuditStep(made);

        if (_discs.Count > 0)
            MeasureDiscs(stepTime);

        MarkStepped();
    }

    /// <summary>Water outside the steps (m³) by event, as audited: activations, refreshes, evictions, settles.</summary>
    public double AuditActivations { get; private set; }
    public double AuditRefreshes { get; private set; }
    public double AuditEvictions { get; private set; }
    public double AuditSettles { get; private set; }

    /// <summary>
    /// Audit (<see cref="Audit"/>): free water an event outside the steps added to a block (negative: took away), summed by
    /// kind, and logged when it is over <see cref="AuditLogThreshold"/>, with the discs over the block.
    /// </summary>
    private void AuditEvent(string kind, WaterBlock block, double free)
    {
        switch (kind)
        {
            case "activation": AuditActivations += free; break;
            case "refresh": AuditRefreshes += free; break;
            case "eviction": AuditEvictions += free; break;
            default: AuditSettles += free; break;
        }

        if (Math.Abs(free) <= AuditLogThreshold || _auditLogged >= AuditLogLimit)
            return;

        _auditLogged++;
        var discs = 0;
        foreach (var disc in _discs)
            discs += Covers(disc.Shape, block.Coord) ? 1 : 0;

        Console.WriteLine($"water audit: {kind} of block {block.Coord} changed its free water by {free:F1} m3 (max fill {block.MaxFill():F2}, {discs} discs over it); totals: activations {AuditActivations:F0}, refreshes {AuditRefreshes:F0}, evictions {AuditEvictions:F0}, settles {AuditSettles:F0}, steps {AuditMade:F1}");
    }

    /// <summary>
    /// Books an audited step (<see cref="Audit"/>): the total, the worst step, and a log line with the three blocks whose
    /// water changed most against their bodies' exchange when the step made or lost more than <see cref="AuditLogThreshold"/>.
    /// A block's own figure includes what crossed its faces to other awake blocks, so only the step's total is water made.
    /// </summary>
    private void AuditStep(double made)
    {
        AuditSteps++;
        AuditMade += made;

        if (Math.Abs(made) <= Math.Abs(AuditWorst.Made) && (Math.Abs(made) <= AuditLogThreshold || _auditLogged >= AuditLogLimit))
            return;

        Span<int> worst = stackalloc int[3];
        worst.Fill(-1);

        for (var k = 0; k < _awake.Count; k++)
        {
            for (var w = 0; w < 3; w++)
            {
                if (worst[w] >= 0 && Math.Abs(_auditMade[k]) <= Math.Abs(_auditMade[worst[w]]))
                    continue;

                for (var m = 2; m > w; m--)
                    worst[m] = worst[m - 1];

                worst[w] = k;
                break;
            }
        }

        if (Math.Abs(made) > Math.Abs(AuditWorst.Made) && worst[0] >= 0)
            AuditWorst = ((float)made, _awake[worst[0]].Block.Coord, (float)_auditMade[worst[0]], _awake[worst[0]].Block.MaxFill(), AuditSteps);

        if (Math.Abs(made) <= AuditLogThreshold || _auditLogged >= AuditLogLimit)
            return;

        _auditLogged++;
        var line = $"water audit: step {AuditSteps} made {made:F3} m3 (total {AuditMade:F1}), awake {_awake.Count}, bodies {Bodies.Count - 2}, discs {_discs.Count};";

        foreach (var k in worst)
            if (k >= 0)
                line += $" block {_awake[k].Block.Coord} {_auditMade[k]:F3} m3 max fill {_awake[k].Block.MaxFill():F2};";

        Console.WriteLine(line);
    }

    /// <summary>
    /// A body has given or taken a level step of water over its area: its held cells everywhere are refilled under the
    /// new level and meshed again, and the blocks holding free water against it (a pit, a stream fed by it) or a rim it
    /// now spills over wake to meet the new level.
    /// </summary>
    private void MoveBody(ushort id)
    {
        foreach (var (_, slot) in _blocks)
        {
            var block = slot.Block;

            if (slot.Pending || !block.Holds(id))
                continue;

            block.RefillHeld();
            slot.MeshDirty = true;

            if (!block.Frozen && (block.HasFreeWater() || block.Spills(id)))
                block.Asleep = false;
        }
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

        Zones.Agitate((float)dt);

        // Violent flow or a surge past the edge grows the zone (rate-limited, reach-limited and capped inside the sim);
        // grown discs that have settled remerge into the ocean.
        foreach (var disc in Zones.Growth)
            AddZone(disc, grown: true);

        Zones.Growth.Clear();
        Zones.CheckRemerge(_bathymetry, (float)dt);

        foreach (var disc in Zones.Remerged)
            RemergeZone(disc);

        Zones.Remerged.Clear();
        _bathymetry.AdvanceZoneFades((float)dt, _fadedOut);

        foreach (var disc in _fadedOut)
            EndZone(disc);

        _fadedOut.Clear();

        Zones.PackFlow();
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
            // A block with a refresh waiting is left for it: one meshed every tick never got its edit (its slot was always
            // meshing when activations were dispatched), and a breach next to always-moving water never opened.
            if (!slot.MeshDirty || slot.Pending || slot.RefreshQueued)
                continue;

            // The mesher reads the halos: a block that never stepped holds none, and a lower lake block meshed so took its top
            // layer for the surface and drew a second one 7 m under the real one (and spikes where corners averaged the two).
            for (var f = 0; f < 6; f++)
            {
                var neighbour = Neighbour(slot.Block.Coord, f);
                WaterHalo.CopyFlags(slot.Block, neighbour, (Face)f);
                WaterHalo.CopyWater(slot.Block, neighbour, (Face)f);
            }

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

    /// <summary>
    /// Sleeping neighbours that could exchange water with an awake block must step too, or mass would leak: those where
    /// free water (not held) touches the shared face on either side. A body's held cells beside a hole wake, so their
    /// own step draws the water and counts it against the body; held against held or dry air never does, so a dig by
    /// the shore wakes a block or two, not the lake. (Waking every neighbour with any water on the face woke 160 lake
    /// blocks in half a second: 8 ms a step with the automaton, 26 ms with the pipe model.)
    /// </summary>
    private void WakeNeighboursOfAwake()
    {
        foreach (var (coord, slot) in _blocks)
        {
            if (slot.Pending || slot.Block.Asleep)
                continue;

            for (var f = 0; f < 6; f++)
            {
                var neighbour = Neighbour(coord, f);
                // A frozen block is a routed reach's picture: the water crossing into it is booked to the reach instead.
                if (neighbour is null || !neighbour.Asleep || neighbour.Frozen)
                    continue;

                // The sea is an infinite source and sink and solids never change, so a sea block whose shared face is all
                // held can stay asleep.
                if (neighbour.Coord.Y * ChunkConstants.Size <= Bodies.SeaTop && FaceHeld(neighbour, Opposite((Face)f)))
                    continue;

                // Its quiet count carries on: if its water really moves its own changes reset it. (Resetting it here, two
                // quiet blocks sharing a pond took turns waking each other and neither ever slept.)
                if (slot.Block.FreeWaterOnFace((Face)f) || neighbour.FreeWaterOnFace(Opposite((Face)f)))
                    neighbour.Asleep = false;
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

            if (Audit)
                AuditEvent("eviction", slot!.Block, -slot.Block.FreeMass());

            _slotPool.Push(slot!);

            for (var f = 0; f < 6; f++)
            {
                var neighbour = Neighbour(coord, f);
                if (neighbour is not null)
                    WaterHalo.CopyFlags(neighbour, null, Opposite((Face)f));
            }

            DirtyNeighbourMeshes(coord);
        }
    }

    /// <summary>
    /// A block arrived or left: its neighbours' surfaces at the shared faces read its columns (corners average across the
    /// face, and a column's top is where the water above it ends), so they are meshed again.
    /// </summary>
    private void DirtyNeighbourMeshes(ChunkCoord coord)
    {
        for (var f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = FaceOffsets[f];
            if (_blocks.TryGetValue(new ChunkCoord(coord.X + dx, coord.Y + dy, coord.Z + dz), out var slot) && !slot.Pending)
                slot.MeshDirty = true;
        }
    }

    /// <summary>Per frame: hand every non-empty surface mesh to the renderer, then the generated bodies' far planes.</summary>
    public void SubmitMeshes(VoxelWaterRenderer renderer)
    {
        foreach (var (coord, slot) in _blocks)
            if (!slot.Pending && slot.Mesh.IndexCount > 0)
                renderer.Add(coord, slot.Mesh);

        SubmitPlanes(renderer);
    }

    /// <summary>A far plane's quads are this many metres a side: the wind's reach is read per vertex.</summary>
    private const int PlaneCell = 8;
    private const int PlaneSide = ChunkConstants.Size / PlaneCell + 1;

    /// <summary>Per generated body, the chunk columns its disc reaches (y unused).</summary>
    private readonly ChunkCoord[][] _planeColumns;
    private readonly WaterMesh _plane = new();

    /// <summary>
    /// A generated body's water exists in the sim only where a block holds it, so past the loaded blocks it is drawn as a
    /// flat plane at its level over every chunk column of its disc no drawn block covers. The basin is closed, so the
    /// terrain, drawn first, hides it wherever the ground stands over the water, and the disc clips it in the shader.
    /// </summary>
    private void SubmitPlanes(VoxelWaterRenderer renderer)
    {
        for (var b = 0; b < Generated.Length; b++)
        {
            var info = Generated[b];
            ref var body = ref Bodies[info.Id];

            if (!body.Alive)
                continue;

            var y = ChunkCoord.FromWorld(new Vector3(info.Centre.X, body.Level - 0.5f, info.Centre.Y)).Y;
            var clip = new Vector4(info.Centre, info.Radius, 0f);
            var built = false;

            foreach (var column in _planeColumns[b])
            {
                var coord = new ChunkCoord(column.X, y, column.Z);

                // A loaded block draws its own surface once meshed, even an empty one (water drained by a dig).
                if (_blocks.TryGetValue(coord, out var slot) && !slot.Pending && !(slot.Mesh.IsEmpty && (slot.MeshDirty || slot.Meshing)))
                    continue;

                if (!built)
                {
                    BuildPlane(body.Level - coord.WorldOrigin.Y, body.Level - body.Bottom);
                    built = true;
                }

                if (!renderer.Add(coord, _plane, clip))
                    return;
            }
        }
    }

    /// <summary>A chunk-sized grid at <paramref name="height"/> over its origin, facing up, still.</summary>
    private void BuildPlane(float height, float thickness)
    {
        _plane.Clear();

        for (var z = 0; z < PlaneSide; z++)
            for (var x = 0; x < PlaneSide; x++)
                _plane.Vertices[_plane.VertexCount++] = new WaterVertex { Position = new Vector3(x * PlaneCell, height, z * PlaneCell), Normal = Vector3.UnitY, Thickness = thickness };

        for (var z = 0; z < PlaneSide - 1; z++)
        {
            for (var x = 0; x < PlaneSide - 1; x++)
            {
                var i = (uint)(x + z * PlaneSide);
                var indices = _plane.Indices.AsSpan(_plane.IndexCount, 6);
                indices[0] = i;
                indices[1] = i + PlaneSide;
                indices[2] = i + 1;
                indices[3] = i + 1;
                indices[4] = i + PlaneSide;
                indices[5] = i + PlaneSide + 1;
                _plane.IndexCount += 6;
            }
        }
    }

    private static ChunkCoord[][] PlaneColumns(WaterBodyInfo[] generated)
    {
        var columns = new ChunkCoord[generated.Length][];
        var list = new List<ChunkCoord>();

        for (var b = 0; b < generated.Length; b++)
        {
            var info = generated[b];
            var low = ChunkCoord.FromWorld(new Vector3(info.Centre.X - info.Radius, 0f, info.Centre.Y - info.Radius));
            var high = ChunkCoord.FromWorld(new Vector3(info.Centre.X + info.Radius, 0f, info.Centre.Y + info.Radius));
            list.Clear();

            for (var z = low.Z; z <= high.Z; z++)
                for (var x = low.X; x <= high.X; x++)
                    if (DiscReaches(info, x, z))
                        list.Add(new ChunkCoord(x, 0, z));

            columns[b] = [.. list];
        }

        return columns;
    }

    /// <summary>Where the fog fill left off round the window, so every stale block gets its turn under the per-frame budget.</summary>
    private int _fogCursor;

    /// <summary>
    /// Per frame, after <see cref="Update"/> and before the volume is recorded: the fog volume's stale blocks in its window,
    /// up to its budget, from the loaded blocks' cells (docs/water-fog.md) and, where no block is loaded, from the
    /// bathymetry: sea water from the bed up to sea level, solid under the ground, air above.
    /// </summary>
    public void FillFog(WaterFogVolume fog)
    {
        var start = Stopwatch.GetTimestamp();
        var (x0, y0, z0) = fog.WindowMin;
        var budget = (long)(FogFillBudgetMs * Stopwatch.Frequency / 1000.0);

        // The bathymetry blocks' keys: a hash of the grid cells under each (with the sea mask's reach), so a block is filled
        // again only when its own ground changed, not on every rebuild of the kilometre grid. Hashed once per rebuild.
        if (_fogKeyVersion != _bathymetry.Version || _fogKeyWindow != fog.WindowMin)
        {
            for (var index = 0; index < WaterFogVolume.Slots; index++)
                _fogKeys[index] = BathymetryKey(FogBlock(fog.WindowMin, index));

            _fogKeyVersion = _bathymetry.Version;
            _fogKeyWindow = fog.WindowMin;
            fog.KeysPeakMs = MathF.Max(fog.KeysPeakMs, (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        for (var k = 0; k < WaterFogVolume.Slots; k++)
        {
            var index = (_fogCursor + k) % WaterFogVolume.Slots;
            var coord = FogBlock(fog.WindowMin, index);
            var loaded = _blocks.TryGetValue(coord, out var slot) && !slot.Pending;
            var revision = loaded ? slot!.Block.Revision : _fogKeys[index];

            if (!fog.Stale(coord.X, coord.Y, coord.Z, revision))
                continue;

            if (!fog.CanUpload || Stopwatch.GetTimestamp() - start > budget)
            {
                _fogCursor = index;
                break;
            }

            // Written to a managed scratch and copied once: the volume's staging is write-combined memory.
            var blockStart = Stopwatch.GetTimestamp();
            var cells = fog.Reserve(coord.X, coord.Y, coord.Z, revision);
            var scratch = _fogScratch ??= new uint[WaterFogVolume.BlockCells];

            if (loaded)
                FogFromBlock(slot!.Block, scratch);
            else
                FogFromBathymetry(coord, scratch);

            scratch.AsSpan().CopyTo(cells);
            fog.BlockPeakMs = MathF.Max(fog.BlockPeakMs, (float)Stopwatch.GetElapsedTime(blockStart).TotalMilliseconds);
        }

        var ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        fog.Fills += fog.UploadsLastFrame;

        // Frames that filled something, once the code is warm (the first calls are the JIT's).
        if (fog.UploadsLastFrame > 0 && fog.Fills > 24)
        {
            fog.FillMs.Record(ms);

            if (ms > fog.FillPeakMs)
                (fog.FillPeakMs, fog.FillPeakUploads) = (ms, fog.UploadsLastFrame);
        }
    }

    /// <summary>
    /// A block's cells as fog: solid stops the march (alpha 255), water carries its own colour and density, air is nothing.
    /// A part-full cell is the water's full density: every march starts or ends at the surface the mesh draws, so it never
    /// crosses the empty part, and scaling by the fill made the top cell of water under a metre deep (the cell under it the
    /// bed) thinner fog than the same water a metre deeper, a sawtooth along the voxel columns of the 1 m depth line.
    /// </summary>
    private static void FogFromBlock(WaterBlock block, Span<uint> cells)
    {
        const byte solidBit = (byte)WaterFlags.Solid;
        var water = block.Water;
        var flags = block.Flags;
        var colour = block.Colour;
        var o = 0;

        for (var z = 0; z < ChunkConstants.Size; z++)
            for (var y = 0; y < ChunkConstants.Size; y++)
            {
                var row = ChunkConstants.Index(0, y, z);

                for (var i = row; i < row + ChunkConstants.Size; i++)
                {
                    var w = water[i];

                    if ((flags[i] & solidBit) != 0)
                        cells[o++] = 0xFF000000u;
                    else if (w < WaterSurfaceMesher.WetThreshold)
                        cells[o++] = WaterColour.None;
                    else
                    {
                        var c = colour[i] == WaterColour.None ? WaterColour.Sea : colour[i];
                        var a = Math.Min(WaterColour.A(c), (byte)254);
                        cells[o++] = (c & 0x00FFFFFFu) | (uint)a << 24;
                    }
                }
            }
    }

    /// <summary>The window's block at a slot index: x fastest, then y, then z.</summary>
    private static ChunkCoord FogBlock((int X, int Y, int Z) min, int index) =>
        new(min.X + index % WaterFogVolume.BlocksX, min.Y + index / WaterFogVolume.BlocksX % WaterFogVolume.BlocksY, min.Z + index / (WaterFogVolume.BlocksX * WaterFogVolume.BlocksY));

    /// <summary>The grid cells a fog block reads: its columns' bilinear cells and the sea mask's reach around them, FogCells a side from (x, z).</summary>
    private (int X, int Z) FogFootprint(ChunkCoord coord)
    {
        var origin = coord.WorldOrigin;
        return ((int)MathF.Floor((origin.X - _bathymetry.Origin.X) / BathymetryGrid.CellSize - 0.5f) - SeaReach + 1,
            (int)MathF.Floor((origin.Z - _bathymetry.Origin.Y) / BathymetryGrid.CellSize - 0.5f) - SeaReach + 1);
    }

    /// <summary>A hash of the ground and sea state of the grid cells under a fog block, with the sign bit set so it never reads as a sim block's revision.</summary>
    private int BathymetryKey(ChunkCoord coord)
    {
        var grid = _bathymetry;
        var (bx, bz) = FogFootprint(coord);
        var tops = grid.Tops;
        var beds = grid.Beds;
        var sea = grid.Sea;
        var h = 2166136261u;

        for (var j = 0; j < FogCells; j++)
        {
            var gz = Math.Clamp(bz + j, 0, BathymetryGrid.Resolution - 1) * BathymetryGrid.Resolution;

            for (var i = 0; i < FogCells; i++)
            {
                // The ground the fill reads (the bed under the sea, the top on the beach: a beach refined by streaming moves
                // its waterline) and the sea state.
                var g = Math.Clamp(bx + i, 0, BathymetryGrid.Resolution - 1) + gz;
                h = (h ^ (uint)BitConverter.SingleToInt32Bits(BathymetryGrid.Ground(tops[g], beds[g], sea[g]))) * 16777619u;
                h = (h ^ (byte)sea[g]) * 16777619u;
            }
        }

        return (int)(h | 0x80000000u);
    }

    /// <summary>
    /// A block no sim block covers, from the bathymetry: per voxel column the ground (bilinear over the 2 m cells, so a
    /// beach slopes instead of stepping), solid under it, sea water from there up to the sea-level layer where the column
    /// is sea (an unknown bed is deep sea), the cell the ground crosses full sea water like the rest (the sim's part-full
    /// cells are full density too: see FogFromBlock); air elsewhere. A roof over a beach counts as ground down to the sand;
    /// the sim's blocks, which know the voxels, cover anywhere a march matters.
    /// </summary>
    private void FogFromBathymetry(ChunkCoord coord, Span<uint> cells)
    {
        var grid = _bathymetry;
        var origin = coord.WorldOrigin;
        var sea = grid.Sea;
        var seaTop = Bodies.SeaTop;
        var raw = _fogSeaRaw ??= new bool[FogCells * FogCells];
        var dilated = _fogSeaDilated ??= new bool[FogCells * FogCells];
        var (bx, bz) = FogFootprint(coord);

        // A column is sea when any cell within SeaReach of it is: the mask marks a cell sea only where its y = -1 voxel holds
        // water, so the last half metre of the beach's slope under sea level, and the run-up over it, several metres on a
        // gentle beach, read as dry and stepped along the cells. The ground still decides where the water is. Dilated in
        // two passes over the block's footprint of cells.
        for (var j = 0; j < FogCells; j++)
        {
            var gz = Math.Clamp(bz + j, 0, BathymetryGrid.Resolution - 1) * BathymetryGrid.Resolution;

            for (var i = 0; i < FogCells; i++)
                raw[i + j * FogCells] = sea[Math.Clamp(bx + i, 0, BathymetryGrid.Resolution - 1) + gz] == BathymetryGrid.SeaState.Sea;
        }

        for (var j = 0; j < FogCells; j++)
            for (var i = 0; i < FogCells; i++)
            {
                var any = false;
                for (var k = Math.Max(i - SeaReach + 1, 0); k <= Math.Min(i + SeaReach, FogCells - 1) && !any; k++)
                    any = raw[k + j * FogCells];

                dilated[i + j * FogCells] = any;
            }

        for (var i = 0; i < FogCells; i++)
            for (var j = 0; j < FogCells; j++)
            {
                var any = false;
                for (var k = Math.Max(j - SeaReach + 1, 0); k <= Math.Min(j + SeaReach, FogCells - 1) && !any; k++)
                    any = dilated[i + k * FogCells];

                raw[i + j * FogCells] = any;
            }

        // The ground at the footprint's cell centres once (SampleGround at a centre is that cell's ground), then bilinear
        // per column by hand: 289 grid reads for a block instead of 4 per column.
        var corners = _fogGround ??= new float[FogGroundSide * FogGroundSide];
        var cx0 = bx + SeaReach - 1;
        var cz0 = bz + SeaReach - 1;

        for (var j = 0; j < FogGroundSide; j++)
            for (var i = 0; i < FogGroundSide; i++)
                corners[i + j * FogGroundSide] = grid.SampleGround(grid.Origin.X + (cx0 + i + 0.5f) * BathymetryGrid.CellSize, grid.Origin.Y + (cz0 + j + 0.5f) * BathymetryGrid.CellSize);

        // Per column: the last solid cell (its centre at or under the ground) and the top water cell (none off the sea);
        // the cells are then two integer compares each.
        var solidTop = _fogSolidTop ??= new int[ChunkConstants.Size * ChunkConstants.Size];
        var waterTop = _fogWaterTop ??= new int[ChunkConstants.Size * ChunkConstants.Size];
        var seaTopCell = seaTop + 1;

        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            var fz = (origin.Z + z - grid.Origin.Y) / BathymetryGrid.CellSize - 0.5f - cz0;
            var iz = (int)MathF.Floor(fz);
            var tz = fz - iz;
            var rowSea = (int)MathF.Floor((origin.Z + z - grid.Origin.Y) / BathymetryGrid.CellSize - 0.5f) - bz;

            for (var x = 0; x < ChunkConstants.Size; x++)
            {
                var fx = (origin.X + x - grid.Origin.X) / BathymetryGrid.CellSize - 0.5f - cx0;
                var ix = (int)MathF.Floor(fx);
                var tx = fx - ix;
                var g = ix + iz * FogGroundSide;
                var ground = float.Lerp(float.Lerp(corners[g], corners[g + 1], tx), float.Lerp(corners[g + FogGroundSide], corners[g + FogGroundSide + 1], tx), tz);
                var isSea = raw[(int)MathF.Floor((origin.X + x - grid.Origin.X) / BathymetryGrid.CellSize - 0.5f) - bx + rowSea * FogCells];
                var c = x + z * ChunkConstants.Size;
                var solid = (int)MathF.Floor(ground - 0.5f);
                solidTop[c] = solid;
                waterTop[c] = isSea ? seaTopCell : int.MinValue;
            }
        }

        var o = 0;
        for (var z = 0; z < ChunkConstants.Size; z++)
        {
            var row = z * ChunkConstants.Size;

            for (var y = 0; y < ChunkConstants.Size; y++)
            {
                var worldY = (int)origin.Y + y;

                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    var c = row + x;
                    cells[o++] = worldY <= solidTop[c] ? 0xFF000000u : worldY <= waterTop[c] ? WaterColour.Sea : WaterColour.None;
                }
            }
        }
    }

    private float[]? _fogGround;
    private int[]? _fogSolidTop;
    private int[]? _fogWaterTop;
    private uint[]? _fogScratch;

    /// <summary>Ground samples a side per block: its 16 cells and one more for the bilinear.</summary>
    private const int FogGroundSide = ChunkConstants.Size / 2 + 2;
    private bool[]? _fogSeaRaw;
    private bool[]? _fogSeaDilated;
    private readonly int[] _fogKeys = new int[WaterFogVolume.Slots];
    private int _fogKeyVersion = -1;
    private (int X, int Y, int Z) _fogKeyWindow;

    /// <summary>Bathymetry cells (2 m) around a fog column within which a sea cell makes it sea.</summary>
    private const int SeaReach = 3;

    /// <summary>Grid cells a side a fog block's footprint covers: its 16 (plus one for the bilinear) and the reach on both sides.</summary>
    private const int FogCells = ChunkConstants.Size / 2 + 1 + 2 * SeaReach;

    /// <summary>Main-thread time (ms) a frame's fog fill may spend before the rest waits for the next frame: a hitch is worse than a block of fog a few frames late.</summary>
    private const float FogFillBudgetMs = 0.5f;

    /// <summary>
    /// The voxel water at world point p, for the camera: true inside a loaded block's water, with the surface over it (the
    /// top of the wet column). The open sea is not voxel water here; the caller tests the swell.
    /// </summary>
    public bool WaterAt(Vector3 p, out float surface)
    {
        surface = float.NaN;
        var cell = new Vector3(MathF.Round(p.X), MathF.Round(p.Y), MathF.Round(p.Z));
        var coord = ChunkCoord.FromWorld(cell);

        if (!_blocks.TryGetValue(coord, out var slot) || slot.Pending)
            return false;

        var block = slot.Block;
        var o = coord.WorldOrigin;
        var (x, y, z) = ((int)(cell.X - o.X), (int)(cell.Y - o.Y), (int)(cell.Z - o.Z));
        var i = ChunkConstants.Index(x, y, z);
        var w = block.Water[i];

        if (w < WaterSurfaceMesher.WetThreshold || p.Y > cell.Y - 0.5f + MathF.Min(w, 1f))
            return false;

        surface = cell.Y - 0.5f + MathF.Min(w, 1f);

        for (var k = y + 1; k < ChunkConstants.Size; k++)
        {
            var above = block.Water[ChunkConstants.Index(x, k, z)];
            if (above < WaterSurfaceMesher.WetThreshold)
                break;

            surface = o.Y + k - 0.5f + MathF.Min(above, 1f);
        }

        return true;
    }

    private WaterBlock? Neighbour(ChunkCoord coord, int face)
    {
        var (dx, dy, dz) = FaceOffsets[face];
        return _blocks.TryGetValue(new ChunkCoord(coord.X + dx, coord.Y + dy, coord.Z + dz), out var slot) && !slot.Pending
            ? slot.Block
            : null;
    }

    private static Face Opposite(Face face) => (Face)((int)face ^ 1);

    private const byte Held = (byte)(WaterFlags.Solid | WaterFlags.Reservoir);

    private static bool FaceHeld(WaterBlock block, Face face)
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

                if ((flags[index] & Held) == 0)
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
