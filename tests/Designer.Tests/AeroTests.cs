using BrutalGame.Ships;
using BrutalGame.Ships.Aero;

namespace BrutalGame.Designer.Tests;

/// <summary>Body lift from shape: a flat plate lifts and stalls like a wing, rooms inside do not count, the wake shadows.</summary>
public class AeroTests
{
    private static HullSolid Box(uint id, double x, double y, double z, double hx, double hy, double hz, SolidOp op = SolidOp.Add) => new()
    {
        Id = id,
        Shape = SolidShape.Box,
        Op = op,
        Rotation = SolidRotation.Identity,
        PosX = (int)Math.Round(x * 1024),
        PosY = (int)Math.Round(y * 1024),
        PosZ = (int)Math.Round(z * 1024),
        HalfX = (ushort)Math.Round(hx * 256),
        HalfY = (ushort)Math.Round(hy * 256),
        HalfZ = (ushort)Math.Round(hz * 256),
    };

    private static AeroTable Bake(params HullSolid[] solids) => HullAero.Bake(HullCsg.Build(solids, [], false).Bricks, default);

    /// <summary>Lift and drag over q (m²) flying forward (+Z) at angle of attack <paramref name="degrees"/>.</summary>
    private static (double Lift, double Drag) At(AeroTable table, double degrees)
    {
        var a = degrees * Math.PI / 180;
        var velocity = new Vector3d(0, -Math.Sin(a), Math.Cos(a));
        table.Lookup(new Vector3d(-velocity.X, -velocity.Y, -velocity.Z), out var f, out _);
        var lift = new Vector3d(0, Math.Cos(a), Math.Sin(a));
        return (f.X * lift.X + f.Y * lift.Y + f.Z * lift.Z, -(f.X * velocity.X + f.Y * velocity.Y + f.Z * velocity.Z));
    }

    [Fact]
    public void Plate_LiftsLikeAWing_AndStalls()
    {
        // 10 m span, 2 m chord, 0.1 m thick: 20 m² of wing.
        var plate = Bake(Box(1, 0, 0, 0, 5, 0.05, 1));
        var (level, levelDrag) = At(plate, 0);
        var (ten, tenDrag) = At(plate, 10);
        var (stall, _) = At(plate, 15);
        var (deep, _) = At(plate, 30);

        Assert.InRange(level, -0.2, 0.2);
        Assert.InRange(levelDrag, 1.0, 1.6);           // its blunt 1 m² of edges, and skin friction
        Assert.InRange(ten / 20, 0.55, 0.8);           // about 4 per radian
        Assert.InRange(ten / tenDrag, 3.5, 8);
        Assert.True(stall > ten && deep < stall * 0.8, $"stall {stall:F2}, deep {deep:F2}");
        Assert.True(plate.ExposedArea > 0.95 * plate.SurfaceArea);
    }

    [Fact]
    public void Broadside_DragsAboutOnePointOneFive()
    {
        var plate = Bake(Box(1, 0, 0, 0, 2, 2, 0.05));   // 4 x 4 m face to +Z
        var (_, drag) = At(plate, 0);
        Assert.InRange(drag / 16, 1.05, 1.3);
    }

    [Fact]
    public void RoomsInside_DoNotCount()
    {
        var solid = Bake(Box(1, 0, 0, 0, 4, 2, 6));
        var hollow = Bake(Box(1, 0, 0, 0, 4, 2, 6), Box(2, 0, 0, 0, 3, 1, 5, SolidOp.Subtract));

        Assert.True(hollow.SurfaceArea > solid.SurfaceArea * 1.5);
        Assert.InRange(hollow.ExposedArea / solid.ExposedArea, 0.97, 1.03);

        foreach (var degrees in new[] { 0.0, 10, 45 })
        {
            var (l0, d0) = At(solid, degrees);
            var (l1, d1) = At(hollow, degrees);
            Assert.InRange(l1 - l0, -0.05 * Math.Abs(l0) - 0.5, 0.05 * Math.Abs(l0) + 0.5);
            Assert.InRange(d1 - d0, -0.05 * d0 - 0.5, 0.05 * d0 + 0.5);
        }
    }

    [Fact]
    public void PlateInTheWake_IsShadowed()
    {
        var one = Bake(Box(1, 0, 0, 0, 2, 2, 0.05));
        var two = Bake(Box(1, 0, 0, 0, 2, 2, 0.05), Box(2, 0, 0, -3, 2, 2, 0.05));
        var (_, single) = At(one, 0);
        var (_, tandem) = At(two, 0);

        // The rear plate's face to the flow is in the front plate's wake: only its base suction is added.
        Assert.InRange(tandem / single, 1.2, 1.5);
    }

    [Fact]
    public void Total_TurnsAPartByItsPose()
    {
        var plate = Bake(Box(1, 0, 0, 2, 5, 0.05, 1));
        var flow = new Vector3d(0, 0.2, -1);
        Span<double> identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        HullAero.Total([plate], identity, [default], flow, 1.225, out var f0, out var m0);

        // Turned 90 degrees about +Y with the flow turned too: the same load, turned.
        Span<double> r = [0, 0, 1, 0, 1, 0, -1, 0, 0];
        var turned = new Vector3d(r[0] * flow.X + r[1] * flow.Y + r[2] * flow.Z, r[3] * flow.X + r[4] * flow.Y + r[5] * flow.Z, r[6] * flow.X + r[7] * flow.Y + r[8] * flow.Z);
        HullAero.Total([plate], r, [default], turned, 1.225, out var f1, out var m1);

        Assert.Equal(f0.Z, f1.X, 3);
        Assert.Equal(-f0.X, f1.Z, 3);
        Assert.Equal(f0.Y, f1.Y, 3);
        Assert.Equal(m0.Y, m1.Y, 2);
    }
}
