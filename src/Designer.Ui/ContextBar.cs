using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// A small bar of actions beside whatever is selected in the 3D view (add a node, branch, delete, duplicate). It follows
/// the selection on screen and hides when the selection is behind the camera. A window, so the view never sees its clicks.
/// </summary>
public static class ContextBar
{
    private const float Offset = 26f;

    private static ImGuiWindowFlags Flags =>
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    /// <summary>Starts the bar beside <paramref name="at"/> (scene metres); false (and nothing to end) when it is off screen.</summary>
    public static bool Begin(string id, OrbitCamera camera, Vector3d at, System.Numerics.Vector2 viewport)
    {
        var viewProj = camera.ViewProjection(viewport.X / Math.Max(1f, viewport.Y));

        if (!camera.Project(at, viewProj, viewport, out var pixel) || pixel.X < 0 || pixel.Y < 0 || pixel.X > viewport.X || pixel.Y > viewport.Y)
            return false;

        // Beside the selection, on its left when the right side of the view is too close.
        DesignerStyle.CentralRect(out _, out var max);
        var left = pixel.X > max.X - Px(320f);
        ImGui.SetNextWindowPos(new float2(left ? pixel.X - Px(Offset) : pixel.X + Px(Offset), pixel.Y - Px(Offset)), ImGuiCond.Always, new float2(left ? 1f : 0f, 0f));
        ImGui.SetNextWindowBgAlpha(0.85f);

        if (ImGui.Begin($"##context{id}", Flags))
            return true;

        ImGui.End();
        return false;
    }

    public static void End() => ImGui.End();

    /// <summary>A button in the bar, with a tooltip.</summary>
    public static bool Button(string label, string tip)
    {
        var clicked = ImGui.Button(label);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tip);

        ImGui.SameLine();
        return clicked;
    }

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
