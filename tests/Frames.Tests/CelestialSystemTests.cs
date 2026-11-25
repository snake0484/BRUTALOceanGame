using Brutal.Numerics;

namespace BrutalGame.Frames.Tests;

public class CelestialSystemTests
{
    private const double TickSeconds = 1.0 / 60.0;

    private static CelestialSystem TestSystem(FrameTree tree) => CelestialSystem.CreateTest(tree, seed: 11, TickSeconds);

    private static byte[] Store(CelestialSystem system)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
            system.Write(writer);

        return stream.ToArray();
    }

    private static CelestialSystem Load(byte[] bytes, FrameTree tree)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        return CelestialSystem.Read(reader, tree);
    }

    [Fact]
    public void Evaluate_AtAlphaZero_IsTheTicksPoseBitForBit()
    {
        var tree = new FrameTree();
        var bodies = TestSystem(tree);
        const long tick = 31_415_926;
        bodies.Update(tick);
        bodies.Evaluate(tick, 0.0);

        for (var i = 0; i < tree.Count; i++)
        {
            Assert.Equal(tree.Local[i].Position, tree.Render[i].Position);
            Assert.Equal(tree.Local[i].Rotation, tree.Render[i].Rotation);
            Assert.Equal(tree.Local[i].Velocity, tree.Render[i].Velocity);
        }
    }

    /// <summary>Between ticks, spin follows the continuous angle and the moon its rail, instead of holding the tick's pose.</summary>
    [Fact]
    public void Evaluate_BetweenTicks_MovesAtRenderRate()
    {
        var tree = new FrameTree();
        var bodies = TestSystem(tree);
        var planet = bodies[1];
        var moon = bodies[2];
        const long tick = 2_000_003;
        var daySeconds = planet.DayTicks * TickSeconds;

        foreach (var alpha in new[] { 0.125, 0.5, 0.875 })
        {
            bodies.Evaluate(tick, alpha);
            var seconds = (tick + alpha) * TickSeconds;
            var angle = 2.0 * Math.PI * (seconds / daySeconds - Math.Floor(seconds / daySeconds));
            var expected = doubleQuat.CreateFromAxisAngle(planet.SpinAxis, angle) * double3.UnitX;
            var spun = tree.Render[planet.Surface.Value].Rotation * double3.UnitX;
            Assert.True((spun - expected).Length() < 1e-12, $"spin off by {(spun - expected).Length():E2} rad at alpha {alpha}");

            bodies.Update(tick);
            var before = tree.Local[moon.Inertial.Value].Position;
            bodies.Update(tick + 1);
            var after = tree.Local[moon.Inertial.Value].Position;
            var step = (after - before).Length();
            var linear = before + (after - before) * alpha;
            Assert.True((tree.Render[moon.Inertial.Value].Position - linear).Length() < 1e-6 * step, $"moon off its chord at alpha {alpha}");
        }
    }

    /// <summary>Teeth for --orbit-test inertial: drawn from the tick's pose, the near-pole marker steps ~1 px at 3.6 m.</summary>
    [Fact]
    public void TickPoses_StepTheNearPoleMarkerByPixels()
    {
        var tree = new FrameTree();
        var bodies = TestSystem(tree);
        var planet = bodies[1];
        const long tick = 1000;
        const double range = 3.6;
        var focal = 900.0 / (2.0 * Math.Tan(Math.PI / 6.0));
        var marker = (planet.SpinAxis * Math.Cos(1e-3) + double3.UnitX * Math.Sin(1e-3)) * planet.Radius;
        bodies.Update(tick);
        bodies.Evaluate(tick, 0.9);

        var surface = planet.Surface.Value;
        var stepped = (tree.Local[surface].Rotation * marker - tree.Render[surface].Rotation * marker).Length() / range * focal;
        Assert.InRange(stepped, 0.5, 2.0);
    }

    [Fact]
    public void TryRelativeRender_ReadsTheRenderPoses()
    {
        var tree = new FrameTree();
        var bodies = TestSystem(tree);
        var planet = bodies[1];
        const long tick = 777;
        bodies.Update(tick);
        bodies.Evaluate(tick, 0.5);

        Assert.True(tree.TryRelative(bodies[0].Inertial, planet.Surface, out var sim));
        Assert.True(tree.TryRelativeRender(bodies[0].Inertial, planet.Surface, out var render));

        // The star seen from the spinning surface: half a tick of spin is ~2.4e-6 rad, ~8.7e4 m at 3.6e10 m.
        var moved = (render.Position - sim.Position).Length();
        Assert.InRange(moved, 1e4, 1e6);
    }

    [Fact]
    public void StoredSystem_RoundTripsBitExactly()
    {
        var bodies = TestSystem(new FrameTree());
        var bytes = Store(bodies);
        var loaded = Load(bytes, new FrameTree());

        Assert.Equal(bytes, Store(loaded));
        Assert.Equal(bodies.Count, loaded.Count);
        Assert.Equal(BitConverter.DoubleToInt64Bits(bodies.TickSeconds), BitConverter.DoubleToInt64Bits(loaded.TickSeconds));

        for (var i = 0; i < bodies.Count; i++)
        {
            Assert.Equal(bodies[i].Inertial, loaded[i].Inertial);
            Assert.Equal(bodies[i].Surface, loaded[i].Surface);
            Assert.Equal(bodies[i].Orbit.PeriodTicks, loaded[i].Orbit.PeriodTicks);
            Assert.Equal(BitConverter.DoubleToInt64Bits(bodies[i].Orbit.SemiMajor), BitConverter.DoubleToInt64Bits(loaded[i].Orbit.SemiMajor));
            Assert.Equal(bodies[i].DayTicks, loaded[i].DayTicks);
        }

        foreach (var tick in new[] { 0L, 123_456_789L, -42L })
        {
            bodies.Update(tick);
            loaded.Update(tick);

            for (var f = 0; f < bodies.Frames.Count; f++)
            {
                Assert.Equal(bodies.Frames.Local[f].Position, loaded.Frames.Local[f].Position);
                Assert.Equal(bodies.Frames.Local[f].Rotation, loaded.Frames.Local[f].Rotation);
                Assert.Equal(bodies.Frames.Local[f].Velocity, loaded.Frames.Local[f].Velocity);
            }
        }
    }

    /// <summary>A load takes the stored period and axis as they are: nothing is rounded or refitted on the client.</summary>
    [Fact]
    public void StoredRail_IsNeverRefitted()
    {
        var bodies = new CelestialSystem(new FrameTree(), TickSeconds);
        var star = bodies.AddStar(1.3e20, 7e8);
        var rail = KeplerOrbit.Fit(1.3e20, 3.0e10, 0.1, 0, 0, 0, 0, TickSeconds);
        rail.PeriodTicks += 1;
        rail.SemiMajor = 3.0e10;
        bodies.Add(new CelestialRecord { Kind = CelestialKind.Planet, Parent = star, Mu = 1e13, Radius = 1e6, SoiRadius = 5e7, SpinAxis = double3.UnitY, Rail = rail });

        var loaded = Load(Store(bodies), new FrameTree());

        Assert.Equal(rail.PeriodTicks, loaded[1].Orbit.PeriodTicks);
        Assert.Equal(3.0e10, loaded[1].Orbit.SemiMajor);
        Assert.Equal(5e7, loaded[1].SoiRadius);
    }
}
