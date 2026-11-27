using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Interior panel and view tool for hallways: walkable spaces cut out of the hull along a network of nodes. The panel
/// shows the selected hallway's properties: its profile (from the Profiles tab), clearance, gravity, stair material and
/// runs. In the view: click a node to select it and drag the arrows or turn gravity on the rings; the bar beside
/// it adds a node (from an end it extends, from the middle it branches into a junction) or deletes; the diamond in the
/// middle of each run of the selected hallway splits it with a new node.
/// </summary>
public sealed class InteriorPanel
{
    private const float NodePixels = 10f;
    private const double NewRunMetres = 3.0;

    private static readonly string[] Axes = ["Yaw", "Pitch", "Roll"];
    private static readonly uint PathColour = OverlayRenderer.Rgba(90, 220, 220, 150);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(140, 255, 255, 255);
    private static readonly uint NodeColour = OverlayRenderer.Rgba(235, 235, 235);
    private static readonly uint PickedColour = OverlayRenderer.Rgba(255, 230, 120);
    private static readonly uint SplitColour = OverlayRenderer.Rgba(140, 255, 255, 180);
    private static readonly uint GravityColour = OverlayRenderer.Rgba(255, 150, 60);

    private readonly HullMaterialCatalog _catalog;
    private readonly TransformGizmo _gizmo = new();
    private string _message = "";

    public InteriorPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public uint Selected;
    public int SelectedNode = -1;

    /// <summary>Set for one frame when "Edit profile" asks the Profiles tab to show this profile.</summary>
    public int EditProfileRequest { get; private set; } = -1;

    public void Reset() => (Selected, SelectedNode) = (0, -1);

    /// <summary>Clears the one-frame request (before the panels draw).</summary>
    public void BeginFrame() => EditProfileRequest = -1;

    private int _addProfile = -1;
    private float _addLength = 4f;

    /// <summary>A level hallway at <paramref name="at"/> with the new-hallway options, selected with its far node.</summary>
    public void AddHallway(DesignSession session, Vector3d at)
    {
        var material = _catalog.All.Count > 0 ? _catalog.All[0].Id : default;
        Selected = InteriorCommands.AddHallway(session, at, Add(at, new Vector3d(0, 0, Math.Clamp(_addLength, 0.5f, 100f))), material, _addProfile);
        SelectedNode = Selected != 0 ? 1 : -1;
        _message = Selected == 0 ? InteriorCommands.LastError : "Drag its nodes in the view; right-click a node to add more.";
    }

