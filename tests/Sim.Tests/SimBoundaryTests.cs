using System.Numerics;
using System.Reflection;
using BrutalGame.Terrain;

namespace BrutalGame.Sim.Tests;

public class SimBoundaryTests
{
    private static readonly string[] Forbidden = ["Brutal.Vulkan", "Brutal.Glfw", "Brutal.ImGui", "Brutal.ShaderC", "Engine", "Debug"];

    [Fact]
    public void Sim_ReferencesNoRenderWindowOrUiAssembly()
    {
        var seen = new HashSet<string>();
        var pending = new Stack<AssemblyName>();
        pending.Push(typeof(SimWorld).Assembly.GetName());

        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!seen.Add(name.Name!))
                continue;

            foreach (var forbidden in Forbidden)
                Assert.False(name.Name == forbidden || name.Name!.StartsWith(forbidden + "."), $"Sim reaches {name.Name}");

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
                pending.Push(reference);
        }

        Assert.Contains("Physics", seen);
        Assert.Contains("Water", seen);
    }

    [Fact]
    public void SimWorld_TicksHeadless()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 2);
        sim.SnapshotTerrain(new TerrainParams(), []);
        var ball = sim.Physics.AddDynamicSphere(new Vector3(0f, 50f, 0f), 0.5f);

        for (var i = 0; i < 60; i++)
            sim.Tick();

        Assert.Equal(60, sim.Clock.TickIndex);
        Assert.Equal(1.0, sim.Clock.SimTime, 9);
        Assert.Equal(59.0 / 60.0, sim.Ocean.Swell.Time, 9);
        Assert.True(sim.Physics.GetPosition(ball).Y < 50f - 4f);
        Assert.True(float.IsFinite(sim.Ocean.SampleHeight(0f, 0f)));
    }
}
