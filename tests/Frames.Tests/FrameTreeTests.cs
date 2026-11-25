using Brutal.Numerics;

namespace BrutalGame.Frames.Tests;

public class FrameTreeTests
{
    private const double TickSeconds = 1.0 / 60.0;

    private static (FrameTree Tree, CelestialSystem Bodies) TestSystem(long tick = 0)
    {
        var tree = new FrameTree();
        var bodies = CelestialSystem.CreateTest(tree, seed: 7, TickSeconds);
        bodies.Update(tick);
        return (tree, bodies);
    }

    private static FramePose Moving(double3 position) => new()
    {
        Position = position,
        Rotation = doubleQuat.CreateFromAxisAngle(double3.Normalize(new double3(1, 2, 3)), 0.7),
        Velocity = new double3(12, -3, 40),
        AngularVelocity = new double3(0.01, 0.2, -0.05),
    };

    private static void AssertIdentity(in FramePose pose, double scale)
    {
        Assert.True(pose.Position.Length() < 1e-9 * scale, $"position {pose.Position}");
        Assert.True(pose.Velocity.Length() < 1e-9 * scale, $"velocity {pose.Velocity}");
        Assert.True(pose.AngularVelocity.Length() < 1e-9, $"angular velocity {pose.AngularVelocity}");
        Assert.True(new double3(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z).Length() < 1e-9, $"rotation {pose.Rotation}");
    }

    [Fact]
    public void Relative_ComposedWithReverse_IsIdentity()
    {
        var (tree, bodies) = TestSystem(tick: 123_456_789);
        var moon = bodies[2];
        var grid = tree.Add(moon.Surface, FrameKind.Grid);
        tree.Local[grid.Value] = Moving(new double3(4.1e5, 300, -20));
        var docked = tree.Add(grid, FrameKind.Grid);
        tree.Local[docked.Value] = Moving(new double3(-35, 8, 120));
        var planetGrid = tree.Add(bodies[1].Surface, FrameKind.Grid);
        tree.Local[planetGrid.Value] = Moving(new double3(0, 1.0e6 + 50, 0));

        FrameId[] frames = [tree.System[grid.Value].Root, bodies[0].Surface, bodies[1].Inertial, bodies[1].Surface, moon.Inertial, moon.Surface, grid, docked, planetGrid];

        foreach (var a in frames)
        {
            foreach (var b in frames)
            {
                Assert.True(tree.TryRelative(a, b, out var ab));
                Assert.True(tree.TryRelative(b, a, out var ba));
                var scale = 1.0 + ab.Position.Length() * (1.0 + ab.AngularVelocity.Length()) + ab.Velocity.Length();
                AssertIdentity(FramePose.Compose(ab, ba), scale);
                AssertIdentity(FramePose.Compose(ba, ab), scale);
            }
        }

        Assert.True(tree.TryRelative(docked, grid, out var near));
        Assert.True(tree.TryRelative(grid, docked, out var back));
        AssertIdentity(FramePose.Compose(near, back), 1.0);
    }

    [Fact]
    public void Relative_VelocityMatchesFiniteDifference()
    {
        var (tree, bodies) = TestSystem();
        var grid = tree.Add(bodies[2].Surface, FrameKind.Grid);
        tree.Local[grid.Value] = new FramePose { Position = new double3(4.0e5, 0, 0), Rotation = doubleQuat.Identity };
        const long tick = 5_000_000;

        foreach (var target in new[] { bodies[1].Inertial, bodies[1].Surface })
        {
            bodies.Update(tick - 1);
            tree.TryRelative(grid, target, out var before);
            bodies.Update(tick + 1);
            tree.TryRelative(grid, target, out var after);
            bodies.Update(tick);
            tree.TryRelative(grid, target, out var now);

            var difference = (after.Position - before.Position) / (2.0 * TickSeconds);
            Assert.True((difference - now.Velocity).Length() < 1e-6 * now.Velocity.Length(), $"{difference} vs {now.Velocity}");
        }
    }

    [Fact]
    public void SurfaceSpin_ComesFromTickModuloDay()
    {
        var (tree, bodies) = TestSystem();
        var planet = bodies[1];
        Assert.Equal((long)Math.Round(6 * 3600 / TickSeconds), planet.DayTicks);

        const long tick = 987_654;
        bodies.Update(tick);
        var spin = tree.Local[planet.Surface.Value];

        foreach (var days in new[] { 1L, 365L, 100_000L })
        {
            bodies.Update(tick + days * planet.DayTicks);
            Assert.Equal(spin.Rotation, tree.Local[planet.Surface.Value].Rotation);
            Assert.Equal(spin.AngularVelocity, tree.Local[planet.Surface.Value].AngularVelocity);
        }

        bodies.Update(planet.DayTicks / 4);
        Assert.True((tree.Local[planet.Surface.Value].Rotation * double3.UnitX + double3.UnitZ).Length() < 1e-12);

        tree.TryRelative(planet.Surface, planet.Inertial, out var surface);
        var equator = surface.TransformVelocity(new double3(planet.Radius, 0, 0), double3.Zero);
        Assert.Equal(2.0 * Math.PI * planet.Radius / (6 * 3600), equator.Length(), 1e-9);
    }

