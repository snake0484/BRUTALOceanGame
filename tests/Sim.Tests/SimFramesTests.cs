using BrutalGame.Terrain;

namespace BrutalGame.Sim.Tests;

public class SimFramesTests
{
    /// <summary>The convention: after Tick() physics and frame poses both describe TickIndex dt, TickIndex already incremented.</summary>
    [Fact]
    public void Tick_LeavesRailsAtTheStepsEnd()
    {
        using var sim = new SimWorld(seed: 3, workerThreads: 1);
        var moon = sim.Celestial[2];

        Assert.Equal(0, sim.Clock.TickIndex);
        Assert.Equal(0, sim.Celestial.Tick);

        for (var i = 0; i < 5; i++)
            sim.Tick();

        moon.Orbit.State(sim.Clock.TickIndex, sim.Clock.TickSeconds, out var position, out var velocity);

        Assert.Equal(5, sim.Clock.TickIndex);
        Assert.Equal(sim.Clock.TickIndex, sim.Celestial.Tick);
        Assert.Equal(sim.Clock.SimTime, sim.Celestial.Tick * sim.Celestial.TickSeconds);
        Assert.Equal(position, sim.Frames.Local[moon.Inertial.Value].Position);
        Assert.Equal(velocity, sim.Frames.Local[moon.Inertial.Value].Velocity);
    }

    /// <summary>Render poses trail the sim by one tick plus alpha, between the last two ticks' states.</summary>
    [Fact]
    public void RenderPoses_InterpolateBetweenTheLastTwoTicks()
    {
        using var sim = new SimWorld(seed: 3, workerThreads: 1);
        var moon = sim.Celestial[2];
        var ticks = sim.Advance(5.5 / SimWorld.TickRate);

        for (var i = 0; i < ticks; i++)
            sim.Tick();

        sim.UpdateRenderPoses();
        var alpha = sim.Clock.Alpha;
        moon.Orbit.State(sim.Clock.TickIndex - 1, sim.Clock.TickSeconds, out var position, out _, alpha);

        Assert.Equal(5, ticks);
        Assert.InRange(alpha, 0.49, 0.51);
        Assert.Equal(sim.Clock.TickIndex - 1, sim.Celestial.RenderTick);
        Assert.Equal((sim.Clock.TickIndex - 1 + alpha) * sim.Clock.TickSeconds, sim.RenderTime);
        Assert.Equal(position, sim.Frames.Render[moon.Inertial.Value].Position);
    }
}
