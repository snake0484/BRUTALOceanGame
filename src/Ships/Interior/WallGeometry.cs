namespace BrutalGame.Ships.Interior;

public enum WallPreset : byte { Straight, Chamfered, Coved, Sloped }

/// <summary>
/// Wall profiles and the rooms that use them, deterministic from the plan. A wall profile is one wall's section from floor
/// to ceiling (X: how far in from the room's side, Y: height); an upright room using one (a box, a cylinder, or a mesh
/// standing straight up) is cut as a stack of its footprint, each shrunk by the profile at that height, so every wall gets
/// the same section. The profile's lower half keeps to the
/// floor and its upper half to the ceiling, so a taller room stretches the middle; a room lower than the profile squeezes
/// it to fit.
/// </summary>
public static class WallGeometry
{
    public const uint IdBase = 0xD4000000;
    public static readonly string[] PresetNames = ["Straight", "Chamfered", "Coved", "Sloped"];

    /// <summary>The shortest edge a mesh may have (1/1024 m), as the mesh validator wants.</summary>
    private const int MinEdge = 16;
    private const double MinRoomMetres = 0.05;

    /// <summary>The shortest side a shrunk footprint may keep (metres), clear of the mesh edge limit.</summary>
    private const double MinSide = MinEdge * 1.5 * HullSolid.MetresPerPositionUnit;

    /// <summary>2-32 points in range, X ≥ 0, starting on the floor, never going down, ending above it.</summary>
    public static bool IsValid(IReadOnlyList<ProfilePoint> points)
    {
        if (points.Count is < HullProfile.MinWallPoints or > HullProfile.MaxPoints || points[0].Y != 0 || points[^1].Y <= 0)
            return false;

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];

            if (p.X < 0 || p.X > HullProfile.MaxUnits || p.Y > HullProfile.MaxUnits || (i > 0 && p.Y < points[i - 1].Y))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A solid that can use a wall profile: cut as a room (Room role, or a subtract) and upright: a box, a cylinder, or a
    /// mesh standing straight up (<see cref="Prism"/>; <paramref name="meshes"/> is the plan's mesh table).
    /// </summary>
    public static bool IsRoom(in HullSolid solid, IReadOnlyList<HullMesh> meshes) =>
        (SolidRoles.Of(solid) == SolidRole.Room || solid.Op == SolidOp.Subtract) && solid.Shape switch
        {
            SolidShape.Box or SolidShape.Cylinder => true,
            SolidShape.Mesh => solid.Param < meshes.Count && Prism(meshes[solid.Param]) is not null,
            _ => false,
        };

    /// <summary>A preset wall <paramref name="height"/> metres tall with features <paramref name="size"/> metres across.</summary>
    public static List<ProfilePoint> Preset(WallPreset preset, double height, double size)
    {
        var points = new List<(double X, double Y)>();
        size = Math.Clamp(size, 0.05, height * 0.45);

        switch (preset)
        {
            case WallPreset.Chamfered:
                points.AddRange([(size, 0), (0, size), (0, height - size), (size, height)]);
                break;
            case WallPreset.Coved:
                points.AddRange([(0, 0), (0, height - size)]);

                for (var i = 1; i <= 4; i++)
                {
                    var a = i * Math.PI / 8;
                    points.Add((size - size * Math.Cos(a), height - size + size * Math.Sin(a)));
                }

                break;
            case WallPreset.Sloped:
                points.AddRange([(0, 0), (0, height * 0.5), (size, height)]);
                break;
            default:
                points.AddRange([(0, 0), (0, height)]);
                break;
        }

        return Quantised(points);
    }

