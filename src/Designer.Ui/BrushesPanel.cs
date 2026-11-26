using Brutal.ImGuiApi;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Brushes panel: the plan's brushes (stamps made from a solid, swept profiles from presets) and the strokes drawn
/// with them: small raised detail drawn from its own geometry, counted for mass. Draw a stroke, pick it to edit its brush,
/// material, offset and size; drag its points in the view.
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

    public BrushesPanel(HullMaterialCatalog catalog) => _catalog = catalog;

    public void Draw(DesignSession session, StrokeTool tool)
    {
        if (tool.FinishRequested)
            Finish(session, tool);

        // Docks with Properties the first time (layouts saved before brushes existed have no place for it).
        ImGui.SetNextWindowDockID(new ImGuiID(DesignerStyle.EditDock), ImGuiCond.FirstUseEver);

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
        _brush = Math.Clamp(_brush, -1, count - 1);

        if (count == 0)
            ImGui.TextDisabled("No brushes yet. Make a stamp from a solid, or a swept profile."u8);

        for (var i = 0; i < count; i++)
        {
            var b = set!.Brushes[i];
            var label = b.Kind == BrushKind.Stamp ? $"{i + 1}  Stamp, every {b.Spacing / Units:0.##} m" : $"{i + 1}  Sweep, {b.Profile.Count} corners";

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

        if (ImGui.Button("New swept profile"u8))
            Pick(BrushCommands.AddSweep(session, (ProfilePreset)_preset, _width, _height), tool);

        if (_brush < 0 || set is null)
            return;

        var brush = set.Brushes[_brush];
        ImGui.SameLine();

        if (brush.Kind == BrushKind.Sweep && ImGui.Button("Apply to brush"u8))
            BrushCommands.SetProfile(session, _brush, (ProfilePreset)_preset, _width, _height);

        if (brush.Kind == BrushKind.Stamp)
        {
            DesignerStyle.ItemWidth(90f);
            ImGui.InputFloat("Spacing"u8, ref _spacing, 0f, 0f, "%.3f m"u8, ImGuiInputTextFlags.None);

            if (ImGui.IsItemDeactivatedAfterEdit())
                BrushCommands.SetSpacing(session, _brush, Math.Max(_spacing, 0.0625f));
        }

        if (ImGui.Button("Delete brush"u8))
        {
            _message = BrushCommands.RemoveBrush(session, _brush) ? "" : BrushCommands.LastError;

            if (_message.Length == 0)
                Pick(-1, tool);
        }
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

        ImGui.TextDisabled("Drag the white handles along the hull to move points."u8);
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
