using System.Diagnostics;
using System.Runtime.InteropServices;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;

namespace BrutalGame.Terrain.Runtime;

/// <summary>One leaf selection: the desired leaves in priority order, filled on a job and swapped in by the main thread.</summary>
public sealed class LeafSet
{
    /// <summary>The leaves' <see cref="ChunkCoord.Key"/>s.</summary>
    public readonly HashSet<ulong> Leaves = new(8192);

    /// <summary>Split nodes at leaf LODs (1..12): the octree above the leaves, so coverage checks walk down it.</summary>
    public readonly HashSet<ulong> Internal = new(4096);
    public readonly List<ChunkCoord> Order = new(8192);

    /// <summary>Each leaf's priority (distance in its own voxels), parallel to <see cref="Order"/> and sorted with it.</summary>
    public readonly List<float> Keys = new(8192);
    public readonly int[] PerLod = new int[PlanetLeafSelector.MaxLeafLod + 1];

    public double3 Camera;
    public int MinLod;
    public double HorizonMargin;

    /// <summary>Finest LOD with a leaf; <see cref="PlanetLeafSelector.MaxLeafLod"/> when there are none.</summary>
    public int FinestLod;
    public int Visits;
    public int Pruned;
    public int Uniform;
    public float Ms;

    public int Count => Order.Count;

    public void Clear()
    {
        Leaves.Clear();
        Internal.Clear();
        Order.Clear();
        Keys.Clear();
        Array.Clear(PerLod);
        FinestLod = PlanetLeafSelector.MaxLeafLod;
        (Visits, Pruned, Uniform, Ms) = (0, 0, 0, 0f);
    }
}

/// <summary>
/// Planet octree leaf selection (planets.md "Streaming at flight speeds", "Main-thread work per frame"): 8 roots at LOD
/// 15 around the planet centre, LOD 13-15 always split, leaves LOD 0..12. A node splits while the camera is within
/// <see cref="SplitFactor"/> × its size, down to the speed bias's minimum LOD. Nodes the shell classifies as uniform air or
/// rock (<see cref="PlanetHeightBounds"/>) are dropped unless an edit reaches them, and so are nodes below the horizon of
/// the planet's lowest ground (view-independent, so turning never pops), by a margin so terrain loads before it can rise
/// into view. Allocation-free once the output has grown.
/// Reads the bounds and the edit store: nothing may write the store while a selection runs.
/// </summary>
public sealed class PlanetLeafSelector
{
    public const int RootLod = 15;
    public const int MaxLeafLod = PlanetGenerator.MaxLod;
    public const double SplitFactor = 3.0;

    /// <summary>Speed (m/s) that fills the LOD-0 ring in under 0.5 s; each doubling raises the finest LOD by one.</summary>
    public const double FillSpeed = 384.0;

    /// <summary>Levels below a leaf <see cref="ProvablyUnseen"/> classifies to prove it empty.</summary>
    public const int EmptyDepth = 3;

    public readonly PlanetHeightBounds Bounds;
    public readonly EditStore Edits;

    private readonly double _radius;
    private readonly float _maxHeight;
    private readonly float _minHeight;

    /// <summary>Radius of the ball under all ground: the horizon occluder.</summary>
    private readonly double _occluder;

    private double3 _camera;
    private int _minLod;
    private double _horizon;
    private double _margin;

    public PlanetLeafSelector(PlanetHeightBounds bounds, EditStore edits)
    {
        Bounds = bounds;
        Edits = edits;
        var p = bounds.Generator.Params;
        _radius = p.Radius;
        _maxHeight = p.MaxHeight;
        _minHeight = p.MinHeight;
        _occluder = _radius + bounds.GlobalMin;
    }

    /// <summary>The finest LOD the speed allows: LOD 0 up to <see cref="FillSpeed"/>, LOD 3 at 2-3 km/s, LOD 5 at 10 km/s.</summary>
    public static int SpeedLod(double speed) =>
        speed <= FillSpeed ? 0 : Math.Min(MaxLeafLod, (int)Math.Ceiling(Math.Log2(speed / FillSpeed)));

