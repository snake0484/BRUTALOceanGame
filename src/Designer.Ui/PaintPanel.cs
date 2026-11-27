using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The Paint tab. The ship's paint slots: each holds a paint (a finish from game data in a colour), and everything painted
/// names a slot (the hull's base paint, paint shapes, brush strokes with their own paint), so changing a slot or
/// importing another paint into it repaints every use at once. Brushes without their own paint carry the hull's. The
/// library on disk (folders as categories) holds paints to import into a slot or save a slot to; a slot keeps its copy,
/// and a change to the file reaches it only with Reimport.
/// </summary>
public sealed class PaintPanel : IDisposable
{
    private readonly FinishCatalog _finishes;
    private readonly PaintLibrary _library;
    private readonly FileDialogs _dialogs;
    private readonly byte[] _name = new byte[HullPaint.MaxNameBytes + 1];
    private (int, int) _nameFor = (-1, -1);
    private float4 _colour;
    private (int, int) _colourFor = (-1, -1);
    private string _message = "";

    public PaintPanel(FinishCatalog finishes, PaintLibrary library)
    {
        _finishes = finishes;
        _library = library;
        _dialogs = new FileDialogs("Paint", PaintLibrary.Extension, library.Folder);
        _library.Refresh();
    }

    /// <summary>The slot shown in the editor (-1: none).</summary>
    public int Selected = -1;

    public void Reset() => (Selected, _nameFor, _colourFor) = (-1, (-1, -1), (-1, -1));

