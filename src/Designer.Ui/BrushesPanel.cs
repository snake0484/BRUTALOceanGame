using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Brushes panel: the plan's brushes (stamps made from a solid, sweeps of the plan's brush profiles, each with its line
/// point and size) and the strokes drawn with them: small raised detail drawn from its own geometry, counted for mass.
/// Draw a stroke, pick it to edit its brush, material, offset and size; drag its points in the view, and size each one.
/// </summary>
public sealed class BrushesPanel
{
    private const double Units = HullSolid.PositionUnitsPerMetre;

    private static readonly string[] KindNames = ["Stamp", "Sweep"];

    private readonly HullMaterialCatalog _catalog;
    private int _brush = -1;
    private int _preset;
    private float _width = 0.5f;
    private float _height = 0.25f;
    private float _spacing = 1f;
    private float _offset;
    private float _scale = 1f;
    private uint _editing;
    private string _message = "";
    private int _newProfile = -1;
    private float _brushScale = 1f;
    private float _lineX;
    private float _lineY;
    private (int, int) _scaleFor = (-1, -1);
    private float _pointScale = 1f;
    private (uint, int, int) _pointFor;
    private readonly ProfileEditor _lineEditor = new();

    /// <summary>Set for one frame when a sweep's "Edit" asks the Profiles tab to show its profile.</summary>
    public int EditProfileRequest { get; private set; } = -1;

    public BrushesPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public void Draw(DesignSession session, StrokeTool tool)
    {
        EditProfileRequest = -1;

        if (tool.FinishRequested)
            Finish(session, tool);

        // Docks with Properties the first time (layouts saved before brushes existed have no place for it).
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Brushes"u8))
        {
            var set = session.Doc.Brushes;
            DrawBrushes(session, set, tool);
            DrawStrokes(session, session.Doc.Brushes, tool);

            var message = tool.Message.Length > 0 ? tool.Message : _message;

            if (message.Length > 0)
                ImGui.TextWrapped($"{message}");
        }

        ImGui.End();
    }

