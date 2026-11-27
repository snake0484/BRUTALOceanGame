using System.Numerics;
using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The ship workspace's components: a tree of prefabs (catalog and your own) to drag into the view, the placed ones, and
/// moving or turning a placement. A placement is the prefab as a whole: its insides cannot be changed here, only opened
/// as a copy in the component editor (saving that makes a new prefab).
/// </summary>
public sealed class PrefabPanel
{
    private static readonly string[] Axes = ["Yaw", "Pitch", "Roll"];

    private readonly PrefabLibrary _library;
    private readonly Func<PlanDocument, PrefabShape> _shape;
    private readonly TransformGizmo _gizmo = new();
    private PrefabNode? _dragging;
    private string _message = "";

    public PrefabPanel(PrefabLibrary library, Func<PlanDocument, PrefabShape> shape)
    {
        _library = library;
        _shape = shape;
    }

    public uint Selected;

    /// <summary>Set for one frame when the user asks to open a placed prefab in the component editor.</summary>
    public PlanDocument? OpenRequest { get; private set; }

    /// <summary>Set when the user asks for the library to be read again.</summary>
    public bool RefreshRequest { get; private set; }

    /// <summary>Clears the one-frame requests (before the panels draw).</summary>
    public void BeginFrame()
    {
        OpenRequest = null;
        RefreshRequest = false;
    }

