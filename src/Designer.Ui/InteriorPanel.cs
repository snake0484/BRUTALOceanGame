using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Interior panel and view tool: hallways, walkable spaces cut out of the hull along a path of nodes. The panel lists
/// them and edits the selected one: its profile (presets, then drag points in the 2D editor; double-click an edge to add a
/// point, right-click a point to remove it), the clear space a person needs and how many fit abreast, its gravity, stair
/// material and path. In the view, click a node to select it, drag the arrows to move it, and in rotate mode (E) turn the
/// hallway's gravity on the rings.
/// </summary>
public sealed class InteriorPanel
{
    private const double Units = HullSolid.PositionUnitsPerMetre;
    private const double ProfileSnap = 0.05;
    private const float PointPixels = 8f;
    private const float NodePixels = 10f;

    private static readonly string[] Axes = ["Yaw", "Pitch", "Roll"];
    private static readonly uint PathColour = OverlayRenderer.Rgba(90, 220, 220, 150);
    private static readonly uint SelectedColour = OverlayRenderer.Rgba(140, 255, 255, 255);
    private static readonly uint NodeColour = OverlayRenderer.Rgba(235, 235, 235);
    private static readonly uint PickedColour = OverlayRenderer.Rgba(255, 230, 120);
    private static readonly uint GravityColour = OverlayRenderer.Rgba(255, 150, 60);
    private static readonly ImColor8 Grid = new(255, 255, 255, 18);
    private static readonly ImColor8 FloorLine = new(255, 255, 255, 90);
    private static readonly ImColor8 OutlineColour = new(140, 255, 255, 255);
    private static readonly ImColor8 Point = new(235, 235, 235, 255);
    private static readonly ImColor8 PointHot = new(255, 230, 120, 255);
    private static readonly ImColor8 PersonFill = new(110, 220, 120, 40);
    private static readonly ImColor8 PersonLine = new(110, 220, 120, 200);
    private static readonly ImColor8 TooSmall = new(255, 90, 80, 220);

    private readonly HullMaterialCatalog _catalog;
    private readonly TransformGizmo _gizmo = new();
    private float _width = (float)InteriorCommands.DefaultWidth;
    private float _height = (float)InteriorCommands.DefaultHeight;
    private string _message = "";

    // Profile editor: a live copy while a point is dragged; committed (one undo step) on release.
    private List<ProfilePoint>? _draft;
    private int _dragPoint = -1;

    public InteriorPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public uint Selected;
    public int SelectedNode = -1;

    /// <summary>The view gizmo's mode (W move, E turns gravity).</summary>
    public GizmoMode Mode;

    /// <summary>Clears the selection (another tab opened).</summary>
    public void Reset() => (Selected, SelectedNode, _draft, _dragPoint) = (0, -1, null, -1);

