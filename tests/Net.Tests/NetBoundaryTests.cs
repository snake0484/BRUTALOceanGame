using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace BrutalGame.Net.Tests;

/// <summary>The dedicated server and the net layer must run headless: no renderer, window, UI or Engine anywhere below them.</summary>
public class NetBoundaryTests
{
    private static readonly string[] ForbiddenAssemblies = ["Brutal.Vulkan", "Brutal.Glfw", "Brutal.ImGui", "Brutal.ShaderC", "Engine", "Debug", "Game"];
    private static readonly string[] ForbiddenProjects = ["Engine", "Water.Rendering", "Terrain.Runtime", "Debug", "Game", "Designer.Ui", "Designer.App"];
    private static readonly string[] ForbiddenPackages = ["BRUTAL.Vulkan", "BRUTAL.Glfw", "BRUTAL.ImGui", "BRUTAL.ShaderC"];

    [Theory]
    [InlineData(typeof(NetServer))]
    [InlineData(typeof(Server.NpcScript))]
    public void Assembly_ReachesNoRenderWindowOrUi(Type root)
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

        Assert.Contains("Sim", seen);
        Assert.Contains("Brutal.RakNet", seen);
    }

    [Theory]
    [InlineData("Net", "Net.csproj")]
    [InlineData("Server", "Server.csproj")]
    [InlineData("Net.Client", "Net.Client.csproj")]
    public void Project_ReferencesNoRenderWindowOrUiProjectOrPackage(string folder, string file)
    {
        var root = Path.Combine(RepoRoot(), "src", folder, file);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var project = pending.Pop();

            if (!seen.Add(project))
                continue;

            Assert.DoesNotContain(Path.GetFileNameWithoutExtension(project), ForbiddenProjects);
            var xml = XDocument.Load(project);

            foreach (var package in xml.Descendants("PackageReference"))
                AssertPackageAllowed((string?)package.Attribute("Include") ?? "", project);

            foreach (var reference in xml.Descendants("ProjectReference"))
                pending.Push(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, ((string)reference.Attribute("Include")!).Replace('\\', Path.DirectorySeparatorChar))));
        }

        Assert.Contains(seen, p => p.EndsWith("Sim.csproj", StringComparison.OrdinalIgnoreCase));

        var assets = Path.Combine(Path.GetDirectoryName(root)!, "obj", "project.assets.json");

        if (folder != "Net.Client")
            Assert.True(File.Exists(assets), $"{folder} has no restore output");

        if (!File.Exists(assets))
            return;

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
            var hit = package.StartsWith("Ahwoo." + forbidden, StringComparison.OrdinalIgnoreCase)
                || package.StartsWith(forbidden.Replace("BRUTAL", "Brutal"), StringComparison.OrdinalIgnoreCase);
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