    public void Draw(DesignSession session)
    {
        if (ImGui.Begin("Components"u8))
        {
            ImGui.TextDisabled("Drag a component into the view to place it."u8);

            if (ImGui.Button("Refresh library"u8))
                RefreshRequest = true;

            foreach (var child in _library.Root.Children)
                Tree(child);

            DrawPlacements(session);

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();

        if (_dragging is not null)
            ImGui.SetTooltip($"Place {_dragging.Name}");
    }

    private void Tree(PrefabNode node)
    {
        if (node.IsFolder)
        {
            if (ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                foreach (var child in node.Children)
                    Tree(child);

                ImGui.TreePop();
            }

            return;
        }

        ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);

        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left, -1f))
            _dragging = node;
    }

    private void DrawPlacements(DesignSession session)
    {
        var data = session.Doc.Components;
        ImGui.SeparatorText("Placed"u8);

        if (data is null || data.Placements.Count == 0)
        {
            ImGui.TextDisabled("Nothing placed yet."u8);
            return;
        }

        foreach (var p in data.Placements)
        {
            var name = session.Doc.Prefabs.TryGetValue(p.Prefab, out var prefab) && prefab.Meta.Name.Length > 0 ? prefab.Meta.Name : $"{p.Prefab:x16}";

            if (ImGui.Selectable($"#{p.Id}  {name}##placement{p.Id}", p.Id == Selected, ImGuiSelectableFlags.None, default))
                Selected = p.Id;
        }

        var index = data.IndexOfPlacement(Selected);

        if (index < 0)
            return;

        var placement = data.Placements[index];
        ImGui.SeparatorText($"Placement #{placement.Id}");
        var at = placement.Position;
        ImGui.Text($"At {at.X:F3}, {at.Y:F3}, {at.Z:F3} m (arrows move, rings turn in the view)");
        var (yaw, pitch, roll) = SolidRotation.ToSteps(placement.Rotation);
        int[] steps = [yaw, pitch, roll];

        for (var a = 0; a < 3; a++)
        {
            ImGui.Text($"{Axes[a]} {steps[a] * SolidRotation.DegreesPerStep} deg");
            ImGui.SameLine();

            if (ImGui.Button($"-##prot{a}"))
            {
                placement.Rotation = SolidRotation.AddSteps(placement.Rotation, a, -1);
                ComponentCommands.SetPlacement(session, placement);
            }

            ImGui.SameLine();

            if (ImGui.Button($"+##prot{a}"))
            {
                placement.Rotation = SolidRotation.AddSteps(placement.Rotation, a, 1);
                ComponentCommands.SetPlacement(session, placement);
            }
        }

        if (session.Doc.Prefabs.TryGetValue(placement.Prefab, out var held))
        {
            var shape = _shape(held);
            ImGui.Text($"Parts and pipes {shape.PartsMassKg:F1} kg; cuts a {(shape.Max.X - shape.Min.X):F2} x {(shape.Max.Y - shape.Min.Y):F2} x {(shape.Max.Z - shape.Min.Z):F2} m bay");

        }
    }

    /// <summary>The bar beside the selected placement in the view: open a copy in the component editor, delete.</summary>
    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var data = session.Doc.Components;
        var index = data?.IndexOfPlacement(Selected) ?? -1;

        if (index < 0 || _gizmo.Dragging)
            return;

        var placement = data!.Placements[index];

        if (!ContextBar.Begin("placement", camera, placement.Position, viewport))
            return;

        if (session.Doc.Prefabs.TryGetValue(placement.Prefab, out var held) && ContextBar.Button("Open a copy", "Open a copy in a component tab (saving it makes a new prefab)"))
            OpenRequest = held;

        if (ContextBar.Button("Delete", "Remove this placed component"))
        {
            ComponentCommands.RemovePlacement(session, placement.Id);
            Selected = 0;
        }

        ContextBar.End();
    }

    /// <summary>
    /// The view: drops a dragged prefab where the mouse ray meets the hull (else in front of the camera), picks placements,
    /// moves the selected one. True when it used the mouse.
    /// </summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, double step, OverlayRenderer overlay)
    {
        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var p0 = camera.Position;
        var eye = new Vector3d(p0.X, p0.Y, p0.Z);
        var ray = camera.Ray(mouse, viewport);

        if (_dragging is not null && !io.MouseDown[0])
        {
            var node = _dragging;
            _dragging = null;

            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow) && node.Prefab is not null)
            {
                var surface = new HullSurface(session.Doc.Solids.AsSpan(), session.Doc.Meshes);
                var at = surface.Raycast(eye, ray, 4000, out var t, out _) ? Add(eye, Scale(ray, t)) : Add(eye, Scale(ray, camera.Distance));
                var snapped = new Vector3d(Math.Round(at.X / step) * step, Math.Round(at.Y / step) * step, Math.Round(at.Z / step) * step);
                Selected = ComponentCommands.PlacePrefab(session, node.Prefab, snapped);
                _message = Selected == 0 ? ComponentCommands.LastError : $"Placed {node.Name}.";
            }

            return true;
        }

        var data = session.Doc.Components;

        if (data is null)
            return false;

        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);
        var index = data.IndexOfPlacement(Selected);

        if (index >= 0)
        {
            var placement = data.Placements[index];

            if (_gizmo.Update(placement.Position, placement.Rotation, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
            {
                if (_gizmo.Turned)
                    placement.Rotation = _gizmo.Rotation;
                else
                    (placement.PosX, placement.PosY, placement.PosZ) = ComponentCommands.Quantise(Add(placement.Position, _gizmo.Offset));

                ComponentCommands.SetPlacement(session, placement);
            }

            if (_gizmo.Dragging || _gizmo.Hovered)
                return true;
        }

        if (!mouseFree || !clicked)
            return false;

        // A placement under the mouse: its turned bounds.
        var best = double.MaxValue;
        var hit = 0u;

        foreach (var p in data.Placements)
        {
            if (!session.Doc.Prefabs.TryGetValue(p.Prefab, out var prefab))
                continue;

            var shape = _shape(prefab);
            var polygons = new List<SurfacePolygon>();
            shape.Place(p, m => m, polygons);

            foreach (var poly in polygons)
            {
                for (var i = 1; i + 1 < poly.Points.Length; i++)
                {
                    if (ComponentWorkspace.Triangle(eye, ray, poly.Points[0], poly.Points[i], poly.Points[i + 1], out var t) && t < best)
                    {
                        best = t;
                        hit = p.Id;
                    }
                }
            }
        }

        if (hit == 0)
            return false;

        Selected = hit;
        return true;
    }
}
