using BrutalGame.Designer.Mesh;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>Reference hulls built through the same commands as the UI: the acceptance demo and the bake benchmark.</summary>
public static class SampleHulls
{
    /// <summary>
    /// A 12 × 6 × 8 m box hollowed with 0.5 m walls, plus a 3 m diameter, 6 m long cylinder through its nose (rolled
    /// 90 degrees onto Z) and a wedge on the roof: a small craft's hull in five commands.
    /// </summary>
    public static void HollowBoxWithCylinder(DesignSession session, DefId hull, DefId trim)
    {
        const int m = HullBrush.PositionUnitsPerMetre;
        const int h = HullBrush.HalfUnitsPerMetre;
        var box = HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Add, 0, 3 * m, 0, 6 * h, 3 * h, 4 * h, BrushRotation.Identity, hull);
        HullCommands.Hollow(session, box, 0.5);
        HullCommands.AddBrush(session, BrushShape.Cylinder, BrushOp.Add, 0, 3 * m, 7 * m, (ushort)(1.5 * h), 3 * h, (ushort)(1.5 * h), BrushRotation.FromSteps(0, 6, 0), trim);
        HullCommands.AddBrush(session, BrushShape.Wedge, BrushOp.Add, 0, 7 * m, -2 * m, 3 * h, 1 * h, 2 * h, BrushRotation.Identity, trim);
        HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Subtract, 6 * m, 3 * m, 0, 1 * h, 1 * h, 1 * h, BrushRotation.Identity, hull);
    }

    /// <summary>
    /// A mesh-modelled hull: a box converted to a mesh, loop cut at mid-length, its nose face extruded, inset and extruded
    /// again, smoothed by two Catmull-Clark levels with the bottom edges kept creased, then cut by a primitive Subtract
    /// (a cargo door) to show it stays cuttable, with a primitive turret on top.
    /// </summary>
    public static void MeshHull(DesignSession session, DefId hull, DefId trim)
    {
        const int m = HullBrush.PositionUnitsPerMetre;
        const int h = HullBrush.HalfUnitsPerMetre;
        var body = HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Add, 0, 3 * m, 0, 3 * h, 2 * h, 8 * h, BrushRotation.Identity, hull);
        MeshCommands.ConvertToMesh(session, body);
        var mesh = MeshCommands.MeshOf(session, body)!;
        var front = FaceAlong(mesh, 2, 1);
        var longEdge = LongEdge(mesh);
        MeshCommands.Apply(session, body, b => MeshOps.LoopCut(b, longEdge, 0.5));
        MeshCommands.Apply(session, body, b => MeshOps.ExtrudeFaces(b, [front], 3 * m));
        MeshCommands.Apply(session, body, b => MeshOps.InsetFaces(b, [front], m / 2));
        MeshCommands.Apply(session, body, b => MeshOps.ExtrudeFaces(b, [front], 2 * m));
        MeshCommands.Apply(session, body, b =>
        {
            var bottom = new HashSet<(int, int)>();

            foreach (var (a, c) in b.Creases)
            {
                if (b.Vertices[a].Y < 0 && b.Vertices[c].Y < 0)
                    bottom.Add((a, c));
            }

            b.Creases.Clear();
            b.Creases.UnionWith(bottom);
            b.Levels = 2;
            return true;
        });
        HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Subtract, 3 * m, 2 * m, -2 * m, 1 * h, 1 * h, (ushort)(1.5 * h), BrushRotation.Identity, hull);
        HullCommands.AddBrush(session, BrushShape.Cylinder, BrushOp.Add, 0, 6 * m, -4 * m, (ushort)(0.75 * h), (ushort)(0.5 * h), (ushort)(0.75 * h), BrushRotation.Identity, trim);
    }

    private static int FaceAlong(HullMesh mesh, int axis, int sign)
    {
        var b = new MeshBuilder(mesh);

        for (var f = 0; f < b.Faces.Count; f++)
        {
            var n = MeshOps.Normal(b, b.Faces[f]);

            if ((axis == 0 ? n.X : axis == 1 ? n.Y : n.Z) * sign > 0.99)
                return f;
        }

        return 0;
    }

    private static (int, int) LongEdge(HullMesh mesh)
    {
        var best = (0, 1);
        long bestLength = 0;

        for (var f = 0; f < mesh.FaceCount; f++)
        {
            var face = mesh.Face(f);

            for (var i = 0; i < face.Length; i++)
            {
                var a = mesh.Vertices[face[i]];
                var c = mesh.Vertices[face[(i + 1) % face.Length]];
                var dz = Math.Abs((long)a.Z - c.Z);

                if (dz > bestLength && a.X == c.X && a.Y == c.Y)
                {
                    bestLength = dz;
                    best = (face[i], face[(i + 1) % face.Length]);
                }
            }
        }

        return best;
    }

    /// <summary>Converts every <paramref name="every"/>-th brush to a mesh brush, smoothed by <paramref name="levels"/> Catmull-Clark levels.</summary>
    public static void MeshifyEvery(DesignSession session, int every, int levels)
    {
        var ids = new List<uint>();

        for (var i = 0; i < session.Doc.Brushes.Count; i += every)
            ids.Add(session.Doc.Brushes[i].Id);

        foreach (var id in ids)
        {
            MeshCommands.ConvertToMesh(session, id);

            if (levels > 0)
                MeshCommands.Apply(session, id, b =>
                {
                    b.Creases.Clear();
                    b.Levels = (byte)levels;
                    return true;
                });
        }
    }

    /// <summary>
    /// <paramref name="count"/> brushes on a 30 × 10 × 60 m frigate-like layout: a deterministic mix of Add boxes, wedges and
    /// cylinders with every fourth a Subtract (rooms, cut-outs), rotations in 15 degree steps. Same output every call.
    /// </summary>
    public static void Bench(DesignSession session, int count, DefId hull, DefId trim)
    {
        const int m = HullBrush.PositionUnitsPerMetre;
        var rng = new Random(12345);
        session.Edit(s =>
        {
            for (var i = 0; i < count; i++)
            {
                var op = i % 4 == 3 ? BrushOp.Subtract : BrushOp.Add;
                var shape = (BrushShape)(i % 3);
                var brush = new HullBrush
                {
                    Id = s.Doc.AllocateId(),
                    Shape = shape,
                    Op = op,
                    Material = s.EnsureMaterial(i % 5 == 0 ? trim : hull),
                    PosX = rng.Next(-12, 13) * m,
                    PosY = rng.Next(-4, 5) * m,
                    PosZ = rng.Next(-28, 29) * m,
                    Rotation = BrushRotation.FromSteps(rng.Next(0, 4) * 6, i % 7 == 0 ? rng.Next(0, 24) : 0, 0),
                    HalfX = (ushort)(rng.Next(2, 9) * HullBrush.HalfUnitsPerMetre / 2),
                    HalfY = (ushort)(rng.Next(2, 7) * HullBrush.HalfUnitsPerMetre / 2),
                    HalfZ = (ushort)(rng.Next(2, 13) * HullBrush.HalfUnitsPerMetre / 2),
                };

                if (op == BrushOp.Subtract)
                {
                    brush.HalfX = (ushort)(brush.HalfX / 2 + 64);
                    brush.HalfY = (ushort)(brush.HalfY / 2 + 64);
                    brush.HalfZ = (ushort)(brush.HalfZ / 2 + 64);
                }

                if (shape == BrushShape.Cylinder)
                    brush.HalfZ = brush.HalfX;

                s.Apply(PlanOp.Insert(s.Doc.Brushes.Count, brush));
            }
        });
    }
}
