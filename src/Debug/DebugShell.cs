using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Time;

namespace BrutalGame.Debug;

/// <summary>
/// The game's debug UI frame, built like the design tools': the designer style (scaled with the window), a menu bar
/// with the panels and their keys and the frame time, and a dock space whose free middle passes the mouse through to
/// the game. The arrangement is saved per user (%LOCALAPPDATA%\BRUTALGame\<see cref="LayoutFile"/>) and seeded with
/// the profiler and terrain on the left and the weather and ocean as tabs on the right. F1 hides all of it.
/// </summary>
public sealed class DebugShell
{
    public const string LayoutFile = "game-debug-layout.ini";
    public const GlfwKey ToggleKey = GlfwKey.F1;

    private const float BudgetMs = 1000f / 60f;

    private readonly DesignerStyle _style = new();
    private readonly DebugPanel[] _panels;
    private readonly FrameStats _stats;

    /// <summary>The menu bar and the open panels are drawn: at first, when any panel starts open.</summary>
    public bool Visible;

    /// <summary>Before the first frame: loads the style's fonts and points ImGui at the saved layout.</summary>
    public DebugShell(FrameStats stats, int width, int height, DebugPanel[] panels)
    {
        _stats = stats;
        _panels = panels;

        foreach (var panel in panels)
            Visible |= panel.Open;

        _style.LoadFonts();
        DesignerStyle.UseLayout(LayoutFile, width, height, Seed);
    }

    public float Scale => _style.Scale;

    /// <summary>Starts the debug UI; when it returns true draw the panels, then <see cref="End"/>.</summary>
    public bool Begin(float windowHeight, float contentScale)
    {
        if (!Visible)
            return false;

        _style.Begin(windowHeight, contentScale);
        DrawMenuBar();
        DesignerStyle.DockSpace();
        return true;
    }

    public void End() => _style.End();

    /// <summary>F1 shows or hides the debug UI; a panel's key toggles it and shows the UI. True when the key was ours.</summary>
    public bool OnKey(GlfwKey key)
    {
        if (key == ToggleKey)
        {
            Visible = !Visible;
            return true;
        }

        foreach (var panel in _panels)
        {
            if (panel.Key != key)
                continue;

            panel.Open = !panel.Open || !Visible;
            Visible = true;
            return true;
        }

        return false;
    }

    private void DrawMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
            return;

        if (ImGui.BeginMenu("View"u8))
        {
            foreach (var panel in _panels)
            {
                if (ImGui.MenuItem(panel.Title, panel.KeyLabel, panel.Open))
                    panel.Open = !panel.Open;
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Hide debug UI"u8, "F1"u8))
                Visible = false;

            ImGui.EndMenu();
        }

        DrawFrameTime();
        ImGui.EndMainMenuBar();
    }

    /// <summary>Right of the menu bar: frame rate and frame time against the 60 Hz budget, coloured by how much is used.</summary>
    private void DrawFrameTime()
    {
        var ms = _stats.Frame.Average;
        var fps = ms > 0f ? 1000f / ms : 0f;
        var width = DebugUi.Ems(13f);
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), ImGui.GetWindowWidth() - width));
        DebugUi.Text(DebugUi.Grade(ms / BudgetMs, 0.8f, 1f), $"{fps,4:F0} fps  {ms,5:F2} ms");
        DebugUi.Tip($"frame time, average of the last {_stats.Frame.Values.Length}; max {_stats.Frame.Max:F2} ms; budget {BudgetMs:F1} ms (60 Hz)");
    }

    private static string Seed(int w, int h)
    {
        var left = Math.Min(420, w / 4);
        var right = Math.Min(440, w / 4);
        var dock = DesignerStyle.DockSpaceId;
        return $"""
            [Window][Profiler]
            DockId=0x00000004,0

            [Window][Terrain]
            DockId=0x00000005,0

            [Window][Weather]
            DockId=0x00000006,0

            [Window][Ocean]
            DockId=0x00000006,1

            [Docking][Data]
            DockSpace     ID=0x{dock:X8} Pos=0,0 Size={w},{h} Split=X
              DockNode    ID=0x00000001 Parent=0x{dock:X8} SizeRef={left},{h} Split=Y
                DockNode  ID=0x00000004 Parent=0x00000001 SizeRef={left},{h * 64 / 100}
                DockNode  ID=0x00000005 Parent=0x00000001 SizeRef={left},{h * 36 / 100}
              DockNode    ID=0x00000002 Parent=0x{dock:X8} SizeRef={w - left},{h} Split=X
                DockNode  ID=0x00000003 Parent=0x00000002 SizeRef={w - left - right},{h} CentralNode=1
                DockNode  ID=0x00000006 Parent=0x00000002 SizeRef={right},{h}

            """;
    }
}
