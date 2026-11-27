using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Rotors and hinges: the view tool (each joint's pivot, axis and disc; pick one by its pivot; the gizmo moves the pivot
/// and turns the axis), the selected joint's properties, and the options new ones get (in the Add dialog). A joint's
/// moving part is the solids under it in the object tree (a solid's "Moves with" in Properties).
/// </summary>
public sealed class JointsPanel
{
    private const float PivotPixels = 12f;

    private static readonly uint RotorColour = OverlayRenderer.Rgba(120, 220, 255, 230);
    private static readonly uint HingeColour = OverlayRenderer.Rgba(255, 170, 80, 230);
    private static readonly uint SliderColour = OverlayRenderer.Rgba(150, 230, 110, 230);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 230, 120, 255);

    private readonly TransformGizmo _gizmo = new();
    private string _message = "";
    private Draft _draft;
    private float _addSpeed = 10f;
    private float _addTorque = 10f;

    private struct Draft
    {
        public uint Id;
        public int Version;
        public float Speed, Torque, Min, Max, Target;
    }

    public uint Selected;

    public void Reset() => Selected = 0;

    /// <summary>The mass of a joint's moving part (kg), from the host's bake.</summary>
    public Func<uint, double>? PartMassKg;

    public static string KindName(JointKind kind) => kind switch { JointKind.Rotor => "Rotor", JointKind.Hinge => "Hinge", _ => "Slider" };

    /// <summary>A short name for the object tree.</summary>
    public static string Label(DesignSession session, uint id)
    {
        var set = session.Doc.Joints;
        var index = set?.IndexOf(id) ?? -1;

        if (index < 0)
            return $"Joint #{id}";

        var j = set!.Joints[index];
        var members = set.Members.Count(m => m.Joint == id);
        return $"{KindName(j.Kind)}, {members} solid{(members == 1 ? "" : "s")} moving  #{id}";
    }

    /// <summary>The options new joints get (in the Add dialog): speed and torque.</summary>
    public void DrawAddOptions(JointKind kind)
    {
        ImGui.TextWrapped(kind switch
        {
            JointKind.Rotor => "A rotor turns without end about the hull's normal where you place it. Put solids under it in the object tree (their Moves with) to make its moving part."u8,
            JointKind.Hinge => "A hinge swings between limits about an axis along the hull where you place it. Put solids under it in the object tree (their Moves with) to make its moving part."u8,
            _ => "A slider slides between limits along an axis along the hull where you place it (0 to 1 m to start). Put solids under it in the object tree (their Moves with) to make its moving part."u8,
        });
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(kind switch { JointKind.Rotor => "Speed rpm##addjoint"u8, JointKind.Hinge => "Speed deg/s##addjoint"u8, _ => "Speed m/s##addjoint"u8 }, ref _addSpeed, 0f, 0f, "%.1f"u8, ImGuiInputTextFlags.None);
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(kind == JointKind.Slider ? "Force kN##addjoint"u8 : "Torque kN m##addjoint"u8, ref _addTorque, 0f, 0f, "%.1f"u8, ImGuiInputTextFlags.None);
    }

    /// <summary>
    /// A joint at <paramref name="at"/> on the surface facing <paramref name="normal"/>, mounted on <paramref name="mountedOn"/>:
    /// a rotor's axis is the normal; a hinge's or slider's lies along the surface. Selected.
    /// </summary>
    public void PlaceAt(DesignSession session, JointKind kind, Vector3d at, Vector3d normal, uint mountedOn)
    {
        var axis = kind == JointKind.Rotor ? normal : Normalise(Cross(normal, Math.Abs(normal.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0));
        Selected = JointCommands.AddJoint(session, kind, at, axis, mountedOn);
        _message = Selected == 0 ? JointCommands.LastError : "Put solids under it: a solid's Moves with in Properties.";

        if (Selected == 0)
            return;

        var speed = (short)Math.Clamp((int)Math.Round(_addSpeed * (kind == JointKind.Slider ? 1000 : 10)), short.MinValue, short.MaxValue);
        var torque = (uint)Math.Clamp(_addTorque * 1000.0, 0, HullJoint.MaxTorqueNm);
        JointCommands.SetJoint(session, Selected, j => (j.Speed, j.TorqueNm) = (speed, torque));
    }

    /// <summary>The selected joint's properties (in the Properties tab).</summary>
    public void DrawProperties(DesignSession session)
    {
        var set = session.Doc.Joints;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0)
            return;

        var joint = set!.Joints[index];
        ImGui.SeparatorText($"{KindName(joint.Kind)} #{joint.Id}");

        // Shown in rpm, deg/s and degrees; a slider's in m/s and metres.
        var unit = joint.Kind == JointKind.Slider ? 1000f : 10f;

        if (_draft.Id != joint.Id || _draft.Version != session.Version)
        {
            _draft = new Draft
            {
                Id = joint.Id, Version = session.Version, Speed = joint.Speed / unit, Torque = joint.TorqueNm / 1000f,
                Min = joint.Min / unit, Max = joint.Max / unit, Target = joint.Target / unit,
            };
        }

        var mount = joint.Parent == 0 ? "the ship" : Label(session, joint.Parent);
        ImGui.Text($"Mounted on {mount}");
        var pivot = joint.Pivot;
        var axis = joint.Axis;
        ImGui.TextDisabled($"Pivot {pivot.X:F2}, {pivot.Y:F2}, {pivot.Z:F2} m; axis {axis.X:F2}, {axis.Y:F2}, {axis.Z:F2} (arrows move it, rings turn it in the view)");

        var kind = (int)joint.Kind;

        if (ImGui.RadioButton("Rotor"u8, kind == 0) && kind != 0)
            JointCommands.SetJoint(session, joint.Id, j => j.Kind = JointKind.Rotor);

        ImGui.SameLine();

        if (ImGui.RadioButton("Hinge"u8, kind == 1) && kind != 1)
            JointCommands.SetJoint(session, joint.Id, j => (j.Kind, j.Min, j.Max, j.Target, j.Speed) = (JointKind.Hinge, -900, 900, 0, 300));

        ImGui.SameLine();

        if (ImGui.RadioButton("Slider"u8, kind == 2) && kind != 2)
            JointCommands.SetJoint(session, joint.Id, j => (j.Kind, j.Min, j.Max, j.Target, j.Speed) = (JointKind.Slider, 0, 1000, 0, 500));

        var slider = joint.Kind == JointKind.Slider;
        ImGui.SeparatorText("Motor"u8);
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(joint.Kind switch { JointKind.Rotor => "Speed rpm"u8, JointKind.Hinge => "Speed deg/s"u8, _ => "Speed m/s"u8 }, ref _draft.Speed, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        var changed = ImGui.IsItemDeactivatedAfterEdit();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat(slider ? "Force kN"u8 : "Torque kN m"u8, ref _draft.Torque, 0f, 0f, "%.1f"u8, ImGuiInputTextFlags.None);
        changed |= ImGui.IsItemDeactivatedAfterEdit();

        if (joint.Limited)
        {
            ImGui.SeparatorText(slider ? "Travel"u8 : "Limits"u8);
            DesignerStyle.ItemWidth(90f);
            ImGui.InputFloat(slider ? "Min m"u8 : "Min deg"u8, ref _draft.Min, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            DesignerStyle.ItemWidth(90f);
            ImGui.InputFloat(slider ? "Max m"u8 : "Max deg"u8, ref _draft.Max, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            DesignerStyle.ItemWidth(90f);
            ImGui.InputFloat(slider ? "Target m"u8 : "Target deg"u8, ref _draft.Target, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
            changed |= ImGui.IsItemDeactivatedAfterEdit();
        }

        if (changed)
        {
            var d = _draft;
            var limit = slider ? short.MaxValue : 1800;
            var min = (short)Math.Clamp((int)Math.Round(d.Min * unit), -limit, limit);
            var max = (short)Math.Clamp((int)Math.Round(d.Max * unit), min, limit);
            var target = (short)Math.Clamp((int)Math.Round(d.Target * unit), min, max);
            var speed = (short)Math.Clamp((int)Math.Round(d.Speed * unit), short.MinValue, short.MaxValue);
            var torque = (uint)Math.Clamp(d.Torque * 1000.0, 0, HullJoint.MaxTorqueNm);
            JointCommands.SetJoint(session, joint.Id, j => (j.Speed, j.TorqueNm, j.Min, j.Max, j.Target) = (speed, torque, min, max, target));
        }

        ImGui.SeparatorText("Moving part"u8);
        var members = set.Members.Count(m => m.Joint == joint.Id);
        ImGui.Text($"{members} solid{(members == 1 ? "" : "s")} move with it");

        if (PartMassKg is not null)
            ImGui.Text($"Mass {PartMassKg(joint.Id) / 1000.0:F3} t");

        ImGui.TextDisabled("Play joints (P, top bar) previews the motion. A solid's Moves with puts it on this joint."u8);

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
    }

    /// <summary>The menu for the selected joint in the view: delete (its solids pass to what it was mounted on).</summary>
    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var set = session.Doc.Joints;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0 || _gizmo.Dragging)
            return;

        var joint = set!.Joints[index];

        if (!ContextBar.Begin("joint", camera, joint.Pivot, viewport))
            return;

        if (ContextBar.Button("Delete", "Remove this joint: its solids pass to what it was mounted on"))
        {
            JointCommands.RemoveJoint(session, joint.Id);
            Selected = 0;
        }

        ContextBar.End();
    }

    // ---- view ----

    /// <summary>Draws every joint (pivot, axis, disc), picks one by its pivot, runs the gizmo on the selected one. True when it used the mouse.</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, double step, OverlayRenderer overlay)
    {
        var set = session.Doc.Joints;

        if (set is null)
        {
            Selected = 0;
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);
        var hover = 0u;
        var best = PivotPixels;

        foreach (var j in set.Joints)
        {
            Marker(overlay, j, j.Id == Selected ? SelectedColour : j.Kind switch { JointKind.Rotor => RotorColour, JointKind.Hinge => HingeColour, _ => SliderColour }, camera);

            if (camera.Project(j.Pivot, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                (best, hover) = (Vector2.Distance(pixel, mouse), j.Id);
        }

        var index = set.IndexOf(Selected);

        if (index >= 0)
        {
            var joint = set.Joints[index];

            if (_gizmo.Update(joint.Pivot, joint.Rotation, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
            {
                var id = joint.Id;

                if (_gizmo.Turned)
                {
                    var rotation = _gizmo.Rotation;
                    JointCommands.SetJoint(session, id, j => j.Rotation = rotation);
                }
                else
                {
                    var q = ComponentCommands.Quantise(Add(joint.Pivot, _gizmo.Offset));
                    JointCommands.SetJoint(session, id, j => (j.X, j.Y, j.Z) = q);
                }

                return true;
            }

            if (_gizmo.Dragging || _gizmo.Hovered)
                return _gizmo.Dragging || (mouseFree && clicked);
        }

        if (mouseFree && clicked && hover != 0)
        {
            Selected = hover;
            return true;
        }

        return false;
    }

    /// <summary>The axis through the pivot and a disc about it (a half disc for a hinge), sized to the view; a slider's travel as a bar with end ticks.</summary>
    private static void Marker(OverlayRenderer overlay, HullJoint joint, uint colour, OrbitCamera camera)
    {
        var c = joint.Pivot;
        var axis = joint.Axis;
        var size = Math.Clamp(camera.Depth(c) * 0.04, 0.2, 5.0);
        overlay.OnTop = true;

        if (joint.Kind == JointKind.Slider)
        {
            var from = Add(c, Scale(axis, joint.Min / 1000.0));
            var to = Add(c, Scale(axis, joint.Max / 1000.0));
            var across = Normalise(Cross(axis, Math.Abs(axis.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0));
            overlay.Ribbon(from, to, size * 0.05, colour);
            overlay.Ribbon(Add(from, Scale(across, -size * 0.3)), Add(from, Scale(across, size * 0.3)), size * 0.04, colour);
            overlay.Ribbon(Add(to, Scale(across, -size * 0.3)), Add(to, Scale(across, size * 0.3)), size * 0.04, colour);
            overlay.Cone(to, Add(to, Scale(axis, size * 0.4)), size * 0.12, colour);
            overlay.Cube(c, size * 0.08, colour);
            overlay.OnTop = false;
            return;
        }

        overlay.Ribbon(Add(c, Scale(axis, -size * 1.5)), Add(c, Scale(axis, size * 1.5)), size * 0.04, colour);
        var u = Normalise(Cross(axis, Math.Abs(axis.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0)), new Vector3d(1, 0, 0));
        var v = Cross(axis, u);
        var segments = joint.Kind == JointKind.Rotor ? 32 : 16;
        var sweep = joint.Kind == JointKind.Rotor ? Math.Tau : Math.PI;
        var previous = Add(c, Scale(u, size));

        for (var s = 1; s <= segments; s++)
        {
            var a = s * sweep / segments;
            var point = Add(c, Add(Scale(u, size * Math.Cos(a)), Scale(v, size * Math.Sin(a))));
            overlay.Ribbon(previous, point, size * 0.03, colour);
            previous = point;
        }

        overlay.Cube(c, size * 0.08, colour);
        overlay.OnTop = false;
    }
}
