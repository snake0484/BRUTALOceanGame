using System.Numerics;

namespace BrutalGame.Terrain.Water;

/// <summary>A flow the generator made: its path, and the index in <see cref="TerrainGenerator.Bodies"/> of the body it runs out of (−1 for none).</summary>
public readonly record struct WaterFlowInfo(RiverPath Path, int SourceIndex)
{
    /// <summary>Set by the sim when it registers the flow.</summary>
    public ushort Id { get; init; }
}

/// <summary>
/// Flows of running water (docs/voxel-water.md "Flows"): rivers, each with an id, the body it runs out of, and its
/// reaches, one per chunk column its centreline crosses, in order downstream. A block's columns know the flow whose water
/// they hold (<see cref="WaterBlock.FlowAt"/>), so the flow is identified wherever it is loaded. A reach the 3D sim runs
/// is <see cref="Reach.Active"/>: its water is its blocks'. Every other reach is routed: its water is a storage that
/// drains downstream at the uniform flow its depth carries, so the river keeps flowing where it is not simulated or not
/// loaded at all. Where an active reach meets a routed one, the sim's blocks take the routed reach's water in, or give
/// theirs to it, through their faces, and that is booked here (<see cref="Book"/>), so the water is never lost or made.
/// </summary>
public sealed class WaterFlows
{
    public const ushort None = 0;

    /// <summary>A routed reach's stage over its design storage is held to this, for the head it gives a sim block's edge.</summary>
    public const float MaxStage = 2f;

    /// <summary>A routed reach passes its design discharge × stage to this power: Manning's, where flow goes as depth^(5/3).</summary>
    private const float RouteExponent = 5f / 3f;

    public sealed class Reach
    {
        /// <summary>The chunk column its centreline runs through.</summary>
        public int X;
        public int Z;

        /// <summary>Its centreline's length (m).</summary>
        public float Length;

        /// <summary>
        /// The water (m³) it holds carrying its design discharge: the river's generated water in its blocks once they have
        /// been loaded (<see cref="Calibrate"/>), till then the channel's width × the generated depth along its centreline.
        /// </summary>
        public float Design;
        public bool Calibrated;

        /// <summary>The water it holds while routed (m³); while active, what its blocks held when it woke.</summary>
        public float Storage;

        /// <summary>What it routed downstream in the last <see cref="Route"/> (m³/s). Diagnostics.</summary>
        public float Outflow;

        /// <summary>The 3D sim runs it: its blocks are loaded and awake, near the camera.</summary>
        public bool Active;

        /// <summary>Every block its water reaches into: its centreline's column and those its banks spill over into.</summary>
        public ChunkCoord[] Blocks = [];

        /// <summary>Its storage over its design storage, 0 to <see cref="MaxStage"/>.</summary>
        public float Stage => Design > 0f ? Math.Clamp(Storage / Design, 0f, MaxStage) : 0f;
    }

    public sealed class Flow(ushort id, RiverPath path, ushort source, Reach[] reaches)
    {
        public readonly ushort Id = id;
        public readonly RiverPath Path = path;

        /// <summary>The body it runs out of over its sill, <see cref="WaterBodies.None"/> for none.</summary>
        public readonly ushort Source = source;
        public readonly Reach[] Reaches = reaches;

        /// <summary>The flow over the sill in the last <see cref="Route"/> while its first reach was routed (m³/s).</summary>
        public float Weir;

        /// <summary>
        /// Diagnostics (m³): taken from the source over the sill while routed, routed into the sea, given to routed reaches
        /// by the sim's blocks through their edges (taken: negative), the difference between routed storage and the blocks'
        /// water when reaches woke, and what calibrating the reaches' design storage scaled their storage by.
        /// </summary>
        public double Drawn;
        public double Delivered;
        public double Exchanged;
        public double Reconciled;
        public double Calibration;
    }

    private readonly List<Flow> _flows = new(2);
    private readonly Dictionary<long, (int Flow, int Reach)> _columns = new(64);

    public int Count => _flows.Count;

    public Flow this[ushort id] => _flows[id - 1];

    public IReadOnlyList<Flow> All => _flows;

