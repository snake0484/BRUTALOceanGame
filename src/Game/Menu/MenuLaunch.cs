namespace BrutalGame.Menu;

/// <summary>
/// What <see cref="GameApp"/> needs to run behind the menu, kept across relaunches: settings, the world seed and whether
/// to go straight into the world. Null (a test or bench flag was given) means no menu at all.
/// </summary>
public sealed class MenuLaunch(SettingsStore settings)
{
    /// <summary>The shipped world's seed (D83): fixed by us, never entered by the player.</summary>
    public const ulong DefaultSeed = 1;
    public const string TestFlag = "--menu-test";
    public const string SettingsFolderFlag = "--settings-dir";

    private static readonly string[] BypassFlags =
    [
        "--bench",
        "--orbit-test",
        "--planet-fly",
        "--fly-test",
        "--edit-test",
        "--moon-test",
        "--shore-fly",
        "--surf-beach",
        "--surf-climb",
        "--no-faces",
        "--bench-faces",
        "--seam-test",
        "--planet-kind",
        "--cam",
        "--hide-ui",
        "--no-menu",
    ];

    public readonly SettingsStore Settings = settings;
    public ulong Seed = DefaultSeed;
    public bool StartInGame;

    /// <summary>--menu-test: <see cref="MenuTest"/> drives the states and logs the menu's frame cost.</summary>
    public bool Scripted;

    private bool _relaunch;

    public static bool SkipsMenu(ReadOnlySpan<string> args)
    {
        foreach (var arg in args)
        {
            if (Array.IndexOf(BypassFlags, arg) >= 0)
                return true;
        }

        return false;
    }

    public static MenuLaunch? FromArgs(string[] args)
    {
        if (SkipsMenu(args))
            return null;

        var folder = SettingsStore.DefaultFolder;
        var flag = Array.IndexOf(args, SettingsFolderFlag);

        if (flag >= 0 && flag + 1 < args.Length)
            folder = args[flag + 1];

        return new MenuLaunch(new SettingsStore(folder)) { Scripted = Array.IndexOf(args, TestFlag) >= 0 };
    }

    /// <summary>A fresh world needs a fresh <see cref="GameApp"/>: Program disposes this one and builds the next from <see cref="Seed"/>.</summary>
    public void RequestNewGame(ulong seed)
    {
        Seed = seed;
        StartInGame = true;
        _relaunch = true;
    }

    public bool TakeRelaunch()
    {
        var relaunch = _relaunch;
        _relaunch = false;
        return relaunch;
    }
}
