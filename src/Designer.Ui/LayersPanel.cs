using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>Cutaway view: hull fragments past a plane are not drawn, so layers, rooms and walls show in section.</summary>
public sealed class Cutaway
{
    public bool Enabled;
    public int Axis = 2;
    public float Offset;
    public bool Flip;
}

/// <summary>
/// The layered hull panel: turn layering on, edit depth stacks (material and millimetres per layer, outside first),
/// pick the stacks for unassigned sections, rooms and components, add and move section cuts, assign stacks per section,
/// and the cutaway view. Every edit is one undo step.
/// </summary>
public sealed class LayersPanel
{
    public const float Width = 360f;

    private static readonly string[] AxisNames = ["X", "Y", "Z"];

    private readonly HullMaterialCatalog _catalog;
    private string _message = "";

    public LayersPanel(HullMaterialCatalog catalog)
    {
        _catalog = catalog;
    }

    public readonly Cutaway Cutaway = new();

    public void Draw(DesignSession session, float x, float y)
    {
        ImGui.SetNextWindowPos(new float2(x, y), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(Width, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Hull layers"u8, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var layers = session.Doc.Layers;

            if (layers is null)
            {
                ImGui.TextWrapped("Solid brush hull. A layered hull models the skin; sections fill it inward with depth stacks, rooms and components are cut out and walled."u8);

                if (ImGui.Button("Make layered hull"u8) && _catalog.All.Count > 0)
                    LayerCommands.Enable(session, _catalog.All[Math.Min(1, _catalog.All.Count - 1)].Id, _catalog.All[0].Id);
            }
            else
            {
                DrawStacks(session, layers);
                DrawCuts(session, layers);
                DrawSections(session, layers);

                if (ImGui.Button("Back to solid hull"u8) && !LayerCommands.Disable(session))
                    _message = LayerCommands.LastError;
            }

            DrawCutaway();

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    private void DrawStacks(DesignSession session, HullLayering layers)
    {
        if (!ImGui.CollapsingHeader("Depth stacks (outside first)"u8, ImGuiTreeNodeFlags.None))
            return;

        for (var s = 0; s < layers.Stacks.Count; s++)
        {
            var stack = layers.Stacks[s];
            ImGui.PushID(s);
            ImGui.Text($"Stack {s}  ({stack.TotalMetres:0.###} m)");

            for (var l = 0; l < stack.Layers.Count; l++)
            {
                ImGui.PushID(l);
                var layer = stack.Layers[l];
                var slot = CatalogSlot(session.Doc, layer.Material);
                ImGui.SetNextItemWidth(150f);

                if (MaterialCombo("##mat"u8, ref slot))
                    LayerCommands.SetLayer(session, s, l, _catalog.All[slot].Id, layer.ThicknessMm);

                ImGui.SameLine();
                int mm = layer.ThicknessMm;
                ImGui.SetNextItemWidth(110f);
                ImGui.InputInt("mm"u8, ref mm, 25, 250, ImGuiInputTextFlags.None);

                if (ImGui.IsItemDeactivatedAfterEdit() || (ImGui.IsItemEdited() && !ImGui.IsItemActive()))
                    LayerCommands.SetLayer(session, s, l, session.Doc.Materials[layer.Material], (ushort)Math.Clamp(mm, 1, HullLayering.MaxStackMm));

                ImGui.SameLine();

                if (ImGui.SmallButton("x"u8))
                    LayerCommands.RemoveLayer(session, s, l);

                ImGui.PopID();
            }

            if (ImGui.SmallButton("+ layer"u8) && _catalog.All.Count > 0)
                LayerCommands.AddLayer(session, s, _catalog.All[0].Id);

            ImGui.PopID();
        }

        if (ImGui.Button("+ stack"u8) && _catalog.All.Count > 0)
            LayerCommands.AddStack(session, _catalog.All[0].Id);

        StackCombo(session, layers, "Hull default"u8, 0, layers.DefaultStack);
        StackCombo(session, layers, "Room walls"u8, 1, layers.RoomStack);
        StackCombo(session, layers, "Component walls"u8, 2, layers.ComponentStack);
    }

    private void DrawCuts(DesignSession session, HullLayering layers)
    {
        if (!ImGui.CollapsingHeader("Section cuts"u8, ImGuiTreeNodeFlags.None))
            return;

        for (var c = 0; c < layers.Cuts.Count; c++)
        {
            var cut = layers.Cuts[c];
            ImGui.PushID(1000 + c);
            ImGui.Text($"{AxisNames[cut.Axis]} =");
            ImGui.SameLine();
            var metres = cut.Position * HullBrush.MetresPerPositionUnit;
            ImGui.SetNextItemWidth(130f);
            ImGui.InputDouble("m"u8, ref metres, 0.25, 1.0, "%.2f"u8, ImGuiInputTextFlags.None);

            if (ImGui.IsItemDeactivatedAfterEdit() || (ImGui.IsItemEdited() && !ImGui.IsItemActive()))
                LayerCommands.MoveCut(session, c, (int)Math.Round(metres * HullBrush.PositionUnitsPerMetre));

            ImGui.SameLine();

            if (ImGui.SmallButton("x"u8))
                LayerCommands.RemoveCut(session, c);

            ImGui.PopID();
        }

        for (var axis = 0; axis < 3; axis++)
        {
            if (axis > 0)
                ImGui.SameLine();

            if (ImGui.Button($"+ cut {AxisNames[axis]}"))
                LayerCommands.AddCut(session, axis, 0);
        }
    }

    private void DrawSections(DesignSession session, HullLayering layers)
    {
        if (!ImGui.CollapsingHeader("Sections"u8, ImGuiTreeNodeFlags.None))
            return;

        foreach (var (section, stack) in LayerCommands.Sections(layers))
        {
            ImGui.PushID(2000 + section.Id);
            ImGui.SetNextItemWidth(110f);

            if (ImGui.BeginCombo($"{section.Label}", $"stack {stack}", ImGuiComboFlags.None))
            {
                for (var s = 0; s < layers.Stacks.Count; s++)
                {
                    if (ImGui.Selectable($"stack {s}", s == stack, ImGuiSelectableFlags.None, default) && s != stack)
                        LayerCommands.AssignSection(session, section, s);
                }

                ImGui.EndCombo();
            }

            ImGui.PopID();
        }
    }

    private void DrawCutaway()
    {
        if (!ImGui.CollapsingHeader("Cutaway"u8, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.Checkbox("Cut view"u8, ref Cutaway.Enabled);
        ImGui.SameLine();

        for (var axis = 0; axis < 3; axis++)
        {
            ImGui.SameLine();

            if (ImGui.RadioButton($"{AxisNames[axis]}##cut", Cutaway.Axis == axis))
                Cutaway.Axis = axis;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Flip"u8, ref Cutaway.Flip);
        ImGui.SetNextItemWidth(260f);
        ImGui.SliderFloat("##offset"u8, ref Cutaway.Offset, -60f, 60f, "%.2f m"u8, ImGuiSliderFlags.None);
    }

    private static void StackCombo(DesignSession session, HullLayering layers, ReadOnlySpan<byte> label, int which, int current)
    {
        ImGui.SetNextItemWidth(110f);

        if (ImGui.BeginCombo(label, $"stack {current}", ImGuiComboFlags.None))
        {
            for (var s = 0; s < layers.Stacks.Count; s++)
            {
                if (ImGui.Selectable($"stack {s}", s == current, ImGuiSelectableFlags.None, default) && s != current)
                    LayerCommands.SetRoleStack(session, which, s);
            }

            ImGui.EndCombo();
        }
    }

    private bool MaterialCombo(ReadOnlySpan<byte> label, ref int index)
    {
        var all = _catalog.All;

        if (all.Count == 0)
            return false;

        index = Math.Clamp(index, 0, all.Count - 1);
        var changed = false;

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

    private int CatalogSlot(PlanDocument doc, byte planIndex)
    {
        if (planIndex >= doc.Materials.Count)
            return 0;

        for (var i = 0; i < _catalog.All.Count; i++)
        {
            if (_catalog.All[i].Id == doc.Materials[planIndex])
                return i;
        }

        return 0;
    }
}