    /// <summary>
    /// Registers a river and splits it into reaches: one per chunk column its centreline enters above sea level, outside
    /// <paramref name="sourceDisc"/> (world x, z, radius: the source's fill, whose water is the body's). Columns its water
    /// spills into beside the centreline's belong to the reach whose station first reaches them.
    /// </summary>
    public ushort Add(RiverPath path, ushort source, Vector3 sourceDisc, float seaLevel)
    {
        var id = (ushort)(_flows.Count + 1);
        var reaches = new List<Reach>();
        var low = new List<float>();
        var high = new List<float>();
        var columns = new Dictionary<long, int>();
        var within = path.HalfWidth + RiverPath.WaterMargin;
        var first = -1;
        var last = -1;

        for (var k = 0; k < path.Count; k++)
        {
            var point = path.Points[k];

            if (path.Surfaces[k] <= seaLevel)
                break;

            if (Vector2.DistanceSquared(point, new Vector2(sourceDisc.X, sourceDisc.Y)) <= sourceDisc.Z * sourceDisc.Z)
                continue;

            var (cx, cz) = Column(point.X, point.Y);

            if (!columns.TryGetValue(Key(cx, cz), out var r))
            {
                r = reaches.Count;
                columns.Add(Key(cx, cz), r);
                reaches.Add(new Reach { X = cx, Z = cz });
                low.Add(float.MaxValue);
                high.Add(float.MinValue);
            }

            var reach = reaches[r];
            reach.Length += 1f;
            reach.Design += path.Width * path.Depth(k);
            low[r] = MathF.Min(low[r], path.Beds[k]);
            high[r] = MathF.Max(high[r], path.Surfaces[k]);
            first = first < 0 ? k : first;
            last = k;
        }

        // Beside the centreline: the columns the water's width reaches into, the first reach to reach one owning it.
        for (var k = Math.Max(first, 0); k <= last; k++)
        {
            var point = path.Points[k];
            var (ox, oz) = Column(point.X, point.Y);
            var owner = columns.GetValueOrDefault(Key(ox, oz), -1);

            if (owner < 0)
                continue;

            for (var c = 0; c < 4; c++)
            {
                var (cx, cz) = Column(point.X + ((c & 1) == 0 ? -within : within), point.Y + ((c & 2) == 0 ? -within : within));
                columns.TryAdd(Key(cx, cz), owner);
            }
        }

        foreach (var reach in reaches)
            reach.Storage = reach.Design;

        var blocks = new List<ChunkCoord>[reaches.Count];

        foreach (var (key, r) in columns)
        {
            var (cx, cz) = ((int)(key >> 32), (int)key);
            var y0 = Math.Max(ChunkConstants.MinChunkY, ChunkCoord.FromWorld(new Vector3(0f, low[r] - 1f, 0f)).Y);
            var y1 = Math.Min(ChunkConstants.MaxChunkY, ChunkCoord.FromWorld(new Vector3(0f, high[r] + 1f, 0f)).Y);
            blocks[r] ??= new List<ChunkCoord>(4);

            for (var y = y0; y <= y1; y++)
                blocks[r].Add(new ChunkCoord(cx, y, cz));

            _columns.TryAdd(key, (_flows.Count, r));
        }

        for (var r = 0; r < reaches.Count; r++)
            reaches[r].Blocks = [.. blocks[r]];

        _flows.Add(new Flow(id, path, source, [.. reaches]));
        return id;
    }

    /// <summary>The flow and reach whose water chunk column (x, z) holds, if any.</summary>
    public bool ReachAt(int x, int z, out Flow flow, out int reach)
    {
        if (_columns.TryGetValue(Key(x, z), out var at))
        {
            flow = _flows[at.Flow];
            reach = at.Reach;
            return true;
        }

        flow = null!;
        reach = -1;
        return false;
    }

    /// <summary>
    /// Once a tick: the routed reaches drain downstream. While the first reach is routed its source's level over the sill
    /// passes a weir's flow into it, paid by the source; while it is active the sim's held cells feed it themselves. Each
    /// routed reach passes on its design discharge × its stage^(5/3) (uniform flow, whose discharge goes as depth^(5/3)),
    /// so at its design storage it carries the design discharge, to the next routed reach or, past the last, into the sea.
    /// A routed reach above an active one passes nothing here: the active blocks draw its water in through their edge at
    /// the head between them. (Manning on each reach's mean slope instead settled its storage far from the water its
    /// blocks are generated with: a reach clipping a waterfall held a third of it, and waking reaches gave back 304 m³.)
    /// </summary>
    public void Route(float dt, WaterBodies bodies)
    {
        foreach (var flow in _flows)
        {
            var reaches = flow.Reaches;
            flow.Weir = 0f;

            if (reaches.Length == 0)
                continue;

            if (!reaches[0].Active && flow.Source != WaterBodies.None && bodies[flow.Source].Alive)
            {
                flow.Weir = RiverPath.WeirFlow(bodies.Levels[flow.Source] - flow.Path.Sill, flow.Path.Width);
                bodies.Exchange(flow.Source, -flow.Weir * dt);
                reaches[0].Storage += flow.Weir * dt;
                flow.Drawn += flow.Weir * dt;
            }

            // Downstream first, so water moves at most a reach a tick.
            for (var i = reaches.Length - 1; i >= 0; i--)
            {
                var reach = reaches[i];
                reach.Outflow = 0f;
                var next = i + 1 < reaches.Length ? reaches[i + 1] : null;

                if (reach.Active || next is { Active: true })
                    continue;

                var stage = MathF.Max(reach.Storage, 0f) / reach.Design;
                var q = MathF.Min(flow.Path.Discharge * MathF.Pow(stage, RouteExponent), MathF.Max(reach.Storage, 0f) / dt);
                reach.Storage -= q * dt;
                reach.Outflow = q;

                if (next is null)
                    flow.Delivered += q * dt;
                else
                    next.Storage += q * dt;
            }
        }
    }

