using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>
/// ship-design-and-construction.md "Enforced, not promised": the headless assemblies (Plans, Designer, Ships) reach no
/// renderer, window, UI or Engine, so a server can run Plans; Designer.App reaches no Sim, networking or physics. An
/// authoring build of Designer.App (<c>-p:Authoring=true</c>, our planet tabs) adds the game's terrain view but still no
/// networking; the shipped app is the build without it.
/// Assemblies are walked (what the compiler kept) and so are the project files and restore graphs (what ships).
/// </summary>
public class DesignerBoundaryTests
{
    private static readonly string[] ForbiddenAssemblies = ["Brutal.Vulkan", "Brutal.Glfw", "Brutal.ImGui", "Brutal.ShaderC", "Engine", "Debug", "Designer.Ui"];
    private static readonly string[] HeadlessForbiddenProjects = ["Engine", "Designer.Ui", "Designer.App", "Debug", "Game", "Terrain.Runtime", "Water.Rendering", "Physics", "Sim"];
    private static readonly string[] HeadlessForbiddenPackages = ["BRUTAL.Vulkan", "BRUTAL.Glfw", "BRUTAL.ImGui", "BRUTAL.ShaderC", "BRUTAL.Jolt"];
    private static readonly string[] AppForbiddenProjects = ["Sim", "Physics", "Net", "Game", "Terrain.Runtime", "Water", "Water.Rendering", "Weather", "Debug"];
    private static readonly string[] AppForbiddenPackages = ["BRUTAL.Jolt", "BRUTAL.RakNet"];
    private static readonly string[] AuthoringForbiddenProjects = ["Net"];
    private static readonly string[] AuthoringForbiddenPackages = ["BRUTAL.RakNet"];

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

    [Fact]
    public void DesignerAppAuthoring_ReferencesNoNetworking() =>
        AssertProjectGraph(Path.Combine(RepoRoot(), "src", "Designer.App", "Designer.App.csproj"), AuthoringForbiddenProjects, AuthoringForbiddenPackages, authoring: true);

    /// <summary>
    /// Walks the project and its project references, then the restore's full package graph. Items conditioned on the
    /// Authoring property count only in that build; its restore output is in obj/authoring, checked when it has been built.
    /// </summary>
    private static void AssertProjectGraph(string root, string[] projects, string[] packages, bool authoring = false)
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
            {
                if (InBuild(package, authoring))
                    AssertPackageAllowed((string?)package.Attribute("Include") ?? "", packages, name);
            }

            foreach (var reference in xml.Descendants("ProjectReference"))
            {
                if (InBuild(reference, authoring))
                    pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))));
            }
        }

        var assets = Path.Combine(Path.GetDirectoryName(root)!, "obj", authoring ? "authoring" : "", "project.assets.json");

        if (authoring && !File.Exists(assets))
            return;

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

    /// <summary>Whether an item is in the build with Authoring on or off: its own or its ItemGroup's condition on Authoring decides.</summary>
    private static bool InBuild(XElement item, bool authoring)
    {
        var condition = ((string?)item.Attribute("Condition") ?? "") + ((string?)item.Parent?.Attribute("Condition") ?? "");

        if (condition.Contains("'$(Authoring)' == 'true'", StringComparison.Ordinal))
            return authoring;

        if (condition.Contains("'$(Authoring)' != 'true'", StringComparison.Ordinal))
            return !authoring;

        return true;
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
