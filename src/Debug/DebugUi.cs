using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Time;

namespace BrutalGame.Debug;

/// <summary>
/// The debug panels' building blocks, in the designer's style: sections, label/value tables, timing rows with a history
/// line, tooltips and status colours. Everything goes through ImGui's string arena: no GC allocation.
/// </summary>
public static class DebugUi
{
    private const float LabelEms = 8.5f;
    private const float NumberEms = 4.5f;
    private const float GraphEms = 7f;

    public static readonly float4 Good = new(0.45f, 0.85f, 0.5f, 1f);
    public static readonly float4 Warn = new(1f, 0.75f, 0.3f, 1f);
    public static readonly float4 Bad = new(1f, 0.38f, 0.32f, 1f);

    /// <summary>A collapsing section, open the first time it is seen.</summary>
    public static bool Section(ImString label) => ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);

    /// <summary>A two-column table of dim labels and values; close with <see cref="End"/> when it returns true.</summary>
    public static bool Props(ImString id)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingFixedFit))
            return false;

        ImGui.TableSetupColumn("##label"u8, ImGuiTableColumnFlags.WidthFixed, Ems(LabelEms));
        ImGui.TableSetupColumn("##value"u8, ImGuiTableColumnFlags.WidthStretch);
        return true;
    }

    /// <summary>Starts a row of <see cref="Props"/>: the label, then the value cell (draw the value next).</summary>
    public static void Row(ImString label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        Text(DesignerStyle.TextDim, label);
        ImGui.TableNextColumn();
    }

    public static void Row(ImString label, ImString value)
    {
        Row(label);
        ImGui.Text(value);
    }

    public static void Row(ImString label, in float4 colour, ImString value)
    {
        Row(label);
        Text(colour, value);
    }

    public static void End() => ImGui.EndTable();

    /// <summary>A table of timings: name, average, max and the history; rows via <see cref="Timing"/>, then <see cref="End"/>.</summary>
    public static bool Timings(ImString id)
    {
        if (!ImGui.BeginTable(id, 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            return false;

        ImGui.TableSetupColumn("##name"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("avg ms"u8, ImGuiTableColumnFlags.WidthFixed, Ems(NumberEms));
        ImGui.TableSetupColumn("max ms"u8, ImGuiTableColumnFlags.WidthFixed, Ems(NumberEms));
        ImGui.TableSetupColumn("##graph"u8, ImGuiTableColumnFlags.WidthFixed, Ems(GraphEms));
        ImGui.TableHeadersRow();
        return true;
    }

    public static void Timing(ImString label, TimingRing ring)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(label);
        ImGui.TableNextColumn();
        ImGui.Text($"{ring.Average:F3}");
        ImGui.TableNextColumn();
        ImGui.Text($"{ring.Max:F3}");
        ImGui.TableNextColumn();
        Spark(ring);
    }

    /// <summary>A row-high history line of a ring, filling the cell, from 0 to the ring's max.</summary>
    public static void Spark(TimingRing ring)
    {
        ImGui.PushID(ring.GetHashCode());
        ImGui.PlotLines("##spark"u8, ring.Values, ring.Offset, default, 0f, MathF.Max(ring.Max, 1e-4f), new float2(-1f, ImGui.GetTextLineHeight()));
        ImGui.PopID();
    }

    /// <summary>A labelled history plot the width of the panel.</summary>
    public static void Plot(ImString id, TimingRing ring, ImString overlay, float heightEms = 3f)
    {
        ImGui.PlotLines(id, ring.Values, ring.Offset, overlay, 0f, MathF.Max(ring.Max, 1e-4f), new float2(-1f, Ems(heightEms)));
    }

    /// <summary>Coloured text taken as it is (ImGui's TextColored and SetTooltip read theirs as a printf format, eating '%').</summary>
    public static void Text(in float4 colour, ImString text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.Text(text);
        ImGui.PopStyleColor(1);
    }

    /// <summary>A dim note wrapped to the panel's width.</summary>
    public static void Note(ImString text)
    {
        ImGui.PushTextWrapPos(0f);
        Text(DesignerStyle.TextDim, text);
        ImGui.PopTextWrapPos();
    }

    /// <summary>A tooltip now, its text taken as it is.</summary>
    public static void Tooltip(ImString text)
    {
        ImGui.BeginTooltip();
        ImGui.Text(text);
        ImGui.EndTooltip();
    }

    /// <summary>The last item's tooltip, after a short hover.</summary>
    public static void Tip(ImString text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip))
            Tooltip(text);
    }

    /// <summary>A dim "(?)" after the last item that explains it on hover.</summary>
    public static void Help(ImString text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)"u8);
        Tip(text);
    }

    /// <summary>Green, amber or red as <paramref name="value"/> passes <paramref name="warn"/> and <paramref name="bad"/>.</summary>
    public static float4 Grade(float value, float warn, float bad) => value >= bad ? Bad : value >= warn ? Warn : Good;

    /// <summary>Pixels for a size in font heights, so widths follow the style's scale.</summary>
    public static float Ems(float ems) => MathF.Round(ems * ImGui.GetFontSize());
}
