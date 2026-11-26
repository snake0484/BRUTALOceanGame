namespace BrutalGame.Menu;

public readonly record struct SessionResult(bool Started, string Message);

/// <summary>
/// Starts a world session from the menu. Single player is a listen server with no remote peers, so New Game, Load,
/// Host and Join all end up here once networking is merged; today only Host and Join are asked of it.
/// </summary>
public interface ISessionLauncher
{
    /// <summary>False greys out Host and Join and shows <see cref="Status"/> instead.</summary>
    bool Available { get; }

    string Status { get; }

    SessionResult Host(ulong seed, ushort port);

    SessionResult Join(string address, ushort port);

    SessionResult Load(in SaveSlot slot);
}

/// <summary>Stub until src/Net and src/Server are merged.</summary>
public sealed class OfflineSessionLauncher : ISessionLauncher
{
    private const string Offline = "Multiplayer is not in this build yet.";
    private const string NoSaves = "Saves are not in this build yet.";

    public bool Available => false;

    public string Status => Offline;

    public SessionResult Host(ulong seed, ushort port) => new(false, Offline);

    public SessionResult Join(string address, ushort port) => new(false, Offline);

    public SessionResult Load(in SaveSlot slot) => new(false, NoSaves);
}
