using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>Reference buildings made through the ship designer's commands, as the UI makes them.</summary>
public static class SampleBuildings
{
    /// <summary>
    /// An 8 × 6 m hut on its 12 × 12 m pad: a floor slab 1 m into the foundation, 3 m walls hollowed to 0.3 m, a wedge
    /// roof, a door cut through the front (+Z) wall and a window through the side (+X) wall.
    /// </summary>
    public static void Hut(DesignSession session, DefId wall, DefId roof)
    {
        const int m = HullSolid.PositionUnitsPerMetre;
        const int h = HullSolid.HalfUnitsPerMetre;
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, -2 * m / 5, 0, (ushort)(4.2 * h), (ushort)(0.6 * h), (ushort)(3.2 * h), SolidRotation.Identity, wall);
        var walls = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 17 * m / 10, 0, 4 * h, (ushort)(1.5 * h), 3 * h, SolidRotation.Identity, wall);
        HullCommands.Hollow(session, walls, 0.3);
        HullCommands.AddSolid(session, SolidShape.Wedge, SolidOp.Add, 0, 4 * m, 0, (ushort)(4.3 * h), (ushort)(0.8 * h), (ushort)(3.3 * h), SolidRotation.Identity, roof);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 0, 7 * m / 5, 3 * m, (ushort)(0.6 * h), (ushort)(1.1 * h), (ushort)(0.4 * h), SolidRotation.Identity, wall);
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 4 * m, 9 * m / 5, 0, (ushort)(0.4 * h), (ushort)(0.6 * h), (ushort)(0.8 * h), SolidRotation.Identity, wall);
        session.Selection.Clear();
    }
}
