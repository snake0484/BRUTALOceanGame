using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalGame.DesignKit.Ui;

/// <summary>
/// An object's properties, laid out like Godot's inspector: a header (the kind and name, a filter, a menu), collapsible
/// sections, and rows of name and editor in two columns (the divider drags, and every section shares it). A row that
/// differs from its default shows a reset arrow; right-click an editor to copy, paste or reset its value; hover a name
/// for what it does. Numbers drag (click or Ctrl-click to type) and commit once, on release, so a drag is one undo step.
/// Immediate mode: call <see cref="Begin"/>, sections and rows, then <see cref="End"/> each frame.
/// </summary>
public sealed class Inspector
{
    private const float LabelEms = 7.5f;

    private readonly ImInputString _filter = new(64);
    private ImGuiID _active;
    private double _draft;
    private int _openAll = -1;
    private bool _inTable;
    private bool _sectionMatches;

    /// <summary>Width for a custom row's editor: the cell less the reset arrow's room.</summary>
    public float EditorWidth => ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X;

    private bool Filtering => _filter.Length > 0;

    public void Begin(ImString kind, ImString name)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DesignerStyle.Accent);
        ImGui.Text(kind);
        ImGui.PopStyleColor(1);
        ImGui.SameLine();
        Dim(name);

        var menu = ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - menu - ImGui.GetStyle().ItemSpacing.X);
        ImGui.InputTextWithHint("##inspectorfilter"u8, "Filter properties"u8, _filter);
        ImGui.SameLine();

        if (ImGui.Button("...##inspectormenu"u8, new float2(menu, menu)))
            ImGui.OpenPopup("##inspectormenu"u8);

        if (ImGui.BeginPopup("##inspectormenu"u8))
        {
            if (ImGui.MenuItem("Expand all"u8))
                _openAll = 1;

            if (ImGui.MenuItem("Collapse all"u8))
                _openAll = 0;

            if (ImGui.MenuItem("Clear filter"u8, default, false, Filtering))
                _filter.Clear();

            ImGui.EndPopup();
        }

        _sectionMatches = false;
    }

    public void End()
    {
        CloseTable();
        _openAll = -1;
    }

    /// <summary>A collapsible section; its rows follow while it returns true. Open by default; a filter opens them all.</summary>
    public bool Section(ImString name)
    {
        CloseTable();

        if (Filtering)
            ImGui.SetNextItemOpen(true);
        else if (_openAll >= 0)
            ImGui.SetNextItemOpen(_openAll == 1);

        _sectionMatches = Filtering && PropertyFilter.Matches(_filter.ValueSpan, name);
        return ImGui.CollapsingHeader(name, ImGuiTreeNodeFlags.DefaultOpen);
    }

    /// <summary>A row for a custom editor: false when the filter hides it. Draw the editor at <see cref="EditorWidth"/>, then <see cref="Revert"/>.</summary>
    public bool Row(ImString name, ImString tip = default)
    {
        if (Filtering && !_sectionMatches && !PropertyFilter.Matches(_filter.ValueSpan, name))
            return false;

        OpenTable();

        if (!_inTable)
            return false;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.AlignTextToFramePadding();
        Dim(name);

        if (tip.IsSet && !tip.IsEmpty)
            ImGui.SetItemTooltip(tip);

        ImGui.TableNextColumn();
        return true;
    }

    /// <summary>The reset arrow after a custom editor (its room kept when the value is at its default); true when clicked.</summary>
    public bool Revert(bool differs)
    {
        ImGui.SameLine();
        var size = ImGui.GetFrameHeight();

        if (!differs)
        {
            ImGui.Dummy(new float2(size, size));
            return false;
        }

        var clicked = ImGui.InvisibleButton("##revert"u8, new float2(size, size));
        var hovered = ImGui.IsItemHovered();
        ImGui.SetItemTooltip("Reset to default"u8);
        var min = ImGui.GetItemRectMin();
        var centre = new float2(min.X + size * 0.5f, min.Y + size * 0.5f);
        var colour = ImGui.GetColorU32(hovered ? DesignerStyle.Accent : DesignerStyle.TextDim);
        var r = size * 0.26f;
        var list = ImGui.GetWindowDrawList();
        list.PathArcTo(centre, r, MathF.PI * -0.75f, MathF.PI * 0.75f, 12);
        list.PathStroke(colour, default, 1.6f);
        var tip = new float2(centre.X + r * MathF.Cos(MathF.PI * -0.75f), centre.Y + r * MathF.Sin(MathF.PI * -0.75f));
        list.AddTriangleFilled(new float2(tip.X - r * 0.55f, tip.Y - r * 0.1f), new float2(tip.X + r * 0.45f, tip.Y - r * 0.45f), new float2(tip.X + r * 0.2f, tip.Y + r * 0.55f), colour);
        return clicked;
    }

    /// <summary>A dragged number; true once, when an edit is committed (drag released, typed, pasted or reset).</summary>
    public bool Float(ImString name, ref double value, double reset, float speed, double min, double max, ImString format, ImString tip = default)
    {
        if (!Row(name, tip))
            return false;

        ImGui.PushID(name);
        var v = Drafted() ? (float)_draft : (float)value;
        ImGui.SetNextItemWidth(EditorWidth);
        ImGui.DragFloat("##v"u8, ref v, speed, (float)min, (float)max, format, ImGuiSliderFlags.AlwaysClamp);
        var result = (double)v;
        var committed = Track(result);
        committed |= Context(ref result, reset, min, max);
        var differs = Math.Abs(value - reset) > 1e-6 * Math.Max(1.0, Math.Abs(reset));

        if (Revert(differs))
            (result, committed) = (reset, true);

        ImGui.PopID();

        if (committed)
            value = result;

        return committed;
    }

    /// <summary>A dragged whole number; true once, when an edit is committed.</summary>
    public bool Int(ImString name, ref int value, int reset, float speed, int min, int max, ImString format, ImString tip = default)
    {
        if (!Row(name, tip))
            return false;

        ImGui.PushID(name);
        var v = Drafted() ? (int)_draft : value;
        ImGui.SetNextItemWidth(EditorWidth);
        ImGui.DragInt("##v"u8, ref v, speed, min, max, format, ImGuiSliderFlags.AlwaysClamp);
        var result = (double)v;
        var committed = Track(result);
        committed |= Context(ref result, reset, min, max);

        if (Revert(value != reset))
            (result, committed) = (reset, true);

        ImGui.PopID();

        if (committed)
            value = (int)Math.Round(result);

        return committed;
    }

    /// <summary>One of <paramref name="names"/>, from a dropdown; true when another is picked (or reset).</summary>
    public bool Choice(ImString name, ref int value, IReadOnlyList<string> names, int reset, ImString tip = default)
    {
        if (!Row(name, tip) || names.Count == 0)
            return false;

        ImGui.PushID(name);
        var picked = Math.Clamp(value, 0, names.Count - 1);
        ImGui.SetNextItemWidth(EditorWidth);

        if (ImGui.BeginCombo("##v"u8, names[picked], ImGuiComboFlags.None))
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (ImGui.Selectable(names[i], i == picked, ImGuiSelectableFlags.None, default))
                    picked = i;
            }

            ImGui.EndCombo();
        }

        if (ImGui.BeginPopupContextItem("##ctx"u8))
        {
            if (ImGui.MenuItem("Reset to default"u8, default, false, value != reset))
                picked = reset;

            ImGui.EndPopup();
        }

        if (Revert(value != reset))
            picked = reset;

        ImGui.PopID();
        var changed = picked != value;
        value = picked;
        return changed;
    }

    /// <summary>A read-only row.</summary>
    public void Info(ImString name, ImString value)
    {
        if (!Row(name))
            return;

        ImGui.TextDisabled(value);
    }

    private bool Drafted() => _active.Value != 0 && _active == ImGui.GetID("##v"u8);

    /// <summary>Keeps the live value while the editor is held; true when it lets go after an edit.</summary>
    private bool Track(double value)
    {
        var id = ImGui.GetItemID();

        if (ImGui.IsItemActive())
            (_active, _draft) = (id, value);
        else if (_active == id)
            _active = default;

        return ImGui.IsItemDeactivatedAfterEdit();
    }

    /// <summary>The editor's right-click menu: copy, paste (a number from the clipboard), reset.</summary>
    private static bool Context(ref double value, double reset, double min, double max)
    {
        if (!ImGui.BeginPopupContextItem("##ctx"u8))
            return false;

        var committed = false;

        if (ImGui.MenuItem("Copy value"u8))
            ImGui.SetClipboardText(value.ToString("R", CultureInfo.InvariantCulture));

        if (ImGui.MenuItem("Paste value"u8)
            && double.TryParse(ImGui.GetClipboardText().ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pasted))
            (value, committed) = (max > min ? Math.Clamp(pasted, min, max) : pasted, true);

        if (ImGui.MenuItem("Reset to default"u8, default, false, value != reset))
            (value, committed) = (reset, true);

        ImGui.EndPopup();
        return committed;
    }

    private void OpenTable()
    {
        if (_inTable)
            return;

        // One id for every section's table, so they share the divider.
        _inTable = ImGui.BeginTable("##inspectorrows"u8, 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit);

        if (!_inTable)
            return;

        ImGui.TableSetupColumn("##name"u8, ImGuiTableColumnFlags.WidthFixed, ImGui.GetFontSize() * LabelEms);
        ImGui.TableSetupColumn("##value"u8, ImGuiTableColumnFlags.WidthStretch);
    }

    private void CloseTable()
    {
        if (!_inTable)
            return;

        ImGui.EndTable();
        _inTable = false;
    }

    private static void Dim(ImString text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DesignerStyle.TextDim);
        ImGui.Text(text);
        ImGui.PopStyleColor(1);
    }
}
