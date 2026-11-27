using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The right-click menu for whatever is selected in the 3D view (add a node, branch, delete, duplicate). A right-click
/// that does not drag (a drag orbits the camera) opens it at the mouse; choosing an action, clicking elsewhere or Esc
/// closes it. A window, so the view never sees its clicks.
/// </summary>
public static class ContextBar
{
    private const float ClickPixels = 4f;

    private static bool _open;
    private static float2 _at;
    private static bool _hovered;
    private static bool _drawn;

    private static ImGuiWindowFlags Flags =>
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    public static bool IsOpen => _open;

    /// <summary>
    /// Once a frame, before any menu is drawn: a right-click in the view (released without dragging) opens the menu at the
    /// mouse; a left-click off the menu or Esc closes it; it also closes when last frame had nothing to show.
    /// </summary>
    public static void Track(bool mouseInView)
    {
        var io = ImGui.GetIO();

        if (_open && !_drawn)
            _open = false;

        if (mouseInView && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
        {
            var drag = ImGui.GetMouseDragDelta(ImGuiMouseButton.Right, 0f);

            if (drag.X * drag.X + drag.Y * drag.Y < ClickPixels * ClickPixels)
                (_open, _at) = (true, io.MousePos);
        }

        if (_open && ((ImGui.IsMouseClicked(ImGuiMouseButton.Left, false) && !_hovered) || ImGui.IsKeyPressed(ImGuiKey.Escape, false)))
            _open = false;

        _drawn = false;
    }

    /// <summary>Starts the menu (true) when it is open; <paramref name="camera"/> and <paramref name="at"/> say nothing now but keep the callers' shape.</summary>
    public static bool Begin(string id, OrbitCamera camera, Vector3d at, System.Numerics.Vector2 viewport)
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

    public static void End()
    {
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

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
