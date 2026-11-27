using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Ui;

/// <summary>What the File menu asks the host to do this frame (file dialogs and disk live in the host).</summary>
public enum FileRequest { None, New, NewComponent, Open, Save, SaveAs }

/// <summary>What the open tab is: a ship, or a component (its own plan, made into a prefab by saving it).</summary>
public enum Workspace { Ship, Component }

/// <summary>Numbers the stats panel shows; the host fills them after each bake and validation.</summary>
public sealed class DesignStats
{
    public HullMass Mass;
    public int Bricks;
    public int Triangles;
    public float BakeMs;
    public float BakeCpuMs;
    public int Rebaked;
    public int Reused;
    public int MaxSolidsPerBrick;
    public bool Baking;
    public float PrepareMs;
    public int Generated;

    /// <summary>Paint and decal boxes the hull shader tests, and how many brick-list entries point at them (each a test per fragment of that brick).</summary>
    public int Projectors, ProjectorRefs;

    /// <summary>The hull draw's GPU time (opaque pass, averaged over the timer's ring).</summary>
    public float HullGpuMs;
    public int Unstuck;
    public float SurfaceMs;
    public int SurfacePieces;
    public int SurfaceTriangles;
    public double FlairKg;
    public double ComponentsKg;

    /// <summary>Debug: draw the 0.25 m grid mesh (mass and collision's view of the hull) instead of the exact surface.</summary>
    public bool ShowGrid;
    public int ExactBricks;
    public PlanHash Hash;
    public readonly ValidationLog Validation = new();
    public string Message = "";
}

/// <summary>
/// The designer's ImGui panels: file and tools, solid list, properties of the primary solid, stats and validation.
/// Field edits go to a draft and commit as one undo group when the field is left after an edit, never per keystroke.
/// </summary>
public sealed class DesignerPanels
{

    private static readonly string[] ShapeNames = ["Box", "Wedge", "Cylinder"];
    private static readonly string[] OpNames = ["Add", "Subtract", "Paint"];
    private static readonly string[] StepNames = ["1/8 m", "1/4 m", "1/2 m", "1 m"];

    private readonly HullMaterialCatalog _catalog;
    private Draft _draft;
    private uint _draftId;
    private int _draftVersion = -1;
    private int _newMaterial;
    private bool _newSubtract;
    private float _wallMetres = 0.5f;
    private readonly MeshPanel _meshPanel = new();
    private readonly LayersPanel _layersPanel;
    private readonly BrushesPanel _brushesPanel;

    /// <summary>The viewport's stroke drawing and editing tool (the Brushes panel drives it).</summary>
    public readonly StrokeTool Strokes = new();

    private static readonly string[] RoleNames = ["Skin", "Room", "Component", "Panel"];

    /// <summary>The cutaway plane the hull renderer clips at.</summary>
    public Cutaway Cutaway => _layersPanel.Cutaway;

    private struct Draft
    {
        public double X, Y, Z;
        public double SizeX, SizeY, SizeZ;
        public int Yaw, Pitch, Roll;
        public bool Interior;
    }

    public DesignerPanels(HullMaterialCatalog catalog)
    {
        _catalog = catalog;
        _layersPanel = new LayersPanel(catalog);
        _brushesPanel = new BrushesPanel(catalog);
    }

    /// <summary>What the open tab is (the host sets it); a ship shows hull layers and brushes.</summary>
    public Workspace Workspace;

    /// <summary>Where new solids go: the host sets it to the camera target.</summary>
    public double3 PlaceAt;

    /// <summary>The interior view: the hull's panels are shown but cannot change anything.</summary>
    public bool Locked;

    /// <summary>Set for one frame when a room's "Edit profile" asks the Profiles tab to show its wall profile.</summary>
    public int EditProfileRequest { get; private set; } = -1;

    public void Draw(DesignSession session, DesignStats stats, MeshEditState meshEdit)
    {
        EditProfileRequest = -1;
        ImGui.BeginDisabled(Locked);
        DrawTools(session, stats);
        DrawSolidList(session);
        DrawProperties(session, meshEdit);
        _meshPanel.Draw(session, meshEdit);

        if (Workspace == Workspace.Ship)
        {
            _layersPanel.Draw(session);
            _brushesPanel.Draw(session, Strokes);

            if (_brushesPanel.EditProfileRequest >= 0)
                EditProfileRequest = _brushesPanel.EditProfileRequest;
        }

        ImGui.EndDisabled();
        DrawStats(session, stats);
    }

