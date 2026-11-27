using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Ui;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;

namespace BrutalGame.Designer.App;

/// <summary>
/// The object tree, the one selection, the right-click menu and the Add dialog. One thing is selected at a time (solids
/// may be several): picking a window clears the solid selection, and so on, whether picked in the tree or the view.
/// A right-click in the view shows the selection's actions and the Add items: on empty space only the Add items (new
/// things go at the camera target, or wait for a click on the hull), on the hull "Add here" (they go at the point clicked).
/// Choosing an item opens the Add dialog with its options; things already added are changed in Properties.
/// </summary>
public sealed partial class DesignerApp
{
    private static readonly (AddKind Kind, string Label)[] ShipAdds =
    [
        (AddKind.Box, "Box"), (AddKind.Wedge, "Wedge"), (AddKind.Cylinder, "Cylinder"), (AddKind.Hallway, "Hallway"),
        (AddKind.Window, "Window"), (AddKind.Decal, "Decal or paint shape"), (AddKind.Stroke, "Brush stroke"), (AddKind.Component, "Component"),
        (AddKind.Door, "Door"), (AddKind.DockingPort, "Docking port"), (AddKind.Rotor, "Rotor"), (AddKind.Hinge, "Hinge"), (AddKind.Slider, "Slider"),
    ];

    private static readonly (AddKind Kind, string Label)[] ComponentAdds =
    [
        (AddKind.Box, "Box"), (AddKind.Wedge, "Wedge"), (AddKind.Cylinder, "Cylinder"), (AddKind.Chamber, "Burn chamber"),
        (AddKind.Nozzle, "Nozzle"), (AddKind.Injector, "Injector"), (AddKind.Igniter, "Igniter"), (AddKind.Pipe, "Pipe"),
        (AddKind.Opening, "Doorway"), (AddKind.PortFace, "Docking port face"), (AddKind.Rotor, "Rotor"), (AddKind.Hinge, "Hinge"), (AddKind.Slider, "Slider"),
    ];

    private readonly ObjectTree _tree = new();
    private (uint, uint, uint, uint, uint, uint, uint) _lastSelection;

    /// <summary>The solid right-clicked (what a new joint is mounted on, and whose moving part a new solid joins), or 0.</summary>
    private uint _addOnSolid;

    /// <summary>The Add dialog's item (None: closed), and the item to add this frame without options.</summary>
    private AddKind _adding;
    private AddKind _pendingAdd;
    private float2 _addWindowAt;
    private Vector3d _addAt;
    private Vector3d _addNormal = new(0, 1, 0);
    private bool _addOnSurface;

    /// <summary>The one selected thing (or the primary solid).</summary>
    private ObjectRef CurrentSelection =>
        _openingsPanel.Selected != 0 ? new ObjectRef(ObjectKind.Opening, _openingsPanel.Selected)
        : _jointsPanel.Selected != 0 ? new ObjectRef(ObjectKind.Joint, _jointsPanel.Selected)
        : _interiorPanel.Selected != 0 ? new ObjectRef(ObjectKind.Hallway, _interiorPanel.Selected)
        : _windowsPanel.Selected != 0 ? new ObjectRef(ObjectKind.Window, _windowsPanel.Selected)
        : _decalsPanel.Selected != 0 ? new ObjectRef(ObjectKind.Decal, _decalsPanel.Selected)
        : _panels.Strokes.Selected != 0 ? new ObjectRef(ObjectKind.Stroke, _panels.Strokes.Selected)
        : _prefabPanel.Selected != 0 ? new ObjectRef(ObjectKind.Placement, _prefabPanel.Selected)
        : _session.Selection.Primary != 0 ? new ObjectRef(ObjectKind.Solid, _session.Selection.Primary)
        : ObjectRef.None;

