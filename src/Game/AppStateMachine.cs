namespace BrutalGame;

/// <summary>
/// Boot -> MainMenu -> Loading -> Playing &lt;-> Paused -> MainMenu, and Quit from anywhere. Holds no world state: the
/// owner polls <see cref="State"/> and acts on a change.
/// </summary>
public sealed class AppStateMachine
{
    public AppState State { get; private set; } = AppState.Boot;

    public AppState Previous { get; private set; } = AppState.Boot;

    public int Transitions { get; private set; }

    /// <summary>Single player: the sim only advances while playing. A networked session keeps ticking under the pause menu.</summary>
    public bool SimRuns => State == AppState.Playing;

    public bool MenuVisible => State is AppState.MainMenu or AppState.Loading or AppState.Paused;

    public static bool TryNext(AppState from, AppTrigger trigger, out AppState to)
    {
        to = (from, trigger) switch
        {
            (AppState.Boot, AppTrigger.ShowMenu) => AppState.MainMenu,
            (AppState.Boot or AppState.MainMenu, AppTrigger.StartGame) => AppState.Loading,
            (AppState.Loading, AppTrigger.WorldReady) => AppState.Playing,
            (AppState.Playing, AppTrigger.Pause) => AppState.Paused,
            (AppState.Paused, AppTrigger.Resume) => AppState.Playing,
            (AppState.Paused or AppState.Loading, AppTrigger.ExitToMenu) => AppState.MainMenu,
            (not AppState.Quit, AppTrigger.Quit) => AppState.Quit,
            _ => from,
        };

        return to != from;
    }

    public bool Fire(AppTrigger trigger)
    {
        if (!TryNext(State, trigger, out var next))
            return false;

        Previous = State;
        State = next;
        Transitions++;
        return true;
    }
}
