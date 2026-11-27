using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Buildings;
using BrutalGame.Designer;
using BrutalGame.Designer.Ui;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.BuildingDesigner.App;

/// <summary>
/// The Building panel (file, pad, foundation, ground checks), the ground and pad drawn in the view, the right-click menu
/// and Add dialog (solids and windows, as in the ship designer), and building files.
/// </summary>
public sealed partial class BuildingDesignerApp
{
    private const int GroundLines = 24;
    private const double GroundStepMetres = 2.0;

    private static readonly uint GroundColour = OverlayRenderer.Rgba(120, 130, 140, 70);
    private static readonly uint PadFill = OverlayRenderer.Rgba(255, 158, 51, 28);
    private static readonly uint PadEdge = OverlayRenderer.Rgba(255, 158, 51, 220);
    private static readonly uint FoundationEdge = OverlayRenderer.Rgba(160, 120, 80, 160);

    private static readonly (AddKind Kind, string Label)[] Adds =
    [
        (AddKind.Box, "Box"), (AddKind.Wedge, "Wedge"), (AddKind.Cylinder, "Cylinder"), (AddKind.Window, "Window"),
    ];

    private BuildingInfo _draftInfo;
    private float _padWidth, _padDepth, _foundation;
    private AddKind _adding;
    private float2 _addWindowAt;
    private Vector3d _addAt;
    private Vector3d _addNormal = new(0, 1, 0);
    private bool _addOnSurface;
    private (uint Solid, uint Window) _lastSelection;

    // ---- Building panel ----

    private void DrawBuildingPanel()
    {
        if (ImGui.Begin("Building"u8))
        {
            var name = _session.Path.Length > 0 ? Path.GetFileName(_session.Path) : "not saved yet";
            ImGui.Text($"Building: {name}{(_session.Modified ? " *" : "")}");

            if (ImGui.Button("New"u8))
                NewBuilding();

            ImGui.SameLine();

            if (ImGui.Button("Open"u8))
                PickAndOpen();

            ImGui.SameLine();

            if (ImGui.Button("Save"u8))
                SaveCurrent();

            ImGui.SameLine();

            if (ImGui.Button("Save as"u8))
                SaveAs();

            DrawPad();
            DrawAddButtons();
            DrawChecks();
        }

        ImGui.End();
    }

    /// <summary>Pad and foundation fields: edits go to a draft and commit as one undo group when the field is left.</summary>
    private void DrawPad()
    {
        var info = _session.Info;

        if (info != _draftInfo)
        {
            _draftInfo = info;
            (_padWidth, _padDepth, _foundation) = ((float)info.PadWidthMetres, (float)info.PadDepthMetres, (float)info.FoundationMetres);
        }

        ImGui.SeparatorText("Pad"u8);
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Width m (X)"u8, ref _padWidth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        var done = ImGui.IsItemDeactivatedAfterEdit();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Depth m (Z)"u8, ref _padDepth, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        done |= ImGui.IsItemDeactivatedAfterEdit();
        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Foundation m"u8, ref _foundation, 0f, 0f, "%.2f"u8, ImGuiInputTextFlags.None);
        done |= ImGui.IsItemDeactivatedAfterEdit();
        ImGui.TextDisabled("y = 0 is finished ground; the foundation goes below it."u8);

        if (done)
            _session.SetInfo(BuildingInfo.FromMetres(_padWidth, _padDepth, _foundation));
    }

    private void DrawAddButtons()
    {
        ImGui.SeparatorText("Add at the camera target"u8);

        foreach (var (kind, label) in Adds)
        {
            if (kind != Adds[0].Kind)
                ImGui.SameLine();

            if (ImGui.Button(label))
                OpenAdd(kind, new float2(ViewportSize.X * 0.5f, ViewportSize.Y * 0.3f));
        }

        ImGui.TextDisabled("Right-click the building to add on it."u8);
    }

    private void DrawChecks()
    {
        ImGui.SeparatorText("Ground"u8);

        if (_outsidePad > 0)
            ImGui.TextColored(DesignerStyle.Accent, $"{_outsidePad} solid(s) reach past the pad");

        if (_belowFoundation > 0)
            ImGui.TextColored(DesignerStyle.Accent, $"{_belowFoundation} solid(s) go below the foundation");

        if (_outsidePad == 0 && _belowFoundation == 0)
            ImGui.TextDisabled("Every solid sits on the pad."u8);

        ImGui.TextDisabled($"Building hash {_buildingHash:x16}");
    }

