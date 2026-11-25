using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>What the panels ask the host to do this frame (file dialogs and disk live in the host).</summary>
public enum FileRequest { None, New, Open, Save, SaveAs, OpenPath, SavePath }

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
    public int MaxBrushesPerBrick;
    public bool Baking;
    public float PrepareMs;
    public PlanHash Hash;
    public readonly ValidationLog Validation = new();
    public string Message = "";
}

/// <summary>
/// The designer's ImGui panels: file and tools, brush list, properties of the primary brush, stats and validation.
/// Field edits go to a draft and commit as one undo group when the field is left after an edit, never per keystroke.
/// </summary>
public sealed class DesignerPanels
{
    private const float DefaultSizeMetres = 4f;
    private const float RightColumnWidth = 400f;

    private static readonly string[] ShapeNames = ["Box", "Wedge", "Cylinder"];
    private static readonly string[] OpNames = ["Add", "Subtract", "Paint"];
    private static readonly string[] StepNames = ["1/8 m", "1/4 m", "1/2 m", "1 m"];

    private readonly HullMaterialCatalog _catalog;
    private readonly byte[] _pathBuffer = new byte[512];
    private Draft _draft;
    private uint _draftId;
    private int _draftVersion = -1;
    private int _newMaterial;
    private bool _newSubtract;
    private float _wallMetres = 0.5f;
    private float _right;
    private readonly MeshPanel _meshPanel = new();

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
    }

    public FileRequest Request { get; private set; }

    /// <summary>The path field's text (fallback when no file dialog is available).</summary>
    public string PathText
    {
        get
        {
            var length = Array.IndexOf(_pathBuffer, (byte)0);
            return System.Text.Encoding.UTF8.GetString(_pathBuffer, 0, length < 0 ? _pathBuffer.Length : length);
        }
        set
        {
            Array.Clear(_pathBuffer);
            System.Text.Encoding.UTF8.GetBytes(value.AsSpan(0, Math.Min(value.Length, _pathBuffer.Length / 4)), _pathBuffer);
        }
    }

    /// <summary>Where new brushes go: the host sets it to the camera target.</summary>
    public double3 PlaceAt;

    public void Draw(DesignSession session, DesignStats stats, MeshEditState meshEdit)
    {
        Request = FileRequest.None;
        _right = ImGui.GetIO().DisplaySize.X - RightColumnWidth;
        DrawTools(session, stats);
        DrawBrushList(session);
        DrawProperties(session);
        _meshPanel.Draw(session, meshEdit, _right - MeshPanel.Width - 10f);
        DrawStats(session, stats);
    }

    private void DrawTools(DesignSession session, DesignStats stats)
    {
        ImGui.SetNextWindowPos(new float2(10f, 10f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Designer"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var name = session.Path.Length > 0 ? Path.GetFileName(session.Path) : "untitled";
            ImGui.Text($"{name}{(session.Modified ? " *" : "")}  ({session.Doc.Brushes.Count} brushes)");

            if (ImGui.Button("New"u8))
                Request = FileRequest.New;

            ImGui.SameLine();

            if (ImGui.Button("Open..."u8))
                Request = FileRequest.Open;

            ImGui.SameLine();

            if (ImGui.Button("Save"u8))
                Request = FileRequest.Save;

            ImGui.SameLine();

            if (ImGui.Button("Save as..."u8))
                Request = FileRequest.SaveAs;

            ImGui.SetNextItemWidth(300f);
            ImGui.InputText("##path"u8, _pathBuffer, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);
            ImGui.SameLine();

            if (ImGui.Button("Load path"u8))
                Request = FileRequest.OpenPath;

            ImGui.SameLine();

            if (ImGui.Button("Save path"u8))
                Request = FileRequest.SavePath;

            if (stats.Message.Length > 0)
                ImGui.TextWrapped($"{stats.Message}");

            ImGui.SeparatorText("Add brush"u8);
            ImGui.Checkbox("Subtract"u8, ref _newSubtract);
            ImGui.SameLine();
            MaterialCombo("Material##new"u8, ref _newMaterial);

            if (ImGui.Button("Box"u8))
                AddBrush(session, BrushShape.Box);

            ImGui.SameLine();

            if (ImGui.Button("Wedge"u8))
                AddBrush(session, BrushShape.Wedge);

            ImGui.SameLine();

            if (ImGui.Button("Cylinder"u8))
                AddBrush(session, BrushShape.Cylinder);

            ImGui.SeparatorText("Snap"u8);
            ImGui.Checkbox("Grid"u8, ref session.Snap.Enabled);
            ImGui.SameLine();
            var step = Snap.Steps.IndexOf(session.Snap.Step);
            ImGui.SetNextItemWidth(90f);

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
            ImGui.TextDisabled("Del delete, Ctrl+D duplicate, Ctrl+Z/Y undo/redo, F frame"u8);
        }

        ImGui.End();
    }

    private void AddBrush(DesignSession session, BrushShape shape)
    {
        var half = (ushort)(DefaultSizeMetres * 0.5f * HullBrush.HalfUnitsPerMetre);
        var material = _catalog.All.Count > 0 ? _catalog.All[Math.Clamp(_newMaterial, 0, _catalog.All.Count - 1)].Id : default;
        var units = HullBrush.PositionUnitsPerMetre;
        HullCommands.AddBrush(session, shape, _newSubtract ? BrushOp.Subtract : BrushOp.Add,
            (int)Math.Round(PlaceAt.X * units), (int)Math.Round(PlaceAt.Y * units), (int)Math.Round(PlaceAt.Z * units),
            half, half, half, BrushRotation.Identity, material);
    }

    private void DrawBrushList(DesignSession session)
    {
        ImGui.SetNextWindowPos(new float2(10f, 330f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(300f, 360f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Brushes"u8))
        {
            var brushes = session.Doc.Brushes.AsSpan();

            for (var i = 0; i < brushes.Length; i++)
            {
                ref readonly var b = ref brushes[i];
                var interior = (b.Flags & BrushFlags.Interior) != 0 ? " interior" : "";

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

    private void DrawProperties(DesignSession session)
    {
        ImGui.SetNextWindowPos(new float2(_right, 10f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Properties"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var id = session.Selection.Primary;

            if (!session.TryGetBrush(id, out var brush))
            {
                ImGui.Text("No brush selected"u8);
                ImGui.End();
                return;
            }

            if (id != _draftId || session.Version != _draftVersion)
                LoadDraft(brush, id, session.Version);

            ImGui.Text($"Brush #{brush.Id}  index {session.Doc.Brushes.IndexOf(id)}");

            var isMesh = brush.Shape == BrushShape.Mesh;

            if (isMesh)
            {
                ImGui.Text($"Mesh brush (table entry {brush.Param})");
            }
            else
            {
                var shape = Math.Min((int)brush.Shape, ShapeNames.Length - 1);
                ImGui.SetNextItemWidth(120f);

                if (Combo("Shape"u8, ref shape, ShapeNames))
                {
                    brush.Shape = (BrushShape)shape;
                    HullCommands.SetBrush(session, id, brush);
                }

                ImGui.SameLine();

                if (ImGui.Button("To mesh"u8))
                    Mesh.MeshCommands.ConvertToMesh(session, id);
            }

            var op = Math.Min((int)brush.Op, OpNames.Length - 1);
            ImGui.SetNextItemWidth(120f);

            if (Combo("Op"u8, ref op, OpNames))
            {
                brush.Op = (BrushOp)op;
                HullCommands.SetBrush(session, id, brush, snap: false);
            }

            var material = MaterialSlot(session.Doc, brush.Material);

            if (MaterialCombo("Material"u8, ref material))
                HullCommands.SetMaterial(session, id, _catalog.All[material].Id);

            var commit = false;
            ImGui.SeparatorText("Position (m)"u8);
            commit |= Field("X##pos"u8, ref _draft.X);
            commit |= Field("Y##pos"u8, ref _draft.Y);
            commit |= Field("Z##pos"u8, ref _draft.Z);
            if (!isMesh)
            {
                ImGui.SeparatorText(brush.Shape == BrushShape.Cylinder ? "Diameter, height (m)"u8 : "Size (m)"u8);
                commit |= Field("X##size"u8, ref _draft.SizeX);
                commit |= Field("Y##size"u8, ref _draft.SizeY);

                if (brush.Shape != BrushShape.Cylinder)
                    commit |= Field("Z##size"u8, ref _draft.SizeZ);
            }

            ImGui.SeparatorText("Rotation (15 deg steps)"u8);
            commit |= StepField("Yaw"u8, ref _draft.Yaw);
            commit |= StepField("Pitch"u8, ref _draft.Pitch);
            commit |= StepField("Roll"u8, ref _draft.Roll);

            if (ImGui.Checkbox("Interior"u8, ref _draft.Interior))
                commit = true;

            if (commit)
                HullCommands.SetBrush(session, id, FromDraft(brush));

            ImGui.Separator();

            if (ImGui.Button("Add/Subtract"u8))
                HullCommands.ToggleOp(session, id);

            ImGui.SameLine();

            if (ImGui.Button("Duplicate"u8))
                HullCommands.Duplicate(session, id);

            ImGui.SameLine();

            if (ImGui.Button("Delete"u8))
                HullCommands.DeleteSelected(session);

            if (ImGui.Button("Earlier"u8))
                HullCommands.Move(session, id, -1);

            ImGui.SameLine();

            if (ImGui.Button("Later"u8))
                HullCommands.Move(session, id, 1);

            ImGui.SetNextItemWidth(80f);
            ImGui.InputFloat("Wall m"u8, ref _wallMetres, 0.25f, 0.5f, "%.2f"u8, ImGuiInputTextFlags.None);
            _wallMetres = MathF.Max(_wallMetres, (float)HullCommands.MinWallMetres);
            ImGui.SameLine();

            if (ImGui.Button("Hollow"u8) && HullCommands.Hollow(session, id, _wallMetres) == 0)
                ImGui.SetTooltip("Hollow needs an Add box or cylinder thicker than two walls"u8);
        }

        ImGui.End();
    }

    private void DrawStats(DesignSession session, DesignStats stats)
    {
        ImGui.SetNextWindowPos(new float2(_right, 520f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Stats"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var mass = stats.Mass;
            var com = mass.CentreOfMass;
            var inertia = mass.Inertia;
            ImGui.Text($"mass {mass.MassKg / 1000.0:F3} t  volume {mass.VolumeM3:F3} m3");
            ImGui.Text($"CoM {com.X:F3}, {com.Y:F3}, {com.Z:F3} m");
            ImGui.Text($"inertia {inertia.Xx:E2}, {inertia.Yy:E2}, {inertia.Zz:E2} kg m2");
            ImGui.Separator();
            ImGui.Text($"bricks {stats.Bricks}  triangles {stats.Triangles}  max brushes/brick {stats.MaxBrushesPerBrick}");
            ImGui.Text($"bake {stats.BakeMs:F2} ms wall  {stats.BakeCpuMs:F2} ms cpu  rebaked {stats.Rebaked} reused {stats.Reused}{(stats.Baking ? "  (baking)" : "")}");
            ImGui.Text($"mesh prepare {stats.PrepareMs:F2} ms (subdivide + BVH, cached by mesh hash)");
            ImGui.Text($"plan hash {stats.Hash}  undo {session.Undo.UndoCount} redo {session.Undo.RedoCount}");
            ImGui.SeparatorText($"Validation: {stats.Validation.Errors} errors, {stats.Validation.Warnings} warnings");

            foreach (var issue in stats.Validation.Issues)
                ImGui.Text($"{issue.Severity} {issue.Code} {issue.Element.Kind} #{issue.Element.Id} ({issue.Value})");
        }

        ImGui.End();
    }

    private void LoadDraft(in HullBrush brush, uint id, int version)
    {
        var (yaw, pitch, roll) = BrushRotation.ToSteps(brush.Rotation);
        _draft = new Draft
        {
            X = brush.PositionX,
            Y = brush.PositionY,
            Z = brush.PositionZ,
            SizeX = brush.HalfX * 2.0 * HullBrush.MetresPerHalfUnit,
            SizeY = brush.HalfY * 2.0 * HullBrush.MetresPerHalfUnit,
            SizeZ = brush.HalfZ * 2.0 * HullBrush.MetresPerHalfUnit,
            Yaw = yaw * BrushRotation.DegreesPerStep,
            Pitch = pitch * BrushRotation.DegreesPerStep,
            Roll = roll * BrushRotation.DegreesPerStep,
            Interior = (brush.Flags & BrushFlags.Interior) != 0,
        };
        _draftId = id;
        _draftVersion = version;
    }

    private HullBrush FromDraft(HullBrush brush)
    {
        const int units = HullBrush.PositionUnitsPerMetre;
        const double halfUnits = HullBrush.HalfUnitsPerMetre * 0.5;
        brush.PosX = (int)Math.Round(_draft.X * units);
        brush.PosY = (int)Math.Round(_draft.Y * units);
        brush.PosZ = (int)Math.Round(_draft.Z * units);
        if (brush.Shape != BrushShape.Mesh)
        {
            brush.HalfX = Half(_draft.SizeX * halfUnits);
            brush.HalfY = Half(_draft.SizeY * halfUnits);
            brush.HalfZ = brush.Shape == BrushShape.Cylinder ? brush.HalfX : Half(_draft.SizeZ * halfUnits);
        }

        brush.Rotation = BrushRotation.FromSteps(_draft.Yaw / BrushRotation.DegreesPerStep, _draft.Pitch / BrushRotation.DegreesPerStep, _draft.Roll / BrushRotation.DegreesPerStep);
        brush.Flags = _draft.Interior ? brush.Flags | BrushFlags.Interior : brush.Flags & ~BrushFlags.Interior;
        return brush;
    }

    private static ushort Half(double units) => (ushort)Math.Clamp(Math.Round(units), PlanLimits.MinHalfUnits, ushort.MaxValue);

    private static bool Field(ReadOnlySpan<byte> label, ref double value)
    {
        ImGui.SetNextItemWidth(140f);
        var changed = ImGui.InputDouble(label, ref value, 0.25, 1.0, "%.3f"u8, ImGuiInputTextFlags.None);
        return ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive());
    }

    /// <summary>Degrees in 15 degree steps; typing commits on leaving the field, the +/- buttons at once.</summary>
    private static bool StepField(ReadOnlySpan<byte> label, ref int degrees)
    {
        ImGui.SetNextItemWidth(140f);
        var changed = ImGui.InputInt(label, ref degrees, BrushRotation.DegreesPerStep, 90, ImGuiInputTextFlags.None);
        var edited = ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive());
        degrees = (int)Math.Round(degrees / (double)BrushRotation.DegreesPerStep) * BrushRotation.DegreesPerStep;
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
        ImGui.SetNextItemWidth(160f);

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

    private static string ShapeName(BrushShape shape) => (int)shape < ShapeNames.Length ? ShapeNames[(int)shape] : shape.ToString();

    private static string OpName(BrushOp op) => (int)op < OpNames.Length ? OpNames[(int)op] : op.ToString();
}
