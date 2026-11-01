using System.Numerics;
namespace BrutalGame.Terrain.Water;

[Flags]
public enum WaterFlags : byte
{
    None = 0,
    Solid = 1,

    /// <summary>Held by a body of water (<see cref="WaterBlock.Body"/>) at the hydrostatic fill under its level; never stepped.</summary>
    Reservoir = 2,

    /// <summary>Simulated sea next to an edit: flows like any water, but is drawn by the ocean renderer (level offset).</summary>
    SeaZone = 4,
}

/// <summary>Block face, ordered so that <c>(Face)((int)f ^ 1)</c> is the opposite face.</summary>
public enum Face
{
    NegX,
    PosX,
    NegY,
    PosY,
    NegZ,
    PosZ,
}

/// <summary>
/// Flow sim state for one LOD-0 chunk (32³ interior + 1-cell halo, <see cref="ChunkConstants.Index"/> layout).
/// See docs/voxel-water.md "Flow simulation v1". Halo cells are written only through <see cref="WaterHalo"/>.
/// </summary>
public sealed class WaterBlock
{
    private const int S = ChunkConstants.Size;
    private const int P = ChunkConstants.Padded;
    private const int A = ChunkConstants.PaddedArea;
    private const byte ReservoirByte = 128;
    private const byte SolidBit = (byte)WaterFlags.Solid;
    private const byte HeldBit = (byte)WaterFlags.Reservoir;
    private const byte ZoneBit = (byte)WaterFlags.SeaZone;

    public ChunkCoord Coord;
    public float[] Water = new float[ChunkConstants.PaddedVolume];
    public float[] Next = new float[ChunkConstants.PaddedVolume];
    public readonly float[] OutScale = new float[ChunkConstants.PaddedVolume];
    public readonly byte[] Flags = new byte[ChunkConstants.PaddedVolume];

    /// <summary>
    /// Per cell, the water's colour and fog density (<see cref="WaterColour"/>, RGBA8): the held cells take their body's,
    /// free water carries its own with it (the pipe model blends inflows by mass). <see cref="WaterColour.None"/> where
    /// there is no water. Halo planes copied each step (<see cref="WaterHalo.CopyColour"/>).
    /// </summary>
    public readonly uint[] Colour = new uint[ChunkConstants.PaddedVolume];

    /// <summary>Counts the changes to the block's water (steps, activation, fills, refills), so a reader (the fog volume) knows when its copy is stale.</summary>
    public int Revision;

    /// <summary>
    /// Per cell, the body of water that holds it (<see cref="WaterBodies"/>), <see cref="WaterBodies.None"/> for free
    /// water, air and solid. Set with the <see cref="WaterFlags.Reservoir"/> bit; halo planes are copied with the flags.
    /// </summary>
    public readonly ushort[] Body = new ushort[ChunkConstants.PaddedVolume];

    /// <summary>The bodies table the block's held cells read their levels from. Set by <see cref="InitFrom"/>; blocks built by hand (tests) share <see cref="WaterBodies.Default"/>.</summary>
    public WaterBodies Bodies = WaterBodies.Default;

    /// <summary>
    /// Per open cell standing on solid: how far the terrain's smooth surface lies above the cell's voxel floor (y − 0.5),
    /// in <see cref="BedUnit"/> steps, −0.5..0.5 m, from the density's zero crossing between the two samples. The voxels
    /// turn a slope into flat terraces and one-cell stairs; water on a bed stands on this surface instead
    /// (<see cref="BedRise"/>). <see cref="NotBed"/> elsewhere. Halo planes are copied with the flags.
    /// </summary>
    public readonly sbyte[] Bed = new sbyte[ChunkConstants.PaddedVolume];

    public const sbyte NotBed = sbyte.MinValue;
    public const float BedUnit = 1f / 254f;

    /// <summary>
    /// Layers over the sea's top layer that a sea zone disc claims (<see cref="Unclamp"/>): the zone sim's surface can stand
    /// that high in a surge or run-up, and the ocean draws it. Higher cells in the disc's columns are the voxels' own, so a
    /// tunnel coming down to the shore shows its water instead of vanishing where the disc begins.
    /// </summary>
    public const int ZoneRunUp = 4;

    /// <summary>
    /// Per padded column (x + 1 + (z + 1)·Padded), the open sea's height over its rest level (m), from the swell
    /// (docs/voxel-water.md "Swell on voxel water"). The sea's held cells outside sea zones stand at it
    /// (<see cref="WaterBodies.SwellLevel"/>), so free water against the sea feels its waves. Set before each step, halo
    /// columns included; all zero while not <see cref="Swelling"/>.
    /// </summary>
    public readonly float[] Swell = new float[ChunkConstants.PaddedArea];
    public bool Swelling;

    /// <summary>The held sea cells stand under a swell, so a flat sea must still be written back once.</summary>
    private bool _swellHeld;

    /// <summary>
    /// Face fluxes of the pipe model (<see cref="WaterPipes"/>), m³/s, positive from the lower-index cell: FluxX[i] is the
    /// face between i and i + 1, FluxY[i] between i and the cell above, FluxZ[i] between i and i + PaddedArea. Null when
    /// the block runs the automaton.
    /// </summary>
    public readonly float[]? FluxX;
    public readonly float[]? FluxY;
    public readonly float[]? FluxZ;

    /// <summary>
    /// Pipe model: per cell, how fast its water falls (m/s, down negative), carried with the water from cell to cell; 0 for
    /// water at rest on a floor. The halo planes beside and above hold the neighbours' (<see cref="WaterHalo.CopyFalling"/>).
    /// </summary>
    public readonly float[]? FallSpeed;

    /// <summary>
    /// Pipe model: per cell, the sideways speed (m/s) of its water while it falls, carried with the water like
    /// <see cref="FallSpeed"/>; 0 for water held up, whose sideways momentum lives on its faces. Water landing on a bed or
    /// a pool hands it to the face it moves toward, so a sheet running over a stair keeps going instead of piling up.
    /// Halos as <see cref="FallSpeed"/>.
    /// </summary>
    public readonly float[]? DriftX;
    public readonly float[]? DriftZ;

    /// <summary>
    /// Pipe model: per cell, how aerated its water is (0..1, white water): set by falling through air, carried with the
    /// water by mass and fading as the bubbles rise and burst (<see cref="WaterPipes.FrothDecay"/>), so a pour's foam rides
    /// downstream from where it lands. Halos as <see cref="Colour"/>; cleared when the block sleeps.
    /// </summary>
    public readonly float[]? Froth;
    public float MaxDelta;

    /// <summary>Largest flux (m³/s) that moved water through a face the block holds in the last pipe step.</summary>
    public float MaxFlux;
    public bool BorderActive;
    public int QuietSteps;

    /// <summary>Steps in a row with fills changing under <see cref="VoxelWaterSystem"/>'s calm delta and no face moving more than its calm flux.</summary>
    public int CalmSteps;
    public bool Asleep;

    /// <summary>
    /// Per body, the water the last step put into (positive) or took out of its held cells here (m³), before they were
    /// held at their fill again; only the ids in <see cref="Touched"/> are set. Summed over a body's blocks, it is what the
    /// body gained or lost (<see cref="WaterBodies.Exchange"/>).
    /// </summary>
    public readonly float[] BodyExchange = new float[WaterBodies.MaxBodies];
    public readonly List<ushort> Touched = new(8);
    private readonly byte[] _touched = new byte[WaterBodies.MaxBodies];

    /// <summary>The bodies with held cells in the block, for refills when their level moves.</summary>
    public readonly List<ushort> Present = new(8);
    private readonly byte[] _present = new byte[WaterBodies.MaxBodies];