    // ---- ground ----

    /// <summary>A ground grid round the origin, the pad (filled, outlined) and the foundation's bottom outline.</summary>
    private void DrawGround()
    {
        var reach = GroundLines * GroundStepMetres;

        for (var i = -GroundLines; i <= GroundLines; i++)
        {
            var at = i * GroundStepMetres;
            Overlay.Line(new double3(at, 0, -reach), new double3(at, 0, reach), GroundColour);
            Overlay.Line(new double3(-reach, 0, at), new double3(reach, 0, at), GroundColour);
        }

        var info = _session.Info;
        var hx = info.PadWidthMetres * 0.5;
        var hz = info.PadDepthMetres * 0.5;
        var a = new double3(-hx, 0.002, -hz);
        var b = new double3(hx, 0.002, -hz);
        var c = new double3(hx, 0.002, hz);
        var d = new double3(-hx, 0.002, hz);
        Overlay.Triangle(a, b, c, PadFill);
        Overlay.Triangle(a, c, d, PadFill);
        Rectangle(a, b, c, d, PadEdge);

        var down = new double3(0, -info.FoundationMetres - 0.002, 0);

        if (info.FoundationDepth > 0)
        {
            Rectangle(a + down, b + down, c + down, d + down, FoundationEdge);
            Overlay.Line(a, a + down, FoundationEdge);
            Overlay.Line(b, b + down, FoundationEdge);
            Overlay.Line(c, c + down, FoundationEdge);
            Overlay.Line(d, d + down, FoundationEdge);
        }
    }

    private void Rectangle(double3 a, double3 b, double3 c, double3 d, uint colour)
    {
        Overlay.Line(a, b, colour);
        Overlay.Line(b, c, colour);
        Overlay.Line(c, d, colour);
        Overlay.Line(d, a, colour);
    }

    // ---- selection, right-click menu, Add dialog ----

    /// <summary>One selection: picking a window clears the solids, picking a solid clears the window.</summary>
    private void KeepOneSelected()
    {
        var now = (Plan.Selection.Primary, _windowsPanel.Selected);

        if (now == _lastSelection)
            return;

        if (now.Selected != 0 && now.Selected != _lastSelection.Window)
            Plan.Selection.Clear();
        else if (now.Primary != 0)
            _windowsPanel.Selected = 0;

        _lastSelection = (Plan.Selection.Primary, _windowsPanel.Selected);
    }

    private void DrawMenus(ImGuiIOPtr io)
    {
        ContextBar.Track(!io.WantCaptureMouse);
        PlaceMenu();

        if (ContextBar.BeginShared())
        {
            if (_addOnSurface)
            {
                _windowsPanel.DrawContext(Plan, _camera, ViewportSize);

                if (_windowsPanel.Selected == 0)
                    _panels.DrawContext(Plan, _meshEdit, _camera, ViewportSize);
            }

            ContextBar.Section("add");
            ImGui.TextDisabled(_addOnSurface ? "Add here"u8 : "Add"u8);

            foreach (var (kind, label) in Adds)
            {
                if (ContextBar.Button($"{label}##add{kind}", _addOnSurface ? "Add it where you right-clicked" : "Add it at the camera target"))
                    _adding = kind;
            }

            ContextBar.EndShared();
        }

        DrawAddDialog();
    }

    /// <summary>On the frame the menu opens: the point on the building right-clicked, or the camera target.</summary>
    private void PlaceMenu()
    {
        if (!ContextBar.OpenedThisFrame)
            return;

        var at = new System.Numerics.Vector2(ContextBar.At.X, ContextBar.At.Y);
        Vector3d eye = _camera.Position;
        Vector3d ray = _camera.Ray(at, ViewportSize);
        var surface = new HullSurface(Plan.Doc.Solids.AsSpan(), Plan.Doc.Meshes);
        _addOnSurface = surface.Raycast(eye, ray, 4000, out var t, out var normal);
        (_addAt, _addNormal) = _addOnSurface
            ? (HullSurface.Add(eye, HullSurface.Scale(ray, t)), normal)
            : (new Vector3d(_camera.Target.X, _camera.Target.Y, _camera.Target.Z), new Vector3d(0, 1, 0));
        _addWindowAt = ContextBar.At;
    }

    private void OpenAdd(AddKind kind, float2 at)
    {
        (_adding, _addOnSurface, _addWindowAt) = (kind, false, at);
        (_addAt, _addNormal) = (new Vector3d(_camera.Target.X, _camera.Target.Y, _camera.Target.Z), new Vector3d(0, 1, 0));
    }