    /// <summary>Selects <paramref name="item"/> and nothing else (<paramref name="toggle"/>: a solid joins the solid selection).</summary>
    private void Select(ObjectRef item, bool toggle = false)
    {
        if (item.Kind != ObjectKind.Solid)
            _session.Selection.Clear();

        if (item.Kind != ObjectKind.Hallway)
            _interiorPanel.Reset();

        if (item.Kind != ObjectKind.Window)
            _windowsPanel.Selected = 0;

        if (item.Kind != ObjectKind.Decal)
            _decalsPanel.Selected = 0;

        if (item.Kind != ObjectKind.Stroke)
            _panels.Strokes.Selected = 0;

        if (item.Kind != ObjectKind.Placement)
            _prefabPanel.Selected = 0;

        if (item.Kind != ObjectKind.Joint)
            _jointsPanel.Selected = 0;

        if (item.Kind != ObjectKind.Opening)
            _openingsPanel.Selected = 0;

        switch (item.Kind)
        {
            case ObjectKind.Solid when toggle:
                _session.Selection.Toggle(item.Id);
                break;
            case ObjectKind.Solid:
                _session.Selection.Set(item.Id);
                break;
            case ObjectKind.Hallway:
                (_interiorPanel.Selected, _interiorPanel.SelectedNode) = (item.Id, -1);
                break;
            case ObjectKind.Window:
                _windowsPanel.Selected = item.Id;
                break;
            case ObjectKind.Decal:
                _decalsPanel.Selected = item.Id;
                break;
            case ObjectKind.Stroke:
                (_panels.Strokes.Selected, _panels.Strokes.Drawing) = (item.Id, false);
                break;
            case ObjectKind.Placement:
                _prefabPanel.Selected = item.Id;
                break;
            case ObjectKind.Joint:
                _jointsPanel.Selected = item.Id;
                break;
            case ObjectKind.Opening:
                _openingsPanel.Selected = item.Id;
                break;
        }

        _lastSelection = SelectionState();
    }

    private (uint, uint, uint, uint, uint, uint, uint) SelectionState() =>
        (_session.Selection.Primary, _interiorPanel.Selected, _windowsPanel.Selected, _decalsPanel.Selected, _panels.Strokes.Selected, _prefabPanel.Selected, _jointsPanel.Selected);

    /// <summary>Whatever was picked last (in the view, a panel or the tree) is the one selection: the others clear.</summary>
    private void KeepOneSelected()
    {
        if (_active.Component is not null)
            return;

        var now = SelectionState();

        if (now == _lastSelection)
            return;

        var last = _lastSelection;
        var picked = now.Item2 != 0 && now.Item2 != last.Item2 ? new ObjectRef(ObjectKind.Hallway, now.Item2)
            : now.Item3 != 0 && now.Item3 != last.Item3 ? new ObjectRef(ObjectKind.Window, now.Item3)
            : now.Item4 != 0 && now.Item4 != last.Item4 ? new ObjectRef(ObjectKind.Decal, now.Item4)
            : now.Item5 != 0 && now.Item5 != last.Item5 ? new ObjectRef(ObjectKind.Stroke, now.Item5)
            : now.Item6 != 0 && now.Item6 != last.Item6 ? new ObjectRef(ObjectKind.Placement, now.Item6)
            : now.Item7 != 0 && now.Item7 != last.Item7 ? new ObjectRef(ObjectKind.Joint, now.Item7)
            : now.Item1 != 0 && now.Item1 != last.Item1 ? new ObjectRef(ObjectKind.Solid, now.Item1)
            : ObjectRef.None;

        if (picked.Kind == ObjectKind.Solid)
        {
            // Keep a multi-solid selection as it is; only clear the other kinds.
            var ids = _session.Selection.Ids.ToList();
            Select(picked);
            _session.Selection.Clear();
            ids.ForEach(_session.Selection.Toggle);
            _lastSelection = SelectionState();
        }
        else if (picked.Kind != ObjectKind.None)
        {
            Select(picked);
        }
        else
        {
            _lastSelection = now;
        }
    }

    /// <summary>Properties shows the selected non-solid (its panel's editor), or the solid editor.</summary>
    private void RouteProperties()
    {
        var selected = CurrentSelection;

        // The component editor's own panels show its parts; joints and openings show here.
        if (_active.Component is not null)
        {
            _panels.OtherProperties = selected.Kind switch
            {
                ObjectKind.Joint => s => _jointsPanel.DrawProperties(s),
                ObjectKind.Opening => s => _openingsPanel.DrawProperties(s),
                _ => null,
            };
            return;
        }

        _panels.OtherProperties = selected.Kind switch
        {
            ObjectKind.Hallway => s => _interiorPanel.DrawProperties(s),
            ObjectKind.Window => s => _windowsPanel.DrawProperties(s),
            ObjectKind.Decal => s => _decalsPanel.DrawProperties(s),
            ObjectKind.Stroke => s => _panels.DrawStrokeProperties(s),
            ObjectKind.Placement => s => _prefabPanel.DrawProperties(s),
            ObjectKind.Joint => s => _jointsPanel.DrawProperties(s),
            _ => null,
        };
    }

    private void DrawTree()
    {
        var ship = _active.Component is null;
        _tree.Draw(_session, CurrentSelection, Label, ship);

        if (_tree.Picked.Kind != ObjectKind.None)
            Select(_tree.Picked, _tree.Toggle);
    }

