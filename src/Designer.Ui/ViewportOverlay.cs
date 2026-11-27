using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Mesh;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Controls that float in the corners of the 3D view (Blender style): top left, the edit mode bar (Object, then vertex,
/// edge and face select); top right, the view axes (click an axis to look along it, click it again to look from the other
/// side, drag to orbit). Both are small ImGui windows, so the view tools never see their clicks.
/// </summary>
public sealed class ViewportOverlay
{
    private const float Pad = 10f;
    private const float AxesSize = 112f;
    private const float Bubble = 11f;

    private static readonly ImColor8[] AxisColours = [new(235, 70, 70, 255), new(110, 210, 90, 255), new(80, 140, 255, 255)];
    private static readonly ImColor8[] AxisFaint = [new(235, 70, 70, 70), new(110, 210, 90, 70), new(80, 140, 255, 70)];
    private static readonly string[] AxisNames = ["X", "Y", "Z"];
    private static readonly string[] ModeTips = ["Vertex select (1)", "Edge select (2)", "Face select (3)"];

    private bool _dragged;
    private readonly (int Axis, int Sign, double Depth, float2 At)[] _bubbles = new (int, int, double, float2)[6];

    private static ImGuiWindowFlags Floating =>
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    /// <summary>Call after the dock space, inside the designer style.</summary>
    public void Draw(DesignSession session, MeshEditState edit, OrbitCamera camera)
    {
        DesignerStyle.CentralRect(out var min, out var max);
        DrawModes(session, edit, min);
        DrawAxes(camera, new float2(max.X - Pad - Px(AxesSize), min.Y + Pad));
    }

    // ---- edit mode bar ----

    private static void DrawModes(DesignSession session, MeshEditState edit, float2 min)
    {
        ImGui.SetNextWindowPos(new float2(min.X + Pad, min.Y + Pad));

        if (!ImGui.Begin("##editmodes"u8, Floating))
        {
            ImGui.End();
            return;
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
        ImGui.End();
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

    // ---- view axes ----

    private void DrawAxes(OrbitCamera camera, float2 at)
    {
        var size = Px(AxesSize);
        ImGui.SetNextWindowPos(at);

        if (!ImGui.Begin("##viewaxes"u8, Floating))
        {
            ImGui.End();
            return;
        }

        ImGui.InvisibleButton("##orbit"u8, new float2(size, size));
        var hovered = ImGui.IsItemHovered();
        var list = ImGui.GetWindowDrawList();
        var centre = new float2(at.X + size * 0.5f, at.Y + size * 0.5f);
        var mouse = ImGui.GetIO().MousePos;
        var reach = size * 0.5f - Px(Bubble) - 2f;
        var bubble = Px(Bubble);

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 2f))
        {
            var delta = ImGui.GetIO().MouseDelta;
            camera.Orbit(delta.X, delta.Y);
            _dragged = true;
        }

        if (hovered || ImGui.IsItemActive())
            list.AddCircleFilled(centre, size * 0.5f, new ImColor8(255, 255, 255, 22), 48);

        // The view's right, up and back axes in ship axes: an axis lands on screen at (right, -up), nearer when along back.
        var rotation = camera.Rotation;
        var right = rotation * double3.UnitX;
        var up = rotation * double3.UnitY;
        var back = camera.Back;

        for (var i = 0; i < 6; i++)
        {
            var axis = i % 3;
            var sign = i < 3 ? 1 : -1;
            var d = new double3(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0);
            var p = new float2(centre.X + (float)(double3.Dot(d, right) * reach), centre.Y - (float)(double3.Dot(d, up) * reach));
            _bubbles[i] = (axis, sign, double3.Dot(d, back), p);
        }

        Array.Sort(_bubbles, (x, y) => x.Depth.CompareTo(y.Depth));
        var under = -1;

        for (var i = _bubbles.Length - 1; i >= 0 && hovered; i--)
        {
            var dx = mouse.X - _bubbles[i].At.X;
            var dy = mouse.Y - _bubbles[i].At.Y;

            if (dx * dx + dy * dy <= bubble * bubble)
            {
                under = i;
                break;
            }
        }

        for (var i = 0; i < _bubbles.Length; i++)
        {
            var (axis, sign, _, p) = _bubbles[i];
            var colour = AxisColours[axis];

            if (sign > 0)
            {
                list.AddLine(centre, p, colour, Px(2f));
                list.AddCircleFilled(p, bubble, colour, 20);
                var label = AxisNames[axis];
                var text = ImGui.CalcTextSize(label);
                list.AddText(new float2(p.X - text.X * 0.5f, p.Y - text.Y * 0.5f), new ImColor8(20, 20, 24, 255), label);
            }
            else
            {
                list.AddCircleFilled(p, bubble, AxisFaint[axis], 20);
                list.AddCircle(p, bubble, colour, 20, 1.5f);

                if (i == under)
                {
                    var label = "-" + AxisNames[axis];
                    var text = ImGui.CalcTextSize(label);
                    list.AddText(new float2(p.X - text.X * 0.5f, p.Y - text.Y * 0.5f), new ImColor8(240, 240, 245, 255), label);
                }
            }

            if (i == under)
                list.AddCircle(p, bubble + 1.5f, new ImColor8(255, 255, 255, 255), 20, 2f);
        }

        if (ImGui.IsItemDeactivated())
        {
            if (!_dragged && under >= 0)
                LookAlong(camera, _bubbles[under].Axis, _bubbles[under].Sign);

            _dragged = false;
        }

        if (hovered && under >= 0)
        {
            var (axis, sign, _, _) = _bubbles[under];
            ImGui.SetTooltip($"View from {(sign > 0 ? "+" : "-")}{AxisNames[axis]}{(axis == 2 ? sign > 0 ? " (front)" : " (back)" : "")}");
        }

        ImGui.End();
    }

    /// <summary>Turns the camera to look from the +/- axis toward the target; from the side it already looks from, it flips round.</summary>
    private static void LookAlong(OrbitCamera camera, int axis, int sign)
    {
        var back = camera.Back;
        var current = axis == 0 ? back.X : axis == 1 ? back.Y : back.Z;

        if (current * sign > 0.999)
            sign = -sign;

        if (axis == 1)
        {
            // Top or bottom: keep the heading, squared to the nearest quarter turn.
            var yaw = Math.Round(camera.Yaw / (Math.PI * 0.5)) * Math.PI * 0.5;
            camera.TurnTo(yaw, sign * Math.PI * 0.5);
            return;
        }

        var heading = axis == 0 ? sign * Math.PI * 0.5 : sign > 0 ? 0.0 : Math.PI;
        camera.TurnTo(heading, 0.0);
    }

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
