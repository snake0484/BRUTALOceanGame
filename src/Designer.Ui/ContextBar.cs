using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The right-click menu in the 3D view. A right-click that does not drag (a drag orbits the camera) opens it at the mouse;
/// choosing an item, clicking elsewhere or Esc closes it. The host opens one shared menu a frame
/// (<see cref="BeginShared"/>): the selection's items (each panel's <see cref="Begin"/> ... <see cref="End"/> joins it,
/// after a separator) and then the Add items. A window, so the view never sees its clicks.
/// </summary>
public static class ContextBar
{
    private const float ClickPixels = 4f;

    private static bool _open;
    private static float2 _at;
    private static bool _hovered;
    private static bool _drawn;
    private static bool _shared;
    private static int _sections;

    private static ImGuiWindowFlags Flags =>
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    public static bool IsOpen => _open;

    /// <summary>True on the frame the menu opened: the host reads <see cref="At"/> to see what was clicked.</summary>
    public static bool OpenedThisFrame { get; private set; }

    /// <summary>Where the menu opened (screen pixels).</summary>
    public static float2 At => _at;

    /// <summary>
    /// Once a frame, before any menu is drawn: a right-click in the view (released without dragging) opens the menu at the
    /// mouse; a left-click off the menu or Esc closes it; it also closes when last frame had nothing to show.
    /// </summary>
    public static void Track(bool mouseInView)
    {
        var io = ImGui.GetIO();
        OpenedThisFrame = false;

        if (_open && !_drawn)
            _open = false;

        if (mouseInView && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            var drag = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right, 0f);

            if (drag.X * drag.X + drag.Y * drag.Y < ClickPixels * ClickPixels)
                (_open, _at, OpenedThisFrame) = (true, io.MousePos, true);
        }

        if (_open && ((ImGui.IsMouseClicked(ImGuiMouseButton.Left, false) && !_hovered) || ImGui.IsKeyPressed(ImGuiKey.Escape, false)))
            _open = false;

        _drawn = false;
    }

    /// <summary>Closes the menu (the host chose to open something else instead).</summary>
    public static void Close() => _open = false;

    /// <summary>Opens the frame's menu window (true when it is open): panels' sections and the host's items join it until <see cref="EndShared"/>.</summary>
    public static bool BeginShared()
    {
        if (!Open("menu"))
            return false;

        (_shared, _sections) = (true, 0);
        return true;
    }

    public static void EndShared()
    {
        _shared = false;
        _hovered |= ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        ImGui.End();
    }

    /// <summary>A section of items: a heading (separated from earlier sections) in the shared menu, or a window of its own.</summary>
    public static bool Begin(string id, OrbitCamera camera, Vector3d at, System.Numerics.Vector2 viewport) => Section(id);

    /// <summary>A section of the shared menu, headed by <paramref name="heading"/> (none when empty).</summary>
    public static bool Section(string heading)
    {
        if (!_shared)
            return Open(heading);

        if (_sections++ > 0)
            ImGui.Separator();

        return true;
    }

    public static void End()
    {
        if (_shared)
            return;

        _hovered |= ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        ImGui.End();
    }

    /// <summary>A menu item with a tooltip; choosing it closes the menu.</summary>
    public static bool Button(string label, string tip)
    {
        var clicked = ImGui.Selectable(label, false, ImGuiSelectableFlags.None, default);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);

        if (clicked)
            _open = false;

        return clicked;
    }

    private static bool Open(string id)
    {
        if (!_open || _drawn)
            return false;

        // At the mouse, opening to the left when the right side of the view is too close.
        DesignerStyle.CentralRect(out _, out var max);
        var left = _at.X > max.X - Px(240f);
        ImGui.SetNextWindowPos(_at, ImGuiCond.Always, new float2(left ? 1f : 0f, 0f));
        ImGui.SetNextWindowBgAlpha(0.92f);

        if (!ImGui.Begin($"##context{id}", Flags))
        {
            ImGui.End();
            return false;
        }

        _drawn = true;
        _hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
        return true;
    }

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
