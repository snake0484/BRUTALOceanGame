namespace BrutalGame.Ships.Interior;

public enum WallPreset : byte { Straight, Chamfered, Coved, Sloped }

/// <summary>
/// Wall profiles and the rooms that use them, deterministic from the plan. A wall profile is one wall's section from floor
/// to ceiling (X: how far in from the room's side, Y: height); a box room using one is cut as a stack of rectangles, each
/// its box inset by the profile at that height, so every wall gets the same section. The profile's lower half keeps to the
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

    /// <summary>A solid that can use a wall profile: a box cut as a room (Room role, or a subtract).</summary>
    public static bool IsRoom(in HullSolid solid) =>
        solid.Shape == SolidShape.Box && (SolidRoles.Of(solid) == SolidRole.Room || solid.Op == SolidOp.Subtract);

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

    /// <summary>The solids every room with a wall profile is cut as instead of its box, keyed by the box's index in <paramref name="solids"/>.</summary>
    public sealed class Expansion
    {
        public readonly List<(int Index, HullSolid Solid)> Rooms = new();
        public readonly List<HullMesh> Meshes = new();
    }

    /// <summary>
    /// Each room in <paramref name="interior"/>'s room walls, as a mesh solid in its box's place (same id, op, role, material,
    /// and position; its rotation baked into the mesh) whose Param indexes a mesh table that already holds <paramref name="meshBase"/> meshes.
    /// </summary>
    public static Expansion Expand(HullInterior? interior, ReadOnlySpan<HullSolid> solids, int meshBase)
    {
        var result = new Expansion();

        if (interior is null)
            return result;

        foreach (var wall in interior.RoomWalls)
        {
            if (wall.Profile >= interior.Profiles.Count || interior.Profiles[wall.Profile] is not { Kind: ProfileKind.Wall } profile || !IsValid(profile.Points))
                continue;

            var index = IndexOf(solids, wall.Solid);

            if (index < 0 || !IsRoom(solids[index]) || Room(solids[index], profile.Points) is not { } mesh)
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

    /// <summary>The room's box inset by <paramref name="wall"/>, about its centre, turned by its rotation. Null when it has no room left.</summary>
    public static HullMesh? Room(in HullSolid box, IReadOnlyList<ProfilePoint> wall)
    {
        const double k = HullSolid.MetresPerPositionUnit;
        var hx = box.HalfX * HullSolid.MetresPerHalfUnit;
        var hy = box.HalfY * HullSolid.MetresPerHalfUnit;
        var hz = box.HalfZ * HullSolid.MetresPerHalfUnit;
        var room = hy * 2;
        var tall = wall[^1].Y * k;
        var limit = Math.Min(hx, hz) - MinRoomMetres;

        if (limit <= 0 || room <= 0)
            return null;

        // Rings: (inset, height) in 1/1024 m, the floor half kept to the floor, the ceiling half to the ceiling.
        var rings = new List<(int Inset, int Y)>(wall.Count);

        foreach (var p in wall)
        {
            var y = p.Y * k;
            var height = room >= tall ? (y <= tall * 0.5 ? y : room - (tall - y)) : y * room / tall;
            var ring = ((int)Math.Round(Math.Min(p.X * k, limit) / k), (int)Math.Round((height - hy) / k));

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

        // Corners (-X -Z, -X +Z, +X +Z, +X -Z): counter-clockwise seen from above.
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(box.Rotation, m);
        var builder = new MeshBuilder();
        var ex = (int)Math.Round(hx / k);
        var ez = (int)Math.Round(hz / k);

        foreach (var (inset, y) in rings)
        {
            int x = ex - inset, z = ez - inset;
            builder.AddVertex(Turned(m, -x, y, -z));
            builder.AddVertex(Turned(m, -x, y, z));
            builder.AddVertex(Turned(m, x, y, z));
            builder.AddVertex(Turned(m, x, y, -z));
        }

        for (var r = 0; r + 1 < rings.Count; r++)
        {
            for (var j = 0; j < 4; j++)
            {
                var jn = (j + 1) % 4;
                builder.AddFace([r * 4 + j, r * 4 + jn, (r + 1) * 4 + jn, (r + 1) * 4 + j], 0);
            }
        }

        var top = (rings.Count - 1) * 4;
        builder.AddFace([3, 2, 1, 0], 0);
        builder.AddFace([top, top + 1, top + 2, top + 3], 0);
        return builder.Build();
    }

    private static MeshVertex Turned(ReadOnlySpan<double> m, int x, int y, int z) => new(
        (int)Math.Round(m[0] * x + m[1] * y + m[2] * z),
        (int)Math.Round(m[3] * x + m[4] * y + m[5] * z),
        (int)Math.Round(m[6] * x + m[7] * y + m[8] * z));

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