    /// <summary>What the last <see cref="JoinBodies"/> or <see cref="BornBodies"/> took in: per body, the water it paid and the surface cells added.</summary>
    public readonly List<(ushort Id, float Paid, int Surface)> Joined = new(4);

    /// <summary>
    /// What the last refresh's digs owe the bodies whose discs they opened room in (<see cref="ReadSolids"/>), booked on
    /// the main thread by <see cref="BookOpened"/>: each cell a dig opened under a body's level in a column one of its discs
    /// frees takes the fill it would be held at there. Free water in disc columns is the body's own (<see
    /// cref="WaterPipes"/> books it as held water), so the water that later flows in from around the disc is a move
    /// inside the body: the body pays for the new room up front, as its level falls when a pit dug in a lake fills.
    /// </summary>
    public readonly List<(ushort Id, float Amount)> Opened = new(2);

    [ThreadStatic]
    private static int[]? _flood;

    [ThreadStatic]
    private static byte[]? _seen;

    /// <summary>
    /// Per interior column (x + z·Size): the body whose held water is free there, inside a disc around a dig into it
    /// (docs/voxel-water.md "Bodies of water"); <see cref="JoinBodies"/> leaves such columns alone for that body.
    /// </summary>
    private readonly ushort[] _discBody = new ushort[S * S];

    /// <summary>The columns' discs before a refresh marks them again: free water a disc newly covers joins its body's books, and leaves them where it no longer does.</summary>
    private readonly ushort[] _discBodyBefore = new ushort[S * S];

    /// <summary>Per interior column (x + z·Size): the body a live disc frees there, 0 for none. The step books its free water to that body (<see cref="WaterPipes"/>).</summary>
    internal ReadOnlySpan<ushort> DiscBodies => _discBody;

    /// <summary>Per interior column: the generated body (its fill disc covers the column) that generated water above the sea belongs to.</summary>
    private readonly ushort[] _generatedBody = new ushort[S * S];

    /// <summary>
    /// Per padded column (x + 1 + (z + 1)·Padded): the flow whose water it holds (<see cref="WaterFlows"/>, docs/voxel-water.md
    /// "Flows"), <see cref="WaterFlows.None"/> for none, and the river's nearest station there. Set by <see cref="InitFrom"/>.
    /// </summary>
    private readonly ushort[] _flowColumn = new ushort[A];
    private readonly int[] _flowStation = new int[A];

    /// <summary>Any column, halo included, holds a flow's water.</summary>
    public bool HasFlow { get; private set; }

    /// <summary>
    /// Held asleep as the picture of a routed reach of a flow (<see cref="WaterFlows"/>): neighbours never wake it, and
    /// an awake neighbour's water crossing into it is booked to that reach. Its fluxes are kept, so it draws the flow it froze with.
    /// </summary>
    public bool Frozen;

    /// <summary>
    /// The chunk's apron beyond the four side faces (NegX, PosX, NegZ, PosZ; a plane each, column along the face, then y),
    /// read with the solids, in flow columns only (solid elsewhere): flags, generated water and bed. An awake block whose
    /// neighbour there is not loaded opens its halo to these (<see cref="OpenFlowEdge"/>), so the river runs on across it.
    /// </summary>
    private readonly byte[] _edgeFlags = new byte[4 * S * S];
    private readonly float[] _edgeWater = new float[4 * S * S];
    private readonly sbyte[] _edgeBed = new sbyte[4 * S * S];

    /// <summary>Per padded row (y, z): any water in the row's 34 cells. Built by pass 1, reused by pass 2.</summary>
    public readonly byte[] WetRows = new byte[ChunkConstants.PaddedArea];
    private byte _openFaces;

    public WaterBlock(bool pipes = false)
    {
        if (pipes)
        {
            FluxX = new float[ChunkConstants.PaddedVolume];
            FluxY = new float[ChunkConstants.PaddedVolume];
            FluxZ = new float[ChunkConstants.PaddedVolume];
            FallSpeed = new float[ChunkConstants.PaddedVolume];
            DriftX = new float[ChunkConstants.PaddedVolume];
            DriftZ = new float[ChunkConstants.PaddedVolume];
            Froth = new float[ChunkConstants.PaddedVolume];
        }

        Reset(default);
    }

    /// <summary>Clears to an empty block whose halo is solid (no neighbours).</summary>
    public void Reset(ChunkCoord coord)
    {
        Coord = coord;
        Water.AsSpan().Clear();
        Next.AsSpan().Clear();
        OutScale.AsSpan().Fill(1f);
        Flags.AsSpan().Fill(SolidBit);
        Body.AsSpan().Clear();
        Colour.AsSpan().Clear();
        Bed.AsSpan().Fill(NotBed);
        WetRows.AsSpan().Clear();
        Swell.AsSpan().Clear();
        Swelling = false;
        _swellHeld = false;
        _discBody.AsSpan().Clear();
        _generatedBody.AsSpan().Clear();
        _flowColumn.AsSpan().Clear();
        _edgeFlags.AsSpan().Fill(SolidBit);
        HasFlow = false;
        Frozen = false;
        ClearFluxes();
        ClearExchange();
        ClearPresent();
        Joined.Clear();

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                Flags.AsSpan(ChunkConstants.Index(0, y, z), S).Clear();

        MaxDelta = 0f;
        BorderActive = false;
        QuietSteps = 0;
        CalmSteps = 0;
        Asleep = false;
        _openFaces = 0;
        Revision++;
    }

    /// <summary>Zeroes the pipe model's fluxes (a block going to sleep carries none).</summary>
    public void ClearFluxes()
    {
        FluxX?.AsSpan().Clear();
        FluxY?.AsSpan().Clear();
        FluxZ?.AsSpan().Clear();
        FallSpeed?.AsSpan().Clear();
        DriftX?.AsSpan().Clear();
        DriftZ?.AsSpan().Clear();
        Froth?.AsSpan().Clear();
        MaxFlux = 0f;
    }

    /// <summary>Before a step's apply pass: no body has exchanged anything yet.</summary>
    public void ClearExchange()
    {
        foreach (var id in Touched)
        {
            BodyExchange[id] = 0f;
            _touched[id] = 0;
        }

        Touched.Clear();
    }

    /// <summary>Books water a held cell of <paramref name="id"/> took in (positive) or gave this step.</summary>
    public void AddExchange(ushort id, float amount)
    {
        if (_touched[id] == 0)
        {
            _touched[id] = 1;
            Touched.Add(id);
        }

        BodyExchange[id] += amount;
    }

    private void ClearPresent()
    {
        foreach (var id in Present)
            _present[id] = 0;

        Present.Clear();
    }

    private void Note(ushort id)
    {
        if (_present[id] != 0)
            return;

        _present[id] = 1;
        Present.Add(id);
    }

    /// <summary>Any held cell of this body in the block.</summary>
    public bool Holds(ushort id) => _present[id] != 0;

    /// <summary>
    /// Chunk generated + ops replayed at LOD 0. Solid where density &lt; 0. Generated water (byte ≥ 128) at or under the
    /// sea's top layer is held by the sea; above it, by the generated body (<paramref name="generated"/>) whose fill disc
    /// covers its column, at the body's level as it now stands; generated water no body claims is free, at the fill the
    /// generator gave it. Inside <paramref name="discs"/> (world x, world z, radius, body id; the discs around digs into
    /// bodies) that body's water is free instead, at the fill it would be held at. Columns the water of one of
    /// <paramref name="flows"/> runs through are marked with it, and in the pipe model its surface cells start moving
    /// downstream at the river's speed, so a block that never steps still draws it flowing.
    /// </summary>
    public void InitFrom(VoxelChunk chunk, WaterBodies? bodies = null, ReadOnlySpan<WaterBodyInfo> generated = default, ReadOnlySpan<Vector4> discs = default, ReadOnlySpan<WaterFlowInfo> flows = default)
    {
        if (chunk.Coord.Lod != 0)
            throw new ArgumentException("Water blocks need a LOD-0 chunk.", nameof(chunk));

        Reset(chunk.Coord);
        Bodies = bodies ?? WaterBodies.Default;
        MarkColumns(generated, discs);
        MarkFlows(flows);
        ReadSolids(chunk, keepWater: false);
        SeedFlow(flows);
        Revision++;
    }

