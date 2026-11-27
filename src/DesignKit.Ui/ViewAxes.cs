using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalGame.DesignKit.Ui;

/// <summary>
/// The view axes (Blender's navigation gizmo): X, Y and Z bubbles turned with the camera, faint ones for the minus
/// sides. Click a bubble to turn smoothly to look along that axis (again to look from the other side; Y keeps the
/// heading squared to a quarter turn), drag inside the circle to orbit. A small ImGui window, so view tools never see
/// its clicks.
/// </summary>
public sealed class ViewAxes
{
    private const float Pad = 10f;
    private const float Size = 112f;
    private const float Bubble = 11f;

    private static readonly ImColor8[] AxisColours = [new(235, 70, 70, 255), new(110, 210, 90, 255), new(80, 140, 255, 255)];
    private static readonly ImColor8[] AxisFaint = [new(235, 70, 70, 70), new(110, 210, 90, 70), new(80, 140, 255, 70)];
    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    private bool _dragged;
    private readonly (int Axis, int Sign, double Depth, float2 At)[] _bubbles = new (int, int, double, float2)[6];

    /// <summary>Text added to a bubble's tooltip: +X, +Y, +Z, then -X, -Y, -Z (e.g. " (front)").</summary>
    public string[] Hints { get; } = ["", "", "", "", "", ""];

    /// <summary>Flags for small floating controls over the 3D view.</summary>
    public static ImGuiWindowFlags Floating =>
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize;

    /// <summary>Draws in the top right of the 3D view (<see cref="DesignerStyle.CentralRect"/>); call after the dock space, inside the style.</summary>
    public void Draw(OrbitCamera camera)
    {
        DesignerStyle.CentralRect(out var min, out var max);
        Draw(camera, new float2(max.X - Pad - Px(Size), min.Y + Pad));
    }

    /// <summary>Draws with its top left corner at <paramref name="at"/> (pixels).</summary>
    public void Draw(OrbitCamera camera, float2 at)
    {
        var size = Px(Size);
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
            ImGui.SetTooltip($"View from {(sign > 0 ? "+" : "-")}{AxisNames[axis]}{Hints[axis + (sign > 0 ? 0 : 3)]}");
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
