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
/// Grid and rotation snapping. Positions snap so an unrotated brush's faces sit on the grid: the minimum face is snapped
/// and the size is a whole number of steps. Rotation is always in 15 degree steps (the brush's snap index).
/// </summary>
public sealed class Snap
{
    public GridStep Step = GridStep.Quarter;
    public bool Enabled = true;

    public static ReadOnlySpan<GridStep> Steps => [GridStep.Eighth, GridStep.Quarter, GridStep.Half, GridStep.One];

    public int PositionStep => Enabled ? (int)Step : 1;

    /// <summary>Half-extent step in 1/256 m: half a grid step, so sizes are whole steps.</summary>
    public int HalfStep => Enabled ? Math.Max(1, (int)Step / 8) : 1;

    public double StepMetres => PositionStep * HullBrush.MetresPerPositionUnit;

    public int SnapPosition(int units) => SnapTo(units, PositionStep);

    public ushort SnapHalf(int halfUnits)
    {
        var step = HalfStep;
        var snapped = Math.Max(step, SnapTo(halfUnits, step));
        return (ushort)Math.Min(snapped, ushort.MaxValue / step * step);
    }

    /// <summary>Snaps sizes to whole steps, then the centre so the minimum face is on the grid.</summary>
    public HullBrush SnapBrush(HullBrush brush)
    {
        if (!Enabled)
            return brush;

        brush.HalfX = SnapHalf(brush.HalfX);
        brush.HalfY = SnapHalf(brush.HalfY);
        brush.HalfZ = SnapHalf(brush.HalfZ);
        brush.PosX = SnapCentre(brush.PosX, brush.HalfX);
        brush.PosY = SnapCentre(brush.PosY, brush.HalfY);
        brush.PosZ = SnapCentre(brush.PosZ, brush.HalfZ);
        return brush;
    }

    private int SnapCentre(int centre, ushort half)
    {
        var half1024 = half * 4;
        return SnapPosition(centre - half1024) + half1024;
    }

    private static int SnapTo(int value, int step) => (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;
}
