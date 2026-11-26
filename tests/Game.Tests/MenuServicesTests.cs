using Brutal.GlfwApi;
using BrutalGame.Menu;

namespace BrutalGame.Game.Tests;

public class MenuServicesTests
{
    [Fact]
    public void KeyTableRoundTrip()
    {
        var keys = new KeyBindings();
        keys[GameAction.MoveUp] = GlfwKey.Space;
        var loaded = new KeyBindings();
        loaded.FromTable(keys.ToTable());
        Assert.True(loaded.SameAs(keys));
        Assert.Equal("Space", loaded.KeyName(GameAction.MoveUp));
    }

    [Fact]
    public void EveryActionHasADefaultAndALabel()
    {
        Assert.Equal(KeyBindings.Count, Enum.GetValues<GameAction>().Length);

        foreach (var action in Enum.GetValues<GameAction>())
        {
            Assert.NotEqual(GlfwKey.Unknown, KeyBindings.Default(action));
            Assert.False(string.IsNullOrEmpty(KeyBindings.Label(action)));
        }
    }

    [Fact]
    public void StubsReportNothingAvailable()
    {
        ISaveCatalog saves = new EmptySaveCatalog();
        saves.Refresh();
        Assert.Equal(0, saves.Slots.Length);

        ISessionLauncher sessions = new OfflineSessionLauncher();
        Assert.False(sessions.Available);
        Assert.False(sessions.Host(1, 27015).Started);
        Assert.False(sessions.Join("127.0.0.1", 27015).Started);
    }

    [Fact]
    public void DesignerIsFoundInTheSourceTreeLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "brutalgame-designer-" + Guid.NewGuid().ToString("N"));

        try
        {
            var game = Path.Combine(root, "src", "Game", "bin", "Release", "net10.0");
            var designer = Path.Combine(root, "src", "Designer.App", "bin", "Release", "net10.0");
            Directory.CreateDirectory(game);
            Assert.Null(DesignerLauncher.Find(game + Path.DirectorySeparatorChar));

            Directory.CreateDirectory(designer);
            File.WriteAllText(Path.Combine(designer, "BRUTALDesigner.exe"), "");
            Assert.Equal(Path.Combine(designer, "BRUTALDesigner.exe"), DesignerLauncher.Find(game + Path.DirectorySeparatorChar));

            File.WriteAllText(Path.Combine(game, "BRUTALDesigner.exe"), "");
            Assert.Equal(Path.Combine(game, "BRUTALDesigner.exe"), DesignerLauncher.Find(game));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
