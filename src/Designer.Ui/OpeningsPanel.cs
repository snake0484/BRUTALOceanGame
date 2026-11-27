using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Doorways and docking port faces in the component editor: the view tool (each opening's outline and its way through;
/// pick one by its middle; the gizmo moves and turns it), the selected one's properties (kind, door profile, how deep it
/// cuts; a port's type and whether its outline can mate with its own kind), and, in a ship, the faces of placed docking
/// ports with whether anything of the hull stands in front of them.
/// </summary>
public sealed class OpeningsPanel
{
    private const float PickPixels = 14f;

    private static readonly uint DoorColour = OverlayRenderer.Rgba(255, 200, 90, 230);
    private static readonly uint PortColour = OverlayRenderer.Rgba(90, 220, 255, 230);
    private static readonly uint BlockedColour = OverlayRenderer.Rgba(255, 90, 80, 230);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(255, 240, 150, 255);

    private readonly TransformGizmo _gizmo = new();
    private string _message = "";
    private float _depth = -1;
    private uint _depthFor;
    private int _depthVersion = -1;

    public uint Selected;

    public void Reset() => Selected = 0;

    public static string KindName(OpeningKind kind) => kind == OpeningKind.Door ? "Doorway" : "Docking port";

    /// <summary>A short name for the object tree.</summary>
    public static string Label(DesignSession session, uint id)
    {
        var set = session.Doc.Openings;
        var index = set?.IndexOf(id) ?? -1;

        if (index < 0)
            return $"Opening #{id}";

        var o = set!.Openings[index];
        var profile = Profile(session.Doc, o);
        return $"{KindName(o.Kind)}, {profile?.Name ?? "no profile"}  #{id}";
    }

    private static HullProfile? Profile(PlanDocument doc, HullOpening o) =>
        doc.Interior is { } interior && o.Profile < interior.Profiles.Count && interior.Profiles[o.Profile].Kind == ProfileKind.Door ? interior.Profiles[o.Profile] : null;

    public void DrawAddOptions(OpeningKind kind) =>
        ImGui.TextWrapped(kind == OpeningKind.Door
            ? "A doorway: a door profile cut through the frame (and, once placed, through the wall the door is set in). It faces out of the surface you put it on, centred there. Put the leaf on a hinge or slider."u8
            : "A docking port's face: a door profile cut through the collar (and the hull behind it), facing out of the surface you put it on. Ports with the same profile mate face to face."u8);

    public void PlaceAt(DesignSession session, OpeningKind kind, Vector3d at, Vector3d normal)
    {
        Selected = OpeningCommands.AddOpening(session, kind, at, normal);
        _message = Selected == 0 ? OpeningCommands.LastError : "Its profile is edited in Profiles.";
    }

    public void DrawProperties(DesignSession session)
    {
        var set = session.Doc.Openings;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0)
            return;

        var o = set!.Openings[index];
        ImGui.SeparatorText($"{KindName(o.Kind)} #{o.Id}");
        var kind = (int)o.Kind;

        if (ImGui.RadioButton("Doorway"u8, kind == 0) && kind != 0)
            OpeningCommands.SetOpening(session, o.Id, x => x.Kind = OpeningKind.Door);

        ImGui.SameLine();

        if (ImGui.RadioButton("Docking port"u8, kind == 1) && kind != 1)
            OpeningCommands.SetOpening(session, o.Id, x => x.Kind = OpeningKind.DockingPort);

        // The door profile it cuts.
        var profiles = session.Doc.Interior?.Profiles;
        var current = Profile(session.Doc, o);
        DesignerStyle.ItemWidth(200f);

        if (ImGui.BeginCombo("Profile"u8, current?.Name ?? "(none)", ImGuiComboFlags.None))
        {
            for (var i = 0; profiles is not null && i < profiles.Count; i++)
            {
                if (profiles[i].Kind != ProfileKind.Door)
                    continue;

                var pick = (byte)i;

                if (ImGui.Selectable($"{profiles[i].Name}##openprof{i}", i == o.Profile, ImGuiSelectableFlags.None, default))
                    OpeningCommands.SetOpening(session, o.Id, x => x.Profile = pick);
            }

            ImGui.EndCombo();
        }

        ImGui.TextDisabled("Door profiles are made and shaped in Profiles (New door)."u8);

        if (_depthFor != o.Id || _depthVersion != session.Version)
            (_depth, _depthFor, _depthVersion) = (o.DepthMm / 1000f, o.Id, session.Version);

        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Cuts each way m"u8, ref _depth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var depth = (ushort)Math.Clamp((int)Math.Round(_depth * 1000), 1, HullOpening.MaxDepthMm);
            OpeningCommands.SetOpening(session, o.Id, x => x.DepthMm = depth);
        }

        var origin = o.Origin;
        ImGui.TextDisabled($"Sill {origin.X:F2}, {origin.Y:F2}, {origin.Z:F2} m (arrows move it, rings turn it in the view)");

        if (o.Kind == OpeningKind.DockingPort && current is not null)
            PortNotes(current);

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
    }

    /// <summary>A port's type (the same shape mates) and whether its outline lines up with its own kind face to face.</summary>
    public static void PortNotes(HullProfile profile)
    {
        ImGui.Text($"Port type {OpeningGeometry.PortType(profile)}: mates with any port of the same profile shape");

        if (!OpeningGeometry.Symmetric(profile))
            ImGui.TextColored(new Brutal.Numerics.float4(1f, 0.6f, 0.3f, 1f), "Not mirror symmetric: two of these would not line up face to face"u8);
    }

    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var set = session.Doc.Openings;
        var index = set?.IndexOf(Selected) ?? -1;

        if (index < 0 || _gizmo.Dragging)
            return;

        if (!ContextBar.Begin("opening", camera, Middle(session.Doc, set!.Openings[index]), viewport))
            return;

        if (ContextBar.Button("Delete", "Remove this opening"))
        {
            OpeningCommands.RemoveOpening(session, Selected);
            Selected = 0;
        }

        ContextBar.End();
    }

    /// <summary>The middle of an opening's outline (its sill raised by half its height).</summary>
    private static Vector3d Middle(PlanDocument doc, HullOpening o)
    {
        var ring = OpeningGeometry.Outline(o, doc.Interior?.Profiles);

        if (ring.Length == 0)
            return o.Origin;

        Vector3d sum = default;

        foreach (var p in ring)
            sum = Add(sum, p);

        return Scale(sum, 1.0 / ring.Length);
    }

    /// <summary>Draws every opening, picks one by its middle, runs the gizmo on the selected one. True when it used the mouse.</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, double step, OverlayRenderer overlay)
    {
        var set = session.Doc.Openings;

        if (set is null)
        {
            Selected = 0;
            return false;
        }

        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);
        var hover = 0u;
        var best = PickPixels;

        foreach (var o in set.Openings)
        {
            var ring = OpeningGeometry.Outline(o, session.Doc.Interior?.Profiles);
            Span<double> m = stackalloc double[9];
            SolidRotation.Matrix(o.Rotation, m);
            Marker(overlay, ring, new Vector3d(m[2], m[5], m[8]), o.Id == Selected ? SelectedColour : o.Kind == OpeningKind.Door ? DoorColour : PortColour, camera);

            if (camera.Project(Middle(session.Doc, o), viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                (best, hover) = (Vector2.Distance(pixel, mouse), o.Id);
        }

        var index = set.IndexOf(Selected);

        if (index >= 0)
        {
            var o = set.Openings[index];

            if (_gizmo.Update(o.Origin, o.Rotation, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
            {
                if (_gizmo.Turned)
                {
                    var rotation = _gizmo.Rotation;
                    OpeningCommands.SetOpening(session, o.Id, x => x.Rotation = rotation);
                }
                else
                {
                    var q = ComponentCommands.Quantise(Add(o.Origin, _gizmo.Offset));
                    OpeningCommands.SetOpening(session, o.Id, x => (x.X, x.Y, x.Z) = q);
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

    /// <summary>In a ship: the faces of placed docking ports, red where the hull stands within 2 m in front of one.</summary>
    public static void DrawPlacedPorts(PlanDocument doc, OverlayRenderer overlay, OrbitCamera camera)
    {
        if (doc.Components is not { } components)
            return;

        HullSurface? surface = null;
        Span<double> m = stackalloc double[9];
        Span<double> f = stackalloc double[9];

        foreach (var placement in components.Placements)
        {
            if (!doc.Prefabs.TryGetValue(placement.Prefab, out var prefab) || prefab.Openings is not { } openings)
                continue;

            SolidRotation.Matrix(placement.Rotation, m);

            foreach (var o in openings.Openings)
            {
                if (o.Kind != OpeningKind.DockingPort)
                    continue;

                var ring = OpeningGeometry.Outline(o, prefab.Interior?.Profiles);

                for (var i = 0; i < ring.Length; i++)
                    ring[i] = Add(placement.Position, Turn(m, ring[i]));

                SolidRotation.Matrix(o.Rotation, f);
                var normal = Turn(m, new Vector3d(f[2], f[5], f[8]));
                surface ??= new HullSurface(doc.Solids.AsSpan(), doc.Meshes);
                var blocked = PortBlocked(surface, ring, normal);
                Marker(overlay, ring, normal, blocked ? BlockedColour : PortColour, camera);
            }
        }
    }

    /// <summary>Whether the hull stands within 2 m in front of a port's face (from its middle).</summary>
    public static bool PortBlocked(HullSurface surface, Vector3d[] ring, Vector3d normal)
    {
        if (ring.Length == 0)
            return false;

        Vector3d sum = default;

        foreach (var p in ring)
            sum = Add(sum, p);

        var middle = Scale(sum, 1.0 / ring.Length);
        return surface.Raycast(Add(middle, Scale(normal, 0.05)), normal, 2.0, out _, out _);
    }

    private static Vector3d Turn(ReadOnlySpan<double> m, Vector3d p) =>
        new(m[0] * p.X + m[1] * p.Y + m[2] * p.Z, m[3] * p.X + m[4] * p.Y + m[5] * p.Z, m[6] * p.X + m[7] * p.Y + m[8] * p.Z);

    /// <summary>The outline and an arrow out through it.</summary>
    private static void Marker(OverlayRenderer overlay, Vector3d[] ring, Vector3d through, uint colour, OrbitCamera camera)
    {
        if (ring.Length == 0)
            return;

        Vector3d sum = default;

        foreach (var p in ring)
            sum = Add(sum, p);

        var middle = Scale(sum, 1.0 / ring.Length);
        var size = Math.Clamp(camera.Depth(middle) * 0.04, 0.2, 5.0);
        overlay.OnTop = true;

        for (var i = 0; i < ring.Length; i++)
            overlay.Ribbon(ring[i], ring[(i + 1) % ring.Length], size * 0.03, colour);

        var tip = Add(middle, Scale(through, size * 1.5));
        overlay.Ribbon(middle, tip, size * 0.03, colour);
        overlay.Cone(tip, Add(tip, Scale(through, size * 0.4)), size * 0.12, colour);
        overlay.Cube(middle, size * 0.06, colour);
        overlay.OnTop = false;
    }
}
