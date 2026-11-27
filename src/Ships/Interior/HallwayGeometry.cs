using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships.Interior;

public enum HallwayPreset : byte { Rectangle, Rounded, Octagon, Arch }

/// <summary>How a run of hallway meets its gravity: walk it, climb stairs, or it needs a ladder (or no gravity).</summary>
public enum RunKind : byte { Level, Ramp, Stairs, Shaft }

/// <summary>A profile's walkable size: the clear width a person can stand in, how tall that space is, and how many fit abreast.</summary>
public readonly record struct Clearance(double Width, double Height, int People)
{
    public bool Fits => Width >= HallwayGeometry.MinClearWidth - HallwayGeometry.Slack && Height >= HallwayGeometry.MinClearHeight - HallwayGeometry.Slack;
}

/// <summary>
/// The hallway rules and geometry, deterministic from the plan (every peer derives the same solids).
/// <list type="bullet">
/// <item>Clearance: a standing person needs <see cref="MinClearWidth"/> × <see cref="MinClearHeight"/> clear above the floor;
/// every further <see cref="LaneWidth"/> of clear width lets one more person pass at once.</item>
/// <item>Each run's floor is square to the hallway's gravity side to side. Up to <see cref="RampMaxDegrees"/> it is a ramp;
/// up to <see cref="StairMaxDegrees"/> it gets stairs (risers about <see cref="StepRise"/>); steeper is a shaft (a ladder or
/// zero-g crawlway, not built yet).</item>
/// <item>Expansion: each run is a closed prism of the profile, mitred at the nodes, cut out as a room (Room role on a layered
/// hull, so it gets a wall stack; Subtract otherwise). Stairs are added after every cut (Panel role on a layered hull, so
/// the room cut does not remove them).</item>
/// </list>
/// </summary>
public static class HallwayGeometry
{
    public const double MinClearWidth = 0.8;
    public const double MinClearHeight = 1.9;
    public const double LaneWidth = 0.8;
    public const double RampMaxDegrees = 20.0;
    public const double StairMaxDegrees = 50.0;
    public const double StepRise = 0.18;
    public const double StairUnder = 0.05;

    /// <summary>Allowance for the 1/1024 m grid when comparing clear sizes (2 mm).</summary>
    public const double Slack = 0.002;
    public const uint CutIdBase = 0xD0000000;
    public const uint StairIdBase = 0xD8000000;
    private const double LevelDegrees = 0.5;
    private const double MaxPieceMetres = 128.0;
    private const double SampleMetres = 0.01;

    public static readonly string[] PresetNames = ["Rectangle", "Rounded", "Octagon", "Arch"];