    public void Draw(DesignSession session)
    {
        ImGui.SetNextWindowDockID(new ImGuiID(ShipLayout.EditDock), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Paint"u8))
        {
            var paint = session.Doc.Paint;
            Selected = Math.Min(Selected, (paint?.Slots.Count ?? 0) - 1);
            DrawHull(session, paint);
            DrawSlots(session, paint);

            if (paint is not null && Selected >= 0)
                DrawSlot(session, paint.Slots[Selected], Selected);

            DrawLibrary(session);

            if (_message.Length > 0)
                ImGui.TextWrapped($"{_message}");
        }

        ImGui.End();
    }

    /// <summary>The paint slot combo used across the designer: "<paramref name="none"/>" (-1) then each slot.</summary>
    public static bool SlotCombo(ReadOnlySpan<byte> label, HullPaintSet? paint, ref int slot, string none)
    {
        var count = paint?.Slots.Count ?? 0;
        var changed = false;
        var current = slot >= 0 && slot < count ? $"{slot + 1}  {paint!.Slots[slot].Name}" : none;

        if (ImGui.BeginCombo(label, $"{current}", ImGuiComboFlags.None))
        {
            if (ImGui.Selectable($"{none}##noslot", slot < 0, ImGuiSelectableFlags.None, default) && slot >= 0)
                (slot, changed) = (-1, true);

            for (var i = 0; i < count; i++)
            {
                Swatch(paint!.Slots[i].Colour);
                ImGui.SameLine();

                if (ImGui.Selectable($"{i + 1}  {paint.Slots[i].Name}##slot{i}", i == slot, ImGuiSelectableFlags.None, default) && i != slot)
                    (slot, changed) = (i, true);
            }

            if (count == 0)
                ImGui.TextDisabled("No paint slots yet: add one in the Paint tab"u8);

            ImGui.EndCombo();
        }

        return changed;
    }

    private void DrawHull(DesignSession session, HullPaintSet? paint)
    {
        ImGui.SeparatorText("Hull"u8);
        var hull = paint is null || paint.Hull == HullPaintSet.None ? -1 : paint.Hull;
        DesignerStyle.ItemWidth(220f);

        if (SlotCombo("Base paint"u8, paint, ref hull, "Material colours"))
            PaintCommands.SetHull(session, hull);

        ImGui.TextDisabled("Brushes carry the hull's paint unless their stroke has its own."u8);
    }

    private void DrawSlots(DesignSession session, HullPaintSet? paint)
    {
        ImGui.SeparatorText("Paint slots"u8);
        var count = paint?.Slots.Count ?? 0;

        if (count == 0)
            ImGui.TextDisabled("None yet."u8);

        for (var i = 0; i < count; i++)
        {
            var slot = paint!.Slots[i];
            var uses = Uses(session, paint, i);
            Swatch(slot.Colour);
            ImGui.SameLine();

            if (ImGui.Selectable($"{i + 1}  {slot.Name}  ({FinishName(slot.Finish)}, {uses} use{(uses == 1 ? "" : "s")}){(slot.Source.Length > 0 ? "  [library]" : "")}##ps{i}", i == Selected, ImGuiSelectableFlags.None, default))
                Selected = i == Selected ? -1 : i;
        }

        if (ImGui.Button("New slot"u8) && _finishes.All.Count > 0)
        {
            var added = PaintCommands.AddSlot(session, new HullPaint { Name = $"Paint {count + 1}", Finish = _finishes.All[0].Id.Value });
            _message = added < 0 ? PaintCommands.LastError : "";

            if (added >= 0)
                Selected = added;
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(Selected < 0);

        if (ImGui.Button("Delete slot"u8))
        {
            if (PaintCommands.RemoveSlot(session, Selected))
                Selected = -1;
            else
                _message = PaintCommands.LastError;
        }

        ImGui.EndDisabled();
    }

    private static int Uses(DesignSession session, HullPaintSet paint, int slot) =>
        (paint.Hull == slot ? 1 : 0) + paint.Strokes.Count(s => s.Slot == slot)
        + (session.Doc.Decals?.Decals.Count(d => d.Shape != DecalShape.Image && d.Paint == slot) ?? 0);

    private void DrawSlot(DesignSession session, HullPaint slot, int index)
    {
        ImGui.SeparatorText($"Slot {index + 1}");

        if (_nameFor != (index, session.Version))
        {
            Array.Clear(_name);
            var bytes = Encoding.UTF8.GetBytes(slot.Name);
            Array.Copy(bytes, _name, Math.Min(bytes.Length, HullPaint.MaxNameBytes));
            _nameFor = (index, session.Version);
        }

        DesignerStyle.ItemWidth(220f);
        ImGui.InputText("Name##paint"u8, _name, ImGuiInputTextFlags.None, (ImGuiInputTextCallback?)null, default);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var length = Array.IndexOf(_name, (byte)0);
            var name = Encoding.UTF8.GetString(_name, 0, length < 0 ? _name.Length : length).Trim();

            if (name.Length > 0)
                PaintCommands.EditSlot(session, index, p => p.Name = name);
        }

        var finish = _finishes.IndexOf(new DefId(slot.Finish));
        DesignerStyle.ItemWidth(220f);

        if (ImGui.BeginCombo("Finish"u8, $"{(finish >= 0 ? _finishes.All[finish].Name : "missing finish")}", ImGuiComboFlags.None))
        {
            for (var i = 0; i < _finishes.All.Count; i++)
            {
                if (ImGui.Selectable($"{_finishes.All[i].Name}##fin{i}", i == finish, ImGuiSelectableFlags.None, default) && i != finish)
                {
                    var id = _finishes.All[i].Id.Value;
                    PaintCommands.EditSlot(session, index, p => p.Finish = id);
                }
            }

            ImGui.EndCombo();
        }

        if (_colourFor != (index, session.Version))
            (_colour, _colourFor) = (Unpack(slot.Colour), (index, session.Version));

        DesignerStyle.ItemWidth(220f);
        ImGui.ColorEdit4("Colour##paint"u8, ref _colour, ImGuiColorEditFlags.None);

        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            var colour = Pack(_colour);
            PaintCommands.EditSlot(session, index, p => p.Colour = colour);
        }

        ImGui.TextDisabled("Alpha is the paint's opacity in paint shapes."u8);

        // The library copy: save to it, or reimport from it on purpose.
        if (slot.Source.Length > 0)
        {
            var current = _library.Current(slot.Source);

            if (current is null)
                ImGui.TextDisabled($"From the library: {slot.Source} (no longer there)");
            else if (current.SameLook(slot))
                ImGui.TextDisabled($"From the library: {slot.Source} (the same)");
            else
                ImGui.TextColored(new float4(1f, 0.8f, 0.4f, 1f), $"From the library: {slot.Source} (the library has changed)");

            ImGui.BeginDisabled(current is null || current.SameLook(slot));

            if (ImGui.Button("Reimport from the library"u8) && current is not null && PaintCommands.SetSlot(session, index, current))
                _message = $"Reimported {slot.Source}: everything painted with slot {index + 1} follows.";

            ImGui.EndDisabled();
            ImGui.SameLine();
        }

        if (ImGui.Button("Save to the library..."u8))
            Save(session, index, slot, finish);
    }