    [Fact]
    public void Gravity_SwitchesDominanceAtSoi()
    {
        var (tree, bodies) = TestSystem(tick: 42_000);
        var planet = bodies[1];
        var moon = bodies[2];
        var moonPosition = tree.Local[moon.Inertial.Value].Position;
        var outward = double3.Normalize(moonPosition);

        Assert.Equal(2, Gravity.Dominant(bodies, planet.Inertial, moonPosition - outward * (0.99 * moon.SoiRadius)));
        Assert.Equal(1, Gravity.Dominant(bodies, planet.Inertial, moonPosition - outward * (1.01 * moon.SoiRadius)));
        Assert.Equal(1, Gravity.Dominant(bodies, planet.Inertial, outward * (0.99 * planet.SoiRadius)));
        Assert.Equal(0, Gravity.Dominant(bodies, planet.Inertial, outward * (1.01 * planet.SoiRadius)));
        Assert.Equal(1, Gravity.Dominant(bodies, planet.Surface, new double3(0, planet.Radius, 0)));
        Assert.InRange(planet.SoiRadius, 4e7, 6e7);
        Assert.True(moon.SoiRadius < 0.5 * planet.SoiRadius);
    }

    [Fact]
    public void Gravity_IsOneGAtThePlanetSurfaceInAnyFrame()
    {
        var (tree, bodies) = TestSystem(tick: 777_777);
        var planet = bodies[1];
        var ground = new double3(0, 0, planet.Radius);

        var g = Gravity.Sample(bodies, planet.Surface, ground, out var body);
        Assert.Equal(1, body);
        Assert.Equal(CelestialSystem.StandardGravity, g.Length(), 1e-9);
        Assert.True((double3.Normalize(g) + double3.UnitZ).Length() < 1e-12);

        tree.TryRelative(planet.Surface, tree.System[planet.Surface.Value].Root, out var toSolar);
        var gSolar = Gravity.Sample(bodies, tree.System[planet.Surface.Value].Root, toSolar.TransformPoint(ground), out body);
        Assert.Equal(1, body);
        Assert.True((gSolar - toSolar.TransformVector(g)).Length() < 1e-4);
    }

    [Fact]
    public void SeveralSolarRoots_AreSeparateTrees()
    {
        var tree = new FrameTree();
        var bodies = new CelestialSystem(tree, TickSeconds);
        var mu = 9.80665e12;
        var starA = bodies.AddStar(1.3e20, 7e8);
        var starB = bodies.AddStar(2.0e20, 9e8);
        var rail = KeplerOrbit.Create(2.0e20, 3.0e10, 0.1, 0, 0, 0, 0, TickSeconds);
        var planetB = bodies.AddBody(starB, CelestialKind.Planet, mu, 1e6, 1e5, 1_296_000, double3.UnitY, rail);
        bodies.Update(1000);

        var rootA = bodies[starA].Inertial;
        var rootB = bodies[starB].Inertial;
        Assert.NotEqual(tree.System[rootA.Value], tree.System[rootB.Value]);
        Assert.Equal(tree.System[rootB.Value], tree.System[bodies[planetB].Surface.Value]);
        Assert.False(tree.TryRelative(rootA, bodies[planetB].Surface, out _));
        Assert.True(tree.TryRelative(rootB, bodies[planetB].Surface, out _));

        Assert.Equal(starA, Gravity.Dominant(bodies, rootA, new double3(3.0e10, 0, 0)));
        Assert.Equal(planetB, Gravity.Dominant(bodies, bodies[planetB].Surface, new double3(1e6, 0, 0)));
        Assert.Equal(starB, Gravity.Dominant(bodies, rootB, new double3(-3.0e10, 0, 0)));

        var tunnel = tree.AddRoot(FrameKind.Tunnel);
        Assert.False(tree.TryRelative(tunnel, rootB, out _));
        Assert.Equal(-1, Gravity.Dominant(bodies, tunnel, double3.Zero));
    }

    [Fact]
    public void TickUpdateAndQueries_AllocateNothing()
    {
        var (tree, bodies) = TestSystem();
        var moon = bodies[2];
        var grid = tree.Add(moon.Surface, FrameKind.Grid);
        var sum = double3.Zero;

        Run(0, 16);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run(16, 10_000);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(double.IsFinite(sum.X));

        void Run(long start, long count)
        {
            for (var tick = start; tick < start + count; tick++)
            {
                bodies.Update(tick);
                tree.TryRelative(grid, bodies[1].Surface, out var pose);
                sum += pose.Position + Gravity.Sample(bodies, grid, new double3(0, moon.Radius, 0), out _);
            }
        }
    }
}