    /// <summary>The options new hallways get (in the Add dialog): profile and length.</summary>
    public void DrawAddOptions(DesignSession session)
    {
        ImGui.TextWrapped($"A walkable space cut out of the hull along a line of nodes. A person needs {HallwayGeometry.MinClearWidth:F1} x {HallwayGeometry.MinClearHeight:F1} m clear; each further {HallwayGeometry.LaneWidth:F1} m of width lets one more pass.");
        var profiles = session.Doc.Interior?.Profiles;
        var name = _addProfile >= 0 && profiles is not null && _addProfile < profiles.Count ? profiles[_addProfile].Name : $"{InteriorCommands.DefaultProfileName} (new)";
        DesignerStyle.ItemWidth(220f);

        if (ImGui.BeginCombo("Profile##addhall"u8, $"{name}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < (profiles?.Count ?? 0); i++)
            {
                if (profiles![i].Kind == ProfileKind.Hallway && ImGui.Selectable($"{profiles[i].Name}##addprof{i}", i == _addProfile, ImGuiSelectableFlags.None, default))
                    _addProfile = i;
            }

            ImGui.EndCombo();
        }

        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Length m##addhall"u8, ref _addLength, 0f, 0f, "%.1f"u8, ImGuiInputTextFlags.None);
    }

    /// <summary>A short name for the object tree.</summary>
    public string Label(DesignSession session, uint id)
    {
        var interior = session.Doc.Interior;
        var index = interior?.IndexOf(id) ?? -1;

        if (index < 0)
            return $"Hallway #{id}";

        var h = interior!.Hallways[index];
        var profile = interior.ProfileOf(h);
        return $"Hallway {h.Length():F1} m, {profile?.Name ?? "?"}  #{id}";
    }

    /// <summary>The selected hallway's properties (in the Properties tab).</summary>
    public void DrawProperties(DesignSession session)
    {
        EditProfileRequest = -1;
        var interior = session.Doc.Interior;
        var index = interior?.IndexOf(Selected) ?? -1;

        if (index >= 0)
            DrawHallway(session, interior!, interior!.Hallways[index]);

        if (_message.Length > 0)
            ImGui.TextWrapped($"{_message}");
    }

    private void DrawHallway(DesignSession session, HullInterior interior, HullHallway hallway)
    {
        ImGui.SeparatorText($"Hallway #{hallway.Id}");

        // Profile: one of the plan's, shaped in the Profiles tab.
        var current = hallway.Profile < interior.Profiles.Count ? interior.Profiles[hallway.Profile].Name : "?";
        DesignerStyle.ItemWidth(220f);

        if (ImGui.BeginCombo("Profile"u8, $"{current}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < interior.Profiles.Count; i++)
            {
                if (interior.Profiles[i].Kind != ProfileKind.Hallway)
                    continue;

                if (ImGui.Selectable($"{interior.Profiles[i].Name}##pick{i}", i == hallway.Profile, ImGuiSelectableFlags.None, default) && i != hallway.Profile)
                    InteriorCommands.SetHallwayProfile(session, hallway.Id, i);
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        if (ImGui.Button("Edit profile"u8))
            EditProfileRequest = hallway.Profile;

        if (interior.ProfileOf(hallway) is { } profile)
        {
            var clear = HallwayGeometry.Measure(profile.Points);

            if (clear.Fits)
                ImGui.Text($"Clear {clear.Width:F2} x {Math.Min(clear.Height, 9.99):F2} m: {clear.People} {(clear.People == 1 ? "person" : "people")} abreast");
            else
                ImGui.TextColored(new float4(1f, 0.45f, 0.35f, 1f), $"Too small: clear {clear.Width:F2} x {clear.Height:F2} m, needs {HallwayGeometry.MinClearWidth:F1} x {HallwayGeometry.MinClearHeight:F1} m");
        }

        // Gravity.
        ImGui.SeparatorText("Gravity"u8);
        var down = HallwayGeometry.Down(hallway.Gravity);
        ImGui.Text($"Down {down.X:F2}, {down.Y:F2}, {down.Z:F2}");
        ImGui.SameLine();

        if (ImGui.Button("Ship down"u8))
            InteriorCommands.SetHallway(session, hallway.Id, h => h.Gravity = SolidRotation.Identity);

        var (yaw, pitch, roll) = SolidRotation.ToSteps(hallway.Gravity);
        int[] steps = [yaw, pitch, roll];

        for (var a = 0; a < 3; a++)
        {
            ImGui.Text($"{Axes[a]} {steps[a] * SolidRotation.DegreesPerStep} deg");
            ImGui.SameLine();
            var axis = a;

            if (ImGui.Button($"-##grav{a}"))
                InteriorCommands.SetHallway(session, hallway.Id, h => h.Gravity = SolidRotation.AddSteps(h.Gravity, axis, -1));

            ImGui.SameLine();

            if (ImGui.Button($"+##grav{a}"))
                InteriorCommands.SetHallway(session, hallway.Id, h => h.Gravity = SolidRotation.AddSteps(h.Gravity, axis, 1));
        }

        ImGui.TextDisabled("Or select a node and turn the rings in the view."u8);
        var material = CatalogIndex(session.Doc, hallway.Material);

        if (MaterialCombo("Stairs##hallmat"u8, ref material))
            InteriorCommands.SetMaterial(session, hallway.Id, _catalog.All[material].Id);

        // Runs.
        ImGui.SeparatorText("Runs"u8);

        for (var r = 0; r < hallway.Runs.Count; r++)
        {
            var (a, b) = hallway.Runs[r];
            var length = Sub(hallway.Node(b), hallway.Node(a)).Length();
            var slope = HallwayGeometry.Slope(hallway.Node(a), hallway.Node(b), down);
            var text = HallwayGeometry.Kind(slope) switch
            {
                RunKind.Level => "level",
                RunKind.Ramp => $"ramp {slope:F0} deg",
                RunKind.Stairs => $"stairs {slope:F0} deg",
                _ => $"shaft {slope:F0} deg: too steep for stairs (needs a ladder)",
            };

            if (HallwayGeometry.Kind(slope) == RunKind.Shaft)
                ImGui.TextColored(new float4(1f, 0.7f, 0.35f, 1f), $"{a}-{b}: {length:F2} m, {text}");
            else
                ImGui.TextDisabled($"{a}-{b}: {length:F2} m, {text}");
        }
    }

    // ---- the bar beside the selected node ----

    public void DrawContext(DesignSession session, OrbitCamera camera, Vector2 viewport)
    {
        var interior = session.Doc.Interior;
        var index = interior?.IndexOf(Selected) ?? -1;

        if (index < 0 || SelectedNode < 0 || SelectedNode >= interior!.Hallways[index].Nodes.Count || _gizmo.Dragging)
            return;

        var hallway = interior.Hallways[index];

        if (!ContextBar.Begin("hallway", camera, hallway.Node(SelectedNode), viewport))
            return;

        var end = hallway.Degree(SelectedNode) <= 1;

        if (ContextBar.Button(end ? "+ Node" : "+ Branch", end ? "Extend the hallway from this node" : "Branch from this node (a junction)"))
        {
            var node = InteriorCommands.AddNode(session, hallway.Id, SelectedNode, NewNodeAt(hallway, SelectedNode, NewRunMetres));
            SelectedNode = node >= 0 ? node : SelectedNode;
        }

        if (ContextBar.Button("Delete node", "Remove this node (a node between two runs joins them)"))
        {
            var count = hallway.Nodes.Count;
            InteriorCommands.RemoveNode(session, hallway.Id, SelectedNode);
            SelectedNode = count <= 2 ? -1 : Math.Max(0, SelectedNode - 1);

            if (count <= 2)
                Selected = 0;
        }

        if (ContextBar.Button("Delete hallway", "Remove the whole hallway"))
        {
            InteriorCommands.RemoveHallway(session, hallway.Id);
            Reset();
        }

        ContextBar.End();
    }

    /// <summary>
    /// Where a new node off <paramref name="node"/> goes: on from an end, straight on; from a node with more runs, square
    /// to its first run, level against the hallway's gravity.
    /// </summary>
    private static Vector3d NewNodeAt(HullHallway hallway, int node, double length)
    {
        var at = hallway.Node(node);
        var runs = hallway.Runs.Where(r => r.A == node || r.B == node).ToList();

        if (runs.Count == 0)
            return Add(at, new Vector3d(0, 0, length));

        var other = runs[0].A == node ? runs[0].B : runs[0].A;
        var away = Normalise(Sub(at, hallway.Node(other)), new Vector3d(0, 0, 1));

        if (runs.Count == 1)
            return Add(at, Scale(away, length));

        var up = Scale(HallwayGeometry.Down(hallway.Gravity), -1);
        var side = Normalise(Cross(away, up), new Vector3d(1, 0, 0));
        return Add(at, Scale(side, length));
    }

    // ---- view ----

    /// <summary>Draws every hallway; picks nodes, splits runs, moves nodes and turns gravity. True when it used the mouse.</summary>
    public bool Update(DesignSession session, OrbitCamera camera, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, double step, OverlayRenderer overlay)
    {
        var interior = session.Doc.Interior;

        if (interior is null)
            return false;

        var io = ImGui.GetIO();
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        var clicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left, false);
        var hoverHall = 0u;
        var hoverNode = -1;
        var hoverRun = -1;
        var best = NodePixels;
        var lineHall = 0u;
        var lineBest = WholeTransform.LinePixels;

        foreach (var h in interior.Hallways)
        {
            var selected = h.Id == Selected;
            var down = HallwayGeometry.Down(h.Gravity);
            var profile = interior.ProfileOf(h);

            for (var r = 0; r < h.Runs.Count; r++)
            {
                var (a, b) = h.Runs[r];

                if (a >= h.Nodes.Count || b >= h.Nodes.Count)
                    continue;

                var pa = h.Node(a);
                var pb = h.Node(b);
                var size = Math.Max(0.03, camera.Depth(pa) * 0.006);
                overlay.Ribbon(pa, pb, size * (selected ? 0.6 : 0.4), selected ? SelectedColour : PathColour);

                // A click on the line picks the whole hallway.
                if (WholeTransform.ToLine(camera, viewProj, viewport, mouse, pa, pb) is var line && line < lineBest)
                    (lineBest, lineHall) = (line, h.Id);

                if (!selected)
                    continue;

                // The split handle in the middle of the run.
                var mid = Scale(Add(pa, pb), 0.5);
                overlay.Cube(mid, size * 0.8, SplitColour);

                if (camera.Project(mid, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                {
                    best = Vector2.Distance(pixel, mouse);
                    (hoverHall, hoverNode, hoverRun) = (h.Id, -1, r);
                }
            }

            for (var i = 0; i < h.Nodes.Count; i++)
            {
                var n = h.Node(i);
                var size = Math.Max(0.03, camera.Depth(n) * 0.006);

                if (profile is not null)
                    Outline(overlay, h, profile.Points, i, down, selected ? SelectedColour : PathColour);

                var picked = selected && i == SelectedNode;
                var junction = h.Degree(i) >= 3;
                overlay.Cube(n, picked ? size * 1.6 : junction ? size * 1.3 : size, picked ? PickedColour : NodeColour);

                if (camera.Project(n, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                {
                    best = Vector2.Distance(pixel, mouse);
                    (hoverHall, hoverNode, hoverRun) = (h.Id, i, -1);
                }
            }
        }

        var index = interior.IndexOf(Selected);

        if (index >= 0 && SelectedNode >= 0 && SelectedNode < interior.Hallways[index].Nodes.Count)
        {
            var hallway = interior.Hallways[index];
            var node = SelectedNode;
            var at = hallway.Node(node);
            var down = HallwayGeometry.Down(hallway.Gravity);
            var length = Math.Max(0.5, camera.Depth(at) * 0.08);
            overlay.OnTop = true;
            overlay.Ribbon(Sub(at, Scale(down, length * 1.6)), Sub(at, Scale(down, length * 0.9)), length * 0.04, GravityColour);
            overlay.Cone(Sub(at, Scale(down, length * 0.9)), Sub(at, Scale(down, length * 0.5)), length * 0.1, GravityColour);
            overlay.OnTop = false;

            if (_gizmo.Update(at, hallway.Gravity, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
            {
                if (_gizmo.Turned)
                {
                    var gravity = _gizmo.Rotation;
                    InteriorCommands.SetHallway(session, hallway.Id, h => h.Gravity = gravity);
                }
                else
                {
                    var moved = ComponentCommands.Quantise(Add(at, _gizmo.Offset));
                    InteriorCommands.SetHallway(session, hallway.Id, h => h.Nodes[node] = moved);
                }
            }

            if (_gizmo.Dragging || _gizmo.Hovered)
                return true;
        }
        else if (index >= 0 && SelectedNode < 0)
        {
            // The whole hallway: move every node, or turn them (and its gravity) about their middle.
            var hallway = interior.Hallways[index];
            var points = new Vector3d[hallway.Nodes.Count];

            for (var i = 0; i < points.Length; i++)
                points[i] = hallway.Node(i);

            var centre = WholeTransform.Centre(points);

            if (WholeTransform.Update(_gizmo, centre, true, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
            {
                Span<double> m = stackalloc double[9];
                WholeTransform.Turn(_gizmo, m);
                var nodes = new (int X, int Y, int Z)[points.Length];

                for (var i = 0; i < points.Length; i++)
                    nodes[i] = ComponentCommands.Quantise(WholeTransform.Apply(_gizmo, m, centre, points[i]));

                var gravity = WholeTransform.Rotation(_gizmo, m, hallway.Gravity);
                InteriorCommands.SetHallway(session, hallway.Id, h =>
                {
                    for (var i = 0; i < nodes.Length && i < h.Nodes.Count; i++)
                        h.Nodes[i] = nodes[i];

                    h.Gravity = gravity;
                });
            }

            if (_gizmo.Dragging || _gizmo.Hovered)
                return true;
        }

        if (!mouseFree || !clicked)
            return false;

        if (hoverRun >= 0)
        {
            var node = InteriorCommands.SplitRun(session, hoverHall, hoverRun);
            (Selected, SelectedNode) = (hoverHall, node);
            return true;
        }

        if (hoverNode >= 0)
        {
            (Selected, SelectedNode) = (hoverHall, hoverNode);
            return true;
        }

        if (lineHall != 0)
        {
            (Selected, SelectedNode) = (lineHall, -1);
            return true;
        }

        SelectedNode = -1;
        return false;
    }

    /// <summary>The profile drawn at node <paramref name="i"/>, square to a run there.</summary>
    private static void Outline(OverlayRenderer overlay, HullHallway h, List<ProfilePoint> profile, int i, Vector3d down, uint colour)
    {
        if (profile.Count < 3)
            return;

        var run = h.Runs.FindIndex(r => r.A == i || r.B == i);

        if (run < 0)
            return;

        var (a, b) = h.Runs[run];
        var along = Normalise(Sub(h.Node(b), h.Node(a)), new Vector3d(0, 0, 1));
        var (across, up) = HallwayGeometry.Frame(along, down, h.Gravity);
        var n = h.Node(i);
        var faint = OverlayRenderer.WithAlpha(colour, 120);
        const double k = HullSolid.MetresPerPositionUnit;

        for (var j = 0; j < profile.Count; j++)
        {
            var p = profile[j];
            var q = profile[(j + 1) % profile.Count];
            overlay.Line(Add(n, Add(Scale(across, p.X * k), Scale(up, p.Y * k))), Add(n, Add(Scale(across, q.X * k), Scale(up, q.Y * k))), faint);
        }
    }

    // ---- helpers ----

    private int CatalogIndex(PlanDocument doc, byte material)
    {
        if (material >= doc.Materials.Count)
            return 0;

        var id = doc.Materials[material];

        for (var i = 0; i < _catalog.All.Count; i++)
        {
            if (_catalog.All[i].Id == id)
                return i;
        }

        return 0;
    }

    private bool MaterialCombo(ReadOnlySpan<byte> label, ref int index)
    {
        var all = _catalog.All;

        if (all.Count == 0)
            return false;

        index = Math.Clamp(index, 0, all.Count - 1);
        var changed = false;
        DesignerStyle.ItemWidth(160f);

        if (ImGui.BeginCombo(label, $"{all[index].Name}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < all.Count; i++)
            {
                if (ImGui.Selectable($"{all[i].Name}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }
}
