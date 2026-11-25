using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim.Tests;

public class SimBoundaryTests
{
    private static readonly string[] Forbidden = ["Brutal.Vulkan", "Brutal.Glfw", "Brutal.ImGui", "Brutal.ShaderC", "Engine", "Debug"];
    private static readonly string[] ForbiddenProjects = ["Engine", "Water.Rendering", "Terrain.Runtime", "Debug", "Game"];
    private static readonly string[] ForbiddenPackages = ["BRUTAL.Vulkan", "BRUTAL.Glfw", "BRUTAL.ImGui", "BRUTAL.ShaderC"];

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

    /// <summary>
    /// The compiler drops unused references, so the assembly walk misses a ProjectReference nothing uses yet (it would
    /// still ship Engine's native packages). Walk Sim.csproj and its project references, then the restore's full
    /// package graph.
    /// </summary>
    [Fact]
    public void SimProject_ReferencesNoRenderWindowOrUiProjectOrPackage()
    {
        var sim = Path.Combine(RepoRoot(), "src", "Sim", "Sim.csproj");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(sim));

        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!seen.Add(project))
                continue;

            var name = Path.GetFileNameWithoutExtension(project);
            Assert.DoesNotContain(name, ForbiddenProjects);

            var xml = XDocument.Load(project);
            foreach (var package in xml.Descendants("PackageReference"))
                AssertPackageAllowed((string?)package.Attribute("Include") ?? "", name);

            foreach (var reference in xml.Descendants("ProjectReference"))
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))));
        }

        Assert.Contains(seen, p => p.EndsWith("Physics.csproj", StringComparison.OrdinalIgnoreCase));

        var assets = Path.Combine(Path.GetDirectoryName(sim)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), "Sim has no restore output");

        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        foreach (var library in json.RootElement.GetProperty("libraries"u8).EnumerateObject())
        {
            var id = library.Name[..library.Name.IndexOf('/')];
            if (library.Value.GetProperty("type"u8).GetString() == "project")
                Assert.DoesNotContain(id, ForbiddenProjects);
            else
                AssertPackageAllowed(id, "restore graph");
        }
    }

    private static void AssertPackageAllowed(string package, string from)
    {
        foreach (var forbidden in ForbiddenPackages)
        {
            var hit = package.Equals("Ahwoo." + forbidden, StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Ahwoo." + forbidden + ".", StringComparison.OrdinalIgnoreCase)
                || package.Equals(forbidden.Replace("BRUTAL", "Brutal"), StringComparison.OrdinalIgnoreCase)
                || package.StartsWith(forbidden.Replace("BRUTAL", "Brutal") + ".", StringComparison.OrdinalIgnoreCase);
            Assert.False(hit, $"{from} references {package}");
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BRUTALGame.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("BRUTALGame.slnx not found above the test output");
    }

    /// <summary>
    /// Ticks with no renderer, window or camera. Known M0 gap: terrain collision is still streamed by the client's
    /// TerrainWorld until M2, so a headless sim has no ground and the ball falls through empty space.
    /// </summary>
    [Fact]
    public void SimWorld_TicksHeadless()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 2, new TerrainParams());
        var ball = sim.Physics.AddDynamicSphere(new Vector3(0f, 50f, 0f), 0.5f);

        for (var i = 0; i < 60; i++)
            sim.Tick();

        Assert.Equal(60, sim.Clock.TickIndex);
        Assert.Equal(1.0, sim.Clock.SimTime, 9);
        Assert.Equal(sim.Clock.SimTime, sim.Ocean.Swell.Time, 9); // swell, physics and frames all describe the step's end
        Assert.True(sim.Physics.GetPosition(ball).Y < 50f - 4f);
        Assert.True(float.IsFinite(sim.Ocean.SampleHeight(0f, 0f)));
    }

    /// <summary>The swell's wind is a function of the tick alone: two sims with the same weather agree, whatever a client's camera does.</summary>
    [Fact]
    public void SwellWind_IsTickQuantisedAndCameraIndependent()
    {
        var climate = new WeatherClimate();
        using var a = new SimWorld(seed: 1, workerThreads: 1, new TerrainParams(), new WeatherField(climate)) { WeatherTimeOffset = 3600.0 };
        using var b = new SimWorld(seed: 1, workerThreads: 1, new TerrainParams(), new WeatherField(climate)) { WeatherTimeOffset = 3600.0 };
        var swell = new SwellParams { WindSpeed = 3f, SwellHeight = 1f, SwellPeriod = 12f };
        a.SetSwell(swell);
        b.SetSwell(swell);
        var sent = a.Ocean.Swell.Wind;

        for (var i = 0; i < 3 * SimWorld.WindInterval; i++)
        {
            var before = a.SeaWind;
            a.Tick();
            b.Tick();

            if (i % SimWorld.WindInterval != 0)
                Assert.Equal(before, a.SeaWind);
        }

        Assert.Equal(a.SeaWind, b.SeaWind);
        Assert.Equal(a.Ocean.Swell.Wind, b.Ocean.Swell.Wind);
        Assert.NotEqual(Vector2.Zero, a.SeaWind);
        Assert.True(sent.WindSpeed > 0f);
        Assert.Equal(a.Ocean.SampleHeight(10f, -400f), b.Ocean.SampleHeight(10f, -400f));
    }

    [Fact]
    public void Edits_ApplyAtTheirTickStart()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1, new TerrainParams());
        var land = TerrainShoreDepthTests.InlandDig(sim.Terrain);

        sim.QueueEdit(land);
        Assert.Equal(0, sim.Edits.Length);

        sim.Tick();
        Assert.Equal(1, sim.Edits.Length);
        Assert.Equal(1, sim.EditVersion);
        Assert.Equal(land, sim.Edits[0]);
    }

    /// <summary>D30: shores and seabeds are unminable; a dig there is refused, a place is not.</summary>
    [Fact]
    public void Edits_DiggingTheShoreOrSeabedIsRefused()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1, new TerrainParams());
        var p = sim.Terrain;
        var shore = TerrainShoreDepthTests.WaterlineX(p, 0f);

        sim.QueueEdit(new BrushOp(new Vector3(shore - 60f, -8f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air));
        sim.QueueEdit(new BrushOp(new Vector3(shore, 0f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air));
        sim.QueueEdit(new BrushOp(new Vector3(shore + ShoreGuard.Band, 1f, 0f), 3f, BrushMode.Dig, VoxelMaterial.Air));
        sim.QueueEdit(new BrushOp(new Vector3(shore, 0f, 0f), 3f, BrushMode.Place, VoxelMaterial.Rock));
        sim.Tick();

        Assert.Equal(3, sim.RefusedEdits);
        Assert.Equal(1, sim.Edits.Length);
        Assert.Equal(BrushMode.Place, sim.Edits[0].Mode);
    }
}
