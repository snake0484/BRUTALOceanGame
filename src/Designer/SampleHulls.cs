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
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var box = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 0, 6 * h, 3 * h, 4 * h, SolidRotation.Identity, hull);
        HullCommands.Hollow(session, box, 0.5);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 3 * m, 7 * m, (ushort)(1.5 * h), 3 * h, (ushort)(1.5 * h), SolidRotation.FromSteps(0, 6, 0), trim);
        HullCommands.AddSolid(session, SolidShape.Wedge, SolidOp.Add, 0, 7 * m, -2 * m, 3 * h, 1 * h, 2 * h, SolidRotation.Identity, trim);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 6 * m, 3 * m, 0, 1 * h, 1 * h, 1 * h, SolidRotation.Identity, hull);
    }

    /// <summary>
    /// The box-and-cylinder hull dressed with brushes: a half-round rib along the roof edge, a ridge along the
    /// starboard side, a row of bolt heads (a stamp made from a small cylinder, then deleted) down the port side, and a
    /// rib over the cylinder nose, drawn above it and stuck down onto its curve.
    /// </summary>
    public static void BrushedHull(DesignSession session, DefId hull, DefId trim)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        HollowBoxWithCylinder(session, hull, trim);
        var bolt = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 20 * m, 0, (ushort)(0.25 * h), (ushort)(0.125 * h), (ushort)(0.25 * h), SolidRotation.Identity, trim);
        var stamp = BrushCommands.AddStampFromSolid(session, bolt);
        session.Selection.Set(bolt);
        HullCommands.DeleteSelected(session);
        var rib = BrushCommands.AddSweep(session, ProfilePreset.HalfRound, 0.4, 0.25);
        var ridge = BrushCommands.AddSweep(session, ProfilePreset.Ridge, 0.3, 0.15);
        var up = new Vector3d(0, 1, 0);
        var trimMaterial = session.EnsureMaterial(trim);
        
        BrushCommands.AddStroke(session, rib, [StrokePoint.From(new Vector3d(-5.5, 6.4, 3.5), up), StrokePoint.From(new Vector3d(5.5, 6.4, 3.5), up)], trimMaterial);
        BrushCommands.AddStroke(session, ridge,
            [StrokePoint.From(new Vector3d(6.4, 5.0, -3.5), new Vector3d(1, 0, 0)), StrokePoint.From(new Vector3d(6.4, 5.0, 3.5), new Vector3d(1, 0, 0))], trimMaterial);
        BrushCommands.AddStroke(session, stamp,
            [StrokePoint.From(new Vector3d(-6.4, 4.5, -3.5), new Vector3d(-1, 0, 0)), StrokePoint.From(new Vector3d(-6.4, 4.5, 3.5), new Vector3d(-1, 0, 0))], trimMaterial);
        BrushCommands.AddStroke(session, rib,
            [StrokePoint.From(new Vector3d(-1.0, 5.0, 4.2), up), StrokePoint.From(new Vector3d(0, 5.0, 7.0), up), StrokePoint.From(new Vector3d(1.0, 5.0, 9.8), up)], trimMaterial);
    }

    /// <summary>
    /// A mesh-modelled hull: a box converted to a mesh, loop cut at mid-length, its nose face extruded, inset and extruded
    /// again, smoothed by two Catmull-Clark levels with the bottom edges kept creased, then cut by a primitive Subtract
    /// (a cargo door) to show it stays cuttable, with a primitive turret on top.
    /// </summary>
    public static void MeshHull(DesignSession session, DefId hull, DefId trim)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        var body = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 0, 3 * h, 2 * h, 8 * h, SolidRotation.Identity, hull);
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
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 3 * m, 2 * m, -2 * m, 1 * h, 1 * h, (ushort)(1.5 * h), SolidRotation.Identity, hull);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 6 * m, -4 * m, (ushort)(0.75 * h), (ushort)(0.5 * h), (ushort)(0.75 * h), SolidRotation.Identity, trim);
    }

    /// <summary>
    /// A layered hull: a skin of a box body, a wedge nose and a cylinder engine pod, sectioned at the nose (Z = 6 m) with
    /// a heavier stack there; a cockpit and a corridor as rooms, a reactor as a component, an armour panel on the roof.
    /// </summary>
    public static void LayeredHull(DesignSession session, DefId armour, DefId alloy, DefId titanium)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        LayerCommands.Enable(session, titanium, alloy);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 0, 4 * h, 3 * h, 6 * h, SolidRotation.Identity, alloy);
        HullCommands.AddSolid(session, SolidShape.Wedge, SolidOp.Add, 0, 3 * m, 8 * m, 4 * h, 3 * h, 2 * h, SolidRotation.Identity, alloy);
        HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 3 * m, -7 * m, (ushort)(2.5 * h), 2 * h, (ushort)(2.5 * h), SolidRotation.FromSteps(0, 6, 0), alloy);

        var cockpit = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 4 * m, (ushort)(2.5 * h), (ushort)(1.5 * h), 1 * h, SolidRotation.Identity, alloy);
        LayerCommands.SetRole(session, cockpit, Ships.SolidRole.Room);
        var corridor = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 0, 1 * h, (ushort)(1.25 * h), 3 * h, SolidRotation.Identity, alloy);
        LayerCommands.SetRole(session, corridor, Ships.SolidRole.Room);
        var reactor = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 3 * m, -4 * m, (ushort)(1.25 * h), (ushort)(1.25 * h), (ushort)(1.25 * h), SolidRotation.Identity, alloy);
        LayerCommands.SetRole(session, reactor, Ships.SolidRole.Component);
        var plate = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, (int)(6.25 * m), 0, 3 * h, (ushort)(0.25 * h), 4 * h, SolidRotation.Identity, armour);
        LayerCommands.SetRole(session, plate, Ships.SolidRole.Panel);

        LayerCommands.AddStack(session, armour);
        LayerCommands.Change(session, (s, l) =>
        {
            l.Stacks[2].Layers.Clear();
            l.Stacks[2].Layers.Add(new HullLayer(s.EnsureMaterial(armour), 500));
            l.Stacks[2].Layers.Add(new HullLayer(s.EnsureMaterial(alloy), 750));
            return true;
        });
        LayerCommands.AddCut(session, 2, 6 * m);
        var nose = LayerCommands.Sections(session.Doc.Layers!).First(x => x.Section.Z == 1).Section;
        LayerCommands.AssignSection(session, nose, 2);
    }

    /// <summary>The layered demo hull with a hallway: a level run along the port side, then stairs up across to starboard.</summary>
    public static void HallwayHull(DesignSession session, DefId armour, DefId alloy, DefId titanium)
    {
        LayeredHull(session, armour, alloy, titanium);
        var id = InteriorCommands.AddHallway(session, new Vector3d(-2.5, 0.9, -4.5), new Vector3d(-2.5, 0.9, 1.5), alloy);
        InteriorCommands.SetHallway(session, id, h => h.Nodes.Add(ComponentCommands.Quantise(new Vector3d(1.5, 2.9, 1.5))));
    }

    /// <summary>The box-and-cylinder hull with windows: a rounded one in the port side, a porthole in the front, and one curving over the cylinder's top.</summary>
    public static void WindowedHull(DesignSession session, DefId hull, DefId trim, DefId glass, DefId alon)
    {
        HollowBoxWithCylinder(session, hull, trim);
        WindowCommands.AddWindow(session, new Vector3d(-6, 3.2, 0), new Vector3d(-1, 0, 0), glass, new Ships.Interior.HullWindow { Shape = Ships.Interior.WindowShape.Rounded, WidthMm = 3000, HeightMm = 1400 });
        WindowCommands.AddWindow(session, new Vector3d(3.5, 3.5, -4), new Vector3d(0, 0, -1), glass, new Ships.Interior.HullWindow { Shape = Ships.Interior.WindowShape.Circle, WidthMm = 1200, HeightMm = 1200 });
        WindowCommands.AddWindow(session, new Vector3d(0, 4.5, 7.5), new Vector3d(0, 1, 0), alon, new Ships.Interior.HullWindow { Shape = Ships.Interior.WindowShape.Rectangle, WidthMm = 2200, HeightMm = 2400, DepthMm = 2000 });
    }

    /// <summary>A solid block (no layers) with the same hallway, so the exact cut and the stairs show in a cutaway.</summary>
    public static void HallwayBlock(DesignSession session, DefId alloy)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 3 * m, 0, 5 * h, 3 * h, 7 * h, SolidRotation.Identity, alloy);
        var id = InteriorCommands.AddHallway(session, new Vector3d(-2.5, 0.9, -4.5), new Vector3d(-2.5, 0.9, 1.5), alloy);
        InteriorCommands.SetHallway(session, id, hall => hall.Nodes.Add(ComponentCommands.Quantise(new Vector3d(1.5, 2.9, 1.5))));
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

    /// <summary>Converts every <paramref name="every"/>-th solid to a mesh solid, smoothed by <paramref name="levels"/> Catmull-Clark levels.</summary>
    public static void MeshifyEvery(DesignSession session, int every, int levels)
    {
        var ids = new List<uint>();

        for (var i = 0; i < session.Doc.Solids.Count; i += every)
            ids.Add(session.Doc.Solids[i].Id);

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
    /// <paramref name="count"/> solids on a 30 × 10 × 60 m frigate-like layout: a deterministic mix of Add boxes, wedges and
    /// cylinders with every fourth a Subtract (rooms, cut-outs), rotations in 15 degree steps. Same output every call.
    /// </summary>
    public static void Bench(DesignSession session, int count, DefId hull, DefId trim)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        var rng = new Random(12345);
        session.Edit(s =>
        {
            for (var i = 0; i < count; i++)
            {
                var op = i % 4 == 3 ? SolidOp.Subtract : SolidOp.Add;
                var shape = (SolidShape)(i % 3);
                var solid = new HullSolid
                {
                    Id = s.Doc.AllocateId(),
                    Shape = shape,
                    Op = op,
                    Material = s.EnsureMaterial(i % 5 == 0 ? trim : hull),
                    PosX = rng.Next(-12, 13) * m,
                    PosY = rng.Next(-4, 5) * m,
                    PosZ = rng.Next(-28, 29) * m,
                    Rotation = SolidRotation.FromSteps(rng.Next(0, 4) * 6, i % 7 == 0 ? rng.Next(0, 24) : 0, 0),
                    HalfX = (ushort)(rng.Next(2, 9) * HullSolid.HalfUnitsPerMetre / 2),
                    HalfY = (ushort)(rng.Next(2, 7) * HullSolid.HalfUnitsPerMetre / 2),
                    HalfZ = (ushort)(rng.Next(2, 13) * HullSolid.HalfUnitsPerMetre / 2),
                };

                if (op == SolidOp.Subtract)
                {
                    solid.HalfX = (ushort)(solid.HalfX / 2 + 64);
                    solid.HalfY = (ushort)(solid.HalfY / 2 + 64);
                    solid.HalfZ = (ushort)(solid.HalfZ / 2 + 64);
                }

                if (shape == SolidShape.Cylinder)
                    solid.HalfZ = solid.HalfX;

                s.Apply(PlanOp.Insert(s.Doc.Solids.Count, solid));
            }
        });
    }
}
