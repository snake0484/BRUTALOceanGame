using Brutal.GlfwApi;
using BrutalGame.Menu;

namespace BrutalGame.Game.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "brutalgame-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private string FilePath => Path.Combine(_folder, SettingsStore.FileName);

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var store = new SettingsStore(_folder);
        Assert.Equal(SettingsLoad.Missing, store.LastLoad);
        Assert.True(store.Current.SameAs(new GameSettings()));
    }

    [Fact]
    public void RoundTrip()
    {
        var store = new SettingsStore(_folder);
        var s = store.Current;
        s.RenderScale = 0.75f;
        s.VSync = false;
        s.WindowMode = WindowMode.Fullscreen;
        s.Width = 2560;
        s.Height = 1440;
        s.FovDegrees = 85f;
        s.MouseSensitivity = 1.7f;
        s.InvertY = true;
        s.MasterVolume = 0.3f;
        s.Keys[GameAction.MoveForward] = GlfwKey.Up;
        s.Keys[GameAction.Pause] = GlfwKey.P;
        Assert.True(store.Save());

        var loaded = new SettingsStore(_folder);
        Assert.Equal(SettingsLoad.Loaded, loaded.LastLoad);
        Assert.True(loaded.Current.SameAs(s));
        Assert.Equal(GlfwKey.Up, loaded.Current.Keys[GameAction.MoveForward]);
        Assert.Equal(GlfwKey.P, loaded.Current.Keys[GameAction.Pause]);
    }

    [Fact]
    public void FileIsVersionedJson()
    {
        var store = new SettingsStore(_folder);
        store.Save();
        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"keys\"", text);
        Assert.Contains("\"MoveForward\": \"W\"", text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"version\": 1, \"fovDegrees\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"version\": \"one\" }")]
    [InlineData("{ \"fovDegrees\": 90 }")]
    [InlineData("null")]
    public void CorruptFileFallsBackToDefaultsAndIsKept(string content)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, content);

        var store = new SettingsStore(_folder);
        Assert.Equal(SettingsLoad.Corrupt, store.LastLoad);
        Assert.True(store.Current.SameAs(new GameSettings()));
        Assert.Equal(content, File.ReadAllText(FilePath + SettingsStore.BadSuffix));

        Assert.True(store.Save());
        Assert.Equal(SettingsLoad.Loaded, new SettingsStore(_folder).LastLoad);
    }

    [Fact]
    public void NewerVersionFallsBackToDefaults()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ \"version\": 99, \"fovDegrees\": 90 }");

        var store = new SettingsStore(_folder);
        Assert.Equal(SettingsLoad.NewerVersion, store.LastLoad);
        Assert.Equal(GameSettings.DefaultFov, store.Current.FovDegrees);
        Assert.True(File.Exists(FilePath + SettingsStore.BadSuffix));
    }

    [Fact]
    public void PartialFileKeepsDefaultsForMissingValues()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ \"version\": 1, \"fovDegrees\": 90, \"keys\": { \"Pause\": \"P\" } }");

        var store = new SettingsStore(_folder);
        Assert.Equal(SettingsLoad.Loaded, store.LastLoad);
        Assert.Equal(90f, store.Current.FovDegrees);
        Assert.True(store.Current.VSync);
        Assert.Equal(GlfwKey.P, store.Current.Keys[GameAction.Pause]);
        Assert.Equal(GlfwKey.W, store.Current.Keys[GameAction.MoveForward]);
    }

    [Fact]
    public void OutOfRangeAndUnknownValuesAreSanitized()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, """
            {
              // hand edited
              "version": 1,
              "renderScale": 9,
              "fovDegrees": -5,
              "mouseSensitivity": 100,
              "masterVolume": 2,
              "width": 10,
              "height": 99999,
              "windowMode": "Sideways",
              "keys": { "MoveForward": "NotAKey", "Jump": "Space", "Slow": "Unknown" },
              "somethingNew": true,
            }
            """);

        var store = new SettingsStore(_folder);
        var s = store.Current;
        Assert.Equal(SettingsLoad.Loaded, store.LastLoad);
        Assert.Equal(GameSettings.MaxRenderScale, s.RenderScale);
        Assert.Equal(GameSettings.MinFov, s.FovDegrees);
        Assert.Equal(GameSettings.MaxSensitivity, s.MouseSensitivity);
        Assert.Equal(1f, s.MasterVolume);
        Assert.Equal(GameSettings.MinWidth, s.Width);
        Assert.Equal(GameSettings.MaxHeight, s.Height);
        Assert.Equal(WindowMode.Windowed, s.WindowMode);
        Assert.Equal(GlfwKey.W, s.Keys[GameAction.MoveForward]);
        Assert.Equal(GlfwKey.LeftControl, s.Keys[GameAction.Slow]);
    }

    [Fact]
    public void NonFiniteValuesFallBack()
    {
        var s = new GameSettings { FovDegrees = float.NaN, RenderScale = float.PositiveInfinity, MouseSensitivity = float.NegativeInfinity };
        s.Sanitize();
        Assert.Equal(GameSettings.DefaultFov, s.FovDegrees);
        Assert.Equal(GameSettings.MaxRenderScale, s.RenderScale);
        Assert.Equal(1f, s.MouseSensitivity);
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var s = new GameSettings { FovDegrees = 100f, InvertY = true };
        s.Keys[GameAction.Fast] = GlfwKey.Tab;
        s.Reset();
        Assert.True(s.SameAs(new GameSettings()));
    }

    [Fact]
    public void UnwritableFolderDoesNotThrow()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "file"), "");
        var store = new SettingsStore(Path.Combine(_folder, "file"));
        Assert.Equal(SettingsLoad.Missing, store.LastLoad);
        Assert.False(store.Save());
    }
}
