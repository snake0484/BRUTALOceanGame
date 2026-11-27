using Brutal.ImGuiApi;
using BrutalGame.Designer.Tools;
using BrutalGame.Plans;
using BrutalGame.Ships;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>What the designer can select: one thing at a time (solids may be several).</summary>
public enum ObjectKind : byte { None, Solid, Hallway, Window, Decal, Stroke, Placement }

public readonly record struct ObjectRef(ObjectKind Kind, uint Id)
{
    public static readonly ObjectRef None = default;
}

/// <summary>
/// The Objects tab: the ship as a tree. Each solid is a hull node; windows, decals, brush strokes and placed components
/// sit under the solid they are on (found from the geometry: the solid a short ray back along their normal hits, or for a
/// component the solid below it), worked out again when the plan changes. Hallways, and anything on no solid, sit under
/// the ship. Click to select (Ctrl adds solids); the host shows the selection in Properties.
/// </summary>
public sealed class ObjectTree
{
    private readonly Dictionary<ObjectRef, uint> _owners = new();
    private readonly Dictionary<uint, List<ObjectRef>> _children = new();
    private readonly List<ObjectRef> _loose = new();
    private int _version = -1;
    private int _frames;
    private DesignSession? _session;

    /// <summary>Set for the frame the user picks something in the tree.</summary>
    public ObjectRef Picked { get; private set; }

    /// <summary>The pick was a Ctrl-click (solids: add to the selection).</summary>
    public bool Toggle { get; private set; }

    /// <param name="selected">The current selection (one thing; or the primary solid).</param>
    /// <param name="label">A short description of a non-solid object.</param>
    public void Draw(DesignSession session, ObjectRef selected, Func<ObjectRef, string> label, bool shipItems)
    {
        Picked = ObjectRef.None;
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.InteriorDock), ImGuiCond.FirstUseEver);

        // The tree is the main list: in front of its dock's other tabs when the designer starts.
        if (++_frames == 3)
            ImGui.SetNextWindowFocus();

        if (ImGui.Begin("Objects"u8))
        {
            if (shipItems)
                Owners(session);

            ImGui.TextDisabled("Right-click empty space in the view to add."u8);

            if (ImGui.TreeNodeEx(shipItems ? "Ship##root"u8 : "Component##root"u8, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                var solids = session.Doc.Solids.AsSpan();

                for (var i = 0; i < solids.Length; i++)
                    Solid(session, solids[i], i, selected, label, shipItems);

                if (shipItems)
                {
                    if (session.Doc.Interior is { } interior)
                    {
                        foreach (var h in interior.Hallways)
                            Leaf(new ObjectRef(ObjectKind.Hallway, h.Id), selected, label);
                    }

                    foreach (var loose in _loose)
                        Leaf(loose, selected, label);
                }

                ImGui.TreePop();
            }
        }

        ImGui.End();
    }

    private void Solid(DesignSession session, in HullSolid solid, int index, ObjectRef selected, Func<ObjectRef, string> label, bool shipItems)
    {
        var children = shipItems && _children.TryGetValue(solid.Id, out var list) ? list : null;
        var flags = ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.DefaultOpen;

        if (children is null || children.Count == 0)
            flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;

        if (session.Selection.Contains(solid.Id))
            flags |= ImGuiTreeNodeFlags.Selected;

        var open = ImGui.TreeNodeEx($"{index,3}  {SolidName(solid)}  #{solid.Id}##solid{solid.Id}", flags);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen())
            (Picked, Toggle) = (new ObjectRef(ObjectKind.Solid, solid.Id), ImGui.GetIO().KeyCtrl);

        if (!open || (flags & ImGuiTreeNodeFlags.NoTreePushOnOpen) != 0)
            return;

        foreach (var child in children!)
            Leaf(child, selected, label);

        ImGui.TreePop();
    }

    private void Leaf(ObjectRef item, ObjectRef selected, Func<ObjectRef, string> label)
    {
        var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;

        if (item == selected)
            flags |= ImGuiTreeNodeFlags.Selected;

        ImGui.TreeNodeEx($"{label(item)}##{item.Kind}{item.Id}", flags);

        if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            (Picked, Toggle) = (item, false);
    }

    private static string SolidName(in HullSolid s)
    {
        var role = SolidRoles.Of(s) switch
        {
            SolidRole.Room => "room",
            SolidRole.Component => "component bay",
            SolidRole.Panel => "panel",
            _ => s.Op switch { SolidOp.Subtract => "cut", SolidOp.Paint => "paint", SolidOp.Intersect => "intersect", _ => "hull" },
        };

        return $"{role}, {s.Shape.ToString().ToLowerInvariant()}";
    }

    /// <summary>Which solid each window, decal, stroke and placement sits on; rebuilt when the plan changes.</summary>
    private void Owners(DesignSession session)
    {
        if (_version == session.Version && ReferenceEquals(_session, session))
            return;

        (_version, _session) = (session.Version, session);
        _owners.Clear();
        _children.Clear();
        _loose.Clear();
        var doc = session.Doc;

        if (doc.Windows is { } windows)
        {
            foreach (var w in windows.Windows)
                Own(doc, new ObjectRef(ObjectKind.Window, w.Id), w.Position, w.Normal);
        }

        if (doc.Decals is { } decals)
        {
            foreach (var d in decals.Decals)
                Own(doc, new ObjectRef(ObjectKind.Decal, d.Id), d.Position, d.Normal);
        }

        if (doc.Brushes is { } brushes)
        {
            foreach (var s in brushes.Strokes)
            {
                if (s.Points.Count > 0)
                    Own(doc, new ObjectRef(ObjectKind.Stroke, s.Id), s.Points[0].Position, s.Points[0].Normal);
            }
        }

        if (doc.Components is { } components)
        {
            foreach (var p in components.Placements)
                Own(doc, new ObjectRef(ObjectKind.Placement, p.Id), p.Position, new Vector3d(0, 1, 0));
        }
    }

    /// <summary>The solid a short ray from just off the point back along its normal hits (the surface it is on).</summary>
    private void Own(PlanDocument doc, ObjectRef item, Vector3d at, Vector3d normal)
    {
        var from = Add(at, Scale(normal, 0.25));
        var owner = HullPicking.Pick(doc, from, Scale(normal, -1), out _);

        if (owner == 0)
        {
            _loose.Add(item);
            return;
        }

        _owners[item] = owner;

        if (!_children.TryGetValue(owner, out var list))
            _children[owner] = list = new List<ObjectRef>();

        list.Add(item);
    }
}