    private void DrawTools(DesignSession session, DesignStats stats)
    {
        if (ImGui.Begin("Designer"u8))
        {
            var name = session.Path.Length > 0 ? Path.GetFileName(session.Path) : "not saved yet";
            ImGui.Text($"{(Workspace == Workspace.Component ? "Component" : "Ship")}: {name}{(session.Modified ? " *" : "")}  ({session.Doc.Solids.Count} solids)");

            if (session.Path.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip($"{session.Path}");

            if (Workspace == Workspace.Component)
                ImGui.TextDisabled("Save it to make it a prefab the ship tabs can place."u8);

            if (stats.Message.Length > 0)
                ImGui.TextWrapped($"{stats.Message}");

            ImGui.SeparatorText("New solids (Add menu)"u8);
            ImGui.Checkbox("Subtract"u8, ref _newSubtract);
            ImGui.SameLine();
            MaterialCombo("Material##new"u8, ref _newMaterial);

            ImGui.SeparatorText("Snap"u8);
            ImGui.Checkbox("Grid"u8, ref session.Snap.Enabled);
            ImGui.SameLine();
            var step = Snap.Steps.IndexOf(session.Snap.Step);
            DesignerStyle.ItemWidth(90f);

            if (ImGui.BeginCombo("Step"u8, $"{StepNames[step]}", ImGuiComboFlags.None))
            {
                for (var i = 0; i < StepNames.Length; i++)
                {
                    if (ImGui.Selectable($"{StepNames[i]}", i == step, ImGuiSelectableFlags.None, default))
                        session.Snap.Step = Snap.Steps[i];
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Text("rotation 15 deg"u8);

            ImGui.SeparatorText("Mirror"u8);
            ImGui.Checkbox("X (port/starboard)"u8, ref session.Symmetry.X);
            ImGui.SameLine();
            ImGui.Checkbox("Y (up/down)"u8, ref session.Symmetry.Y);
            ImGui.SameLine();
            ImGui.Checkbox("Z (front/back)"u8, ref session.Symmetry.Z);

            ImGui.BeginDisabled(session.Undo.UndoCount == 0);

            if (ImGui.Button("Undo"u8))
                session.UndoLast();

            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(session.Undo.RedoCount == 0);

            if (ImGui.Button("Redo"u8))
                session.RedoNext();

            ImGui.EndDisabled();
            ImGui.TextDisabled("RMB orbit, MMB/Shift+RMB pan, wheel zoom, LMB pick (Ctrl adds)"u8);
            ImGui.TextDisabled("Gizmo: arrows move, rings turn, cubes resize; Del delete, Ctrl+D duplicate"u8);
            ImGui.TextDisabled("Ctrl+Z/Y undo/redo, F frame, Ctrl+W close tab"u8);
        }

        ImGui.End();
    }

    /// <summary>Add menu: a solid at the camera target with the new-solid settings, selected.</summary>
    public void AddSolid(DesignSession session, SolidShape shape)
    {
        // A ship's new solid is 4 m; a component's housing pieces are much smaller.
        var size = Workspace == Workspace.Component ? 0.25f : 4f;
        var half = (ushort)(size * 0.5f * HullSolid.HalfUnitsPerMetre);
        var material = _catalog.All.Count > 0 ? _catalog.All[Math.Clamp(_newMaterial, 0, _catalog.All.Count - 1)].Id : default;
        var units = HullSolid.PositionUnitsPerMetre;
        HullCommands.AddSolid(session, shape, _newSubtract ? SolidOp.Subtract : SolidOp.Add,
            (int)Math.Round(PlaceAt.X * units), (int)Math.Round(PlaceAt.Y * units), (int)Math.Round(PlaceAt.Z * units),
            half, half, half, SolidRotation.Identity, material);
    }

    private void DrawSolidList(DesignSession session)
    {

        if (ImGui.Begin("Solids"u8))
        {
            var solids = session.Doc.Solids.AsSpan();

            for (var i = 0; i < solids.Length; i++)
            {
                ref readonly var b = ref solids[i];
                var interior = (b.Flags & SolidFlags.Interior) != 0 ? " interior" : "";

                if (ImGui.Selectable($"{i,3}  #{b.Id}  {OpName(b.Op)} {ShapeName(b.Shape)}{interior}##{b.Id}", session.Selection.Contains(b.Id), ImGuiSelectableFlags.None, default))
                {
                    if (ImGui.GetIO().KeyCtrl)
                        session.Selection.Toggle(b.Id);
                    else
                        session.Selection.Set(b.Id);
                }
            }
        }

        ImGui.End();
    }

    /// <summary>A box room's walls: plain, or one of the plan's wall profiles (made in the Profiles tab).</summary>
    private void DrawWalls(DesignSession session, uint id)
    {
        var interior = session.Doc.Interior;
        var current = interior?.WallOf(id) ?? -1;
        var name = current >= 0 && current < interior!.Profiles.Count ? interior.Profiles[current].Name : "Plain";
        DesignerStyle.ItemWidth(120f);

        if (ImGui.BeginCombo("Walls"u8, $"{name}", ImGuiComboFlags.None))
        {
            if (ImGui.Selectable("Plain##walls"u8, current < 0, ImGuiSelectableFlags.None, default) && current >= 0)
                InteriorCommands.SetRoomWall(session, id, -1);

            for (var i = 0; i < (interior?.Profiles.Count ?? 0); i++)
            {
                if (interior!.Profiles[i].Kind != ProfileKind.Wall)
                    continue;

                if (ImGui.Selectable($"{interior.Profiles[i].Name}##wall{i}", i == current, ImGuiSelectableFlags.None, default) && i != current)
                    InteriorCommands.SetRoomWall(session, id, i);
            }

            if (interior is null || !interior.Profiles.Exists(p => p.Kind == ProfileKind.Wall))
                ImGui.TextDisabled("No wall profiles yet: make one in Profiles"u8);

            ImGui.EndCombo();
        }

        if (current >= 0)
        {
            ImGui.SameLine();

            if (ImGui.Button("Edit##wallprofile"u8))
                EditProfileRequest = current;
        }
    }

    private void DrawProperties(DesignSession session, MeshEditState meshEdit)
    {
        if (ImGui.Begin("Properties"u8))
        {
            var id = session.Selection.Primary;

            if (!session.TryGetSolid(id, out var solid))
            {
                ImGui.Text("No solid selected"u8);
                ImGui.End();
                return;
            }

            if (id != _draftId || session.Version != _draftVersion)
                LoadDraft(solid, id, session.Version);

            ImGui.Text($"Solid #{solid.Id}  index {session.Doc.Solids.IndexOf(id)}");

            var isMesh = solid.Shape == SolidShape.Mesh;

            if (isMesh)
            {
                ImGui.Text($"Mesh solid (table entry {solid.Param})");
            }
            else
            {
                var shape = Math.Min((int)solid.Shape, ShapeNames.Length - 1);
                DesignerStyle.ItemWidth(120f);

                if (Combo("Shape"u8, ref shape, ShapeNames))
                {
                    solid.Shape = (SolidShape)shape;
                    HullCommands.SetSolid(session, id, solid);
                }

            }

            if (ImGui.Button(meshEdit.Enabled ? "Stop editing vertices (Tab)"u8 : "Edit vertices (Tab)"u8))
                meshEdit.Toggle(session);

            var op = Math.Min((int)solid.Op, OpNames.Length - 1);
            DesignerStyle.ItemWidth(120f);

            if (Combo("Op"u8, ref op, OpNames))
            {
                solid.Op = (SolidOp)op;
                HullCommands.SetSolid(session, id, solid, snap: false);
            }

            if (session.Doc.Layers is not null)
            {
                var role = (int)SolidRoles.Of(solid);
                DesignerStyle.ItemWidth(120f);

                if (Combo("Role"u8, ref role, RoleNames))
                    Designer.LayerCommands.SetRole(session, id, (SolidRole)role);
            }

            if (WallGeometry.IsRoom(solid, session.Doc.Meshes))
                DrawWalls(session, id);

            var material = MaterialSlot(session.Doc, solid.Material);

            if (MaterialCombo("Material"u8, ref material))
                HullCommands.SetMaterial(session, id, _catalog.All[material].Id);

            var commit = false;
            ImGui.SeparatorText("Position (m)"u8);
            commit |= Field("X##pos"u8, ref _draft.X);
            commit |= Field("Y##pos"u8, ref _draft.Y);
            commit |= Field("Z##pos"u8, ref _draft.Z);
            if (!isMesh)
            {
                ImGui.SeparatorText(solid.Shape == SolidShape.Cylinder ? "Diameter, height (m)"u8 : "Size (m)"u8);
                commit |= Field("X##size"u8, ref _draft.SizeX);
                commit |= Field("Y##size"u8, ref _draft.SizeY);

                if (solid.Shape != SolidShape.Cylinder)
                    commit |= Field("Z##size"u8, ref _draft.SizeZ);
            }

            ImGui.SeparatorText("Rotation (15 deg steps)"u8);
            commit |= StepField("Yaw"u8, ref _draft.Yaw);
            commit |= StepField("Pitch"u8, ref _draft.Pitch);
            commit |= StepField("Roll"u8, ref _draft.Roll);

            if (ImGui.Checkbox("Interior"u8, ref _draft.Interior))
                commit = true;

            if (commit)
                HullCommands.SetSolid(session, id, FromDraft(solid));

            ImGui.Separator();

            if (ImGui.Button("Earlier"u8))
                HullCommands.Move(session, id, -1);

            ImGui.SameLine();

            if (ImGui.Button("Later"u8))
                HullCommands.Move(session, id, 1);

            DesignerStyle.ItemWidth(80f);
            ImGui.InputFloat("Wall m"u8, ref _wallMetres, 0.25f, 0.5f, "%.2f"u8, ImGuiInputTextFlags.None);
            _wallMetres = MathF.Max(_wallMetres, (float)HullCommands.MinWallMetres);
            ImGui.SameLine();

            if (ImGui.Button("Hollow"u8) && HullCommands.Hollow(session, id, _wallMetres) == 0)
                ImGui.SetTooltip("Hollow needs an Add box or cylinder thicker than two walls"u8);
        }

        ImGui.End();
    }

    /// <summary>The bar beside the selected solid in the view: duplicate, add/subtract, delete.</summary>
    public void DrawContext(DesignSession session, MeshEditState meshEdit, OrbitCamera camera, System.Numerics.Vector2 viewport)
    {
        if (Locked || meshEdit.Enabled || !session.TryGetSolid(session.Selection.Primary, out var solid))
            return;

        if (!ContextBar.Begin("solid", camera, new Vector3d(solid.PositionX, solid.PositionY, solid.PositionZ), viewport))
            return;

        if (ContextBar.Button("Duplicate", "Copy the selected solids (Ctrl+D)"))
            HullCommands.Duplicate(session, solid.Id);

        if (ContextBar.Button(solid.Op == SolidOp.Subtract ? "Make add" : "Make subtract", "Switch the solid between adding and cutting"))
            HullCommands.ToggleOp(session, solid.Id);

        if (ContextBar.Button("Delete", "Remove the selected solids (Del)"))
            HullCommands.DeleteSelected(session);

        ContextBar.End();
    }

    private void DrawStats(DesignSession session, DesignStats stats)
    {
        if (ImGui.Begin("Stats"u8))
        {
            var mass = stats.Mass;
            var com = mass.CentreOfMass;
            var inertia = mass.Inertia;
            ImGui.Text($"mass {mass.MassKg / 1000.0:F3} t  volume {mass.VolumeM3:F3} m3");
            ImGui.Text($"CoM {com.X:F3}, {com.Y:F3}, {com.Z:F3} m");
            ImGui.Text($"inertia {inertia.Xx:E2}, {inertia.Yy:E2}, {inertia.Zz:E2} kg m2");
            ImGui.Separator();
            ImGui.Text($"grid (mass, collision): {stats.Bricks} bricks, {stats.Triangles} triangles, max solids/brick {stats.MaxSolidsPerBrick}");
            ImGui.Text($"bake {stats.BakeMs:F2} ms wall  {stats.BakeCpuMs:F2} ms cpu  rebaked {stats.Rebaked} reused {stats.Reused}{(stats.Baking ? "  (baking)" : "")}");
            ImGui.Text($"mesh prepare {stats.PrepareMs:F2} ms (subdivide + BVH, cached by mesh hash)");

            ImGui.Text($"surface: {stats.SurfacePieces} exact pieces, {stats.SurfaceTriangles} triangles, {stats.SurfaceMs:F2} ms");

            if (stats.ComponentsKg > 0)
                ImGui.Text($"components: {stats.ComponentsKg:F1} kg of parts and pipes");

            if (stats.Generated > 0 || stats.Unstuck > 0)
                ImGui.Text($"brushes and window panes: {stats.Generated} pieces, {stats.FlairKg:F0} kg, {stats.Unstuck} points off the hull");

            ImGui.Text($"hull draw {stats.HullGpuMs:F3} ms gpu");

            if (stats.Projectors > 0)
                ImGui.Text($"paint and decals: {stats.Projectors} boxes, {stats.ProjectorRefs} brick refs");

            ImGui.Checkbox("Show grid mesh (debug)"u8, ref stats.ShowGrid);
            ImGui.Text($"plan hash {stats.Hash}  undo {session.Undo.UndoCount} redo {session.Undo.RedoCount}");
            ImGui.SeparatorText($"Validation: {stats.Validation.Errors} errors, {stats.Validation.Warnings} warnings");

            foreach (var issue in stats.Validation.Issues)
                ImGui.Text($"{issue.Severity} {issue.Code} {issue.Element.Kind} #{issue.Element.Id} ({issue.Value})");
        }

        ImGui.End();
    }

    private void LoadDraft(in HullSolid solid, uint id, int version)
    {
        var (yaw, pitch, roll) = SolidRotation.ToSteps(solid.Rotation);
        _draft = new Draft
        {
            X = solid.PositionX,
            Y = solid.PositionY,
            Z = solid.PositionZ,
            SizeX = solid.HalfX * 2.0 * HullSolid.MetresPerHalfUnit,
            SizeY = solid.HalfY * 2.0 * HullSolid.MetresPerHalfUnit,
            SizeZ = solid.HalfZ * 2.0 * HullSolid.MetresPerHalfUnit,
            Yaw = yaw * SolidRotation.DegreesPerStep,
            Pitch = pitch * SolidRotation.DegreesPerStep,
            Roll = roll * SolidRotation.DegreesPerStep,
            Interior = (solid.Flags & SolidFlags.Interior) != 0,
        };
        _draftId = id;
        _draftVersion = version;
    }

    private HullSolid FromDraft(HullSolid solid)
    {
        const int units = HullSolid.PositionUnitsPerMetre;
        const double halfUnits = HullSolid.HalfUnitsPerMetre * 0.5;
        solid.PosX = (int)Math.Round(_draft.X * units);
        solid.PosY = (int)Math.Round(_draft.Y * units);
        solid.PosZ = (int)Math.Round(_draft.Z * units);
        if (solid.Shape != SolidShape.Mesh)
        {
            solid.HalfX = Half(_draft.SizeX * halfUnits);
            solid.HalfY = Half(_draft.SizeY * halfUnits);
            solid.HalfZ = solid.Shape == SolidShape.Cylinder ? solid.HalfX : Half(_draft.SizeZ * halfUnits);
        }

        solid.Rotation = SolidRotation.FromSteps(_draft.Yaw / SolidRotation.DegreesPerStep, _draft.Pitch / SolidRotation.DegreesPerStep, _draft.Roll / SolidRotation.DegreesPerStep);
        solid.Flags = _draft.Interior ? solid.Flags | SolidFlags.Interior : solid.Flags & ~SolidFlags.Interior;
        return solid;
    }

    private static ushort Half(double units) => (ushort)Math.Clamp(Math.Round(units), PlanLimits.MinHalfUnits, ushort.MaxValue);

    private static bool Field(ReadOnlySpan<byte> label, ref double value)
    {
        DesignerStyle.ItemWidth(140f);
        var changed = ImGui.InputDouble(label, ref value, 0.25, 1.0, "%.3f"u8, ImGuiInputTextFlags.None);
        return ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive());
    }

    /// <summary>Degrees in 15 degree steps; typing commits on leaving the field, the +/- buttons at once.</summary>
    private static bool StepField(ReadOnlySpan<byte> label, ref int degrees)
    {
        DesignerStyle.ItemWidth(140f);
        var changed = ImGui.InputInt(label, ref degrees, SolidRotation.DegreesPerStep, 90, ImGuiInputTextFlags.None);
        var edited = ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive());
        degrees = (int)Math.Round(degrees / (double)SolidRotation.DegreesPerStep) * SolidRotation.DegreesPerStep;
        return edited;
    }

    private static bool Combo(ReadOnlySpan<byte> label, ref int index, string[] names)
    {
        var changed = false;

        if (ImGui.BeginCombo(label, $"{names[index]}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < names.Length; i++)
            {
                if (ImGui.Selectable($"{names[i]}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
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

    /// <summary>Catalog index of the plan's material at <paramref name="planIndex"/>, 0 if unknown.</summary>
    private int MaterialSlot(PlanDocument doc, byte planIndex)
    {
        if (planIndex >= doc.Materials.Count)
            return 0;

        var id = doc.Materials[planIndex];

        for (var i = 0; i < _catalog.All.Count; i++)
        {
            if (_catalog.All[i].Id == id)
                return i;
        }

        return 0;
    }

    private static string ShapeName(SolidShape shape) => (int)shape < ShapeNames.Length ? ShapeNames[(int)shape] : shape.ToString();

    private static string OpName(SolidOp op) => (int)op < OpNames.Length ? OpNames[(int)op] : op.ToString();
}