    /// <summary>
    /// The right half of a hallway outline as a wall: from its floor up its right side to the top, X measured in from the
    /// outline's widest point. Null when the outline has no such side.
    /// </summary>
    public static List<ProfilePoint>? FromHallway(IReadOnlyList<ProfilePoint> outline)
    {
        if (outline.Count < HullProfile.MinPoints || HallwayGeometry.Area2(outline) == 0)
            return null;

        var ccw = HallwayGeometry.Area2(outline) > 0;
        var n = outline.Count;
        ProfilePoint At(int i) => outline[ccw ? ((i % n) + n) % n : (n - 1 - ((i % n) + n) % n)];

        // Start at the lowest point furthest right; counter-clockwise from there climbs the right side.
        var start = 0;

        for (var i = 1; i < n; i++)
        {
            var p = At(i);
            var s = At(start);

            if (p.Y < s.Y || (p.Y == s.Y && p.X > s.X))
                start = i;
        }

        var floor = At(start).Y;
        var side = new List<ProfilePoint> { At(start) };

        for (var i = 1; i < n; i++)
        {
            var p = At(start + i);
            var last = side[^1];

            if (p.Y < last.Y || p.X < 0)
            {
                // The top crosses the middle: end there.
                if (p.X < 0 && last.X > 0 && p.Y >= last.Y)
                    side.Add(new ProfilePoint(0, (int)Math.Round(last.Y + (double)(p.Y - last.Y) * last.X / (last.X - p.X))));

                break;
            }

            side.Add(p);
        }

        var widest = side.Max(p => p.X);
        var wall = new List<ProfilePoint>(side.Count);

        foreach (var p in side)
        {
            var q = new ProfilePoint(widest - p.X, p.Y - floor);

            if (wall.Count == 0 || wall[^1] != q)
                wall.Add(q);
        }

        // A flat ceiling ends where the side meets it: the rest is the room's own ceiling.
        while (wall.Count > 2 && wall[^1].Y == wall[^2].Y)
            wall.RemoveAt(wall.Count - 1);

        return IsValid(wall) ? wall : null;
    }

    /// <summary>The solids every room with a wall profile is cut as instead of its own shape, keyed by its index in the solid list.</summary>
    public sealed class Expansion
    {
        public readonly List<(int Index, HullSolid Solid)> Rooms = new();
        public readonly List<HullMesh> Meshes = new();
    }

