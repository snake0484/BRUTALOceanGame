using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace BrutalGame.Crafting.Tests;

/// <summary>Crafting is a headless library: it reaches no renderer, window, UI, physics or engine, only the BCL.</summary>
public class CraftingBoundaryTests
{
    private static readonly string[] Forbidden = ["Brutal", "Engine", "Debug", "Game", "Designer", "Ships", "Plans", "Terrain", "Water", "Physics", "Sim", "Frames"];

    [Fact]
    public void CraftingAssembly_ReferencesOnlyTheBcl()
    {
        foreach (var reference in typeof(CraftingData).Assembly.GetReferencedAssemblies())
        {
            var name = reference.Name!;
            Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name is "netstandard" or "mscorlib", $"Crafting references {name}");

            foreach (var f in Forbidden)
                Assert.False(name == f || name.StartsWith(f + ".", StringComparison.Ordinal), $"Crafting references {name}");
        }
    }

    [Fact]
    public void CraftingProject_HasNoProjectOrPackageReferences()
    {
        var project = Path.Combine(RepoRoot(), "src", "Crafting", "Crafting.csproj");
        var xml = XDocument.Load(project);

        Assert.Empty(xml.Descendants("ProjectReference"));
        Assert.Empty(xml.Descendants("PackageReference"));

        var assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
        Assert.True(File.Exists(assets), "Crafting has no restore output");
        using var json = JsonDocument.Parse(File.ReadAllText(assets));
        Assert.Empty(json.RootElement.GetProperty("libraries"u8).EnumerateObject());
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
