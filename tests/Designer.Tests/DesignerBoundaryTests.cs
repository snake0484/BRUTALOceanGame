using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>
/// ship-design-and-construction.md "Enforced, not promised": the headless assemblies (Plans, Designer, Ships) reach no
/// renderer, window, UI or Engine, so a server can run Plans; Designer.App reaches no Sim, networking or physics.
/// Assemblies are walked (what the compiler kept) and so are the project files and restore graphs (what ships).
/// </summary>
public class DesignerBoundaryTests
{
    private static readonly string[] ForbiddenAssemblies = ["Brutal.Vulkan", "Brutal.Glfw", "Brutal.ImGui", "Brutal.ShaderC", "Engine", "Debug", "Designer.Ui"];
    private static readonly string[] HeadlessForbiddenProjects = ["Engine", "Designer.Ui", "Designer.App", "Debug", "Game", "Terrain.Runtime", "Water.Rendering", "Physics", "Sim"];
    private static readonly string[] HeadlessForbiddenPackages = ["BRUTAL.Vulkan", "BRUTAL.Glfw", "BRUTAL.ImGui", "BRUTAL.ShaderC", "BRUTAL.Jolt"];
    private static readonly string[] AppForbiddenProjects = ["Sim", "Physics", "Net", "Game", "Terrain.Runtime", "Water", "Water.Rendering", "Weather", "Debug"];
    private static readonly string[] AppForbiddenPackages = ["BRUTAL.Jolt", "BRUTAL.RakNet"];

    [Theory]
    [InlineData(typeof(PlanDocument))]
    [InlineData(typeof(DesignSession))]
    [InlineData(typeof(HullBaker))]
    public void HeadlessAssemblies_ReachNoRendererWindowOrUi(Type root)
    {
        var seen = new HashSet<string>();
        var pending = new Stack<AssemblyName>();
        pending.Push(root.Assembly.GetName());

        while (pending.Count > 0)
        {
            var name = pending.Pop();

            if (!seen.Add(name.Name!))
                continue;

            foreach (var forbidden in ForbiddenAssemblies)
                Assert.False(name.Name == forbidden || name.Name!.StartsWith(forbidden + "."), $"{root.Assembly.GetName().Name} reaches {name.Name}");

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
                pending.Push(reference);
        }

        Assert.Contains("Ships", seen);
    }

    [Theory]
    [InlineData("Plans")]
    [InlineData("Designer")]
    [InlineData("Ships")]
    public void HeadlessProjects_ReferenceNoRendererWindowUiOrPhysics(string project) =>
        AssertProjectGraph(Path.Combine(RepoRoot(), "src", project, project + ".csproj"), HeadlessForbiddenProjects, HeadlessForbiddenPackages);

    [Fact]
    public void DesignerApp_ReferencesNoSimNetworkingOrPhysics() =>
        AssertProjectGraph(Path.Combine(RepoRoot(), "src", "Designer.App", "Designer.App.csproj"), AppForbiddenProjects, AppForbiddenPackages);

    /// <summary>Walks the project and its project references, then the restore's full package graph when present.</summary>
    private static void AssertProjectGraph(string root, string[] projects, string[] packages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var project = pending.Pop();

            if (!seen.Add(project))
                continue;

            var name = Path.GetFileNameWithoutExtension(project);
            Assert.DoesNotContain(name, projects);
            var xml = XDocument.Load(project);

            foreach (var package in xml.Descendants("PackageReference"))
                AssertPackageAllowed((string?)package.Attribute("Include") ?? "", packages, name);

            foreach (var reference in xml.Descendants("ProjectReference"))
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))));
        }

        var assets = Path.Combine(Path.GetDirectoryName(root)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{root} has no restore output");

        using var json = JsonDocument.Parse(File.ReadAllText(assets));

        foreach (var library in json.RootElement.GetProperty("libraries"u8).EnumerateObject())
        {
            var id = library.Name[..library.Name.IndexOf('/')];

            if (library.Value.GetProperty("type"u8).GetString() == "project")
                Assert.DoesNotContain(id, projects);
            else
                AssertPackageAllowed(id, packages, "restore graph");
        }
    }

    private static void AssertPackageAllowed(string package, string[] forbidden, string from)
    {
        foreach (var f in forbidden)
        {
            var hit = package.Equals("Ahwoo." + f, StringComparison.OrdinalIgnoreCase)
                || package.StartsWith("Ahwoo." + f + ".", StringComparison.OrdinalIgnoreCase);
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
}