    private void DrawBrushes(DesignSession session, HullBrushSet? set, StrokeTool tool)
    {
        ImGui.SeparatorText("Brushes"u8);
        var count = set?.Brushes.Count ?? 0;
        var profiles = session.Doc.Interior?.Profiles;
        _brush = Math.Clamp(_brush, -1, count - 1);

        if (count == 0)
            ImGui.TextDisabled("No brushes yet. Make a stamp from a solid, or a sweep of a brush profile."u8);

        for (var i = 0; i < count; i++)
        {
            var b = set!.Brushes[i];
            var label = b.Kind == BrushKind.Stamp ? $"{i + 1}  Stamp, every {b.Spacing / Units:0.##} m" : $"{i + 1}  Sweep: {ProfileName(profiles, b.Outline)}";

            if (ImGui.Selectable($"{label}##brush{i}", i == _brush, ImGuiSelectableFlags.None, default))
            {
                _brush = i;
                tool.Brush = i;
                _spacing = (float)(b.Spacing / Units);
            }
        }

        ImGui.BeginDisabled(session.Selection.Primary == 0);

        if (ImGui.Button("Stamp from selected solid"u8))
        {
            var index = BrushCommands.AddStampFromSolid(session, session.Selection.Primary);
            _message = index < 0 ? BrushCommands.LastError : "";
            Pick(index, tool);
        }

        ImGui.EndDisabled();

        // A sweep of a brush profile the plan has, or of a new one from a preset.
        if (BrushProfiles(profiles).Count > 0)
        {
            DesignerStyle.ItemWidth(160f);
            ProfileCombo("##newsweep"u8, profiles!, ref _newProfile);
            ImGui.SameLine();

            if (ImGui.Button("New sweep"u8))
                Made(BrushCommands.AddSweep(session, _newProfile), tool);
        }

        DesignerStyle.ItemWidth(120f);
        Combo("##profile"u8, ref _preset, BrushCommands.PresetNames);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(70f);
        ImGui.InputFloat("w##profile"u8, ref _width, 0f, 0f, "%.2f m"u8, ImGuiInputTextFlags.None);
        ImGui.SameLine();
        DesignerStyle.ItemWidth(70f);
        ImGui.InputFloat("d##profile"u8, ref _height, 0f, 0f, "%.2f m"u8, ImGuiInputTextFlags.None);
        _width = Math.Clamp(_width, 0.05f, 8f);
        _height = Math.Clamp(_height, 0.05f, 8f);

        if (ImGui.Button("New sweep from preset"u8))
            Made(BrushCommands.AddSweep(session, (ProfilePreset)_preset, _width, _height), tool);

        if (_brush < 0 || set is null)
            return;

        var brush = set.Brushes[_brush];
        ImGui.SeparatorText($"Brush {_brush + 1}");

        if (_scaleFor != (_brush, session.Version))
        {
            _brushScale = brush.Scale / (float)HullBrush.UnitScale;
            (_lineX, _lineY) = ((float)(brush.Anchor.X / Units), (float)(brush.Anchor.Y / Units));
            _scaleFor = (_brush, session.Version);
        }

        if (brush.Kind == BrushKind.Sweep && profiles is not null)
            DrawSweep(session, brush, profiles);

        if (brush.Kind == BrushKind.Stamp)
        {
            DesignerStyle.ItemWidth(90f);
            ImGui.InputFloat("Spacing"u8, ref _spacing, 0f, 0f, "%.3f m"u8, ImGuiInputTextFlags.None);

            if (ImGui.IsItemDeactivatedAfterEdit())
                BrushCommands.SetSpacing(session, _brush, Math.Max(_spacing, 0.0625f));
        }

        DesignerStyle.ItemWidth(140f);
        ImGui.SliderFloat("Size##brush"u8, ref _brushScale, 0.05f, 4f, "%.2f x"u8, ImGuiSliderFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
            BrushCommands.SetScale(session, _brush, _brushScale);

        if (ImGui.Button("Delete brush"u8))
        {
            _message = BrushCommands.RemoveBrush(session, _brush) ? "" : BrushCommands.LastError;

            if (_message.Length == 0)
                Pick(-1, tool);
        }
    }

    /// <summary>A sweep brush: its profile, and where in it the stroke runs (drag the cross, or type it).</summary>
    private void DrawSweep(DesignSession session, HullBrush brush, List<HullProfile> profiles)
    {
        var outline = (int)brush.Outline;
        DesignerStyle.ItemWidth(160f);

        if (ProfileCombo("Profile##sweep"u8, profiles, ref outline) && outline != brush.Outline)
            BrushCommands.SetOutline(session, _brush, outline);

        ImGui.SameLine();

        if (ImGui.Button("Edit##brushprofile"u8))
            EditProfileRequest = brush.Outline;

        if (brush.Outline >= profiles.Count)
            return;

        ImGui.TextDisabled("Line point: where the stroke runs through the outline."u8);

        if (_lineEditor.DrawLinePoint(profiles[brush.Outline].Points, brush.Anchor) is { } at)
            BrushCommands.SetAnchor(session, _brush, at);

        DesignerStyle.ItemWidth(80f);
        ImGui.InputFloat("X##line"u8, ref _lineX, 0f, 0f, "%.3f m"u8, ImGuiInputTextFlags.None);
        var done = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        DesignerStyle.ItemWidth(80f);
        ImGui.InputFloat("Y##line"u8, ref _lineY, 0f, 0f, "%.3f m"u8, ImGuiInputTextFlags.None);
        done |= ImGui.IsItemDeactivatedAfterEdit();

        if (done)
            BrushCommands.SetAnchor(session, _brush, new ProfilePoint((int)Math.Round(_lineX * Units), (int)Math.Round(_lineY * Units)));
    }

    private void Made(int index, StrokeTool tool)
    {
        _message = index < 0 ? BrushCommands.LastError : "";

        if (index >= 0)
            Pick(index, tool);
    }

    private static List<int> BrushProfiles(List<HullProfile>? profiles)
    {
        var result = new List<int>();

        for (var i = 0; i < (profiles?.Count ?? 0); i++)
        {
            if (profiles![i].Kind == ProfileKind.Brush)
                result.Add(i);
        }

        return result;
    }

    private static string ProfileName(List<HullProfile>? profiles, int index) =>
        profiles is not null && index < profiles.Count ? profiles[index].Name : "?";

    private static bool ProfileCombo(ReadOnlySpan<byte> label, List<HullProfile> profiles, ref int index)
    {
        var brushes = BrushProfiles(profiles);

        if (!brushes.Contains(index))
            index = brushes.Count > 0 ? brushes[0] : -1;

        var changed = false;

        if (ImGui.BeginCombo(label, $"{ProfileName(profiles, index)}", ImGuiComboFlags.None))
        {
            foreach (var i in brushes)
            {
                if (ImGui.Selectable($"{profiles[i].Name}##bp{i}", i == index, ImGuiSelectableFlags.None, default) && i != index)
                {
                    index = i;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    private void DrawStrokes(DesignSession session, HullBrushSet? set, StrokeTool tool)
    {
        ImGui.SeparatorText("Strokes"u8);
        var canDraw = tool.Brush >= 0 && set is not null && tool.Brush < set.Brushes.Count;
        ImGui.BeginDisabled(!canDraw && !tool.Drawing);

        if (!tool.Drawing)
        {
            if (ImGui.Button("Draw stroke"u8))
            {
                tool.Drawing = true;
                tool.ClearPending();
                tool.Selected = 0;
            }
        }
        else
        {
            if (ImGui.Button("Finish (Enter)"u8))
                Finish(session, tool);

            ImGui.SameLine();

            if (ImGui.Button("Cancel (Esc)"u8))
                tool.Cancel();

            ImGui.SameLine();
            ImGui.Text($"{tool.PendingPoints} points");
        }

        ImGui.EndDisabled();

        if (!canDraw && !tool.Drawing)
            ImGui.TextDisabled("Pick a brush to draw with."u8);

        MaterialCombo("Material##stroke"u8, ref tool.Material);

        if (tool.Drawing)
            ImGui.TextDisabled("Click on the hull to add points. Double click or Enter finishes."u8);

        if (set is null)
            return;

        for (var i = 0; i < set.Strokes.Count; i++)
        {
            var s = set.Strokes[i];
            var name = $"#{s.Id}  brush {s.Brush + 1}, {s.Points.Count} points";

            if (ImGui.Selectable($"{name}##stroke{s.Id}", s.Id == tool.Selected, ImGuiSelectableFlags.None, default))
            {
                tool.Selected = s.Id;
                tool.Drawing = false;
            }
        }

        var index = set.IndexOfStroke(tool.Selected);

        if (index < 0)
            return;

        var stroke = set.Strokes[index];

        if (_editing != stroke.Id)
        {
            _editing = stroke.Id;
            _offset = (float)(stroke.Offset / Units);
            _scale = stroke.Scale / (float)HullStroke.UnitScale;
        }

        ImGui.SeparatorText("Selected stroke"u8);
        var brushIndex = (int)stroke.Brush;
        var names = new string[set.Brushes.Count];

        for (var i = 0; i < names.Length; i++)
            names[i] = $"{i + 1}  {KindNames[(int)set.Brushes[i].Kind]}";

        DesignerStyle.ItemWidth(130f);

        if (Combo("Brush"u8, ref brushIndex, names))
            BrushCommands.SetStroke(session, stroke.Id, s => s.Brush = (ushort)brushIndex);

        var material = CatalogIndex(session.Doc, stroke.Material);

        if (MaterialCombo("Material##sel"u8, ref material))
        {
            var id = _catalog.All[material].Id;
            session.Edit(s =>
            {
                var m = s.EnsureMaterial(id);
                BrushCommands.SetStroke(s, stroke.Id, st => st.Material = m);
            });
        }

        DesignerStyle.ItemWidth(90f);
        ImGui.InputFloat("Offset"u8, ref _offset, 0f, 0f, "%.3f m"u8, ImGuiInputTextFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var units = (int)Math.Round(Math.Clamp(_offset, -16f, 16f) * Units);
            BrushCommands.SetStroke(session, stroke.Id, s => s.Offset = units);
        }

        DesignerStyle.ItemWidth(140f);
        ImGui.SliderFloat("Size"u8, ref _scale, 0.1f, 4f, "%.2f x"u8, ImGuiSliderFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var scale = (ushort)Math.Round(Math.Clamp(_scale, 0.1f, 10f) * HullStroke.UnitScale);
            BrushCommands.SetStroke(session, stroke.Id, s => s.Scale = scale);
        }

        // Each point's size, blended along the line to the next; the point last grabbed in the view is picked.
        var point = Math.Clamp(tool.Point, 0, stroke.Points.Count - 1);
        DesignerStyle.ItemWidth(140f);

        if (ImGui.SliderInt("Point"u8, ref point, 0, stroke.Points.Count - 1, "%d"u8, ImGuiSliderFlags.None))
            tool.Point = point;

        if (_pointFor != (stroke.Id, point, session.Version))
        {
            _pointScale = stroke.Points[point].Scale / (float)HullBrush.UnitScale;
            _pointFor = (stroke.Id, point, session.Version);
        }

        DesignerStyle.ItemWidth(140f);
        ImGui.SliderFloat("Point size"u8, ref _pointScale, 0.05f, 4f, "%.2f x"u8, ImGuiSliderFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
            BrushCommands.SetPointScale(session, stroke.Id, point, _pointScale);

        ImGui.SameLine();

        if (ImGui.Button("All 1x"u8))
            BrushCommands.SetStroke(session, stroke.Id, s =>
            {
                for (var i = 0; i < s.Points.Count; i++)
                    s.Points[i] = s.Points[i] with { Scale = HullBrush.UnitScale };
            });

        ImGui.BeginDisabled(stroke.Points.Count <= HullStroke.MinPoints);

        if (ImGui.Button("Remove last point"u8))
            BrushCommands.SetStroke(session, stroke.Id, s => s.Points.RemoveAt(s.Points.Count - 1));

        ImGui.EndDisabled();
        ImGui.SameLine();

        if (ImGui.Button("Delete stroke"u8))
        {
            BrushCommands.RemoveStroke(session, stroke.Id);
            tool.Selected = 0;
        }

        ImGui.TextDisabled("Drag the white handles along the hull to move points; the one grabbed is the point sized above."u8);
    }

    private void Finish(DesignSession session, StrokeTool tool)
    {
        if (tool.PendingPoints < HullStroke.MinPoints)
        {
            tool.Message = $"Click at least {HullStroke.MinPoints} points on the hull";
            return;
        }

        uint id = 0;

        if (_catalog.All.Count > 0)
        {
            var material = _catalog.All[Math.Clamp(tool.Material, 0, _catalog.All.Count - 1)].Id;
            session.Edit(s => id = BrushCommands.AddStroke(s, tool.Brush, tool.Pending, s.EnsureMaterial(material)));
        }

        tool.Message = id == 0 ? BrushCommands.LastError : "";
        tool.Cancel();
        tool.Selected = id;
    }

    private void Pick(int index, StrokeTool tool)
    {
        _brush = index;
        tool.Brush = index;
    }

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
        DesignerStyle.ItemWidth(150f);

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

    private static bool Combo(ReadOnlySpan<byte> label, ref int index, string[] names)
    {
        if (names.Length == 0)
            return false;

        index = Math.Clamp(index, 0, names.Length - 1);
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
}