    private string Label(ObjectRef item) => item.Kind switch
    {
        ObjectKind.Hallway => _interiorPanel.Label(_session, item.Id),
        ObjectKind.Window => _windowsPanel.Label(_session, item.Id),
        ObjectKind.Decal => _decalsPanel.Label(_session, item.Id),
        ObjectKind.Stroke => BrushesPanel.Label(_session, item.Id),
        ObjectKind.Placement => PrefabPanel.Label(_session, item.Id),
        ObjectKind.Joint => JointsPanel.Label(_session, item.Id),
        ObjectKind.Opening => OpeningsPanel.Label(_session, item.Id),
        _ => $"#{item.Id}",
    };

    // ---- the right-click menu and the Add dialog ----

    /// <summary>On the frame the menu opens: what was right-clicked (a point on the hull, or empty space).</summary>
    private void PlaceMenu()
    {
        if (!ContextBar.OpenedThisFrame)
            return;

        var at = new System.Numerics.Vector2(ContextBar.At.X, ContextBar.At.Y);
        Vector3d eye = _camera.Position;
        Vector3d ray = _camera.Ray(at, ViewportSize);
        var surface = new HullSurface(_session.Doc.Solids.AsSpan(), _session.Doc.Meshes);
        _addOnSurface = surface.Raycast(eye, ray, 4000, out var t, out var normal);
        _addOnSolid = _addOnSurface ? Tools.HullPicking.Pick(_session.Doc, eye, ray, out _) : 0;
        (_addAt, _addNormal) = _addOnSurface ? (HullSurface.Add(eye, HullSurface.Scale(ray, t)), normal) : (new Vector3d(_camera.Target.X, _camera.Target.Y, _camera.Target.Z), new Vector3d(0, 1, 0));
        _addWindowAt = ContextBar.At;
    }

    /// <summary>The Add section of the menu: choosing an item opens its Add dialog (component parts add straight away).</summary>
    private void AddMenuItems()
    {
        ContextBar.Section("add");
        ImGui.TextDisabled(_addOnSurface ? "Add here"u8 : "Add"u8);
        var items = _active.Component is null ? ShipAdds : ComponentAdds;

        foreach (var (kind, label) in items)
        {
            if (!ContextBar.Button($"{label}##add{kind}", _addOnSurface ? "Add it where you right-clicked" : "Add it at the camera target"))
                continue;

            if (kind is AddKind.Chamber or AddKind.Nozzle or AddKind.Injector or AddKind.Igniter or AddKind.Pipe)
                _pendingAdd = kind;
            else
                _adding = kind;
        }
    }

