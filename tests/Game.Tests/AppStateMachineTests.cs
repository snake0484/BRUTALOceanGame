namespace BrutalGame.Game.Tests;

public class AppStateMachineTests
{
    [Fact]
    public void FullSessionPath()
    {
        var app = new AppStateMachine();
        Assert.Equal(AppState.Boot, app.State);

        Step(app, AppTrigger.ShowMenu, AppState.MainMenu);
        Step(app, AppTrigger.StartGame, AppState.Loading);
        Step(app, AppTrigger.WorldReady, AppState.Playing);
        Step(app, AppTrigger.Pause, AppState.Paused);
        Step(app, AppTrigger.Resume, AppState.Playing);
        Step(app, AppTrigger.Pause, AppState.Paused);
        Step(app, AppTrigger.ExitToMenu, AppState.MainMenu);
        Step(app, AppTrigger.Quit, AppState.Quit);
        Assert.Equal(8, app.Transitions);
    }

    [Fact]
    public void BootCanStartStraightIntoAWorld()
    {
        var app = new AppStateMachine();
        Step(app, AppTrigger.StartGame, AppState.Loading);
        Assert.Equal(AppState.Boot, app.Previous);
    }

    [Theory]
    [InlineData(AppState.Boot)]
    [InlineData(AppState.MainMenu)]
    [InlineData(AppState.Loading)]
    [InlineData(AppState.Playing)]
    [InlineData(AppState.Paused)]
    public void QuitFromAnywhere(AppState from)
    {
        Assert.True(AppStateMachine.TryNext(from, AppTrigger.Quit, out var to));
        Assert.Equal(AppState.Quit, to);
    }

    [Fact]
    public void QuitIsFinal()
    {
        foreach (var trigger in Enum.GetValues<AppTrigger>())
            Assert.False(AppStateMachine.TryNext(AppState.Quit, trigger, out _));
    }

    [Theory]
    [InlineData(AppState.Boot, AppTrigger.Pause)]
    [InlineData(AppState.Boot, AppTrigger.WorldReady)]
    [InlineData(AppState.MainMenu, AppTrigger.Pause)]
    [InlineData(AppState.MainMenu, AppTrigger.Resume)]
    [InlineData(AppState.MainMenu, AppTrigger.WorldReady)]
    [InlineData(AppState.MainMenu, AppTrigger.ExitToMenu)]
    [InlineData(AppState.Loading, AppTrigger.Pause)]
    [InlineData(AppState.Loading, AppTrigger.StartGame)]
    [InlineData(AppState.Playing, AppTrigger.StartGame)]
    [InlineData(AppState.Playing, AppTrigger.ExitToMenu)]
    [InlineData(AppState.Playing, AppTrigger.Resume)]
    [InlineData(AppState.Paused, AppTrigger.Pause)]
    [InlineData(AppState.Paused, AppTrigger.StartGame)]
    public void InvalidTriggersAreIgnored(AppState from, AppTrigger trigger)
    {
        Assert.False(AppStateMachine.TryNext(from, trigger, out var to));
        Assert.Equal(from, to);
    }

    [Fact]
    public void LoadingCanBeCancelled()
    {
        Assert.True(AppStateMachine.TryNext(AppState.Loading, AppTrigger.ExitToMenu, out var to));
        Assert.Equal(AppState.MainMenu, to);
    }

    [Fact]
    public void SimRunsOnlyWhilePlaying()
    {
        var app = new AppStateMachine();
        Assert.False(app.SimRuns);
        app.Fire(AppTrigger.ShowMenu);
        Assert.False(app.SimRuns);
        Assert.True(app.MenuVisible);
        app.Fire(AppTrigger.StartGame);
        Assert.False(app.SimRuns);
        app.Fire(AppTrigger.WorldReady);
        Assert.True(app.SimRuns);
        Assert.False(app.MenuVisible);
        app.Fire(AppTrigger.Pause);
        Assert.False(app.SimRuns);
        Assert.True(app.MenuVisible);
    }

    [Fact]
    public void RefusedTriggerChangesNothing()
    {
        var app = new AppStateMachine();
        app.Fire(AppTrigger.ShowMenu);
        Assert.False(app.Fire(AppTrigger.Resume));
        Assert.Equal(AppState.MainMenu, app.State);
        Assert.Equal(AppState.Boot, app.Previous);
        Assert.Equal(1, app.Transitions);
    }

    private static void Step(AppStateMachine app, AppTrigger trigger, AppState expected)
    {
        Assert.True(app.Fire(trigger), $"{trigger} from {app.State}");
        Assert.Equal(expected, app.State);
    }
}
