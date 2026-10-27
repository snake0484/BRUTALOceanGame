using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
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

    /// <summary>The generator's bodies, with their ids, and every chunk their water reaches into.</summary>
    public readonly WaterBodyInfo[] Generated;
    private readonly ChunkCoord[] _generatedBlocks;

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
    public int Discs => _discs.Count;
    public int Outlets { get; private set; }

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

    /// <summary>The slowest zone tick since the last <see cref="TakePeakZoneMs"/> (growth and remerge ticks resize the sim).</summary>
    public float TakePeakZoneMs()
    {
        var peak = _peakZoneMs;
        _peakZoneMs = 0f;
        return peak;
    }

    private float _peakZoneMs;

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
        Bodies = new WaterBodies(terrain.Params.SeaLevel);
        Generated = TerrainGenerator.Bodies(terrain.Params);

        for (var i = 0; i < Generated.Length; i++)
        {
            var info = Generated[i];
            Generated[i] = info with { Id = Bodies.Add(info.Level, info.Area, info.Centre, info.Radius, info.Bottom) };
        }

        _generatedBlocks = GeneratedBlocks(Generated);
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

    /// <summary>Diagnostics: the voxel water (m³) in the 1 m column at (x, z) between heights y0 and y1, over loaded blocks.</summary>
    public float ColumnWater(float x, float z, float y0, float y1)
    {
        // A voxel's sample point is its integer world position.
        var (cx, cz) = (MathF.Round(x), MathF.Round(z));
        var sum = 0f;

        for (var y = MathF.Ceiling(y0); y <= y1; y += 1f)
        {
            var coord = ChunkCoord.FromWorld(new Vector3(cx, y, cz));
            if (!_blocks.TryGetValue(coord, out var slot))
                continue;

            var o = coord.WorldOrigin;
            sum += slot.Block.Water[ChunkConstants.Index((int)(cx - o.X), (int)(y - o.Y), (int)(cz - o.Z))];
        }

        return sum;
    }

    /// <summary>
    /// Where voxel water slammed into a wall or landed from a pour this tick, after the sea zones' own: the ocean renderer
    /// throws spray there (ocean_spray_splash.comp), so a waterfall's foot and a stream's wall splash as the sea's do.
    /// </summary>
    public ReadOnlySpan<WaterSplash> Splashes => _splashes.AsSpan(0, _splashCount);

    private const int MaxSplashes = 512;
    private readonly WaterSplash[] _splashes = new WaterSplash[MaxSplashes];
    private int _splashCount;

    /// <summary>
    /// A pour lands this fast (m/s, a fall of about 2 m) or faster, with at least this much water a second over its
    /// metre, to splash: at 3–5 m/s every stair of a run-off sheet counted as a pour (a falling cell fed sideways builds
    /// its fall speed past free fall) and the whole slope stood in mist. Water runs into a wall this fast or faster.
    /// </summary>
    public float PourSplashSpeed = 6.5f;
    public float PourSplashDischarge = 0.5f;
    public float WallSplashSpeed = 0.4f;

    /// <summary>
    /// After a step (pipe model): the zones' splashes first, then every wet, held-up cell of an awake block that water
    /// falls into from above at <see cref="PourSplashSpeed"/> or faster (a pour, impact speed the fall's, discharge the
    /// fall rate over the cell's metre), and every one whose water arrives at <see cref="WallSplashSpeed"/> or faster
    /// against a solid side that stands a cell over the surface (a wall it can only climb). Capped at <see cref="MaxSplashes"/>.
    /// </summary>
    private void FindSplashes()
    {
        _splashCount = 0;

        foreach (var splash in Zones.Splashes)
            if (_splashCount < MaxSplashes)
                _splashes[_splashCount++] = splash;

        if (!Pipes)
            return;

        const int P = ChunkConstants.Padded;
        const int A = ChunkConstants.PaddedArea;
        const byte solid = (byte)WaterFlags.Solid;

        foreach (var slot in _awake)
        {
            var block = slot.Block;
            var origin = block.Coord.WorldOrigin;
            var w = block.Water;
            var fl = block.Flags;
            var v = block.FallSpeed!;

            for (var z = 0; z < ChunkConstants.Size && _splashCount < MaxSplashes; z++)
            {
                for (var y = 0; y < ChunkConstants.Size; y++)
                {
                    if (block.WetRows[(y + 1) + (z + 1) * P] == 0)
                        continue;

                    var row = ChunkConstants.Index(0, y, z);

                    for (var x = 0; x < ChunkConstants.Size; x++)
                    {
                        var i = row + x;
                        var wc = w[i];

                        if (wc < 0.15f || (fl[i] & solid) != 0 || ((fl[i - P] & solid) == 0 && w[i - P] < WaterFlow.SupportThreshold))
                            continue;

                        var surface = origin.Y + y - 0.5f + MathF.Min(wc, 1f);
                        var fall = -v[i + P];

                        if (fall >= PourSplashSpeed && w[i + P] > 0.02f && (fl[i + P] & solid) == 0 && fall * MathF.Min(w[i + P], 1f) >= PourSplashDischarge)
                        {
                            var drift = new Vector2(block.DriftX![i + P], block.DriftZ![i + P]);
                            var along = drift.LengthSquared() > 1e-4f ? Vector2.Normalize(drift) : Vector2.UnitX;
                            Add(new Vector4(origin.X + x, surface, origin.Z + z, fall), new Vector4(along, fall * MathF.Min(w[i + P], 1f), WaterSplash.Pour));
                        }

                        // Into a wall on each side: the flux arriving from the far side over the cell's water.
                        Wall(block, i, 1, P, x + 0.5f, 0f, origin, x, z, surface, wc);
                        Wall(block, i, -1, P, x - 0.5f, 0f, origin, x, z, surface, wc);
                        Wall(block, i, A, P, 0f, z + 0.5f, origin, x, z, surface, wc);
                        Wall(block, i, -A, P, 0f, z - 0.5f, origin, x, z, surface, wc);
                    }
                }
            }
        }
    }

    private void Wall(WaterBlock block, int i, int side, int up, float footX, float footZ, Vector3 origin, int x, int z, float surface, float wc)
    {
        const byte solid = (byte)WaterFlags.Solid;
        var n = i + side;

        if ((block.Flags[n] & solid) == 0 || (block.Flags[n + up] & solid) == 0)
            return;

        // The face on the far side carries what arrives; positive along +side.
        var flux = side == 1 ? block.FluxX![i - 1] : side == -1 ? -block.FluxX![i] : side > 0 ? block.FluxZ![i - ChunkConstants.PaddedArea] : -block.FluxZ![i];
        var into = flux / MathF.Max(MathF.Min(wc, 1f), 0.05f);

        if (into < WallSplashSpeed)
            return;

        var dir = side == 1 ? Vector2.UnitX : side == -1 ? -Vector2.UnitX : side > 0 ? Vector2.UnitY : -Vector2.UnitY;
        var foot = new Vector2(origin.X + (side == 1 || side == -1 ? footX : x), origin.Z + (side == 1 || side == -1 ? z : footZ));
        Add(new Vector4(foot.X, surface, foot.Y, into), new Vector4(dir, MathF.Min(wc, 1f), WaterSplash.Wall));
    }

    private void Add(Vector4 positionSpeed, Vector4 direction)
    {
        if (_splashCount < MaxSplashes)
            _splashes[_splashCount++] = new WaterSplash { PositionSpeed = positionSpeed, Direction = direction };
    }

    /// <summary>Diagnostics: free (not held) water over loaded blocks (m³), the largest fill of any free cell, and the count of free wet cells.</summary>
    public (float Free, float MaxFill, int Cells) FreeWater()
    {
        var free = 0.0;
        var max = 0f;
        var cells = 0;

        foreach (var (_, slot) in _blocks)
        {
            if (slot.Pending)
                continue;

            var block = slot.Block;

            for (var z = 0; z < ChunkConstants.Size; z++)
                for (var y = 0; y < ChunkConstants.Size; y++)
                    for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(ChunkConstants.Size, y, z); i++)
                    {
                        var w = block.Water[i];

                        if (w <= 0f || (block.Flags[i] & Held) != 0)
                            continue;

                        free += w;
                        max = MathF.Max(max, w);
                        cells++;
                    }
        }

        return ((float)free, max, cells);
    }

    /// <summary>Diagnostics: activations and refreshes waiting to be dispatched, blocks being (re)activated, blocks being meshed.</summary>
    public (int Queued, int Pending, int Meshing) Activity
    {
        get
        {
            var pending = 0;
            foreach (var (_, slot) in _blocks)
                pending += slot.Pending ? 1 : 0;

            return (_activationRequests.Count, pending, _meshing.Count);
        }
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

    /// <summary>
    /// Fills every open cell inside a sphere up to <paramref name="top"/> (world y) with water: a pond, a flooded room. It
    /// lands at rest: the cell the surface crosses part full, those under it at the pressure of the water above (as a
    /// body's held cells). Filled whole up to a layer's top, the surface sat on full cells, where the head grows 50 times
    /// as steep, and a pond chattered there and never slept. The blocks it touches are created if needed; the water lands
    /// in each at the start of the first step it is ready in (never while a mesh job reads it). Not for per-frame use.
    /// </summary>
    public void Fill(Vector3 centre, float radius, float top)
    {
        var fill = new PendingFill(centre, radius, top);
        var min = ChunkCoord.FromWorld(centre - new Vector3(radius));
        var max = ChunkCoord.FromWorld(centre + new Vector3(radius));

        for (var z = min.Z; z <= max.Z; z++)
            for (var y = min.Y; y <= max.Y; y++)
                for (var x = min.X; x <= max.X; x++)
                {
                    var coord = new ChunkCoord(x, y, z);
                    Request(coord, refresh: false);
                    fill.Waiting.Add(coord);
                }

        _fills.Add(fill);
    }

    private sealed class PendingFill(Vector3 centre, float radius, float top)
    {
        public readonly Vector3 Centre = centre;
        public readonly float Radius = radius;
        public readonly float Top = top;
        public readonly HashSet<ChunkCoord> Waiting = new();
    }

    private readonly List<PendingFill> _fills = new(4);

    /// <summary>Pours pending fills into the blocks that are ready; a fill is done once every block it touches has had it.</summary>
    private void ApplyFills()
    {
        for (var f = _fills.Count - 1; f >= 0; f--)
        {
            var fill = _fills[f];
            _scratch.Clear();

            foreach (var coord in fill.Waiting)
            {
                if (!_blocks.TryGetValue(coord, out var slot) || slot.Pending || slot.Meshing)
                    continue;

                FillBlock(slot, fill);
                _scratch.Add(coord);
            }

            foreach (var coord in _scratch)
                fill.Waiting.Remove(coord);

            if (fill.Waiting.Count == 0)
                _fills.RemoveAt(f);
        }
    }

    private static void FillBlock(Slot slot, PendingFill fill)
    {
        var block = slot.Block;
        var origin = block.Coord.WorldOrigin;
        var poured = false;

        for (var z = 0; z < ChunkConstants.Size; z++)
            for (var y = 0; y < ChunkConstants.Size; y++)
                for (var x = 0; x < ChunkConstants.Size; x++)
                {
                    var world = origin + new Vector3(x, y, z);
                    var i = ChunkConstants.Index(x, y, z);
                    var over = fill.Top - (world.Y + 0.5f);
                    if (over <= -1f || Vector3.DistanceSquared(world, fill.Centre) > fill.Radius * fill.Radius || (block.Flags[i] & Held) != 0)
                        continue;

                    block.Water[i] = MathF.Max(block.Water[i], over >= 0f ? 1f + WaterFlow.Compression * over : 1f + over);
                    poured = true;
                }

        if (!poured)
            return;

        block.Asleep = false;
        block.QuietSteps = 0;
        slot.MeshDirty = true;
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
        Outlets = 0;

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
                else
                    Outlets++;

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
                });

                continue;
            }

            if (calm)
                continue;

            Grow(d, disc, dt);
            Outlets += disc.IsOutlet ? 1 : 0;
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

                if (!block.Holds(disc.Body) || !Covers(disc.Shape, block.Coord))
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

    /// <summary>Bodies whose ledger holds a level step move; their blocks refill and wake where they must.</summary>
    private void SettleBodies()
    {
        Bodies.SettleLevels();

        foreach (var id in Bodies.Moved)
            MoveBody(id);
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
    /// standing level on its own becomes a body. What the bodies paid and the surface they gained is booked.
    /// </summary>
    private void Settle(Slot slot)
    {
        var block = slot.Block;
        block.JoinBodies();
        var joined = block.Joined.Count;
        block.BornBodies();

        foreach (var (id, paid, surface) in block.Joined)
            Bodies.Took(id, paid, surface);

        if (block.Joined.Count == 0)
            return;

        RefreshFlagHalos(block.Coord);
        slot.MeshDirty = true;

        // A new body: the neighbours' water may stand at its level too, and will join it when they next settle.
        if (block.Joined.Count > joined)
            DirtyNeighbourMeshes(block.Coord);
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

                // A body's level may have moved while it was activating.
                slot.Block.RefillHeld();
                slot.Block.Asleep = slot.Block.AllHeld() || job.AtRest;
                slot.Block.QuietSteps = 0;
                RefreshFlagHalos(job.Block.Coord);
                DirtyNeighbourMeshes(job.Block.Coord);
            }

            job.Block = null;
            _activationPool.Push(job);
        }

        _activationsInFlight.Clear();

        if (_digs.Count > 0)
            TestDigs();

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
            var job = _activationPool.Pop();
            job.Prepare(slot.Block, refresh, _terrain.Params, _terrain.Ops, _zones, _discShapes, Generated, Bodies);
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

    /// <summary>Every chunk the generator's bodies' water reaches into.</summary>
    private static ChunkCoord[] GeneratedBlocks(WaterBodyInfo[] generated)
    {
        var coords = new HashSet<ChunkCoord>();

        foreach (var info in generated)
        {
            var low = ChunkCoord.FromWorld(new Vector3(info.Centre.X - info.Radius, info.Bottom, info.Centre.Y - info.Radius));
            var high = ChunkCoord.FromWorld(new Vector3(info.Centre.X + info.Radius, info.Level, info.Centre.Y + info.Radius));

            for (var z = low.Z; z <= high.Z; z++)
            {
                for (var x = low.X; x <= high.X; x++)
                {
                    var origin = new ChunkCoord(x, 0, z).WorldOrigin;
                    var nx = Math.Clamp(info.Centre.X, origin.X, origin.X + ChunkConstants.Size) - info.Centre.X;
                    var nz = Math.Clamp(info.Centre.Y, origin.Z, origin.Z + ChunkConstants.Size) - info.Centre.Y;

                    if (nx * nx + nz * nz > info.Radius * info.Radius)
                        continue;

                    for (var y = low.Y; y <= high.Y; y++)
                        coords.Add(new ChunkCoord(x, y, z));
                }
            }
        }

        return [.. coords];
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

        // The voxel steps alone: the zone sim below has its own timing (LastZoneMs).
        if (steps > 0)
            LastStepMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds / steps;

        StartMeshing();
        StepZones(dt);
        FindSplashes();
        RainOnBodies((float)dt);

        if (_discs.Count > 0)
            SettleDiscs((float)dt);

        SettleBodies();
    }

    private void Step()
    {
        if (_fills.Count > 0)
            ApplyFills();

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
                WaterHalo.CopyFlags(slot.Block, neighbour, (Face)f);
                WaterHalo.CopyWater(slot.Block, neighbour, (Face)f);

                if (Pipes && f != (int)Face.NegY)
                    WaterHalo.CopyFalling(slot.Block, neighbour, (Face)f);
            }
        }
        RunPass(Pipes ? WaterStepJob.Kind.PipeFluxes : WaterStepJob.Kind.OutflowScale);

        foreach (var slot in _awake)
            for (var f = 0; f < 6; f++)
                WaterHalo.CopyOutScale(slot.Block, Neighbour(slot.Block.Coord, f), (Face)f);
        RunPass(Pipes ? WaterStepJob.Kind.PipeApply : WaterStepJob.Kind.ApplyFlows);

        var mass = 0f;
        var stepTime = 1f / StepsPerSecond;
        foreach (var slot in _awake)
        {
            var block = slot.Block;
            WaterFlow.Swap(block);
            mass += block.Mass();

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

            if (block.QuietSteps >= QuietStepsToSleep || block.CalmSteps >= CalmSeconds * StepsPerSecond)
            {
                block.Asleep = true;
                block.ClearFluxes();
                block.MarkDiscs(RemainingDiscs(block.Coord));
                Settle(slot);
            }

            if (block.BorderActive)
                GrowAcrossFaces(block);
        }

        Mass = mass;

        if (_discs.Count > 0)
            MeasureDiscs(stepTime);

        MarkStepped();
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

            if (block.HasFreeWater() || block.Spills(id))
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
        _bathymetry.AdvanceZoneFades((float)dt, _fadedOut);

        foreach (var disc in _fadedOut)
            EndZone(disc);

        _fadedOut.Clear();

        var pack = Stopwatch.GetTimestamp();
        Zones.PackFlow();
        LastAgitateMs += (float)Stopwatch.GetElapsedTime(pack).TotalMilliseconds;
        Zones.Publish(_bathymetry);
        LastZoneMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        _peakZoneMs = MathF.Max(_peakZoneMs, LastZoneMs);
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
                if (neighbour is null || !neighbour.Asleep)
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
