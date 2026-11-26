using Brutal.GlfwApi;

namespace BrutalGame.Menu;

public enum GameAction : byte
{
    MoveForward,
    MoveBack,
    MoveLeft,
    MoveRight,
    MoveUp,
    MoveDown,
    Fast,
    Slow,
    Pause,
}

/// <summary>Action to key table. Saved by name (<see cref="ToTable"/>), so actions and keys can be added without breaking old files.</summary>
public sealed class KeyBindings
{
    public const int Count = 9;

    private static readonly GlfwKey[] Defaults =
    [
        GlfwKey.W,
        GlfwKey.S,
        GlfwKey.A,
        GlfwKey.D,
        GlfwKey.E,
        GlfwKey.Q,
        GlfwKey.LeftShift,
        GlfwKey.LeftControl,
        GlfwKey.Escape,
    ];

    private static readonly string[] ActionNames = Enum.GetNames<GameAction>();

    private static readonly string[] ActionLabels =
    [
        "Move forward",
        "Move back",
        "Move left",
        "Move right",
        "Move up",
        "Move down",
        "Fast",
        "Slow",
        "Pause",
    ];

    private readonly GlfwKey[] _keys = new GlfwKey[Count];
    private readonly string[] _keyNames = new string[Count];

    public KeyBindings() => Reset();

    public GlfwKey this[GameAction action]
    {
        get => _keys[(int)action];
        set
        {
            _keys[(int)action] = value;
            _keyNames[(int)action] = Enum.GetName(value) ?? "?";
        }
    }

    public static string Label(GameAction action) => ActionLabels[(int)action];

    public static GlfwKey Default(GameAction action) => Defaults[(int)action];

    public string KeyName(GameAction action) => _keyNames[(int)action];

    public void Reset()
    {
        for (var i = 0; i < Count; i++)
            this[(GameAction)i] = Defaults[i];
    }

    public void CopyFrom(KeyBindings other)
    {
        for (var i = 0; i < Count; i++)
            this[(GameAction)i] = other._keys[i];
    }

    public bool SameAs(KeyBindings other) => _keys.AsSpan().SequenceEqual(other._keys);

    public Dictionary<string, string> ToTable()
    {
        var table = new Dictionary<string, string>(Count);

        for (var i = 0; i < Count; i++)
            table[ActionNames[i]] = _keyNames[i];

        return table;
    }

    /// <summary>Unknown actions are ignored; a missing action or an unknown key keeps its default.</summary>
    public void FromTable(Dictionary<string, string>? table)
    {
        Reset();

        if (table is null)
            return;

        for (var i = 0; i < Count; i++)
        {
            if (table.TryGetValue(ActionNames[i], out var name) && Enum.TryParse<GlfwKey>(name, ignoreCase: true, out var key) && Enum.IsDefined(key) && key != GlfwKey.Unknown)
                this[(GameAction)i] = key;
        }
    }
}