    private void Save(DesignSession session, int index, HullPaint slot, int finish)
    {
        if (finish < 0)
        {
            _message = "This slot's finish is not in the game data";
            return;
        }

        var start = slot.Source.Length > 0 ? Path.Combine(_library.Folder, slot.Source) : Path.Combine(_library.Folder, Safe(slot.Name) + "." + PaintLibrary.Extension);
        var path = _dialogs.PickSave(start);

        if (path is null)
        {
            _message = _dialogs.HasDialogs ? "" : "File dialogs are unavailable on this system (nfd.dll missing)";
            return;
        }

        try
        {
            var relative = _library.Save(slot, _finishes.All[finish].Key, path);
            PaintCommands.EditSlot(session, index, p => p.Source = relative);
            _library.Refresh();
            _message = $"Saved {relative}";
        }
        catch (IOException e)
        {
            _message = $"Save failed: {e.Message}";
        }
    }

    private void DrawLibrary(DesignSession session)
    {
        ImGui.SeparatorText("Library"u8);
        ImGui.TextDisabled($"{_library.Folder}");

        if (ImGui.Button("Refresh library"u8))
            _library.Refresh();

        if (_library.Root.Children.Count == 0)
            ImGui.TextDisabled("Empty: save a slot to start it (subfolders become categories)."u8);

        foreach (var node in _library.Root.Children)
            Tree(session, node);
    }

    private void Tree(DesignSession session, PaintNode node)
    {
        if (node.IsFolder)
        {
            if (ImGui.TreeNodeEx($"{node.Name}##{node.GetHashCode()}", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
            {
                foreach (var child in node.Children)
                    Tree(session, child);

                ImGui.TreePop();
            }

            return;
        }

        Swatch(node.Paint!.Colour);
        ImGui.SameLine();
        var known = _finishes.IndexOf(new DefId(node.Paint.Finish)) >= 0;
        ImGui.TreeNodeEx($"{node.Name}{(known ? "" : "  (finish not in this game)")}##{node.Relative}", ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(Selected >= 0 ? "Double-click to import into the selected slot (everything using it follows)"u8 : "Double-click to import as a new slot"u8);

        if (!ImGui.IsItemHovered() || !ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            return;

        if (!known)
        {
            _message = $"{node.Relative} uses a finish this game does not have";
            return;
        }

        if (Selected >= 0)
        {
            PaintCommands.SetSlot(session, Selected, node.Paint);
            _message = $"Imported {node.Relative} into slot {Selected + 1}: everything using it follows. Later changes to the file arrive only through Reimport.";
        }
        else
        {
            var added = PaintCommands.AddSlot(session, node.Paint);
            _message = added < 0 ? PaintCommands.LastError : $"Imported {node.Relative} as slot {added + 1}.";

            if (added >= 0)
                Selected = added;
        }
    }

    private string FinishName(uint finish) => _finishes.IndexOf(new DefId(finish)) is var i and >= 0 ? _finishes.All[i].Name : "missing finish";

    /// <summary>A small square of the paint's colour.</summary>
    public static void Swatch(uint colour)
    {
        var size = ImGui.GetFontSize();
        var at = ImGui.GetCursorScreenPos();
        var list = ImGui.GetWindowDrawList();
        list.AddRectFilled(new float2(at.X, at.Y + 1), new float2(at.X + size, at.Y + size + 1), new ImColor8((byte)(colour & 0xFF), (byte)((colour >> 8) & 0xFF), (byte)((colour >> 16) & 0xFF), 255));
        ImGui.Dummy(new float2(size, size));
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return safe.Length > 0 ? safe : "paint";
    }

    private static uint Pack(float4 c)
    {
        static uint B(float v) => (uint)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
        return B(c.X) | (B(c.Y) << 8) | (B(c.Z) << 16) | (B(c.W) << 24);
    }

    private static float4 Unpack(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, (c >> 24) / 255f);

    public void Dispose() => _dialogs.Dispose();
}
