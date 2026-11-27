using BrutalGame.Menu;

namespace BrutalGame.Game.Tests;

public class MenuLaunchTests
{
    [Theory]
    [InlineData("--bench")]
    [InlineData("--bench", "20")]
    [InlineData("--orbit-test")]
    [InlineData("--orbit-test", "inertial")]
    [InlineData("--planet-fly")]
    [InlineData("--fly-test")]
    [InlineData("--edit-test")]
    [InlineData("--moon-test")]
    [InlineData("--shore-fly")]
    [InlineData("--wind", "14", "--bench", "12")]
    [InlineData("--hide-ui", "--cam", "0", "10", "0", "0", "0")]
    [InlineData("--no-menu")]
    public void TestAndBenchFlagsSkipTheMenu(params string[] args)
    {
        Assert.True(MenuLaunch.SkipsMenu(args));
        Assert.Null(MenuLaunch.FromArgs(args));
    }

    [Theory]
    [InlineData]
    [InlineData("--wind", "14")]
    [InlineData("--render-scale", "0.75")]
    [InlineData("--weather-offset", "933")]
    [InlineData("--menu-test")]
    public void OtherLaunchesShowTheMenu(params string[] args)
    {
        Assert.False(MenuLaunch.SkipsMenu(args));
    }

    [Fact]
    public void SettingsFolderFlag()
    {
        var folder = Path.Combine(Path.GetTempPath(), "brutalgame-launch-" + Guid.NewGuid().ToString("N"));
        var launch = MenuLaunch.FromArgs(["--settings-dir", folder, "--menu-test"]);
        Assert.NotNull(launch);
        Assert.True(launch.Scripted);
        Assert.Equal(Path.Combine(folder, SettingsStore.FileName), launch.Settings.FilePath);
        Assert.Equal(MenuLaunch.DefaultSeed, launch.Seed);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void NewGameRelaunchesOnce()
    {
        var launch = new MenuLaunch(new SettingsStore(Path.Combine(Path.GetTempPath(), "brutalgame-none-" + Guid.NewGuid().ToString("N"))));
        Assert.False(launch.TakeRelaunch());
        launch.RequestNewGame(7);
        Assert.Equal(7UL, launch.Seed);
        Assert.True(launch.StartInGame);
        Assert.True(launch.TakeRelaunch());
        Assert.False(launch.TakeRelaunch());
    }
}
