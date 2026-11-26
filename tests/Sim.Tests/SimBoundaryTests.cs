using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Brutal.Numerics;
using BrutalGame.Terrain;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Sim.Tests;

public class SimBoundaryTests
{
    /// <summary>The nearest point to the spawn, out along 16 bearings of the sea frame, where the sea is this deep over open seabed (the coast is the planet kind's).</summary>
    private static double3 Offshore(SimWorld sim, float depth)
    {
        var surface = sim.Generator.Surface;

        for (var range = 1000f; range <= 400_000f; range += 1000f)
        {
            for (var k = 0; k < 16; k++)
            {
                var bearing = k * (MathF.PI / 8f);
                var d = double3.Normalize(sim.Bubble.ToPlanet(new Vector3(range * MathF.Cos(bearing), 0f, range * MathF.Sin(bearing))));

                if (surface.Height(d.X, d.Y, d.Z) > surface.SeaHeight - depth)
                    continue;

                // Open seabed, not a pit of the coastal ramp: the ground all round is under water too.
                surface.Bounds(d.X, d.Y, d.Z, 5e-5, 0, 0, out _, out var highest);

                if (highest < surface.SeaHeight - 0.5f * depth)
                    return d;
            }
        }

        throw new InvalidOperationException($"No sea {depth} m deep within 400 km of the spawn");
    }