    /// <summary>
    /// Each room in <paramref name="interior"/>'s room walls, as a mesh solid in its place (same id, op, role, material and
    /// position; its rotation baked into the mesh) whose Param indexes a mesh table that already holds <paramref name="meshBase"/>
    /// meshes. <paramref name="meshes"/> is the plan's own table (mesh rooms read their shape from it).
    /// </summary>
    public static Expansion Expand(HullInterior? interior, ReadOnlySpan<HullSolid> solids, IReadOnlyList<HullMesh> meshes, int meshBase)
    {
        var result = new Expansion();

        if (interior is null)
            return result;

        foreach (var wall in interior.RoomWalls)
        {
            if (wall.Profile >= interior.Profiles.Count || interior.Profiles[wall.Profile] is not { Kind: ProfileKind.Wall } profile || !IsValid(profile.Points))
                continue;

            var index = IndexOf(solids, wall.Solid);

            if (index < 0 || !IsRoom(solids[index], meshes) || Room(solids[index], profile.Points, meshes) is not { } mesh)
                continue;

            var solid = solids[index];
            solid.Shape = SolidShape.Mesh;
            solid.Bevel = 0;
            solid.Rotation = SolidRotation.Identity;
            solid.Param = (ushort)(meshBase + result.Meshes.Count);
            (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
            result.Meshes.Add(mesh);
            result.Rooms.Add((index, solid));
        }

        return result;
    }

    /// <summary>
    /// The room inset by <paramref name="wall"/>, about its position, turned by its rotation: a stack of its footprint shrunk
    /// by the profile at each height. Null when it is not a room shape or has no room left.
    /// </summary>
    public static HullMesh? Room(in HullSolid solid, IReadOnlyList<ProfilePoint> wall, IReadOnlyList<HullMesh>? meshes = null)
    {
        const double k = HullSolid.MetresPerPositionUnit;

        if (Footprint.Of(solid, meshes, wall.Count) is not { } footprint)
            return null;

        var room = footprint.Ceiling - footprint.Floor;
        var tall = wall[^1].Y * k;
        var limit = footprint.MaxInset;

        if (limit <= 0 || room <= 0)
            return null;

        // Rings: (inset, height) in 1/1024 m, the floor half kept to the floor, the ceiling half to the ceiling.
        var rings = new List<(int Inset, int Y)>(wall.Count);

        foreach (var p in wall)
        {
            var y = p.Y * k;
            var height = room >= tall ? (y <= tall * 0.5 ? y : room - (tall - y)) : y * room / tall;
            var ring = ((int)Math.Round(Math.Min(p.X * k, limit) / k), (int)Math.Round((height + footprint.Floor) / k));

            if (rings.Count > 0)
            {
                var last = rings[^1];

                if (Math.Abs(ring.Item2 - last.Y) < MinEdge)
                {
                    if (Math.Abs(ring.Item1 - last.Inset) < MinEdge)
                        continue;

                    ring.Item2 = last.Y;
                }
            }

            rings.Add(ring);
        }

        // A ledge on the floor or ceiling is the cap's: keep the ring that meets the wall.
        while (rings.Count > 2 && rings[0].Y == rings[1].Y)
            rings.RemoveAt(0);

        while (rings.Count > 2 && rings[^1].Y == rings[^2].Y)
            rings.RemoveAt(rings.Count - 1);

        if (rings.Count < 2 || rings[^1].Y == rings[0].Y)
            return null;

        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(solid.Rotation, m);
        var builder = new MeshBuilder();
        var n = footprint.Count;

        foreach (var (inset, y) in rings)
        {
            foreach (var (x, z) in footprint.At(inset * k))
                builder.AddVertex(Turned(m, (int)Math.Round(x / k), y, (int)Math.Round(z / k)));
        }

        for (var r = 0; r + 1 < rings.Count; r++)
        {
            for (var j = 0; j < n; j++)
            {
                var jn = (j + 1) % n;
                builder.AddFace([r * n + j, r * n + jn, (r + 1) * n + jn, (r + 1) * n + j], 0);
            }
        }

        var top = (rings.Count - 1) * n;
        Cap(builder, Enumerable.Range(0, n).Reverse().ToArray());
        Cap(builder, Enumerable.Range(top, n).ToArray());
        return builder.Build();
    }

    /// <summary>A cap face, fanned into pieces of at most <see cref="HullMesh.MaxCorners"/> when it has more (only convex footprints do).</summary>
    private static void Cap(MeshBuilder builder, int[] ring)
    {
        if (ring.Length <= HullMesh.MaxCorners)
        {
            builder.AddFace(ring, 0);
            return;
        }

        for (var a = 1; a < ring.Length - 1;)
        {
            var b = Math.Min(a + HullMesh.MaxCorners - 2, ring.Length - 1);
            var piece = new int[b - a + 2];
            piece[0] = ring[0];

            for (var i = a; i <= b; i++)
                piece[i - a + 1] = ring[i];

            builder.AddFace(piece, 0);
            a = b;
        }
    }

    private static MeshVertex Turned(ReadOnlySpan<double> m, int x, int y, int z) => new(
        (int)Math.Round(m[0] * x + m[1] * y + m[2] * z),
        (int)Math.Round(m[3] * x + m[4] * y + m[5] * z),
        (int)Math.Round(m[6] * x + m[7] * y + m[8] * z));

    /// <summary>
    /// A room's plan in its own frame (metres): an outline round local Y, wound clockwise in X-Z (as the box's corners
    /// -X-Z, -X+Z, +X+Z, +X-Z), that shrinks inward by an inset, between floor and ceiling heights. A circle shrinks its
    /// radius; a polygon moves each side in, corners mitred.
    /// </summary>
    private sealed class Footprint
    {
        public double Floor;
        public double Ceiling;
        public double MaxInset;
        public double Radius;
        public int Sides;
        public (double X, double Z)[]? Outline;
        private (double X, double Z)[]? _miters;

        public int Count => Outline?.Length ?? Sides;

        public static Footprint? Of(in HullSolid solid, IReadOnlyList<HullMesh>? meshes, int rings)
        {
            const double k = HullSolid.MetresPerPositionUnit;
            var hy = solid.HalfY * HullSolid.MetresPerHalfUnit;

            switch (solid.Shape)
            {
                case SolidShape.Box:
                {
                    var hx = solid.HalfX * HullSolid.MetresPerHalfUnit;
                    var hz = solid.HalfZ * HullSolid.MetresPerHalfUnit;
                    return Polygon([(-hx, -hz), (-hx, hz), (hx, hz), (hx, -hz)], -hy, hy);
                }
                case SolidShape.Cylinder:
                {
                    // The exact surface's sides, as many as the mesh limits allow for this many rings.
                    var r = solid.HalfX * HullSolid.MetresPerHalfUnit;
                    var sides = Math.Min(ExactSurface.SidesFor(r), Math.Min(HullMesh.MaxVertices / rings, (HullMesh.MaxFaces - 64) / Math.Max(1, rings - 1)));
                    var smallest = Math.Max(MinRoomMetres, MinSide * sides / Math.Tau);
                    return sides < 8 ? null : new Footprint { Floor = -hy, Ceiling = hy, Radius = r, Sides = sides, MaxInset = r - smallest };
                }
                case SolidShape.Mesh when meshes is not null && solid.Param < meshes.Count:
                {
                    if (Prism(meshes[solid.Param]) is not { } prism)
                        return null;

                    var outline = prism.Outline.Select(p => (p.X * k, p.Z * k)).ToArray();
                    return Polygon(outline, prism.Floor * k, prism.Ceiling * k);
                }
                default:
                    return null;
            }
        }

        /// <summary>The outline shrunk by <paramref name="inset"/> metres.</summary>
        public (double X, double Z)[] At(double inset)
        {
            var ring = new (double X, double Z)[Count];

            if (Outline is null)
            {
                // The exact cylinder's angles, in the clockwise order.
                for (var i = 0; i < Sides; i++)
                {
                    var angle = (Sides - i) % Sides * Math.Tau / Sides;
                    ring[i] = (Math.Cos(angle) * (Radius - inset), Math.Sin(angle) * (Radius - inset));
                }

                return ring;
            }

            for (var i = 0; i < ring.Length; i++)
                ring[i] = (Outline[i].X + _miters![i].X * inset, Outline[i].Z + _miters[i].Z * inset);

            return ring;
        }

        private static Footprint? Polygon((double X, double Z)[] outline, double floor, double ceiling)
        {
            var n = outline.Length;
            var area = 0.0;

            for (var i = 0; i < n; i++)
                area += outline[i].X * outline[(i + 1) % n].Z - outline[(i + 1) % n].X * outline[i].Z;

            if (Math.Abs(area) < 1e-9)
                return null;

            if (area > 0)
                Array.Reverse(outline);

            // Each corner moves along its mitre: inward normals (right of each side) summed over 1 + their dot.
            var miters = new (double X, double Z)[n];

            for (var i = 0; i < n; i++)
            {
                var (ax, az) = Inward(outline[(i + n - 1) % n], outline[i]);
                var (bx, bz) = Inward(outline[i], outline[(i + 1) % n]);
                var dot = 1.0 + ax * bx + az * bz;

                if (dot < 1e-3)
                    return null;

                miters[i] = ((ax + bx) / dot, (az + bz) / dot);
            }

            var footprint = new Footprint { Floor = floor, Ceiling = ceiling, Outline = outline, _miters = miters };
            footprint.MaxInset = footprint.Deepest();
            return footprint;
        }

        private static (double X, double Z) Inward((double X, double Z) a, (double X, double Z) b)
        {
            double dx = b.X - a.X, dz = b.Z - a.Z, length = Math.Sqrt(dx * dx + dz * dz);
            return length < 1e-9 ? (0, 0) : (dz / length, -dx / length);
        }

        /// <summary>The deepest inset that keeps every side at least <see cref="MinRoomMetres"/> × 2 long (shorter sides half their length) and the outline simple.</summary>
        private double Deepest()
        {
            var outline = Outline!;
            var n = outline.Length;
            var deepest = double.MaxValue;

            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                double dx = outline[j].X - outline[i].X, dz = outline[j].Z - outline[i].Z, length = Math.Sqrt(dx * dx + dz * dz);
                var shrink = -((_miters![j].X - _miters[i].X) * dx + (_miters[j].Z - _miters[i].Z) * dz) / length;
                var keep = Math.Max(MinSide, Math.Min(MinRoomMetres * 2, length * 0.5));

                if (shrink > 1e-9)
                    deepest = Math.Min(deepest, Math.Max(0, (length - keep) / shrink));
            }

            if (deepest == double.MaxValue)
                deepest = 0;

            // A concave outline can fold over itself before a side runs out: back off until it does not.
            if (!Simple(At(deepest)))
            {
                double lo = 0, hi = deepest;

                for (var step = 0; step < 24; step++)
                {
                    var mid = (lo + hi) * 0.5;
                    (lo, hi) = Simple(At(mid)) ? (mid, hi) : (lo, mid);
                }

                deepest = lo;
            }

            return deepest;
        }

        private static bool Simple((double X, double Z)[] ring)
        {
            var n = ring.Length;

            for (var i = 0; i < n; i++)
            {
                for (var j = i + 2; j < n; j++)
                {
                    if (i == 0 && j == n - 1)
                        continue;

                    if (Cross(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n]))
                        return false;
                }
            }

            return true;
        }