    /// <summary>
    /// The sim takes a reach over. Its blocks show the water they were loaded or frozen with (<paramref name="inBlocks"/>,
    /// m³); what its routed storage held more (or less) goes on to the next reach if that is routed, or the sea.
    /// </summary>
    public static void Activate(Flow flow, int reach, float inBlocks)
    {
        var r = flow.Reaches[reach];
        var excess = r.Storage - inBlocks;
        flow.Reconciled += excess;

        if (reach + 1 < flow.Reaches.Length && !flow.Reaches[reach + 1].Active)
            flow.Reaches[reach + 1].Storage += excess;
        else
            flow.Delivered += excess;

        r.Storage = inBlocks;
        r.Active = true;
    }

    /// <summary>The sim lets a reach go: its blocks' water (<paramref name="inBlocks"/>, m³) becomes its routed storage.</summary>
    public static void Freeze(Flow flow, int reach, float inBlocks)
    {
        var r = flow.Reaches[reach];
        r.Storage = inBlocks;
        r.Active = false;
    }

    /// <summary>Water an active block gave a routed reach through its edge (positive) or took from it (negative), m³.</summary>
    public static void Book(Flow flow, int reach, float amount)
    {
        flow.Reaches[reach].Storage += amount;
        flow.Exchanged += amount;
    }

    /// <summary>
    /// A routed reach's blocks are loaded fresh from the generator for the first time: the water they hold
    /// (<paramref name="picture"/>, m³) is its design storage from now on, and its storage is scaled with it, so the
    /// stage it stands at is kept and a reach the sim wakes at its design flow finds its blocks holding what it held.
    /// </summary>
    public static void Calibrate(Flow flow, int reach, float picture)
    {
        var r = flow.Reaches[reach];
        r.Calibrated = true;

        if (picture <= 0f || r.Design <= 0f)
            return;

        var scaled = r.Storage * picture / r.Design;
        flow.Calibration += scaled - r.Storage;
        r.Storage = scaled;
        r.Design = picture;
    }

    /// <summary>Diagnostics (the terrain panel): one line per flow. Allocates.</summary>
    public string Describe(WaterBodies bodies)
    {
        var text = new System.Text.StringBuilder();

        foreach (var flow in _flows)
        {
            var (active, storage, design) = (0, 0f, 0f);

            foreach (var reach in flow.Reaches)
            {
                active += reach.Active ? 1 : 0;
                storage += reach.Storage;
                design += reach.Design;
            }

            var level = flow.Source != WaterBodies.None ? bodies[flow.Source].Level : float.NaN;
            var mouth = flow.Reaches.Length > 0 ? flow.Reaches[^1].Outflow : 0f;
            text.AppendLine($"flow #{flow.Id} from body #{flow.Source} (level {level:F2}, sill {flow.Path.Sill:F2}): {flow.Reaches.Length} reaches, {active} simulated; weir {flow.Weir:F2} m3/s, mouth {mouth:F2} m3/s; storage {storage:F0} of {design:F0} m3; drawn {flow.Drawn:F0}, delivered {flow.Delivered:F0}, edges {flow.Exchanged:F1}, reconciled {flow.Reconciled:F1}, calibration {flow.Calibration:F1} m3");
        }

        return text.ToString();
    }

    public static (int X, int Z) Column(float x, float z) =>
        ((int)MathF.Floor(x / ChunkConstants.Size), (int)MathF.Floor(z / ChunkConstants.Size));

    private static long Key(int x, int z) => (long)x << 32 | (uint)z;
}
