namespace BrutalGame;

public enum AppState : byte
{
    Boot,
    MainMenu,
    Loading,
    Playing,
    Paused,
    Quit,
}

public enum AppTrigger : byte
{
    ShowMenu,
    StartGame,
    WorldReady,
    Pause,
    Resume,
    ExitToMenu,
    Quit,
}
