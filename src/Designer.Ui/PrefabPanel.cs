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

    private PrefabNode? _addPick;

    /// <summary>The options new placements get (in the Add dialog): which component, from the library.</summary>
    public void DrawAddOptions()
    {
        ImGui.TextWrapped("A component: a prefab from the library, cut into the hull as a bay. Its insides change only in the component editor."u8);

        if (ImGui.Button("Refresh library"u8))
            RefreshRequest = true;

        foreach (var child in _library.Root.Children)
            Tree(child);

        if (_addPick is null)
            ImGui.TextDisabled("Pick a component."u8);
    }

    /// <summary>The component picked in the Add dialog, or null.</summary>
    public bool HasAddPick => _addPick?.Prefab is not null;

    /// <summary>The picked component placed at <paramref name="at"/> (snapped); selected.</summary>
    public void PlaceAt(DesignSession session, Vector3d at, double step)
    {
        if (_addPick?.Prefab is not { } prefab)
            return;

        var snapped = step > 0 ? new Vector3d(Math.Round(at.X / step) * step, Math.Round(at.Y / step) * step, Math.Round(at.Z / step) * step) : at;
        Selected = ComponentCommands.PlacePrefab(session, prefab, snapped);
        _message = Selected == 0 ? ComponentCommands.LastError : $"Placed {_addPick.Name}.";
    }

    /// <summary>A short name for the object tree.</summary>
    public static string Label(DesignSession session, uint id)
    {
        var data = session.Doc.Components;
        var index = data?.IndexOfPlacement(id) ?? -1;

        if (index < 0)
            return $"Component #{id}";

        var p = data!.Placements[index];
        var name = session.Doc.Prefabs.TryGetValue(p.Prefab, out var prefab) && prefab.Meta.Name.Length > 0 ? prefab.Meta.Name : $"{p.Prefab:x16}";
        return $"Component, {name}  #{id}";
    }

    /// <summary>The selected placement's properties (in the Properties tab).</summary>
    public void DrawProperties(DesignSession session)
    {
        DrawPlacement(session);

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
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

        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;

        if (ReferenceEquals(node, _addPick))
            flags |= ImGuiTreeNodeFlags.Selected;

        ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", flags);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            _addPick = node;
    }

    private void DrawPlacement(DesignSession session)
    {
        var data = session.Doc.Components;

        if (data is null)
            return;

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