    /// <param name="horizonMargin">Nodes are pruned only this far (m) past the horizon.</param>
    public void Select(double3 camera, int minLod, double horizonMargin, LeafSet output)
    {
        var start = Stopwatch.GetTimestamp();
        output.Clear();
        output.Camera = camera;
        output.MinLod = minLod;
        output.HorizonMargin = horizonMargin;
        _camera = camera;
        _minLod = minLod;
        _margin = horizonMargin;
        _horizon = Horizon(camera);

        for (var i = 0; i < 8; i++)
            Visit(new ChunkCoord(-1 + (i & 1), -1 + ((i >> 1) & 1), -1 + ((i >> 2) & 1), RootLod), double.MaxValue, output);

        CollectionsMarshal.AsSpan(output.Keys).Sort(CollectionsMarshal.AsSpan(output.Order));
        output.Ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <param name="parentTop">The parent's bound on its ground's distance from the centre: also a bound on this node's, so pruning nests.</param>
    private void Visit(ChunkCoord node, double parentTop, LeafSet output)
    {
        output.Visits++;
        var lod = node.Lod;
        var s = node.VoxelSize;
        var size = ChunkConstants.Size * s;
        var ox = node.X * size;
        var oy = node.Y * size;
        var oz = node.Z * size;
        var distance = Distance(ox, oy, oz, size);
        var rMax = MaxRadius(ox - s, oy - s, oz - s, ox + size, oy + size, oz + size);
        var top = (float)_maxHeight;
        var shell = lod <= MaxLeafLod ? Bounds.Classify(node, out top) : GlobalClass(ox - s, oy - s, oz - s, ox + size, oy + size, oz + size, rMax, lod);

        if (shell != NodeClass.Surface && (lod > MaxLeafLod || !Edits.Reaches(node, distance)))
        {
            output.Uniform++;
            return;
        }

        var topRadius = Math.Min(TopRadius(rMax, top, lod), parentTop);

        if (Beyond(distance - _margin, _horizon, topRadius))
        {
            output.Pruned++;
            return;
        }

        if (lod > MaxLeafLod || (lod > _minLod && distance < SplitFactor * size))
        {
            var child = lod - 1;

            if (lod <= MaxLeafLod)
                output.Internal.Add(node.Key);

            for (var i = 0; i < 8; i++)
                Visit(new ChunkCoord(node.X * 2 + (i & 1), node.Y * 2 + ((i >> 1) & 1), node.Z * 2 + ((i >> 2) & 1), child), topRadius, output);

            return;
        }

        output.Leaves.Add(node.Key);
        output.Order.Add(node);
        output.Keys.Add((float)(distance / s));
        output.PerLod[lod]++;
        output.FinestLod = Math.Min(output.FinestLod, lod);
    }

    /// <summary>
    /// The leaf provably shows nothing to <paramref name="camera"/>: it is below the horizon with no margin (its ground
    /// bounded by its own and its ancestors' bounds, as selection nests them), or the shell classifies everything inside
    /// it uniform down to <see cref="EmptyDepth"/> levels, so it meshes empty. What a hole check wants. Main thread,
    /// between edits; safe against a running <see cref="Select"/>.
    /// </summary>
    public bool ProvablyUnseen(ChunkCoord leaf, double3 camera)
    {
        var topRadius = double.MaxValue;

        for (var a = leaf; a.Lod <= MaxLeafLod; a = a.Parent)
        {
            Bounds.Classify(a, out var top);
            topRadius = Math.Min(topRadius, TopRadius(MaxRadius(a), top, a.Lod));
        }

        return Beyond(Distance(camera, leaf), Horizon(camera), topRadius) || Empty(leaf, camera, EmptyDepth + 1);
    }

    private bool Empty(ChunkCoord node, double3 camera, int depth)
    {
        if (Edits.Reaches(node, Distance(camera, node)))
            return false;

        if (Bounds.Classify(node) != NodeClass.Surface)
            return true;

        if (depth == 1 || node.Lod == 0)
            return false;

        for (var i = 0; i < 8; i++)
        {
            if (!Empty(new ChunkCoord(node.X * 2 + (i & 1), node.Y * 2 + ((i >> 1) & 1), node.Z * 2 + ((i >> 2) & 1), node.Lod - 1), camera, depth - 1))
                return false;
        }

        return true;
    }

    /// <summary>Bound on the distance from the centre of a node's ground: its box's far corner or the highest ground plus the saturation pad.</summary>
    private double TopRadius(double rMax, float top, int lod) => Math.Min(rMax, _radius + top + PlanetGenerator.Pad(Math.Min(lod, MaxLeafLod)));

    private static double MaxRadius(ChunkCoord node)
    {
        var s = node.VoxelSize;
        var size = ChunkConstants.Size * s;
        var ox = node.X * size;
        var oy = node.Y * size;
        var oz = node.Z * size;
        return MaxRadius(ox - s, oy - s, oz - s, ox + size, oy + size, oz + size);
    }

    private static double Distance(double3 camera, ChunkCoord node)
    {
        var size = ChunkConstants.Size * node.VoxelSize;
        return Distance(camera, node.X * size, node.Y * size, node.Z * size, size);
    }

    /// <summary>The camera's distance to its horizon over the occluder ball; -1 inside it.</summary>
    private double Horizon(double3 camera)
    {
        var c2 = double3.Dot(camera, camera);
        return c2 > _occluder * _occluder ? Math.Sqrt(c2 - _occluder * _occluder) : -1.0;
    }

    /// <summary>
    /// Every point of the node lies beyond the camera's horizon over the occluder ball: two points above a ball see each
    /// other only if they are at most the sum of their horizon distances apart.
    /// </summary>
    private bool Beyond(double distance, double horizon, double topRadius)
    {
        if (horizon < 0.0)
            return false;

        var h = topRadius > _occluder ? Math.Sqrt(topRadius * topRadius - _occluder * _occluder) : 0.0;
        return distance > horizon + h;
    }

    private double Distance(double ox, double oy, double oz, double size) => Distance(_camera, ox, oy, oz, size);

    private static double Distance(double3 camera, double ox, double oy, double oz, double size)
    {
        var dx = Outside(camera.X, ox, ox + size);
        var dy = Outside(camera.Y, oy, oy + size);
        var dz = Outside(camera.Z, oz, oz + size);
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static double Outside(double v, double lo, double hi) => v < lo ? lo - v : v > hi ? v - hi : 0.0;

    private static double MaxRadius(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var x = Math.Max(Math.Abs(x0), Math.Abs(x1));
        var y = Math.Max(Math.Abs(y0), Math.Abs(y1));
        var z = Math.Max(Math.Abs(z0), Math.Abs(z1));
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>Internal nodes (LOD 13-15): uniform only if wholly outside the relief band.</summary>
    private NodeClass GlobalClass(double x0, double y0, double z0, double x1, double y1, double z1, double rMax, int lod)
    {
        var nx = x0 > 0.0 ? x0 : x1 < 0.0 ? x1 : 0.0;
        var ny = y0 > 0.0 ? y0 : y1 < 0.0 ? y1 : 0.0;
        var nz = z0 > 0.0 ? z0 : z1 < 0.0 ? z1 : 0.0;
        var rMin = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        var pad = PlanetGenerator.Pad(MaxLeafLod);

        if (rMin - _radius > _maxHeight + pad)
            return NodeClass.Air;

        return rMax - _radius < _minHeight - pad ? NodeClass.Solid : NodeClass.Surface;
    }
}

/// <summary>Runs one <see cref="PlanetLeafSelector.Select"/> on a worker.</summary>
internal sealed class SelectJob : IJob
{
    public PlanetLeafSelector Selector = null!;
    public LeafSet Output = new();
    public double3 Camera;
    public int MinLod;
    public double HorizonMargin;

    public void Run() => Selector.Select(Camera, MinLod, HorizonMargin, Output);
}