    public void Draw(DesignSession session, double3 placeAt)
    {
        // Docks with Properties the first time (layouts saved before interiors existed have no place for it).
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Interior"u8))
        {
            ImGui.TextWrapped($"Hallways are walkable spaces cut out of the hull. A person needs {HallwayGeometry.MinClearWidth:F1} m wide by {HallwayGeometry.MinClearHeight:F1} m tall clear; each further {HallwayGeometry.LaneWidth:F1} m of width lets one more pass at once.");

            if (ImGui.Button("New hallway"u8))
            {
                var at = new Vector3d(placeAt.X, placeAt.Y, placeAt.Z);
                var material = _catalog.All.Count > 0 ? _catalog.All[0].Id : default;
                Selected = InteriorCommands.AddHallway(session, at, Add(at, new Vector3d(0, 0, 4)), material);
                SelectedNode = Selected != 0 ? 1 : -1;
                _message = Selected == 0 ? InteriorCommands.LastError : "Drag its nodes in the view; shape it below.";
            }

            DrawList(session);
            var interior = session.Doc.Interior;
            var index = interior?.IndexOf(Selected) ?? -1;

            if (index >= 0)
                DrawHallway(session, interior!.Hallways[index]);

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    private void DrawList(DesignSession session)
    {
        var interior = session.Doc.Interior;
        ImGui.SeparatorText("Hallways"u8);

        if (interior is null || interior.IsEmpty)
        {
            ImGui.TextDisabled("None yet."u8);
            return;
        }

        foreach (var h in interior.Hallways)
        {
            var clear = HallwayGeometry.Measure(h.Profile);
            var label = clear.Fits ? $"{clear.People} abreast" : "too small";

            if (ImGui.Selectable($"#{h.Id}  {h.Length():F1} m, {label}##hall{h.Id}", h.Id == Selected, ImGuiSelectableFlags.None, default))
                (Selected, SelectedNode, _draft) = (h.Id, -1, null);
        }
    }

    private void DrawHallway(DesignSession session, HullHallway hallway)
    {
        ImGui.SeparatorText($"Hallway #{hallway.Id}");
        var profile = _draft ?? hallway.Profile;
        var clear = HallwayGeometry.Measure(profile);

        if (clear.Fits)
            ImGui.Text($"Clear {clear.Width:F2} x {Math.Min(clear.Height, 9.99):F2} m: {clear.People} {(clear.People == 1 ? "person" : "people")} abreast");
        else
            ImGui.TextColored(new float4(1f, 0.45f, 0.35f, 1f), $"Too small: clear {clear.Width:F2} x {clear.Height:F2} m, needs {HallwayGeometry.MinClearWidth:F1} x {HallwayGeometry.MinClearHeight:F1} m");

        // Profile: presets, then the editor.
        ImGui.SeparatorText("Profile"u8);
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m"u8, ref _width, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Height m"u8, ref _height, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        _width = Math.Clamp(_width, 0.2f, 16f);
        _height = Math.Clamp(_height, 0.2f, 16f);

        for (var p = 0; p < HallwayGeometry.PresetNames.Length; p++)
        {
            if (p > 0)
                ImGui.SameLine();

            if (ImGui.Button($"{HallwayGeometry.PresetNames[p]}##preset{p}"))
                Commit(session, hallway.Id, HallwayGeometry.Preset((HallwayPreset)p, _width, _height));
        }

        ProfileEditor(session, hallway, profile, clear);
        ImGui.TextDisabled("Drag points; double-click an edge to add one, right-click a point to remove it."u8);

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

        ImGui.TextDisabled("Or select a node and turn the rings in the view (E)."u8);
        var material = CatalogIndex(session.Doc, hallway.Material);

        if (MaterialCombo("Stairs##hallmat"u8, ref material))
            InteriorCommands.SetMaterial(session, hallway.Id, _catalog.All[material].Id);

        DrawPath(session, hallway, down);
    }

    private void DrawPath(DesignSession session, HullHallway hallway, Vector3d down)
    {
        ImGui.SeparatorText("Path"u8);

        for (var i = 0; i < hallway.Nodes.Count; i++)
        {
            var n = hallway.Node(i);

            if (ImGui.Selectable($"{i}: {n.X:F2}, {n.Y:F2}, {n.Z:F2}##hnode{i}", i == SelectedNode, ImGuiSelectableFlags.None, default))
                SelectedNode = i;

            if (i + 1 < hallway.Nodes.Count)
            {
                var slope = HallwayGeometry.Slope(n, hallway.Node(i + 1), down);
                var kind = HallwayGeometry.Kind(slope);
                var text = kind switch
                {
                    RunKind.Level => "level",
                    RunKind.Ramp => $"ramp {slope:F0} deg",
                    RunKind.Stairs => $"stairs {slope:F0} deg",
                    _ => $"shaft {slope:F0} deg: too steep for stairs (needs a ladder)",
                };

                if (kind == RunKind.Shaft)
                    ImGui.TextColored(new float4(1f, 0.7f, 0.35f, 1f), $"   {Sub(hallway.Node(i + 1), n).Length():F2} m, {text}");
                else
                    ImGui.TextDisabled($"   {Sub(hallway.Node(i + 1), n).Length():F2} m, {text}");
            }
        }

        var node = SelectedNode;
        ImGui.BeginDisabled(node < 0 || node >= hallway.Nodes.Count || hallway.Nodes.Count >= HullHallway.MaxNodes);

        if (ImGui.Button("Add node after"u8))
        {
            var a = hallway.Node(node);
            var b = node + 1 < hallway.Nodes.Count ? Scale(Add(a, hallway.Node(node + 1)), 0.5) : Add(a, Scale(Normalise(Sub(a, hallway.Node(Math.Max(node - 1, 0))), new Vector3d(0, 0, 1)), 3.0));
            InteriorCommands.SetHallway(session, hallway.Id, h => h.Nodes.Insert(node + 1, ComponentCommands.Quantise(b)));
            SelectedNode = node + 1;
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(node < 0 || node >= hallway.Nodes.Count || hallway.Nodes.Count <= HullHallway.MinNodes);

        if (ImGui.Button("Delete node"u8))
        {
            InteriorCommands.SetHallway(session, hallway.Id, h => h.Nodes.RemoveAt(node));
            SelectedNode = Math.Max(0, node - 1);
        }

        ImGui.EndDisabled();
        ImGui.SameLine();

        if (ImGui.Button("Delete hallway"u8))
        {
            InteriorCommands.RemoveHallway(session, hallway.Id);
            Reset();
        }
    }

    // ---- profile editor ----

    private void ProfileEditor(DesignSession session, HullHallway hallway, List<ProfilePoint> profile, Clearance clear)
    {
        var size = new float2(Px(320f), Px(260f));
        var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##profile"u8, size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(origin, new float2(origin.X + size.X, origin.Y + size.Y), new ImColor8(0, 0, 0, 90));

        // Fit the outline (and a person) with a margin, one scale on both axes.
        double x0 = -0.5, x1 = 0.5, y1 = HallwayGeometry.MinClearHeight;

        foreach (var p in profile)
        {
            (x0, x1) = (Math.Min(x0, p.X / Units), Math.Max(x1, p.X / Units));
            y1 = Math.Max(y1, p.Y / Units);
        }

        const double margin = 0.35;
        x0 -= margin;
        x1 += margin;
        var y0 = -margin;
        y1 += margin;
        var scale = Math.Min(size.X / (x1 - x0), size.Y / (y1 - y0));
        var cx = (x0 + x1) * 0.5;
        var cy = (y0 + y1) * 0.5;
        float2 ToScreen(double x, double y) => new((float)(origin.X + size.X * 0.5 + (x - cx) * scale), (float)(origin.Y + size.Y * 0.5 - (y - cy) * scale));
        (double X, double Y) ToWorld(float2 s) => (cx + (s.X - origin.X - size.X * 0.5) / scale, cy - (s.Y - origin.Y - size.Y * 0.5) / scale);

        for (var g = Math.Ceiling(x0 / 0.5) * 0.5; g <= x1; g += 0.5)
            list.AddLine(ToScreen(g, y0), ToScreen(g, y1), Grid);

        for (var g = Math.Ceiling(y0 / 0.5) * 0.5; g <= y1; g += 0.5)
            list.AddLine(ToScreen(x0, g), ToScreen(x1, g), Grid);

        list.AddLine(ToScreen(x0, 0), ToScreen(x1, 0), FloorLine, 1.5f);

        // People abreast: one 0.8 × 1.9 lane each, centred on the floor; a red one when not even one fits.
        var (lo, hi) = HallwayGeometry.FloorSpan(profile);
        var mid = (lo + hi) * 0.5;
        var people = Math.Max(clear.People, 1);

        for (var i = 0; i < people; i++)
        {
            var left = mid - people * HallwayGeometry.LaneWidth * 0.5 + i * HallwayGeometry.LaneWidth;
            var a = ToScreen(left + 0.04, HallwayGeometry.MinClearHeight);
            var b = ToScreen(left + HallwayGeometry.LaneWidth - 0.04, 0.0);

            if (clear.Fits)
            {
                list.AddRectFilled(a, b, PersonFill);
                list.AddRect(a, b, PersonLine, 0f, default, 1f);
            }
            else
            {
                list.AddRect(a, b, TooSmall, 0f, default, 1.5f);
            }
        }

        var mouse = ImGui.GetIO().MousePos;
        var hot = -1;
        var best = Px(PointPixels);

        for (var j = 0; j < profile.Count; j++)
        {
            var s = ToScreen(profile[j].X / Units, profile[j].Y / Units);
            var distance = MathF.Sqrt((s.X - mouse.X) * (s.X - mouse.X) + (s.Y - mouse.Y) * (s.Y - mouse.Y));

            if (hovered && distance < best)
            {
                best = distance;
                hot = j;
            }
        }

        if (_dragPoint >= 0)
            hot = _dragPoint;

        for (var j = 0; j < profile.Count; j++)
        {
            var a = profile[j];
            var b = profile[(j + 1) % profile.Count];
            list.AddLine(ToScreen(a.X / Units, a.Y / Units), ToScreen(b.X / Units, b.Y / Units), OutlineColour, 2f);
        }

        for (var j = 0; j < profile.Count; j++)
        {
            var s = ToScreen(profile[j].X / Units, profile[j].Y / Units);
            var r = Px(j == hot ? 5f : 3.5f);
            list.AddRectFilled(new float2(s.X - r, s.Y - r), new float2(s.X + r, s.Y + r), j == hot ? PointHot : Point);
        }

        // Dragging a point: a live copy, committed on release.
        if (_dragPoint < 0 && hovered && hot >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
        {
            _dragPoint = hot;
            _draft = new List<ProfilePoint>(hallway.Profile);
        }

        if (_dragPoint >= 0 && _draft is not null)
        {
            var (x, y) = ToWorld(mouse);
            _draft[_dragPoint] = new ProfilePoint(Snap(x), Snap(y));

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var done = _draft;
                (_draft, _dragPoint) = (null, -1);
                Commit(session, hallway.Id, done);
            }

            return;
        }

        if (hovered && hot >= 0 && ImGui.IsMouseClicked(ImGuiMouseButton.Right, false))
        {
            if (profile.Count > HullHallway.MinProfilePoints)
            {
                var fewer = new List<ProfilePoint>(profile);
                fewer.RemoveAt(hot);
                Commit(session, hallway.Id, fewer);
            }

            return;
        }

        if (hovered && hot < 0 && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && profile.Count < HullHallway.MaxProfilePoints)
        {
            // The nearest edge gets a new point under the mouse.
            var edge = -1;
            var near = Px(PointPixels);

            for (var j = 0; j < profile.Count; j++)
            {
                var a = ToScreen(profile[j].X / Units, profile[j].Y / Units);
                var b = ToScreen(profile[(j + 1) % profile.Count].X / Units, profile[(j + 1) % profile.Count].Y / Units);
                var d = GizmoMath.SegmentDistance(new Vector2(mouse.X, mouse.Y), new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

                if (d < near)
                {
                    near = d;
                    edge = j;
                }
            }

            if (edge >= 0)
            {
                var (x, y) = ToWorld(mouse);
                var more = new List<ProfilePoint>(profile);
                more.Insert(edge + 1, new ProfilePoint(Snap(x), Snap(y)));
                Commit(session, hallway.Id, more);
            }
        }
    }

    private void Commit(DesignSession session, uint id, IReadOnlyList<ProfilePoint> profile)
    {
        if (!InteriorCommands.SetProfile(session, id, profile))
            _message = InteriorCommands.LastError;
    }

    private static int Snap(double metres) => (int)Math.Round(Math.Round(metres / ProfileSnap) * ProfileSnap * Units);

    // ---- view ----

    /// <summary>Draws every hallway (path, nodes, the profile at each node, gravity on the selected one); picks and moves nodes, turns gravity. True when it used the mouse.</summary>
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
        var best = NodePixels;

        foreach (var h in interior.Hallways)
        {
            var selected = h.Id == Selected;
            var down = HallwayGeometry.Down(h.Gravity);

            for (var i = 0; i < h.Nodes.Count; i++)
            {
                var n = h.Node(i);
                var size = Math.Max(0.03, camera.Depth(n) * 0.006);

                if (i + 1 < h.Nodes.Count)
                    overlay.Ribbon(n, h.Node(i + 1), size * (selected ? 0.6 : 0.4), selected ? SelectedColour : PathColour);

                Outline(overlay, h, i, down, selected ? SelectedColour : PathColour);
                var picked = selected && i == SelectedNode;
                overlay.Cube(n, picked ? size * 1.6 : size, picked ? PickedColour : NodeColour);

                if (camera.Project(n, viewProj, viewport, out var pixel) && Vector2.Distance(pixel, mouse) < best)
                {
                    best = Vector2.Distance(pixel, mouse);
                    (hoverHall, hoverNode) = (h.Id, i);
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

            if (_gizmo.Update(at, hallway.Gravity, Mode, camera, viewProj, viewport, mouse, mouseFree, io.MouseDown[0], clicked, step, overlay))
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

        if (!mouseFree || !clicked)
            return false;

        if (hoverNode >= 0)
        {
            (Selected, SelectedNode, _draft) = (hoverHall, hoverNode, null);
            return true;
        }

        SelectedNode = -1;
        return false;
    }

    /// <summary>The profile drawn at node <paramref name="i"/>, square to its run (the run out of it; the last node uses the run in).</summary>
    private static void Outline(OverlayRenderer overlay, HullHallway h, int i, Vector3d down, uint colour)
    {
        if (h.Nodes.Count < 2 || h.Profile.Count < 3)
            return;

        var s = Math.Min(i, h.Nodes.Count - 2);
        var along = Normalise(Sub(h.Node(s + 1), h.Node(s)), new Vector3d(0, 0, 1));
        var (across, up) = HallwayGeometry.Frame(along, down, h.Gravity);
        var n = h.Node(i);
        var faint = OverlayRenderer.WithAlpha(colour, 120);

        for (var j = 0; j < h.Profile.Count; j++)
        {
            var a = h.Profile[j];
            var b = h.Profile[(j + 1) % h.Profile.Count];
            overlay.Line(Add(n, Add(Scale(across, a.X / Units), Scale(up, a.Y / Units))), Add(n, Add(Scale(across, b.X / Units), Scale(up, b.Y / Units))), faint);
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

    private static float Px(float v) => MathF.Round(v * ImGui.GetFontSize() / DesignerStyle.BodySize);
}