    /// <summary>The Add dialog: the item's options, then Add (or Place / Draw); things added are changed later in Properties.</summary>
    private void DrawAddDialog()
    {
        if (_adding == AddKind.None)
            return;

        var open = true;
        var name = Array.Find(_active.Component is null ? ShipAdds : ComponentAdds, a => a.Kind == _adding).Label ?? $"{_adding}";
        ImGui.SetNextWindowPos(_addWindowAt, ImGuiCond.Appearing, default);
        ImGui.SetNextWindowSizeConstraints(new float2(ImGui.GetFontSize() * 18, 0), new float2(ImGui.GetFontSize() * 34, 10000), (ImGuiSizeCallback?)null, default);

        if (ImGui.Begin($"Add {name.ToLowerInvariant()}###adddialog", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            var done = DrawAddOptions();
            ImGui.Separator();
            ImGui.TextDisabled(_addOnSurface ? "Goes where you right-clicked."u8 : "Goes at the camera target."u8);

            if (!done && ImGui.Button("Cancel"u8))
                done = true;

            if (done || ImGui.IsKeyPressed(ImGuiKey.Escape, false) && !_panels.Strokes.Drawing)
                open = false;
        }

        ImGui.End();

        if (!open)
        {
            if (_adding == AddKind.Stroke && _panels.Strokes.Drawing)
                _panels.Strokes.Cancel();

            _adding = AddKind.None;
        }
    }

    /// <summary>The options of the item being added and its Add button; true when it was added (the dialog closes).</summary>
    private bool DrawAddOptions()
    {
        _panels.PlaceAt = new double3(_addAt.X, _addAt.Y, _addAt.Z);

        switch (_adding)
        {
            case AddKind.Box or AddKind.Wedge or AddKind.Cylinder:
            {
                _panels.DrawAddSolidOptions();

                if (!ImGui.Button("Add"u8))
                    return false;

                _panels.AddSolid(_session, _adding switch { AddKind.Wedge => SolidShape.Wedge, AddKind.Cylinder => SolidShape.Cylinder, _ => SolidShape.Box });

                // Added on a moving part: it moves with that part's joint.
                if (_session.Doc.Joints?.JointOf(_addOnSolid) is var joint and not 0 and not null)
                    JointCommands.SetMember(_session, _session.Selection.Primary, joint.Value);

                Select(new ObjectRef(ObjectKind.Solid, _session.Selection.Primary));
                return true;
            }
            case AddKind.Hallway:
                _interiorPanel.DrawAddOptions(_session);

                if (!ImGui.Button("Add"u8))
                    return false;

                _interiorPanel.AddHallway(_session, _addAt);
                Select(new ObjectRef(ObjectKind.Hallway, _interiorPanel.Selected));
                return true;
            case AddKind.Window:
                _windowsPanel.DrawAddOptions();
                return Place(() => _windowsPanel.PlaceAt(_session, _addAt, _addNormal), _windowsPanel.StartPlacing, ObjectKind.Window, () => _windowsPanel.Selected);
            case AddKind.Decal:
                _decalsPanel.DrawAddOptions(_session);
                return Place(() => _decalsPanel.PlaceAt(_session, _addAt, _addNormal), _decalsPanel.StartPlacing, ObjectKind.Decal, () => _decalsPanel.Selected);
            case AddKind.Stroke:
            {
                // Draw, then the dialog stays to finish or cancel; it closes when the stroke is made.
                var before = _panels.Strokes.Selected;
                _panels.DrawAddStroke(_session);
                var made = _panels.Strokes.Selected != 0 && _panels.Strokes.Selected != before && !_panels.Strokes.Drawing;

                if (made)
                    Select(new ObjectRef(ObjectKind.Stroke, _panels.Strokes.Selected));

                return made;
            }
            case AddKind.Rotor or AddKind.Hinge or AddKind.Slider:
            {
                var kind = _adding switch { AddKind.Rotor => JointKind.Rotor, AddKind.Hinge => JointKind.Hinge, _ => JointKind.Slider };
                _jointsPanel.DrawAddOptions(kind);

                if (!ImGui.Button("Add"u8))
                    return false;

                _jointsPanel.PlaceAt(_session, kind, _addAt, _addNormal, _addOnSolid);
                Select(new ObjectRef(ObjectKind.Joint, _jointsPanel.Selected));
                return true;
            }
            case AddKind.Door or AddKind.DockingPort:
            {
                var kind = _adding == AddKind.Door ? OpeningKind.Door : OpeningKind.DockingPort;
                _prefabPanel.DrawAddOptions(kind);
                ImGui.BeginDisabled(!_prefabPanel.HasAddPick);

                if (ImGui.Button("Add"u8))
                {
                    _prefabPanel.PlaceFacing(_session, _addAt, _addNormal, _session.Snap.StepMetres);
                    Select(new ObjectRef(ObjectKind.Placement, _prefabPanel.Selected));
                    ImGui.EndDisabled();
                    return true;
                }

                ImGui.EndDisabled();
                return false;
            }
            case AddKind.Opening or AddKind.PortFace:
            {
                var kind = _adding == AddKind.Opening ? OpeningKind.Door : OpeningKind.DockingPort;
                _openingsPanel.DrawAddOptions(kind);

                if (!ImGui.Button("Add"u8))
                    return false;

                _openingsPanel.PlaceAt(_session, kind, _addAt, _addNormal);
                Select(new ObjectRef(ObjectKind.Opening, _openingsPanel.Selected));
                return true;
            }
            case AddKind.Component:
                _prefabPanel.DrawAddOptions();
                ImGui.BeginDisabled(!_prefabPanel.HasAddPick);

                if (ImGui.Button("Add"u8))
                {
                    _prefabPanel.PlaceAt(_session, _addAt, _session.Snap.StepMetres);
                    Select(new ObjectRef(ObjectKind.Placement, _prefabPanel.Selected));
                    ImGui.EndDisabled();
                    return true;
                }

                ImGui.EndDisabled();
                return false;
            default:
                return true;
        }
    }

    /// <summary>A surface item: placed at the right-clicked point, or (from empty space) by the next click on the hull.</summary>
    private bool Place(Action placeHere, Action startPlacing, ObjectKind kind, Func<uint> selected)
    {
        if (_addOnSurface)
        {
            if (!ImGui.Button("Add"u8))
                return false;

            placeHere();
            Select(new ObjectRef(kind, selected()));
            return true;
        }

        if (!ImGui.Button("Place: click the hull"u8))
            return false;

        startPlacing();
        return true;
    }

    /// <summary>Items added straight from the menu (component parts).</summary>
    private void HandleAdd()
    {
        var kind = _pendingAdd;
        _pendingAdd = AddKind.None;

        if (kind == AddKind.None || _active.Component is not { } component)
            return;

        if (kind == AddKind.Pipe)
            component.AddPipe(_session, _addAt);
        else
            component.AddPart(_session, kind switch { AddKind.Chamber => PartKind.Chamber, AddKind.Nozzle => PartKind.Nozzle, AddKind.Injector => PartKind.Injector, _ => PartKind.Igniter });
    }
}