        private static bool Cross((double X, double Z) a, (double X, double Z) b, (double X, double Z) c, (double X, double Z) d)
        {
            static double Side((double X, double Z) p, (double X, double Z) q, (double X, double Z) r) => (q.X - p.X) * (r.Z - p.Z) - (q.Z - p.Z) * (r.X - p.X);
            return Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;
        }
    }

    /// <summary>
    /// A mesh standing upright in its own frame, read as a room: every vertex on its floor or ceiling, one floor face and
    /// one ceiling face, each ceiling corner straight above a floor corner, not smoothed. Its floor outline and heights
    /// (1/1024 m), or null.
    /// </summary>
    public static (List<(int X, int Z)> Outline, int Floor, int Ceiling)? Prism(HullMesh mesh)
    {
        if (mesh.SubdivLevels > 0 || mesh.Vertices.Length < 6)
            return null;

        int floor = int.MaxValue, ceiling = int.MinValue;

        foreach (var v in mesh.Vertices)
            (floor, ceiling) = (Math.Min(floor, v.Y), Math.Max(ceiling, v.Y));

        if (ceiling - floor < MinEdge)
            return null;

        int bottom = -1, top = -1;

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var onFloor = true;
            var onCeiling = true;

            foreach (var c in mesh.Face(f))
            {
                onFloor &= mesh.Vertices[c].Y == floor;
                onCeiling &= mesh.Vertices[c].Y == ceiling;
            }

            if (onFloor)
                (bottom, top) = (bottom < 0 ? f : -2, top);
            else if (onCeiling)
                (bottom, top) = (bottom, top < 0 ? f : -2);
        }

        if (bottom < 0 || top < 0)
            return null;

        var outline = new List<(int X, int Z)>();

        foreach (var c in mesh.Face(bottom))
            outline.Add((mesh.Vertices[c].X, mesh.Vertices[c].Z));

        var corners = new HashSet<(int, int)>(outline);

        if (corners.Count != outline.Count || mesh.Face(top).Length != outline.Count)
            return null;

        foreach (var v in mesh.Vertices)
        {
            if ((v.Y != floor && v.Y != ceiling) || !corners.Contains((v.X, v.Z)))
                return null;
        }

        return (outline, floor, ceiling);
    }

    private static int IndexOf(ReadOnlySpan<HullSolid> solids, uint id)
    {
        for (var i = 0; i < solids.Length; i++)
        {
            if (solids[i].Id == id)
                return i;
        }

        return -1;
    }

    private static List<ProfilePoint> Quantised(List<(double X, double Y)> points)
    {
        var result = new List<ProfilePoint>(points.Count);

        foreach (var (x, y) in points)
        {
            var p = new ProfilePoint((int)Math.Round(x * HullSolid.PositionUnitsPerMetre), (int)Math.Round(y * HullSolid.PositionUnitsPerMetre));

            if (result.Count == 0 || result[^1] != p)
                result.Add(p);
        }

        return result;
    }
}