    /// <summary>Marks every padded column within a flow's water (its bed and the banks' foot) with the flow and its nearest station.</summary>
    private void MarkFlows(ReadOnlySpan<WaterFlowInfo> flows)
    {
        var origin = Coord.WorldOrigin;

        foreach (var flow in flows)
        {
            var path = flow.Path;
            var within = path.HalfWidth + RiverPath.WaterMargin;

            if (!path.Touches(origin.X - 1f, origin.Z - 1f, origin.X + S, origin.Z + S))
                continue;

            for (var z = -1; z <= S; z++)
            {
                for (var x = -1; x <= S; x++)
                {
                    var c = x + 1 + (z + 1) * P;

                    if (_flowColumn[c] != WaterFlows.None || !path.Nearest(origin.X + x, origin.Z + z, within, out var station, out _))
                        continue;

                    _flowColumn[c] = flow.Id;
                    _flowStation[c] = station;
                    HasFlow = true;
                }
            }
        }
    }

    /// <summary>
    /// Pipe model: the top wet free cell of each interior flow column moves downstream at the river's generated speed at
    /// its station (its side faces carry speed × its water, as the mesher reads them back), and the column's water carries
    /// the froth the river runs with there (<see cref="RiverPath.Froth"/>), so a frozen reach is drawn running and white
    /// where the sim would churn it. A block that wakes keeps both; the faces it shares with blocks that did not step are
    /// zeroed then. Without the froth, the foam of the sim's reaches ended in a line where the frozen ones began.
    /// </summary>
    private void SeedFlow(ReadOnlySpan<WaterFlowInfo> flows)
    {
        if (FluxX is null || !HasFlow)
            return;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var c = x + 1 + (z + 1) * P;
                var id = _flowColumn[c];

                if (id == WaterFlows.None)
                    continue;

                var path = PathOf(flows, id);

                if (path is null)
                    continue;

                var station = _flowStation[c];
                var froth = path.Froth(station);
                var top = true;

                for (var y = S - 1; y >= 0; y--)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    var w = Water[i];

                    if (w <= WaterSurfaceMesher.WetThreshold || (Flags[i] & (SolidBit | HeldBit)) != 0)
                        continue;

                    Froth![i] = froth;

                    if (!top)
                        continue;

                    var flow = path.Directions[station] * (path.Speed(station) * MathF.Min(w, 1f));
                    FluxX[i - 1] = FluxX[i] = flow.X;
                    FluxZ![i - A] = FluxZ[i] = flow.Y;
                    top = false;
                }
            }
        }
    }

    private static RiverPath? PathOf(ReadOnlySpan<WaterFlowInfo> flows, ushort id)
    {
        foreach (var flow in flows)
            if (flow.Id == id)
                return flow.Path;

        return null;
    }

    /// <summary>The flow whose water interior column (x, z) holds, <see cref="WaterFlows.None"/> for none.</summary>
    public ushort FlowAt(int x, int z) => _flowColumn[x + 1 + (z + 1) * P];

    /// <summary>The free water (m³) in the interior flow columns: the flow's water this block holds.</summary>
    public float FlowWater()
    {
        if (!HasFlow)
            return 0f;

        var sum = 0.0;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                if (_flowColumn[x + 1 + (z + 1) * P] == WaterFlows.None)
                    continue;

                for (var y = 0; y < S; y++)
                {
                    var i = ChunkConstants.Index(x, y, z);

                    if ((Flags[i] & (SolidBit | HeldBit)) == 0)
                        sum += Water[i];
                }
            }
        }

        return (float)sum;
    }

    /// <summary>The padded column a side face's halo plane holds at <paramref name="k"/> along it (NegX, PosX, NegZ, PosZ only).</summary>
    private static (int X, int Z) EdgeColumn(Face face, int k) => face switch
    {
        Face.NegX => (-1, k),
        Face.PosX => (S, k),
        Face.NegZ => (k, -1),
        _ => (k, S),
    };

    private static int EdgeSlot(Face face) => face switch
    {
        Face.NegX => 0,
        Face.PosX => 1,
        Face.NegZ => 2,
        _ => 3,
    };

    /// <summary>
    /// Before a step, with no neighbour loaded on this side face: the halo's flow columns take the chunk's apron there (its
    /// solids, its generated water and beds), so the river runs on across the face rather than against a wall. Other
    /// columns stay solid.
    /// </summary>
    public void OpenFlowEdge(Face face)
    {
        var slot = EdgeSlot(face) * S * S;

        for (var k = 0; k < S; k++)
        {
            var (x, z) = EdgeColumn(face, k);

            if (_flowColumn[x + 1 + (z + 1) * P] == WaterFlows.None)
                continue;

            for (var y = 0; y < S; y++)
            {
                var i = ChunkConstants.Index(x, y, z);
                var e = slot + k + y * S;
                Flags[i] = _edgeFlags[e];
                Body[i] = WaterBodies.None;
                Bed[i] = _edgeBed[e];
                Water[i] = _edgeWater[e];
                Colour[i] = _edgeWater[e] > 0f ? WaterColour.Lake : WaterColour.None;
            }
        }
    }

    /// <summary>
    /// Before a step: the water in the halo's flow columns on this side face is scaled to <paramref name="stage"/> (the
    /// routed reach's storage over its design), refilled from the bed up, so the head the block feels there is the reach's.
    /// </summary>
    public void ScaleFlowEdge(Face face, float stage)
    {
        if (face is Face.NegY or Face.PosY || stage == 1f)
            return;

        for (var k = 0; k < S; k++)
        {
            var (x, z) = EdgeColumn(face, k);

            if (_flowColumn[x + 1 + (z + 1) * P] == WaterFlows.None)
                continue;

            var total = 0f;

            for (var y = 0; y < S; y++)
            {
                var i = ChunkConstants.Index(x, y, z);

                if ((Flags[i] & (SolidBit | HeldBit)) == 0)
                    total += MathF.Min(Water[i], 1f);
            }

            var left = total * stage;

            for (var y = 0; y < S; y++)
            {
                var i = ChunkConstants.Index(x, y, z);

                if ((Flags[i] & (SolidBit | HeldBit)) != 0)
                    continue;

                Water[i] = MathF.Min(left, 1f);
                left -= Water[i];
            }
        }
    }

    /// <summary>
    /// Pipe model, after a step's apply pass: the water (m³) the step moved out across a face (negative: in), as the
    /// apply pass moved it (each face's limited flux, none under <see cref="WaterFlow.SnapEpsilon"/>). A −side face is held
    /// in the halo's slots, a +side face in the border cells'.
    /// </summary>
    public float EdgeOutflow(Face face, float dt)
    {
        var axis = (int)face >> 1;
        var flux = axis == 0 ? FluxX : axis == 1 ? FluxY : FluxZ;

        if (flux is null)
            return 0f;

        var negative = ((int)face & 1) == 0;
        Plane(face, halo: negative, out var start, out var su, out var sv);
        var sum = 0f;

        for (var v = 0; v < S; v++)
        {
            for (var u = 0; u < S; u++)
            {
                var moved = flux[start + u * su + v * sv] * dt;

                if (MathF.Abs(moved) >= WaterFlow.SnapEpsilon)
                    sum += moved;
            }
        }

        return negative ? -sum : sum;
    }

    /// <summary>
    /// After an edit: recompute flags, keep water in open cells, zero solids, refill held cells at their body's level.
    /// Held water inside <paramref name="discs"/> of its body goes free with the water it holds; nothing is joined here
    /// (that happens as the block sleeps, <see cref="JoinBodies"/>).
    /// </summary>
    public void RefreshFrom(VoxelChunk chunk, ReadOnlySpan<Vector4> discs = default)
    {
        if (chunk.Coord.Lod != 0 || chunk.Coord != Coord)
            throw new ArgumentException("Refresh needs this block's LOD-0 chunk.", nameof(chunk));

        _discBody.CopyTo(_discBodyBefore, 0);
        MarkColumns(default, discs);
        ReadSolids(chunk, keepWater: true);
        Revision++;
        QuietSteps = 0;
        CalmSteps = 0;
        Asleep = false;
    }

    /// <summary>The discs now live over the block (the rest ended): their columns are left free by <see cref="JoinBodies"/>.</summary>
    public void MarkDiscs(ReadOnlySpan<Vector4> discs) => MarkColumns(default, discs);

    private void MarkColumns(ReadOnlySpan<WaterBodyInfo> generated, ReadOnlySpan<Vector4> discs)
    {
        _discBody.AsSpan().Clear();
        var origin = Coord.WorldOrigin;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var wx = origin.X + x;
                var wz = origin.Z + z;

                foreach (var disc in discs)
                {
                    var dx = wx - disc.X;
                    var dz = wz - disc.Y;

                    if (dx * dx + dz * dz > disc.Z * disc.Z)
                        continue;

                    _discBody[x + z * S] = (ushort)disc.W;
                    break;
                }

                if (generated.IsEmpty)
                    continue;

                _generatedBody[x + z * S] = 0;

                foreach (var info in generated)
                {
                    if (!info.Contains(wx, wz))
                        continue;

                    _generatedBody[x + z * S] = info.Id;
                    break;
                }
            }
        }
    }

    private void ReadSolids(VoxelChunk chunk, bool keepWater)
    {
        var density = chunk.Density;
        var source = chunk.Water;
        var levels = Bodies.Levels;
        var seaTop = Bodies.SeaTop;
        ClearPresent();
        Opened.Clear();

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var row = ChunkConstants.Index(0, y, z);
                var worldY = Coord.Y * S + y;

                for (var i = row; i < row + S; i++)
                {
                    var solid = density[i] < 0;
                    var col = i - row + z * S;

                    // The surface crosses between the sample below (solid, < 0) and this one (open, >= 0); the cell's floor
                    // is halfway between them.
                    var under = density[i - P];
                    Bed[i] = solid || under >= 0 ? NotBed : (sbyte)MathF.Round(((float)under / (under - density[i]) - 0.5f) * 254f);

                    var disc = _discBody[col];
                    if (keepWater && !solid && (Flags[i] & SolidBit) != 0 && disc != 0)
                        Owe(disc, -WaterBodies.Fill(worldY, levels[disc]));

                    if (solid)
                    {
                        Flags[i] = SolidBit;
                        Body[i] = 0;
                        Water[i] = 0f;
                        Colour[i] = WaterColour.None;
                        continue;
                    }

                    ushort id;
                    float water;

                    if (keepWater)
                    {
                        id = Body[i];
                        water = Water[i];
                    }
                    else
                    {
                        var generated = source[i] >= ReservoirByte;
                        id = generated ? worldY <= seaTop ? WaterBodies.Sea : _generatedBody[col] : (ushort)0;
                        water = source[i] * (1f / byte.MaxValue);
                        var flow = _flowColumn[col % S + 1 + (col / S + 1) * P] != WaterFlows.None;
                        Colour[i] = id != 0 ? Bodies[id].Colour : water <= 0f ? WaterColour.None : flow ? WaterColour.Lake : WaterColour.Sea;
                    }

                    if (id == 0 && worldY > seaTop && worldY <= seaTop + ZoneRunUp && OverSea(i, y, worldY, density, source))
                    {
                        id = WaterBodies.Sea;
                        Colour[i] = Bodies[id].Colour;
                    }

                    if (id != 0)
                    {
                        var fill = WaterBodies.Fill(worldY, levels[id]);

                        if (_discBody[col] == id)
                        {
                            // Freed by a disc: the water it was held with, standing on its bed at the body's level.
                            water = keepWater && (Flags[i] & HeldBit) == 0 ? water : FreeFill(fill, Bed[i]);
                            id = 0;
                        }
                        else
                        {
                            water = fill;
                            Note(id);
                        }
                    }

                    // Free water a disc newly covers is its body's from now on, and water one no longer covers leaves it.
                    if (keepWater && id == 0 && (Flags[i] & (HeldBit | SolidBit)) == 0 && water > 0f && _discBodyBefore[col] != disc)
                    {
                        if (_discBodyBefore[col] != 0)
                            Owe(_discBodyBefore[col], -water);

                        if (disc != 0)
                            Owe(disc, water);
                    }

                    Flags[i] = id != 0 ? HeldBit : (byte)0;
                    Body[i] = id;
                    Water[i] = water;

                    if (water > 0f && Colour[i] == WaterColour.None)
                        Colour[i] = WaterColour.Sea;
                }
            }
        }

        _openFaces = 0;

        for (var f = 0; f < 6; f++)
            if (ApronOpen(density, (Face)f))
                _openFaces |= (byte)(1 << f);

        if (HasFlow)
            ReadEdges(density, source);
    }

    /// <summary>The apron beyond the side faces in flow columns (<see cref="OpenFlowEdge"/>): solid or open, its bed, and the generated water.</summary>
    private void ReadEdges(sbyte[] density, byte[] source)
    {
        for (var f = 0; f < 4; f++)
        {
            var face = f switch { 0 => Face.NegX, 1 => Face.PosX, 2 => Face.NegZ, _ => Face.PosZ };

            for (var k = 0; k < S; k++)
            {
                var (x, z) = EdgeColumn(face, k);
                var flow = _flowColumn[x + 1 + (z + 1) * P] != WaterFlows.None;

                for (var y = 0; y < S; y++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    var e = f * S * S + k + y * S;
                    var solid = density[i] < 0;
                    var under = density[i - P];
                    _edgeFlags[e] = !flow || solid ? SolidBit : (byte)0;
                    _edgeBed[e] = !flow || solid || under >= 0 ? NotBed : (sbyte)MathF.Round(((float)under / (under - density[i]) - 0.5f) * 254f);
                    _edgeWater[e] = !flow || solid ? 0f : source[i] * (1f / byte.MaxValue);
                }
            }
        }
    }

    /// <summary>
    /// The open cell under i is the sea's: held in this block, or in the apron the sea's top layer of generated water. The
    /// sea's run-up layers over it are held too, empty at rest, so a crest stands in them and water that runs into them
    /// joins the sea, and none stands over the ocean as free water the voxel mesh would draw.
    /// </summary>
    private bool OverSea(int i, int y, int worldY, sbyte[] density, byte[] source)
    {
        var below = i - P;

        if (y > 0)
            return (Flags[below] & HeldBit) != 0 && Body[below] == WaterBodies.Sea;

        return worldY - 1 == Bodies.SeaTop && density[below] >= 0 && source[below] >= ReservoirByte;
    }

    /// <summary>
    /// The fill a held cell freed by a disc starts with: its held fill, except that in the pipe model a part-full cell on a
    /// bed stands on the bed (<see cref="BedRise"/>): the generated half-cell at the shore would stand over the body and
    /// slump into it, or under it (a bed below its floor) and draw the body in across a seam. It takes the fill whose
    /// surface on that bed is the body's: head + rise (1 − w) = fill − ½. What it then holds more or less than the held
    /// fill is booked when it rejoins.
    /// </summary>
    private float FreeFill(float fill, sbyte bed)
    {
        if (FluxX is null || fill > 1f || bed == NotBed)
            return fill;

        var rise = bed * BedUnit;
        return Math.Clamp((fill - rise) / (1f - rise), 0f, 1f);
    }

    private bool ApronOpen(sbyte[] density, Face face)
    {
        Plane(face, halo: false, out var inner, out var su, out var sv);
        var outward = Outward(face);

        for (var v = 0; v < S; v++)
        {
            for (var u = 0; u < S; u++)
            {
                var i = inner + u * su + v * sv;

                if ((Flags[i] & SolidBit) == 0 && density[i + outward] >= 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Main thread, as the block sleeps (its water still, halos fresh): free water connected to a body's held cells (here
    /// or in the halos) that stands at the body's level joins it. A cell with a free surface joins when that surface is
    /// within <see cref="WaterBodies.JoinTolerance"/> of the level; a cell under other water, when its top is under the
    /// level and it is full. Dry cells never join, so a tunnel out of a lake drains it instead of the lake flooding the
    /// hillside outside to its level, as a flood through every open cell under the surface once did. Columns inside a
    /// live disc of the body are left alone (a dig it is pouring into; they join when the disc ends). Results in
    /// <see cref="Joined"/>: what each body paid (fill − water, about nothing at the level) and the surface cells it gained.
    /// </summary>
    public void JoinBodies()
    {
        Joined.Clear();
        var levels = Bodies.Levels;
        var stack = _flood ??= new int[ChunkConstants.PaddedVolume];
        var seen = _seen ??= new byte[ChunkConstants.PaddedVolume];
        seen.AsSpan().Clear();
        var count = 0;

        for (var i = 0; i < ChunkConstants.PaddedVolume; i++)
        {
            if ((Flags[i] & HeldBit) == 0 || Body[i] <= WaterBodies.Sea)
                continue;

            stack[count++] = i;
            seen[i] = 1;
        }

        // The walk crosses every wet free cell it reaches (a not quite full cell under the surface must not cut the rest
        // of a pit off into a body of its own at the same level), and takes in those that stand at the level.
        while (count > 0)
        {
            var i = stack[--count];
            var id = Body[i];
            var level = levels[id];

            for (var f = 0; f < 6; f++)
            {
                var n = i + (f >> 1 == 0 ? 1 : f >> 1 == 1 ? P : A) * ((f & 1) == 0 ? -1 : 1);
                var (x, y, z) = (n % P - 1, n / P % P - 1, n / A - 1);

                if ((uint)x >= S || (uint)y >= S || (uint)z >= S || seen[n] != 0 || (Flags[n] & (SolidBit | HeldBit)) != 0 || _discBody[x + z * S] == id || FlowAt(x, z) != WaterFlows.None)
                    continue;

                var w = Water[n];

                if (w <= WaterSurfaceMesher.WetThreshold)
                    continue;

                seen[n] = 1;
                var worldY = Coord.Y * S + y;

                if (AtLevel(n, worldY, w, level))
                {
                    var fill = WaterBodies.Fill(worldY, level);
                    Take(id, fill - w, Surface(n) ? 1 : 0);
                    Flags[n] = HeldBit;
                    Body[n] = id;
                    Water[n] = fill;
                    Note(id);
                }

                stack[count++] = n;
            }
        }
    }

    /// <summary>Its cell above holds no water (or is solid): this cell's water has a free surface.</summary>
    private bool Surface(int i) => (Flags[i + P] & SolidBit) != 0 || Water[i + P] <= WaterSurfaceMesher.WetThreshold;

    /// <summary>The cell's water stands at the level: its surface within tolerance of it, or full and wholly under it.</summary>
    private bool AtLevel(int i, int worldY, float w, float level)
    {
        if (Surface(i))
        {
            var surface = worldY - 0.5f + MathF.Min(w, 1f) + BedRise(Bed[i], w);
            return MathF.Abs(surface - level) <= WaterBodies.JoinTolerance;
        }

        return w >= WaterFlow.SupportThreshold && worldY + 0.5f <= level + WaterBodies.JoinTolerance;
    }

    private void Take(ushort id, float paid, int surface)
    {
        for (var k = 0; k < Joined.Count; k++)
        {
            if (Joined[k].Id != id)
                continue;

            Joined[k] = (id, Joined[k].Paid + paid, Joined[k].Surface + surface);
            return;
        }

        Joined.Add((id, paid, surface));
    }

    /// <summary>
    /// A connected set of wet free cells (<see cref="BornBodies"/>): a cell of it, its surface cells with the sum, lowest
    /// and highest of their surfaces (world y) and the sums of their world x and z, its lowest floor, and the faces (a bit
    /// per <see cref="Face"/>) where it meets wet free water in the halo, so goes on into the neighbour.
    /// </summary>
    public readonly record struct Pool(int Seed, int Surfaces, double Sum, float Low, float High, double X, double Z, float Bottom, byte Faces)
    {
        public float Level => (float)(Sum / Surfaces);

        public bool Flat => High - Low <= 2f * WaterBodies.JoinTolerance;

        public Vector2 Centre => new((float)(X / Surfaces), (float)(Z / Surfaces));
    }

    /// <summary>Pools the last <see cref="BornBodies"/> left free as too small here that go on into a neighbour (<see cref="BornAcross"/>).</summary>
    public readonly List<Pool> EdgePools = new(2);

    /// <summary>A pool and the pools it meets are counted over at most this many blocks (<see cref="BornAcross"/>).</summary>
    public const int MaxPoolBlocks = 16;

    [ThreadStatic]
    private static List<Pool>? _pools;

    [ThreadStatic]
    private static List<(ChunkCoord At, byte Faces)>? _poolBlocks;

    /// <summary>
    /// Main thread, as the block sleeps, after <see cref="JoinBodies"/>: free water left that stands level becomes a body
    /// of its own (docs/voxel-water.md "Bodies of water"): each connected set of wet free cells whose free surfaces lie
    /// within twice the join tolerance of one another, with at least <paramref name="minSurface"/> surface cells (specks
    /// and films are left free). Its level is their mean, its area their count here (neighbouring blocks add theirs as
    /// they join it), its centre their mean. Cells are held at the fill under that level and what they lacked is booked.
    /// Pools under <see cref="MinBornSurface"/> stay free: a body of a few m² moved metres for each m³ it booked, and those
    /// born in a tunnel's outflow ran to 16 m over their floors; free water at rest sleeps and costs nothing anyway. Those
    /// that go on into a neighbour are left in <see cref="EdgePools"/>, to be counted with it (<see cref="BornAcross"/>).
    /// </summary>
    public void BornBodies(int minSurface = MinBornSurface)
    {
        EdgePools.Clear();
        var members = _flood ??= new int[ChunkConstants.PaddedVolume];
        var seen = _seen ??= new byte[ChunkConstants.PaddedVolume];
        seen.AsSpan().Clear();

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                for (var x = 0; x < S; x++)
                {
                    var start = ChunkConstants.Index(x, y, z);

                    if (seen[start] != 0 || (Flags[start] & (SolidBit | HeldBit)) != 0 || Water[start] <= WaterSurfaceMesher.WetThreshold || FlowAt(x, z) != WaterFlows.None)
                        continue;

                    var pool = Gather(start, members, seen, out var count, out var beside);

                    if (pool.Surfaces == 0 || !pool.Flat)
                        continue;

                    if (pool.Surfaces < minSurface)
                    {
                        if (pool.Faces != 0)
                            EdgePools.Add(pool);

                        continue;
                    }

                    // Level with a body it touches (here or in the halos): it is that body's, not a twin of it.
                    if (beside != 0 && MathF.Abs(Bodies.Levels[beside] - pool.Level) <= 2f * WaterBodies.JoinTolerance)
                        Hold(members, count, beside, pool.Surfaces);
                    else
                        Hold(members, count, Born(pool, pool.Level, pool.Centre), 0);
                }
            }
        }
    }

    /// <summary>
    /// Main thread, after <see cref="BornBodies"/>: an edge pool too small here is born a body if, with the pools at its
    /// level it meets in sleeping neighbours (<paramref name="asleep"/>: the block at a coord if loaded and asleep), it has
    /// <paramref name="minSurface"/> surface cells. Each block counted only its own part, so a pond across a block edge
    /// was never born. Its level and centre are the whole pool's, its area its part here: the neighbours' parts join it
    /// as they settle. A neighbour it goes on into that is awake or not loaded leaves it free, to be tried again as that
    /// one sleeps. Returns the body, or <see cref="WaterBodies.None"/>.
    /// </summary>
    public ushort BornAcross(in Pool pool, Func<ChunkCoord, WaterBlock?> asleep, int minSurface = MinBornSurface)
    {
        var pools = _pools ??= new List<Pool>(4);
        var blocks = _poolBlocks ??= new List<(ChunkCoord, byte)>(MaxPoolBlocks);
        blocks.Clear();
        blocks.Add((Coord, pool.Faces));
        var (surfaces, sum, low, high, x, z) = (pool.Surfaces, pool.Sum, pool.Low, pool.High, pool.X, pool.Z);

        for (var b = 0; b < blocks.Count && surfaces < minSurface; b++)
        {
            for (var f = 0; f < 6; f++)
            {
                if ((blocks[b].Faces & 1 << f) == 0)
                    continue;

                var at = Across(blocks[b].At, f);

                if (Visited(blocks, at))
                    continue;

                if (blocks.Count == MaxPoolBlocks || asleep(at) is not { } neighbour)
                    return WaterBodies.None;

                var back = (byte)(1 << (f ^ 1));
                var faces = (byte)0;
                neighbour.Survey(pools);

                foreach (var p in pools)
                {
                    if ((p.Faces & back) == 0 || !p.Flat)
                        continue;

                    if (p.Surfaces > 0 && MathF.Max(high, p.High) - MathF.Min(low, p.Low) > 2f * WaterBodies.JoinTolerance)
                        continue;

                    (surfaces, sum, x, z) = (surfaces + p.Surfaces, sum + p.Sum, x + p.X, z + p.Z);
                    (low, high) = p.Surfaces > 0 ? (MathF.Min(low, p.Low), MathF.Max(high, p.High)) : (low, high);
                    faces |= (byte)(p.Faces & ~back);
                }

                blocks.Add((at, faces));
            }
        }

        if (surfaces < minSurface)
            return WaterBodies.None;

        var members = _flood ??= new int[ChunkConstants.PaddedVolume];
        var seen = _seen ??= new byte[ChunkConstants.PaddedVolume];
        seen.AsSpan().Clear();
        Gather(pool.Seed, members, seen, out var count, out _);
        var id = Born(pool, (float)(sum / surfaces), new Vector2((float)(x / surfaces), (float)(z / surfaces)));
        Hold(members, count, id, 0);
        return id;
    }

    /// <summary>Every connected set of wet free cells in the block (<see cref="Pool"/>).</summary>
    public void Survey(List<Pool> into)
    {
        into.Clear();
        var members = _flood ??= new int[ChunkConstants.PaddedVolume];
        var seen = _seen ??= new byte[ChunkConstants.PaddedVolume];
        seen.AsSpan().Clear();

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var x = 0; x < S; x++)
                {
                    var i = ChunkConstants.Index(x, y, z);

                    if (seen[i] == 0 && (Flags[i] & (SolidBit | HeldBit)) == 0 && Water[i] > WaterSurfaceMesher.WetThreshold)
                        into.Add(Gather(i, members, seen, out _, out _));
                }
    }

    /// <summary>Floods the wet free cells connected to <paramref name="start"/> into <paramref name="members"/> and surveys them; <paramref name="beside"/> is a body held next to them.</summary>
    private Pool Gather(int start, int[] members, byte[] seen, out int count, out ushort beside)
    {
        var origin = Coord.WorldOrigin;
        count = 0;
        var read = 0;
        members[count++] = start;
        seen[start] = 1;
        var surfaces = 0;
        var sum = 0.0;
        var low = float.MaxValue;
        var high = float.MinValue;
        var x = 0.0;
        var z = 0.0;
        var bottom = float.MaxValue;
        var faces = (byte)0;
        beside = 0;

        while (read < count)
        {
            var i = members[read++];
            var (ix, iy, iz) = (i % P - 1, i / P % P - 1, i / A - 1);
            var worldY = Coord.Y * S + iy;
            bottom = MathF.Min(bottom, worldY - 0.5f);

            if (Surface(i))
            {
                var surface = worldY - 0.5f + MathF.Min(Water[i], 1f) + BedRise(Bed[i], Water[i]);
                surfaces++;
                sum += surface;
                low = MathF.Min(low, surface);
                high = MathF.Max(high, surface);
                x += origin.X + ix;
                z += origin.Z + iz;
            }

            for (var f = 0; f < 6; f++)
            {
                var n = i + (f >> 1 == 0 ? 1 : f >> 1 == 1 ? P : A) * ((f & 1) == 0 ? -1 : 1);
                var (nx, ny, nz) = (n % P - 1, n / P % P - 1, n / A - 1);
                var open = (Flags[n] & (SolidBit | HeldBit)) == 0 && Water[n] > WaterSurfaceMesher.WetThreshold && _flowColumn[nx + 1 + (nz + 1) * P] == WaterFlows.None;

                if ((Flags[n] & HeldBit) != 0 && Body[n] > WaterBodies.Sea)
                    beside = Body[n];

                if ((uint)nx >= S || (uint)ny >= S || (uint)nz >= S)
                {
                    faces |= open ? (byte)(1 << f) : (byte)0;
                    continue;
                }

                if (seen[n] != 0 || !open)
                    continue;

                seen[n] = 1;
                members[count++] = n;
            }
        }

        return new Pool(start, surfaces, sum, low, high, x, z, bottom, faces);
    }

    /// <summary>A body born of the pool, its area the pool's surface here.</summary>
    private ushort Born(in Pool pool, float level, Vector2 centre)
    {
        var colour = Colour[pool.Seed] == WaterColour.None ? WaterColour.Lake : Colour[pool.Seed];
        return Bodies.Add(level, pool.Surfaces, centre, 0f, pool.Bottom, colour, born: true);
    }

    /// <summary>Holds the flooded cells at the fill under the body's level and books what they lacked, with the surface they add.</summary>
    private void Hold(int[] members, int count, ushort id, int surface)
    {
        var level = Bodies.Levels[id];
        var paid = 0f;

        for (var k = 0; k < count; k++)
        {
            var i = members[k];
            var fill = WaterBodies.Fill(Coord.Y * S + i / P % P - 1, level);
            paid += fill - Water[i];
            Flags[i] = HeldBit;
            Body[i] = id;
            Water[i] = fill;
        }

        Note(id);
        Take(id, paid, surface);
    }

    private static ChunkCoord Across(ChunkCoord at, int face) => (Face)face switch
    {
        Face.NegX => new ChunkCoord(at.X - 1, at.Y, at.Z),
        Face.PosX => new ChunkCoord(at.X + 1, at.Y, at.Z),
        Face.NegY => new ChunkCoord(at.X, at.Y - 1, at.Z),
        Face.PosY => new ChunkCoord(at.X, at.Y + 1, at.Z),
        Face.NegZ => new ChunkCoord(at.X, at.Y, at.Z - 1),
        _ => new ChunkCoord(at.X, at.Y, at.Z + 1),
    };

    private static bool Visited(List<(ChunkCoord At, byte Faces)> blocks, ChunkCoord at)
    {
        foreach (var (c, _) in blocks)
            if (c.Equals(at))
                return true;

        return false;
    }

    /// <summary>
    /// Brings the beds in line with the flags alone, for blocks built or edited by hand instead of from a chunk (tests):
    /// an open interior cell over solid keeps its rise, or stands on a flat bed if it had none; every other cell is
    /// <see cref="NotBed"/>. Call after the halo flags are in, then copy the halos again.
    /// </summary>
    public void DeriveBeds()
    {
        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    Bed[i] = (Flags[i] & SolidBit) != 0 || (Flags[i - P] & SolidBit) == 0 ? NotBed : Bed[i] == NotBed ? (sbyte)0 : Bed[i];
    }

    /// <summary>
    /// How much higher water with fill <paramref name="w"/> stands on a bed than on its voxel floor: the bed's whole rise
    /// under a film, fading to none as the cell fills, so full cells and the layers over them keep their heads.
    /// </summary>
    public static float BedRise(sbyte bed, float w) => bed == NotBed ? 0f : bed * BedUnit * (1f - MathF.Min(w, 1f));

    /// <summary>Fill a held cell is held at under its body's level (<see cref="WaterBodies.Fill"/>).</summary>
    public float HeldFill(int i)
    {
        var worldY = Coord.Y * S + i / P % P - 1;
        var id = Body[i];
        var level = Bodies.Levels[id];

        if (id == WaterBodies.Sea && (Flags[i] & ZoneBit) == 0)
            level = WaterBodies.SwellLevel(level, Swell[i % P + i / A * P], worldY, Bodies.SeaTop);

        return WaterBodies.Fill(worldY, level);
    }

    /// <summary>
    /// Pipe model, before a step: the sea's held cells outside sea zones, interior and halo, at the sea's level over their
    /// column under <see cref="Swell"/>. Both blocks of a shared face compute the same fill for the same cell, so the face
    /// stays bit-identical.
    /// </summary>
    public void HoldSwell()
    {
        if (!Swelling && !_swellHeld)
            return;

        _swellHeld = Swelling;
        var seaTop = Bodies.SeaTop;
        var rest = Bodies.Levels[WaterBodies.Sea];
        var low = Math.Max(seaTop - (int)WaterBodies.SwellDepth - Coord.Y * S, -1);
        var high = Math.Min(seaTop + ZoneRunUp - Coord.Y * S, S);

        for (var y = low; y <= high; y++)
        {
            var worldY = Coord.Y * S + y;

            for (var z = -1; z <= S; z++)
            {
                var row = ChunkConstants.Index(-1, y, z);
                var column = (z + 1) * P;

                for (var x = 0; x < P; x++)
                {
                    var i = row + x;

                    if ((Flags[i] & (HeldBit | ZoneBit)) == HeldBit && Body[i] == WaterBodies.Sea)
                        Water[i] = WaterBodies.Fill(worldY, WaterBodies.SwellLevel(rest, Swell[column + x], worldY, seaTop));
                }
            }
        }
    }

    /// <summary>Any of the sea's held cells in the block or its halo: free water here can feel the swell.</summary>
    public bool TouchesSea()
    {
        if (Holds(WaterBodies.Sea))
            return true;

        for (var f = 0; f < 6; f++)
        {
            Plane((Face)f, halo: true, out var start, out var su, out var sv);

            for (var v = 0; v < S; v++)
            {
                for (var u = 0; u < S; u++)
                {
                    var i = start + u * su + v * sv;

                    if ((Flags[i] & HeldBit) != 0 && Body[i] == WaterBodies.Sea)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>Tests: holds interior cell (x, y, z) for the body at its fill.</summary>
    public void Hold(int x, int y, int z, ushort id)
    {
        var i = ChunkConstants.Index(x, y, z);
        Flags[i] = HeldBit;
        Body[i] = id;
        Water[i] = HeldFill(i);
        Colour[i] = Bodies[id].Colour;
        Note(id);
        Revision++;
    }

    /// <summary>Sets every held cell to its fill again, after its body's level moved.</summary>
    public void RefillHeld()
    {
        var levels = Bodies.Levels;

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var row = ChunkConstants.Index(0, y, z);
                var worldY = Coord.Y * S + y;

                for (var i = row; i < row + S; i++)
                    if ((Flags[i] & HeldBit) != 0)
                        Water[i] = WaterBodies.Fill(worldY, levels[Body[i]]);
            }
        }

        if (Swelling)
            HoldSwell();

        Revision++;
    }

    /// <summary>
    /// A held cell of the body in its top layer with open air beside it whose bed lies under the level: where a rising
    /// body spills over its rim. The block must wake for the spill to run.
    /// </summary>
    public bool Spills(ushort id)
    {
        var level = Bodies.Levels[id];

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var worldY = Coord.Y * S + y;

                if (worldY + 0.5f < level - 1f || worldY - 0.5f > level)
                    continue;

                var row = ChunkConstants.Index(0, y, z);

                for (var i = row; i < row + S; i++)
                {
                    if ((Flags[i] & HeldBit) == 0 || Body[i] != id)
                        continue;

                    for (var f = 0; f < 6; f++)
                    {
                        if (f >> 1 == 1)
                            continue;

                        var n = i + (f >> 1 == 0 ? 1 : A) * ((f & 1) == 0 ? -1 : 1);

                        if ((Flags[n] & (SolidBit | HeldBit)) == 0 && Water[n] <= WaterSurfaceMesher.WetThreshold && (Flags[n - P] & SolidBit) != 0
                            && worldY - 0.5f + MathF.Max(0f, BedRise(Bed[n], 0f)) < level)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Inside the given discs (world x, world z, radius), hands open cells below sea level to the zone shallow-water sim:
    /// they become the sea's for this automaton and are tagged <see cref="WaterFlags.SeaZone"/>.
    /// Must be re-applied after <see cref="RefreshFrom"/>, which re-derives flags from the voxel chunk.
    /// </summary>
    public void Unclamp(ReadOnlySpan<Vector3> discs)
    {
        const byte zone = (byte)WaterFlags.SeaZone;
        var origin = Coord.WorldOrigin;
        var seaTop = Bodies.SeaTop;
        var seaLevel = Bodies.Levels[WaterBodies.Sea];

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var inside = false;
                foreach (var disc in discs)
                {
                    var dx = origin.X + x - disc.X;
                    var dz = origin.Z + z - disc.Y;
                    if (dx * dx + dz * dz <= disc.Z * disc.Z)
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                    continue;

                // Zone cells belong to the shallow-water zone sim; here they act as the sea (a source/sink for water
                // flowing out of the zone) and are never drawn by the voxel-water mesh.
                for (var y = 0; y < S; y++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    if ((Flags[i] & SolidBit) != 0)
                        continue;

                    var worldY = Coord.Y * S + y;
                    if (worldY > seaTop + ZoneRunUp)
                        continue;

                    // The sea's run-up layers (ReadSolids) are the zone sim's here.
                    if (worldY >= seaTop + 1)
                    {
                        Flags[i] = (byte)(Flags[i] & ~HeldBit | zone);
                        Body[i] = WaterBodies.None;
                        continue;
                    }

                    Flags[i] = (byte)(Flags[i] | HeldBit | zone);
                    Body[i] = WaterBodies.Sea;
                    Water[i] = WaterBodies.Fill(worldY, seaLevel);
                }
            }
        }

        Note(WaterBodies.Sea);
    }

    /// <summary>Interior water volume (cells), summed in double.</summary>
    public float Mass()
    {
        var sum = 0.0;

        for (var z = 0; z < S; z++)
        {
            for (var y = 0; y < S; y++)
            {
                var row = Water.AsSpan(ChunkConstants.Index(0, y, z), S);

                foreach (var w in row)
                    sum += w;
            }
        }

        return (float)sum;
    }

    /// <summary>Surface cells (m²) free water at rest needs here to be born a body (<see cref="BornBodies"/>).</summary>
    public const int MinBornSurface = 64;

    /// <summary>
    /// Main thread: held cells of bodies no longer alive (released, <see cref="WaterBodies.Released"/>) go back to free
    /// water at the fill they were held at. True if any did: the caller wakes the block and refreshes its halos.
    /// </summary>
    public bool ReleaseDead()
    {
        var any = false;

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                {
                    var id = Body[i];
                    if ((Flags[i] & HeldBit) == 0 || id <= WaterBodies.Sea || Bodies[id].Alive)
                        continue;

                    Flags[i] &= unchecked((byte)~HeldBit);
                    Body[i] = 0;
                    _present[id] = 0;
                    any = true;
                }

        if (any)
            Revision++;

        return any;
    }

    private void Owe(ushort id, float amount)
    {
        if (amount == 0f)
            return;

        for (var k = 0; k < Opened.Count; k++)
        {
            if (Opened[k].Id != id)
                continue;

            Opened[k] = (id, Opened[k].Amount + amount);
            return;
        }

        Opened.Add((id, amount));
    }

    /// <summary>Main thread, once a refresh has landed: books what its digs owe (<see cref="Opened"/>).</summary>
    public void BookOpened()
    {
        foreach (var (id, amount) in Opened)
            Bodies.Exchange(id, amount);

        Opened.Clear();
    }

    /// <summary>Main thread: tells each body the top of the highest cell it holds here (<see cref="WaterBodies.Body.Top"/>).</summary>
    public void RaiseTops()
    {
        for (var y = S - 1; y >= 0; y--)
        {
            var top = Coord.Y * S + y + 0.5f;

            for (var z = 0; z < S; z++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    if ((Flags[i] & HeldBit) != 0 && Bodies[Body[i]].Top < top)
                        Bodies.RaiseTop(Body[i], top);
        }
    }

    /// <summary>
    /// Free water (m³) in the interior columns inside <paramref name="disc"/> (world x, z, radius, body id) that no live
    /// disc of its body frees any more: water the body counted as its own until the disc ended.
    /// </summary>
    public double FreeWaterLeft(Vector4 disc)
    {
        var origin = Coord.WorldOrigin;
        var sum = 0.0;

        for (var z = 0; z < S; z++)
        {
            for (var x = 0; x < S; x++)
            {
                var dx = origin.X + x - disc.X;
                var dz = origin.Z + z - disc.Y;

                if (dx * dx + dz * dz > disc.Z * disc.Z || _discBody[x + z * S] == (ushort)disc.W)
                    continue;

                for (var y = 0; y < S; y++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    if ((Flags[i] & HeldBit) == 0)
                        sum += Water[i];
                }
            }
        }

        return sum;
    }

    /// <summary>
    /// Diagnostics: the free water no body counts as its own (m³): interior cells neither held nor in a column a live disc
    /// frees (that water is its body's, <see cref="DiscBodies"/>).
    /// </summary>
    public double FreeMass()
    {
        var sum = 0.0;

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var x = 0; x < S; x++)
                {
                    var i = ChunkConstants.Index(x, y, z);
                    if ((Flags[i] & HeldBit) == 0 && _discBody[x + z * S] == 0)
                        sum += Water[i];
                }

        return sum;
    }

    /// <summary>Diagnostics: the largest fill of any interior cell that is not held.</summary>
    public float MaxFill()
    {
        var max = 0f;

        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    if ((Flags[i] & HeldBit) == 0)
                        max = MathF.Max(max, Water[i]);

        return max;
    }

    /// <summary>Any interior cell on this face holds water.</summary>
    public bool AnyWaterOnFace(Face face)
    {
        Plane(face, halo: false, out var start, out var su, out var sv);

        for (var v = 0; v < S; v++)
            for (var u = 0; u < S; u++)
                if (Water[start + u * su + v * sv] > 0f)
                    return true;

        return false;
    }

    /// <summary>Any water in the block that is free to move: neither solid nor held by a body.</summary>
    public bool HasFreeWater()
    {
        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    if (Water[i] > 0f && (Flags[i] & (SolidBit | HeldBit)) == 0)
                        return true;

        return false;
    }

    /// <summary>Any water on this face's border cells that is free to move: neither solid nor held by a body.</summary>
    public bool FreeWaterOnFace(Face face)
    {
        Plane(face, halo: false, out var start, out var su, out var sv);

        for (var v = 0; v < S; v++)
        {
            for (var u = 0; u < S; u++)
            {
                var i = start + u * su + v * sv;

                if (Water[i] > 0f && (Flags[i] & (SolidBit | HeldBit)) == 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Every interior cell is solid or the sea's: nothing in the block can ever change.</summary>
    public bool AllSea()
    {
        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    if ((Flags[i] & SolidBit) == 0 && ((Flags[i] & HeldBit) == 0 || Body[i] != WaterBodies.Sea))
                        return false;

        return true;
    }

    /// <summary>Every interior cell is solid or held: at rest until a body's level moves or an edit comes.</summary>
    public bool AllHeld()
    {
        for (var z = 0; z < S; z++)
            for (var y = 0; y < S; y++)
                for (var i = ChunkConstants.Index(0, y, z); i < ChunkConstants.Index(S, y, z); i++)
                    if ((Flags[i] & (SolidBit | HeldBit)) == 0)
                        return false;

        return true;
    }

    /// <summary>The chunk's terrain lets water leave through this face (an open border cell meets open apron). Set by InitFrom/RefreshFrom.</summary>
    public bool FaceOpen(Face face) => (_openFaces & (1 << (int)face)) != 0;

    /// <summary>Start index and the two in-plane strides of a face plane: the interior border layer, or the halo layer beyond it.</summary>
    internal static void Plane(Face face, bool halo, out int start, out int su, out int sv)
    {
        var positive = ((int)face & 1) != 0;
        var fixedCoord = positive ? (halo ? S : S - 1) : (halo ? -1 : 0);

        switch ((int)face >> 1)
        {
            case 0:
                start = ChunkConstants.Index(fixedCoord, 0, 0);
                su = P;
                sv = A;
                break;
            case 1:
                start = ChunkConstants.Index(0, fixedCoord, 0);
                su = 1;
                sv = A;
                break;
            default:
                start = ChunkConstants.Index(0, 0, fixedCoord);
                su = 1;
                sv = P;
                break;
        }
    }

    internal static int Outward(Face face)
    {
        var stride = ((int)face >> 1) switch { 0 => 1, 1 => P, _ => A };
        return ((int)face & 1) != 0 ? stride : -stride;
    }
}
