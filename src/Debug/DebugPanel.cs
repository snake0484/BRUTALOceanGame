using Brutal.GlfwApi;

namespace BrutalGame.Debug;

/// <summary>A debug window the <see cref="DebugShell"/> lists in its View menu: its title (also its key in the saved layout) and the key that toggles it.</summary>
public abstract class DebugPanel(string title, GlfwKey key, string keyLabel)
{
    public readonly string Title = title;
    public readonly GlfwKey Key = key;
    public readonly string KeyLabel = keyLabel;
    public bool Open = true;
}
