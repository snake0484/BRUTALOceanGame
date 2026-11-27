using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Buildings;
using BrutalGame.Designer.Ui;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.App;

/// <summary>
/// Building tabs: a ship plan standing on the ground (y = 0 is finished ground), so every ship tool works on it, plus a
/// pad and foundation (<see cref="BuildingInfo"/>) kept on the plan's own history. Here: the Building panel (pad,
/// foundation, ground checks, building hash), the ground and pad drawn in the view, and <c>.bbld</c> files.
/// </summary>
public sealed partial class DesignerApp
{
    private const int GroundLines = 24;
    private const double GroundStepMetres = 2.0;

    private static readonly uint GroundColour = OverlayRenderer.Rgba(120, 130, 140, 70);
    private static readonly uint PadFill = OverlayRenderer.Rgba(255, 158, 51, 28);
    private static readonly uint PadEdge = OverlayRenderer.Rgba(255, 158, 51, 220);
    private static readonly uint FoundationEdge = OverlayRenderer.Rgba(160, 120, 80, 160);

    private readonly FileDialogs _buildingFiles = new("BRUTAL building", BuildingFile.Extension,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Buildings"));

    private int _buildingCount;
    private Func<bool>? _undoBuilding, _redoBuilding;
    private BuildingInfo _draftInfo;
    private float _padWidth, _padDepth, _foundation;
    private int _outsidePad, _belowFoundation;
    private ulong _buildingHash;

    /// <summary>--demo-building: a new building tab with the sample hut (walls, slab, roof, a door and a window cut).</summary>
    public static bool BuildingDemo;

    /// <summary>The Designer panel's Undo and Redo on a building tab: the pad follows the plan's history.</summary>
    private Func<bool> BuildingUndo => _undoBuilding ??= () => _active.Building?.Undo() ?? false;

    private Func<bool> BuildingRedo => _redoBuilding ??= () => _active.Building?.Redo() ?? false;

    /// <summary>On a commit in a building tab: the info at this history position, its hash and the ground checks.</summary>
    private void CommitBuilding()
    {
        if (_active.Building is not { } building)
            return;

        building.Sync();
        _buildingHash = BuildingFile.Hash(building.Info, _stats.Hash);
        _outsidePad = BuildingCheck.OutsidePad(_session.Doc, building.Info);
        _belowFoundation = BuildingCheck.BelowFoundation(_session.Doc, building.Info);
    }

    // ---- Building panel ----

    private void DrawBuildingPanel()
    {
        if (_active.Building is not { } building)
            return;

        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Building"u8))
        {
            ImGui.BeginDisabled(InteriorView);
            DrawPad(building);
            ImGui.EndDisabled();
            DrawChecks();
        }

        ImGui.End();
    }

    /// <summary>Pad and foundation fields: edits go to a draft and commit as one undo group when the field is left.</summary>
    private void DrawPad(BuildingSession building)
    {
        var info = building.Info;

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

        if (done && !building.SetInfo(BuildingInfo.FromMetres(_padWidth, _padDepth, _foundation)))
            _draftInfo = default;
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

    /// <summary>On a building tab: a ground grid round the origin, the pad (filled, outlined) and the foundation's bottom outline.</summary>
    private void DrawGround()
    {
        if (_active.Building is not { } building)
            return;

        var reach = GroundLines * GroundStepMetres;

        for (var i = -GroundLines; i <= GroundLines; i++)
        {
            var at = i * GroundStepMetres;
            _overlay.Line(new double3(at, 0, -reach), new double3(at, 0, reach), GroundColour);
            _overlay.Line(new double3(-reach, 0, at), new double3(reach, 0, at), GroundColour);
        }

        var info = building.Info;
        var hx = info.PadWidthMetres * 0.5;
        var hz = info.PadDepthMetres * 0.5;
        var a = new double3(-hx, 0.002, -hz);
        var b = new double3(hx, 0.002, -hz);
        var c = new double3(hx, 0.002, hz);
        var d = new double3(-hx, 0.002, hz);
        _overlay.Triangle(a, b, c, PadFill);
        _overlay.Triangle(a, c, d, PadFill);
        Rectangle(a, b, c, d, PadEdge);

        if (info.FoundationDepth <= 0)
            return;

        var down = new double3(0, -info.FoundationMetres - 0.002, 0);
        Rectangle(a + down, b + down, c + down, d + down, FoundationEdge);
        _overlay.Line(a, a + down, FoundationEdge);
        _overlay.Line(b, b + down, FoundationEdge);
        _overlay.Line(c, c + down, FoundationEdge);
        _overlay.Line(d, d + down, FoundationEdge);
    }

    private void Rectangle(double3 a, double3 b, double3 c, double3 d, uint colour)
    {
        _overlay.Line(a, b, colour);
        _overlay.Line(b, c, colour);
        _overlay.Line(c, d, colour);
        _overlay.Line(d, a, colour);
    }

    /// <summary>Framing a building tab takes in its pad, its foundation and a storey above the ground.</summary>
    private void PadBounds(ref double x0, ref double y0, ref double z0, ref double x1, ref double y1, ref double z1)
    {
        if (_active.Building is not { } building)
            return;

        var info = building.Info;
        var hx = info.PadWidthMetres * 0.5;
        var hz = info.PadDepthMetres * 0.5;
        (x0, y0, z0) = (Math.Min(x0, -hx), Math.Min(y0, -info.FoundationMetres), Math.Min(z0, -hz));
        (x1, y1, z1) = (Math.Max(x1, hx), Math.Max(y1, 3.0), Math.Max(z1, hz));
    }

    // ---- tabs and files ----

    /// <summary>A building tab: a new empty one, or a loaded plan with its info. An untouched empty tab gives its place up.</summary>
    private Document NewBuilding(PlanDocument? plan = null, BuildingInfo? info = null, string path = "")
    {
        var document = NewDocument(component: false, plan ?? BuildingSession.NewPlan(), path, $"Building {++_buildingCount}");
        document.Building = new BuildingSession(document.Session, info ?? BuildingInfo.Default);
        // Again, now that it is a building: its panels, undo hooks and a frame that takes in the pad.
        document.Framed = false;
        Activate(document);
        return document;
    }

    private void DemoBuilding()
    {
        var all = _catalog.All;
        NewBuilding();
        SampleBuildings.Hut(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);
        _session.MarkSaved("");
        FrameHull();
    }

    private void PickAndOpenBuilding()
    {
        if (_buildingFiles.PickOpen() is { } path)
            OpenBuilding(path);
        else if (!_buildingFiles.HasDialogs)
            _stats.Message = "File dialogs are unavailable on this system (nfd.dll missing)";
    }

    private void SaveBuilding(BuildingSession building, string path)
    {
        try
        {
            var bytes = BuildingFile.Encode(building.Info, _session.Doc, out var hash);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));

            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllBytes(path, bytes);
            building.MarkSaved(path);
            _stats.Message = $"Saved {Path.GetFileName(path)} ({hash:x16})";
            Console.WriteLine($"saved {path}: {_session.Doc.Solids.Count} solids, {bytes.Length} B, building {hash:x16}");
        }
        catch (IOException e)
        {
            _stats.Message = $"Save failed: {e.Message}";
        }
    }

    private void OpenBuilding(string path)
    {
        string error;

        if (!File.Exists(path))
            error = $"No file at {path}";
        else if (new FileInfo(path).Length > BuildingFile.HeaderBytes + BuildingInfo.SizeInBytes + PlanLimits.MaxBundleBytes)
            error = $"{Path.GetFileName(path)} is too large";
        else if (BuildingFile.TryDecode(File.ReadAllBytes(path), out var info, out var plan, out var hash) is var result and not BuildingFileError.None)
            error = $"{Path.GetFileName(path)}: {result}";
        else
        {
            var full = Path.GetFullPath(path);

            if (_docs.Find(d => d.Session.Path.Length > 0 && string.Equals(Path.GetFullPath(d.Session.Path), full, StringComparison.OrdinalIgnoreCase)) is { } already)
            {
                Activate(already);
                _stats.Message = $"{Path.GetFileName(path)} is already open";
                return;
            }

            NewBuilding(plan, info, path);
            _stats.Message = $"Opened {Path.GetFileName(path)}";
            Console.WriteLine($"opened {path}: {plan.Solids.Count} solids, building {hash:x16}");
            return;
        }

        _stats.Message = error;
        Console.WriteLine($"open failed: {error}");
    }
}