    private void DrawAddDialog()
    {
        if (_adding == AddKind.None)
            return;

        var open = true;
        ImGui.SetNextWindowPos(_addWindowAt, ImGuiCond.Appearing, default);

        if (ImGui.Begin($"Add {_adding.ToString().ToLowerInvariant()}###adddialog", ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse))
        {
            var done = DrawAddOptions();
            ImGui.Separator();
            ImGui.TextDisabled(_addOnSurface ? "Goes where you right-clicked."u8 : "Goes at the camera target."u8);

            if (!done && ImGui.Button("Cancel"u8))
                done = true;

            if (done || ImGui.IsKeyPressed(ImGuiKey.Escape, false))
                open = false;
        }

        ImGui.End();

        if (!open)
            _adding = AddKind.None;
    }

    /// <summary>The item's options and its Add button; true when added (the dialog closes).</summary>
    private bool DrawAddOptions()
    {
        _panels.PlaceAt = new double3(_addAt.X, _addAt.Y, _addAt.Z);

        if (_adding == AddKind.Window)
        {
            _windowsPanel.DrawAddOptions();

            if (_addOnSurface)
            {
                if (!ImGui.Button("Add"u8))
                    return false;

                _windowsPanel.PlaceAt(Plan, _addAt, _addNormal);
                Plan.Selection.Clear();
                return true;
            }

            if (!ImGui.Button("Place: click the building"u8))
                return false;

            _windowsPanel.StartPlacing();
            return true;
        }

        _panels.DrawAddSolidOptions();

        if (!ImGui.Button("Add"u8))
            return false;

        _panels.AddSolid(Plan, _adding switch { AddKind.Wedge => SolidShape.Wedge, AddKind.Cylinder => SolidShape.Cylinder, _ => SolidShape.Box });
        _windowsPanel.Selected = 0;
        return true;
    }

    // ---- files ----

    private void NewBuilding()
    {
        _session.Open(BuildingSession.NewPlan(), BuildingInfo.Default, "");
        ResetTools();
        _stats.Message = "New building";
    }

    private void PickAndOpen()
    {
        if (_files.PickOpen() is { } path)
            Open(path);
        else if (!_files.HasDialogs)
            _stats.Message = "File dialogs are unavailable on this system (nfd.dll missing)";
    }

    private void SaveCurrent()
    {
        if (_session.Path.Length > 0)
            Save(_session.Path);
        else
            SaveAs();
    }

    private void SaveAs()
    {
        if (_files.PickSave(_session.Path) is { } path)
            Save(path);
        else if (!_files.HasDialogs)
            _stats.Message = "File dialogs are unavailable on this system (nfd.dll missing)";
    }

    private void Save(string path)
    {
        try
        {
            var bytes = BuildingFile.Encode(_session.Info, Plan.Doc, out var hash);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllBytes(path, bytes);
            _session.MarkSaved(path);
            _stats.Message = $"Saved {Path.GetFileName(path)} ({hash:x16})";
            Console.WriteLine($"saved {path}: {Plan.Doc.Solids.Count} solids, {bytes.Length} B, building {hash:x16}");
        }
        catch (IOException e)
        {
            _stats.Message = $"Save failed: {e.Message}";
        }
    }

    private void Open(string path)
    {
        var error = "";

        if (!File.Exists(path))
            error = $"No file at {path}";
        else if (new FileInfo(path).Length > BuildingFile.HeaderBytes + BuildingInfo.SizeInBytes + PlanLimits.MaxBundleBytes)
            error = $"{Path.GetFileName(path)} is too large";
        else if (BuildingFile.TryDecode(File.ReadAllBytes(path), out var info, out var plan, out var hash) is var result and not BuildingFileError.None)
            error = $"{Path.GetFileName(path)}: {result}";
        else
        {
            _session.Open(plan, info, path);
            ResetTools();
            FrameBuilding();
            _stats.Message = $"Opened {Path.GetFileName(path)}";
            Console.WriteLine($"opened {path}: {plan.Solids.Count} solids, building {hash:x16}");
            return;
        }

        _stats.Message = error;
        Console.WriteLine($"open failed: {error}");
    }

    private void ResetTools()
    {
        _meshEdit.Enabled = false;
        _windowsPanel.Reset();
        _adding = AddKind.None;
        _seenVersion = -1;
    }
}
