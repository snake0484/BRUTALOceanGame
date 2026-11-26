using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>Grid step in 1/1024 m (ship-hulls: 1/8, 1/4, 1/2 or 1 m).</summary>
public enum GridStep
{
    Eighth = 128,
    Quarter = 256,
    Half = 512,
    One = 1024,
}

/// <summary>
/// Grid and rotation snapping. Positions snap so an unrotated solid's faces sit on the grid: the minimum face is snapped
/// and the size is a whole number of steps. Rotation is always in 15 degree steps (the solid's snap index).
/// </summary>
public sealed class Snap
{
    public GridStep Step = GridStep.Quarter;
    public bool Enabled = true;

    public static ReadOnlySpan<GridStep> Steps => [GridStep.Eighth, GridStep.Quarter, GridStep.Half, GridStep.One];

    public int PositionStep => Enabled ? (int)Step : 1;

    /// <summary>Half-extent step in 1/256 m: half a grid step, so sizes are whole steps.</summary>
    public int HalfStep => Enabled ? Math.Max(1, (int)Step / 8) : 1;

    public double StepMetres => PositionStep * HullSolid.MetresPerPositionUnit;

    public int SnapPosition(int units) => SnapTo(units, PositionStep);

    public ushort SnapHalf(int halfUnits)
    {
        var step = HalfStep;
        var snapped = Math.Max(step, SnapTo(halfUnits, step));
        return (ushort)Math.Min(snapped, ushort.MaxValue / step * step);
    }

    /// <summary>Snaps sizes to whole steps, then the centre so the minimum face is on the grid.</summary>
    public HullSolid SnapSolid(HullSolid solid)
    {
        if (!Enabled)
            return solid;

        // A mesh solid's half extents are its mesh's bounds: only its origin snaps.
        if (solid.Shape == SolidShape.Mesh)
        {
            solid.PosX = SnapPosition(solid.PosX);
            solid.PosY = SnapPosition(solid.PosY);
            solid.PosZ = SnapPosition(solid.PosZ);
            return solid;
        }

        solid.HalfX = SnapHalf(solid.HalfX);
        solid.HalfY = SnapHalf(solid.HalfY);
        solid.HalfZ = SnapHalf(solid.HalfZ);
        solid.PosX = SnapCentre(solid.PosX, solid.HalfX);
        solid.PosY = SnapCentre(solid.PosY, solid.HalfY);
        solid.PosZ = SnapCentre(solid.PosZ, solid.HalfZ);
        return solid;
    }

    private int SnapCentre(int centre, ushort half)
    {
        var half1024 = half * 4;
        return SnapPosition(centre - half1024) + half1024;
    }

    private static int SnapTo(int value, int step) => (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;
}