    /// <summary>A preset outline <paramref name="width"/> × <paramref name="height"/> metres, floor at Y = 0, centred on X = 0, counter-clockwise.</summary>
    public static List<ProfilePoint> Preset(HallwayPreset preset, double width, double height)
    {
        var w = width * 0.5;
        var points = new List<(double X, double Y)>();

        switch (preset)
        {
            case HallwayPreset.Rounded:
            {
                var r = Math.Min(width, height) * 0.25;
                points.Add((-w, 0));
                points.Add((w, 0));
                Arc(points, w - r, height - r, r, 0, 90);
                Arc(points, -w + r, height - r, r, 90, 180);
                break;
            }
            case HallwayPreset.Octagon:
            {
                var c = Math.Min(width, height) * 0.25;
                points.AddRange([(-w + c, 0), (w - c, 0), (w, c), (w, height - c), (w - c, height), (-w + c, height), (-w, height - c), (-w, c)]);
                break;
            }
            case HallwayPreset.Arch:
            {
                var spring = Math.Max(height - w, height * 0.5);
                var rise = height - spring;
                points.Add((-w, 0));
                points.Add((w, 0));

                for (var i = 0; i <= 8; i++)
                {
                    var a = i * Math.PI / 8;
                    points.Add((w * Math.Cos(a), spring + rise * Math.Sin(a)));
                }

                break;
            }
            default:
                points.AddRange([(-w, 0), (w, 0), (w, height), (-w, height)]);
                break;
        }

        var result = new List<ProfilePoint>(points.Count);

        foreach (var (x, y) in points)
        {
            var p = new ProfilePoint((int)Math.Round(x * HullSolid.PositionUnitsPerMetre), (int)Math.Round(y * HullSolid.PositionUnitsPerMetre));

            if (result.Count == 0 || result[^1] != p)
                result.Add(p);
        }

        if (result.Count > 1 && result[0] == result[^1])
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static void Arc(List<(double X, double Y)> points, double cx, double cy, double r, double from, double to)
    {
        for (var i = 0; i <= 4; i++)
        {
            var a = (from + (to - from) * i / 4.0) * Math.PI / 180.0;
            points.Add((cx + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
    }

    /// <summary>Twice the signed area (1/1024 m units squared); positive when counter-clockwise.</summary>
    public static long Area2(IReadOnlyList<ProfilePoint> profile)
    {
        var area = 0L;

        for (var j = 0; j < profile.Count; j++)
        {
            var a = profile[j];
            var b = profile[(j + 1) % profile.Count];
            area += (long)a.X * b.Y - (long)b.X * a.Y;
        }

        return area;
    }

    /// <summary>
    /// The clear space a person can stand in: from the floor (the profile's lowest Y) up, the widest span inside the
    /// outline around the floor's middle that stays clear all the way up. Width is measured over the first
    /// <see cref="MinClearHeight"/>; Height is how far up at least <see cref="MinClearWidth"/> stays clear.
    /// </summary>
    public static Clearance Measure(IReadOnlyList<ProfilePoint> profile)
    {
        if (profile.Count < HullProfile.MinPoints || Area2(profile) == 0)
            return default;

        const double k = HullSolid.MetresPerPositionUnit;
        double floor = double.MaxValue, top = double.MinValue;

        foreach (var p in profile)
        {
            floor = Math.Min(floor, p.Y * k);
            top = Math.Max(top, p.Y * k);
        }

        var crossings = new List<double>();
        double lo = double.MinValue, hi = double.MaxValue, centre = double.NaN, width = 0, height = 0;

        for (var y = floor + SampleMetres * 0.5; y < top; y += SampleMetres)
        {
            Crossings(profile, y, crossings);
            var found = false;

            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                var (a, b) = (crossings[i], crossings[i + 1]);

                if (double.IsNaN(centre))
                    centre = (a + b) * 0.5;

                if (a <= centre && centre <= b)
                {
                    (lo, hi) = (Math.Max(lo, a), Math.Min(hi, b));
                    found = true;
                    break;
                }
            }

            if (!found || hi - lo < MinClearWidth - Slack)
                break;

            height = y - floor + SampleMetres * 0.5;

            if (height <= MinClearHeight + 1e-9)
                width = hi - lo;
        }

        var people = height >= MinClearHeight - Slack ? (int)Math.Floor((width + Slack) / LaneWidth) : 0;
        return new Clearance(width, height, people);
    }

    private static void Crossings(IReadOnlyList<ProfilePoint> profile, double y, List<double> output)
    {
        const double k = HullSolid.MetresPerPositionUnit;
        output.Clear();

        for (var j = 0; j < profile.Count; j++)
        {
            double ax = profile[j].X * k, ay = profile[j].Y * k;
            var b = profile[(j + 1) % profile.Count];
            double bx = b.X * k, by = b.Y * k;

            if ((ay <= y) == (by <= y))
                continue;

            output.Add(ax + (y - ay) / (by - ay) * (bx - ax));
        }

        output.Sort();
    }

    /// <summary>Down for a gravity rotation (the rotation applied to -Y).</summary>
    public static Vector3d Down(uint gravity)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(gravity, m);
        return new Vector3d(-m[1], -m[4], -m[7]);
    }

    /// <summary>How steep a run from <paramref name="a"/> to <paramref name="b"/> is against <paramref name="down"/>, degrees.</summary>
    public static double Slope(Vector3d a, Vector3d b, Vector3d down)
    {
        var d = Sub(b, a);
        var rise = -Dot(d, down);
        var run = Sub(d, Scale(down, -rise)).Length();
        return Math.Atan2(Math.Abs(rise), run) * 180.0 / Math.PI;
    }

    public static RunKind Kind(double slopeDegrees) =>
        slopeDegrees <= LevelDegrees ? RunKind.Level : slopeDegrees <= RampMaxDegrees ? RunKind.Ramp : slopeDegrees <= StairMaxDegrees ? RunKind.Stairs : RunKind.Shaft;

    /// <summary>A run's cross-section frame: up square to the run and as close to against gravity as it can be, across = up × along.</summary>
    public static (Vector3d Across, Vector3d Up) Frame(Vector3d along, Vector3d down, uint gravity)
    {
        var up0 = Scale(down, -1);
        var up = Sub(up0, Scale(along, Dot(up0, along)));

        if (up.Length() < 1e-6)
        {
            // A shaft along gravity: keep the gravity frame's front as the profile's up.
            Span<double> m = stackalloc double[9];
            SolidRotation.Matrix(gravity, m);
            var front = new Vector3d(m[2], m[5], m[8]);
            up = Sub(front, Scale(along, Dot(front, along)));
        }

        up = Normalise(up, new Vector3d(0, 1, 0));
        return (Cross(up, along), up);
    }

    /// <summary>The solids every hallway expands to, in order (all cuts, then all stairs), and their meshes.</summary>
    public sealed class Expansion
    {
        public readonly List<HullSolid> Solids = new();
        public readonly List<HullMesh> Meshes = new();

        /// <summary>Runs that are too steep for stairs (shafts), counted for the stats.</summary>
        public int Shafts;
    }

    /// <summary>
    /// Expands <paramref name="interior"/>'s hallways into mesh solids whose Param indexes a mesh table that already holds
    /// <paramref name="meshBase"/> meshes (the plan's own); append <see cref="Expansion.Meshes"/> after them.
    /// </summary>
    public static Expansion Expand(HullInterior? interior, bool layered, int meshBase)
    {
        var result = new Expansion();

        if (interior is null)
            return result;

        var stairs = new List<HullSolid>();
        var stairMeshes = new List<HullMesh>();

        for (var h = 0; h < interior.Hallways.Count; h++)
        {
            var hallway = interior.Hallways[h];

            var profile = interior.ProfileOf(hallway)?.Points;

            if (profile is null || hallway.Runs.Count == 0 || profile.Count < HullProfile.MinPoints || Area2(profile) == 0)
                continue;

            Cuts(hallway, profile, h, layered, meshBase, result);
            Stairs(hallway, profile, h, layered, stairs, stairMeshes, out var shafts);
            result.Shafts += shafts;
        }

        // Stairs index the table after every cut's mesh.
        for (var i = 0; i < stairs.Count; i++)
        {
            var solid = stairs[i];
            solid.Param = (ushort)(meshBase + result.Meshes.Count + i);
            result.Solids.Add(solid);
        }

        result.Meshes.AddRange(stairMeshes);
        return result;
    }

    private static void Cuts(HullHallway hallway, List<ProfilePoint> outline, int index, bool layered, int meshBase, Expansion result)
    {
        const double k = HullSolid.MetresPerPositionUnit;
        var down = Down(hallway.Gravity);
        var profile = new (double X, double Y)[outline.Count];
        var reach = 0.0;

        for (var j = 0; j < profile.Length; j++)
        {
            profile[j] = (outline[j].X * k, outline[j].Y * k);
            reach = Math.Max(reach, Math.Abs(profile[j].X));
        }

        var template = new HullSolid
        {
            Shape = SolidShape.Mesh,
            Op = layered ? SolidOp.Add : SolidOp.Subtract,
            Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Room) : SolidFlags.None,
            Material = hallway.Material,
            Rotation = SolidRotation.Identity,
        };
        var piece = 0;

        foreach (var (na, nb) in hallway.Runs)
        {
            if (na >= hallway.Nodes.Count || nb >= hallway.Nodes.Count)
                continue;

            var a = hallway.Node(na);
            var b = hallway.Node(nb);
            var along = Sub(b, a);
            var length = along.Length();

            if (length < 1e-3)
                continue;

            along = Scale(along, 1.0 / length);
            var (across, up) = Frame(along, down, hallway.Gravity);

            // Joints: mitred where exactly two runs meet; at a junction each run reaches past the node by half the
            // profile's width so the runs overlap into one space; a free end is square.
            var (startPlane, startReach) = Joint(hallway, na, nb, along, reach);
            var (endPlane, endReach) = Joint(hallway, nb, na, Scale(along, -1), reach);
            endPlane = Scale(endPlane, -1);
            a = Sub(a, Scale(along, startReach));
            b = Add(b, Scale(along, endReach));
            length += startReach + endReach;
            var pieces = Math.Max(1, (int)Math.Ceiling(length / MaxPieceMetres));

            for (var p = 0; p < pieces; p++)
            {
                var from = Add(a, Scale(along, length * p / pieces));
                var to = Add(a, Scale(along, length * (p + 1) / pieces));
                var ring0 = Ring(profile, from, across, up, along, p == 0 ? startPlane : along);
                var ring1 = Ring(profile, to, across, up, along, p == pieces - 1 ? endPlane : along);

                if (Prism(ring0, ring1, along, out var anchor) is { } mesh && piece < 4096)
                {
                    var solid = template;
                    solid.Id = CutIdBase | ((uint)index << 12) | (uint)piece++;
                    (solid.PosX, solid.PosY, solid.PosZ) = anchor;
                    solid.Param = (ushort)(meshBase + result.Meshes.Count);
                    (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
                    result.Meshes.Add(mesh);
                    result.Solids.Add(solid);
                }
            }
        }
    }

    /// <summary>
    /// The joint where a run leaves <paramref name="node"/> along <paramref name="away"/> (towards <paramref name="far"/>):
    /// the plane's normal (pointing along the run) and how far the run reaches back past the node. Two runs: the plane
    /// halfway between them (square if the turn is too sharp to mitre). A junction: square, reaching back half the
    /// profile's width. A free end: square, no reach.
    /// </summary>
    private static (Vector3d Plane, double Reach) Joint(HullHallway hallway, int node, int far, Vector3d away, double reach)
    {
        var degree = hallway.Degree(node);

        if (degree >= 3)
            return (away, reach);

        if (degree != 2)
            return (away, 0.0);

        foreach (var (a, b) in hallway.Runs)
        {
            var other = a == node ? b : b == node ? a : -1;

            if (other < 0 || other == far)
                continue;

            var inDir = Normalise(Sub(hallway.Node(node), hallway.Node(other)), away);

            if (Dot(inDir, away) < -0.5)
                return (away, 0.0);

            return (Normalise(Add(inDir, away), away), 0.0);
        }

        return (away, 0.0);
    }

    /// <summary>The profile at <paramref name="centre"/> in the run's frame, slid along the run onto the plane with normal <paramref name="plane"/>.</summary>
    private static Vector3d[] Ring((double X, double Y)[] profile, Vector3d centre, Vector3d across, Vector3d up, Vector3d along, Vector3d plane)
    {
        var ring = new Vector3d[profile.Length];
        var denominator = Dot(along, plane);

        for (var j = 0; j < profile.Length; j++)
        {
            var p = Add(centre, Add(Scale(across, profile[j].X), Scale(up, profile[j].Y)));
            var t = Math.Abs(denominator) > 1e-6 ? Dot(Sub(centre, p), plane) / denominator : 0.0;
            ring[j] = Add(p, Scale(along, t));
        }

        return ring;
    }

    /// <summary>
    /// A closed prism between two rings of the same outline, wound outward (the rings' Newell normal is turned to point along
    /// <paramref name="along"/>, the sweep convention). Null when it would exceed the mesh coordinate limit.
    /// </summary>
    public static HullMesh? Prism(Vector3d[] ring0, Vector3d[] ring1, Vector3d along, out (int X, int Y, int Z) anchor)
    {
        var count = ring0.Length;
        var normal = new Vector3d(0, 0, 0);

        for (var j = 0; j < count; j++)
        {
            var a = ring0[j];
            var b = ring0[(j + 1) % count];
            normal = Add(normal, new Vector3d((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y)));
        }

        var flip = Dot(normal, along) < 0;
        anchor = Quantise(ring0[0]);
        var origin = new Vector3d(anchor.X * HullSolid.MetresPerPositionUnit, anchor.Y * HullSolid.MetresPerPositionUnit, anchor.Z * HullSolid.MetresPerPositionUnit);
        var builder = new MeshBuilder();

        foreach (var ring in (ReadOnlySpan<Vector3d[]>)[ring0, ring1])
        {
            for (var j = 0; j < count; j++)
            {
                var q = Quantise(Sub(ring[flip ? count - 1 - j : j], origin));

                if (Math.Abs(q.X) >= HullMesh.MaxCoordinate || Math.Abs(q.Y) >= HullMesh.MaxCoordinate || Math.Abs(q.Z) >= HullMesh.MaxCoordinate)
                    return null;

                builder.AddVertex(new MeshVertex(q.X, q.Y, q.Z));
            }
        }

        // MeshBuilder keeps the corner arrays, so each face gets its own.
        for (var j = 0; j < count; j++)
        {
            var jn = (j + 1) % count;
            builder.AddFace([j, jn, count + jn, count + j], 0);
        }

        var start = new int[count];
        var end = new int[count];

        for (var j = 0; j < count; j++)
        {
            start[j] = count - 1 - j;
            end[j] = count + j;
        }

        builder.AddFace(start, 0);
        builder.AddFace(end, 0);
        return builder.Build();
    }

    /// <summary>Steps on every run between ramp and shaft steepness: one convex block per step, sitting on the run's floor.</summary>
    private static void Stairs(HullHallway hallway, List<ProfilePoint> profile, int index, bool layered, List<HullSolid> output, List<HullMesh> meshes, out int shafts)
    {
        shafts = 0;
        var down = Down(hallway.Gravity);
        var up0 = Scale(down, -1);
        var (lo, hi) = FloorSpan(profile);

        if (hi - lo < 1e-3)
            return;

        var template = new HullSolid
        {
            Shape = SolidShape.Mesh,
            Op = SolidOp.Add,
            Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Panel) : SolidFlags.None,
            Material = hallway.Material,
            Rotation = SolidRotation.Identity,
        };
        var step = 0;

        foreach (var (na, nb) in hallway.Runs)
        {
            if (na >= hallway.Nodes.Count || nb >= hallway.Nodes.Count)
                continue;

            var a = hallway.Node(na);
            var b = hallway.Node(nb);
            var kind = Kind(Slope(a, b, down));

            if (kind == RunKind.Shaft)
                shafts++;

            if (kind != RunKind.Stairs)
                continue;

            // From the lower end up: u runs level along the run, v up against gravity, x across.
            var (low, high) = Dot(Sub(b, a), up0) >= 0 ? (a, b) : (b, a);
            var d = Sub(high, low);
            var rise = Dot(d, up0);
            var level = Sub(d, Scale(up0, rise));
            var run = level.Length();
            var u = Scale(level, 1.0 / run);
            var along = Normalise(d, u);
            var (across, _) = Frame(along, down, hallway.Gravity);
            var n = Math.Max(1, (int)Math.Round(rise / StepRise));
            double r = rise / n, w = run / n;

            for (var i = 0; i < n && step < 4096; i++)
            {
                (double U, double V)[] outline = [(i * w, i * r - StairUnder), ((i + 1) * w, (i + 1) * r - StairUnder), ((i + 1) * w, (i + 1) * r), (i * w, (i + 1) * r)];
                var ring0 = new Vector3d[4];
                var ring1 = new Vector3d[4];

                for (var j = 0; j < 4; j++)
                {
                    var p = Add(low, Add(Scale(u, outline[j].U), Scale(up0, outline[j].V)));
                    ring0[j] = Add(p, Scale(across, lo));
                    ring1[j] = Add(p, Scale(across, hi));
                }

                if (Prism(ring0, ring1, across, out var anchor) is not { } mesh)
                    continue;

                var solid = template;
                solid.Id = StairIdBase | ((uint)index << 12) | (uint)step++;
                (solid.PosX, solid.PosY, solid.PosZ) = anchor;
                (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
                meshes.Add(mesh);
                output.Add(solid);
            }
        }
    }

    /// <summary>The profile's floor: its span across, just above its lowest point (metres).</summary>
    public static (double Lo, double Hi) FloorSpan(IReadOnlyList<ProfilePoint> profile)
    {
        const double k = HullSolid.MetresPerPositionUnit;
        var floor = double.MaxValue;

        foreach (var p in profile)
            floor = Math.Min(floor, p.Y * k);

        var crossings = new List<double>();
        Crossings(profile, floor + SampleMetres * 0.5, crossings);
        return crossings.Count >= 2 ? (crossings[0], crossings[^1]) : (0, 0);
    }

    private static (int X, int Y, int Z) Quantise(Vector3d v) => (
        (int)Math.Round(v.X * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Y * HullSolid.PositionUnitsPerMetre),
        (int)Math.Round(v.Z * HullSolid.PositionUnitsPerMetre));
}