    /// <summary>Straight out to sea from the spawn (the bubble's +x), the first point within 40 km where the sea is this deep.</summary>
    private static double3 Seaward(SimWorld sim, float depth)
    {
        var surface = sim.Generator.Surface;

        for (var x = 500f; x <= 40_000f; x += 500f)
        {
            var d = Dir(sim, x);

            if (surface.Height(d.X, d.Y, d.Z) <= surface.SeaHeight - depth)
                return d;
        }

        throw new InvalidOperationException($"No sea {depth} m deep within 40 km straight out from the spawn");
    }

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
    /// Ticks with no renderer, window or camera. Known gap: terrain collision is still streamed by the client's
    /// TerrainWorld round the camera (sim-owned anchors are M3), so a headless sim has no ground and the ball falls.
    /// </summary>
    [Fact]
    public void SimWorld_TicksHeadless()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 2);
        var ball = sim.Physics.AddDynamicSphere(new Vector3(0f, 50f, 0f), 0.5f);

        for (var i = 0; i < 60; i++)
            sim.Tick();

        Assert.Equal(60, sim.Clock.TickIndex);
        Assert.Equal(1.0, sim.Clock.SimTime, 9);
        Assert.Equal(sim.Clock.SimTime, sim.Ocean.Swell.Time, 9); // swell, physics and frames all describe the step's end
        Assert.True(sim.Physics.GetPosition(ball).Y < 50f - 4f);
        Assert.True(float.IsFinite(sim.Ocean.SampleHeight(sim.Bubble.ToPlanet(new Vector3(300f, 0f, 0f)), sim.Clock.TickIndex)));
    }

    /// <summary>The swell's wind is a function of the tick alone: two sims with the same weather agree, whatever a client's camera does.</summary>
    [Fact]
    public void SwellWind_IsTickQuantisedAndCameraIndependent()
    {
        var climate = new WeatherClimate();
        using var a = new SimWorld(seed: 1, workerThreads: 1, new WeatherField(climate)) { WeatherTimeOffset = 3600.0 };
        using var b = new SimWorld(seed: 1, workerThreads: 1, new WeatherField(climate)) { WeatherTimeOffset = 3600.0 };
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
        var offshore = a.Bubble.ToPlanet(new Vector3(400f, 0f, -400f));
        Assert.Equal(a.Ocean.SampleHeight(offshore, a.Clock.TickIndex), b.Ocean.SampleHeight(offshore, b.Clock.TickIndex));
        Assert.NotEqual(0f, a.Ocean.SampleHeight(offshore, a.Clock.TickIndex));
    }

    /// <summary>An edit <paramref name="below"/> m under the generated ground at a point of the bubble's plane (x out to sea).</summary>
    private static EditOp Under(SimWorld sim, float x, float below, BrushMode mode) => Under(sim, double3.Normalize(sim.Bubble.ToPlanet(new Vector3(x, 0f, 0f))), below, mode);

    private static EditOp Under(SimWorld sim, double3 d, float below, BrushMode mode)
    {
        var r = sim.Planet.Radius + sim.Generator.Surface.Height(d.X, d.Y, d.Z) - below;
        return EditOp.At(d.X * r, d.Y * r, d.Z * r, 3f, mode, mode == BrushMode.Dig ? VoxelMaterial.Air : VoxelMaterial.Rock);
    }

    [Fact]
    public void Edits_ApplyAtTheirTickStart()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1);
        var land = Under(sim, -400f, 1f, BrushMode.Dig);

        sim.QueueEdit(land);
        Assert.Equal(0, sim.Edits.Length);

        sim.Tick();
        Assert.Equal(1, sim.Edits.Length);
        Assert.Equal(1, sim.EditVersion);
        Assert.Equal(land with { Seq = 1 }, sim.Edits[0]);
    }

    /// <summary>D30: shores and seabeds are unminable; an edit wholly inside the protected band is dropped unlogged, inland is not.</summary>
    [Fact]
    public void Edits_InsideTheProtectedSeabedAreDropped()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1);
        // Where the sea is 70 m deep the seabed's conservative bounds sit wholly under the protected band.
        var surface = sim.Generator.Surface;
        var offshore = Seaward(sim, 70f);
        surface.Bounds(offshore.X, offshore.Y, offshore.Z, 5e-5, 0, 0, out var lowest, out var highest);
        var seabed = surface.Height(offshore.X, offshore.Y, offshore.Z);
        Assert.True(highest < surface.ProtectHeight - 30f && lowest > seabed - 40f, $"Seabed at {seabed} m bounded {lowest} .. {highest} m, band top {surface.ProtectHeight} m");
        sim.QueueEdit(Under(sim, offshore, 400f, BrushMode.Dig));
        sim.QueueEdit(Under(sim, offshore, 400f, BrushMode.Place));
        sim.QueueEdit(Under(sim, -400f, 150f, BrushMode.Dig));
        sim.Tick();

        Assert.Equal(2, sim.RefusedEdits);
        Assert.Equal(1, sim.Edits.Length);
        Assert.Equal(BrushMode.Dig, sim.Edits[0].Mode);
    }

    /// <summary>The spawn is on a coast: sea level at the bubble's origin, sea out along +x, land in along -x.</summary>
    [Fact]
    public void Spawn_IsOnACoastFacingTheSea()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1);
        var surface = sim.Generator.Surface;
        var origin = double3.Normalize(sim.Bubble.Origin);

        Assert.InRange(surface.Height(origin.X, origin.Y, origin.Z) - surface.SeaHeight, -1.0f, 1.0f);
        Assert.InRange(sim.Bubble.Origin.Length(), sim.Planet.SeaRadius - 1e-6, sim.Planet.SeaRadius + 1e-6);
        var depth = new PlanetShoreDepth(sim.Shore);
        Assert.True(depth.Depth(Dir(sim, 200f)) > 0f, "out to sea is water");
        Assert.True(depth.Depth(Dir(sim, -400f)) < -3f, "inland is dry land");
        Assert.True(sim.Physics.Gravity.Y < -9.7f && MathF.Abs(sim.Physics.Gravity.X) < 0.1f && MathF.Abs(sim.Physics.Gravity.Z) < 0.1f);
    }

    /// <summary>
    /// The sea is the planet's, not the bubble's (review C5, C7): fill placed on the seabed makes the water shallower for
    /// the ocean's depth cap at once, on a sim with no camera, and the sea is the same function of planet position and
    /// tick whichever way it is asked.
    /// </summary>
    [Fact]
    public void PlacedFill_ShallowsTheSea()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1);
        sim.SetSwell(new SwellParams { WindSpeed = 8f, SwellHeight = 1.5f, SwellPeriod = 12f });
        var surface = sim.Generator.Surface;
        var d = Dir(sim, 120f);
        var before = sim.Shore.Depth(d.X, d.Y, d.Z);
        Assert.True(before > 0.5f, $"120 m out is water ({before} m)");

        // A mound whose top stands 2 m above the sea.
        var ground = sim.Planet.Radius + surface.Height(d.X, d.Y, d.Z);
        var radius = before + 2f;
        sim.QueueEdit(EditOp.At(d.X * ground, d.Y * ground, d.Z * ground, radius, BrushMode.Place, VoxelMaterial.Rock));

        for (var i = 0; i < 30; i++)
            sim.Tick();

        Assert.Equal(1, sim.Edits.Length);
        Assert.Equal(1, sim.Shore.IndexedOps);
        Assert.Equal(-2f, sim.Shore.Depth(d.X, d.Y, d.Z), 0.01f);
        Assert.Equal(0f, sim.Ocean.SampleHeight(d * sim.Planet.SeaRadius, sim.Clock.TickIndex));

        var open = Offshore(sim, 30f);
        Assert.NotEqual(0f, sim.Ocean.SampleHeight(open * sim.Planet.SeaRadius, sim.Clock.TickIndex));
        Assert.Equal(sim.Ocean.SampleHeight(open * sim.Planet.SeaRadius, sim.Clock.TickIndex), sim.Ocean.SampleHeight(open * (sim.Planet.SeaRadius + 500.0), sim.Clock.TickIndex));
    }

    private static double3 Dir(SimWorld sim, float x) => double3.Normalize(sim.Bubble.ToPlanet(new Vector3(x, 0f, 0f)));
}
