using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Buildings;

/// <summary>Ground checks on a building's added solids: inside the pad's footprint, and no deeper than the foundation.</summary>
public static class BuildingCheck
{
    private const double ToleranceMetres = 1e-3;

    public static int OutsidePad(PlanDocument plan, in BuildingInfo info)
    {
        var hx = info.PadWidthMetres * 0.5 + ToleranceMetres;
        var hz = info.PadDepthMetres * 0.5 + ToleranceMetres;
        var count = 0;

        foreach (ref readonly var solid in plan.Solids.AsSpan())
        {
            if (solid.Op != SolidOp.Add)
                continue;

            SolidGeometry.Bounds(solid, out var min, out var max);

            if (min.X < -hx || max.X > hx || min.Z < -hz || max.Z > hz)
                count++;
        }

        return count;
    }

    public static int BelowFoundation(PlanDocument plan, in BuildingInfo info)
    {
        var floor = -info.FoundationMetres - ToleranceMetres;
        var count = 0;

        foreach (ref readonly var solid in plan.Solids.AsSpan())
        {
            if (solid.Op != SolidOp.Add)
                continue;

            SolidGeometry.Bounds(solid, out var min, out _);

            if (min.Y < floor)
                count++;
        }

        return count;
    }
}
