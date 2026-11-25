using BrutalGame.Terrain;

namespace BrutalGame.Sim.Tests;

public class SimFramesTests
{
    [Fact]
    public void Tick_RefreshesRailsForTheTick()
    {
        using var sim = new SimWorld(seed: 3, workerThreads: 1, new TerrainParams());

        for (var i = 0; i < 5; i++)
            sim.Tick();

        var moon = sim.Celestial[2];
        moon.Orbit.State(4, sim.Clock.TickSeconds, out var position, out var velocity);

        Assert.Equal(4, sim.Celestial.Tick);
        Assert.Equal(position, sim.Frames.Local[moon.Inertial.Value].Position);
        Assert.Equal(velocity, sim.Frames.Local[moon.Inertial.Value].Velocity);
    }
}
