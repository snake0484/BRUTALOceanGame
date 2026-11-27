using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Mesh;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Controls that float in the corners of the 3D view (Blender style): top left, the edit mode bar (Object, then vertex,
/// edge and face select); top right, the kit's <see cref="ViewAxes"/>. Both are small ImGui windows, so the view tools
/// never see their clicks.
/// </summary>
public sealed class ViewportOverlay
{
    private const float Pad = 10f;

    private static readonly string[] ModeTips = ["Vertex select (1)", "Edge select (2)", "Face select (3)"];

    /// <summary>The ship's axes: +Z is the front.</summary>
    private readonly ViewAxes _axes = new() { Hints = { [2] = " (front)", [5] = " (back)" } };

    private static ImGuiWindowFlags Floating => ViewAxes.Floating;

    private static readonly string[] CutAxes = ["X", "Y", "Z"];
    private float _barBottom;

    /// <summary>Set while the cutaway's Pick is armed: the next click on the hull puts the cut plane there (the host does the ray).</summary>
    public bool PickingCut;

    /// <summary>
    /// Call after the dock space, inside the designer style. <paramref name="editable"/> false (the interior view) hides the
    /// edit mode bar. The cutaway bar sits under it.
    /// </summary>
    public void Draw(DesignSession session, MeshEditState edit, OrbitCamera camera, Cutaway cut, bool editable = true)
    {
        DesignerStyle.CentralRect(out var min, out var max);
        _barBottom = min.Y + Pad;

        if (editable)
            _barBottom = DrawModes(session, edit, min) + Px(6f);

        DrawCutaway(cut, new float2(min.X + Pad, _barBottom));
        _axes.Draw(camera);
    }

    // ---- cutaway bar ----

    /// <summary>Cut (on or off), the axis, flip, the plane's position (drag the number, or Pick and click the hull).</summary>
    private void DrawCutaway(Cutaway cut, float2 at)
    {
        ImGui.SetNextWindowPos(at);

        if (!ImGui.Begin("##cutaway"u8, Floating))
        {
            ImGui.End();
            return;
        }

        var size = ImGui.GetFrameHeight() * 1.25f;

        if (Toggle("Cut"u8, cut.Enabled, new float2(0, size)))
        {
            cut.Enabled = !cut.Enabled;
            PickingCut &= cut.Enabled;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Cutaway: hide the hull past a plane to see inside"u8);

        ImGui.SameLine(0, Px(8f));

        for (var a = 0; a < 3; a++)
        {
            if (a > 0)
                ImGui.SameLine(0, 1f);

            if (Toggle($"{CutAxes[a]}##cutaxis", cut.Axis == a, new float2(size, size)))
                (cut.Axis, cut.Enabled) = (a, true);
        }

        ImGui.SameLine(0, Px(8f));

        if (Toggle("Flip"u8, cut.Flip, new float2(0, size)))
            cut.Flip = !cut.Flip;

        ImGui.SameLine(0, Px(8f));
        ImGui.SetNextItemWidth(Px(110f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new float2(Px(8f), (size - ImGui.GetFontSize()) * 0.5f));

        if (ImGui.DragFloat("##cutoffset"u8, ref cut.Offset, 0.05f, -2000f, 2000f, "%.2f m"u8, ImGuiSliderFlags.None))
            cut.Enabled = true;

        ImGui.PopStyleVar(1);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("The plane's position along the axis: drag, or double-click to type"u8);

        ImGui.SameLine(0, Px(8f));

        if (Toggle("Pick"u8, PickingCut, new float2(0, size)))
            PickingCut = !PickingCut;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click the hull to put the cut there"u8);

        ImGui.End();

        if (PickingCut)
            ImGui.SetTooltip("Click the hull to place the cut (Esc cancels)"u8);
    }

    // ---- edit mode bar ----

    /// <summary>Draws the bar; returns its bottom edge (pixels).</summary>
    private static float DrawModes(DesignSession session, MeshEditState edit, float2 min)
    {
        ImGui.SetNextWindowPos(new float2(min.X + Pad, min.Y + Pad));

        if (!ImGui.Begin("##editmodes"u8, Floating))
        {
            ImGui.End();
            return min.Y + Pad;
        }

        var canEdit = session.Selection.Primary != 0;
        var size = ImGui.GetFrameHeight() * 1.25f;

        if (Toggle("Object"u8, !edit.Enabled, new float2(0, size)) && edit.Enabled)
            edit.Toggle(session);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pick and move whole solids (Tab switches)"u8);

        ImGui.SameLine(0, Px(8f));
        ImGui.BeginDisabled(!canEdit && !edit.Enabled);

        for (var i = 0; i < 3; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 1f);

            var on = edit.Enabled && (int)edit.Selection.Mode == i;

            if (Toggle($"##mode{i}", on, new float2(size, size)))
            {
                edit.Selection.Mode = (MeshSelectMode)i;

                if (!edit.Enabled)
                    edit.Toggle(session);
            }

            Icon(i, ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), on);

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(canEdit || edit.Enabled ? ModeTips[i] : "Select a solid to edit its vertices, edges or faces");
        }

        ImGui.EndDisabled();
        var bottom = ImGui.GetWindowPos().Y + ImGui.GetWindowSize().Y;
        ImGui.End();
        return bottom;
    }

    private static bool Toggle(ImString label, bool on, float2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, on ? DesignerStyle.AccentPress : DesignerStyle.Frame);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(1);
        return clicked;
    }

    /// <summary>Blender's mode glyphs: a square with its corners lit (vertex), one side lit (edge), or filled (face).</summary>
    private static void Icon(int mode, float2 min, float2 max, bool on)
    {
        var list = ImGui.GetWindowDrawList();
        var inset = (max.X - min.X) * 0.27f;
        var a = new float2(min.X + inset, min.Y + inset);
        var b = new float2(max.X - inset, max.Y - inset);
        var lit = on ? new ImColor8(255, 255, 255, 255) : new ImColor8(255, 170, 70, 255);
        var dim = new ImColor8(160, 168, 180, 200);
        var corner = Px(2.5f);

        switch (mode)
        {
            case 0:
                list.AddRect(a, b, dim, 0f, default, 1f);
                list.AddRectFilled(new float2(a.X - corner, a.Y - corner), new float2(a.X + corner, a.Y + corner), lit);
                list.AddRectFilled(new float2(b.X - corner, a.Y - corner), new float2(b.X + corner, a.Y + corner), lit);
                list.AddRectFilled(new float2(a.X - corner, b.Y - corner), new float2(a.X + corner, b.Y + corner), lit);
                list.AddRectFilled(new float2(b.X - corner, b.Y - corner), new float2(b.X + corner, b.Y + corner), lit);
                break;
            case 1:
                list.AddRect(a, b, dim, 0f, default, 1f);
                list.AddLine(new float2(a.X, a.Y), new float2(b.X, a.Y), lit, Px(3f));
                list.AddLine(new float2(b.X, a.Y), new float2(b.X, b.Y), lit, Px(3f));
                break;
            default:
                list.AddRectFilled(a, b, lit);
                break;
        }
    }

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
