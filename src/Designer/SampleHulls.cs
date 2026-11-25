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
