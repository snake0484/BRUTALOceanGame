using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// Paint slot edits, each one undo group that swaps the plan's paints for an edited copy. Everything painted names a slot,
/// so changing a slot (or importing another paint into it) repaints every use on the ship at once.
/// </summary>
public static class PaintCommands
{
    public static string LastError { get; private set; } = "";

    /// <summary>A new slot holding <paramref name="paint"/>; returns its index, or -1.</summary>
    public static int AddSlot(DesignSession session, HullPaint paint)
    {
        if (session.Doc.Paint is { } set && set.Slots.Count >= HullPaintSet.MaxSlots)
        {
            LastError = $"A ship has at most {HullPaintSet.MaxSlots} paint slots";
            return -1;
        }

        var index = session.Doc.Paint?.Slots.Count ?? 0;
        Change(session, data => data.Slots.Add(paint.Clone()));
        return index;
    }

    /// <summary>Slot <paramref name="slot"/> now holds <paramref name="paint"/> (an import, or a new colour or finish): every use follows.</summary>
    public static bool SetSlot(DesignSession session, int slot, HullPaint paint) =>
        EditSlot(session, slot, p =>
        {
            (p.Name, p.Source, p.Finish, p.Colour) = (paint.Name, paint.Source, paint.Finish, paint.Colour);
        });

    public static bool EditSlot(DesignSession session, int slot, Action<HullPaint> edit)
    {
        if (session.Doc.Paint is not { } set || slot < 0 || slot >= set.Slots.Count)
            return false;

        Change(session, data => edit(data.Slots[slot]));
        return true;
    }

    /// <summary>Removes a slot nothing uses (later slots move down one, and their uses with them).</summary>
    public static bool RemoveSlot(DesignSession session, int slot)
    {
        if (session.Doc.Paint is not { } set || slot < 0 || slot >= set.Slots.Count)
            return false;

        if (set.Hull == slot || set.Strokes.Exists(s => s.Slot == slot) || session.Doc.Decals is { } decals && decals.Decals.Exists(d => d.Shape != DecalShape.Image && d.Paint == slot))
        {
            LastError = "Something is painted with this slot: repaint it first";
            return false;
        }

        session.Edit(s =>
        {
            if (s.Doc.Decals is { } d && d.Decals.Exists(x => x.Shape != DecalShape.Image && x.Paint > slot))
            {
                DecalCommands.EditAll(s, x =>
                {
                    if (x.Shape != DecalShape.Image && x.Paint > slot)
                        x.Paint--;
                });
            }

            Change(s, data =>
            {
                data.Slots.RemoveAt(slot);

                if (data.Hull != HullPaintSet.None && data.Hull > slot)
                    data.Hull--;

                for (var i = 0; i < data.Strokes.Count; i++)
                {
                    if (data.Strokes[i].Slot > slot)
                        data.Strokes[i] = (data.Strokes[i].Stroke, (byte)(data.Strokes[i].Slot - 1));
                }
            });
        });
        return true;
    }

    /// <summary>The hull's base paint: slot <paramref name="slot"/>, or -1 for the structural materials' colours.</summary>
    public static bool SetHull(DesignSession session, int slot)
    {
        if (slot >= 0 && (session.Doc.Paint is not { } set || slot >= set.Slots.Count))
            return false;

        Change(session, data => data.Hull = slot < 0 ? HullPaintSet.None : (byte)slot);
        return true;
    }

    /// <summary>Brush stroke <paramref name="stroke"/>'s own paint: slot <paramref name="slot"/>, or -1 to carry the hull's.</summary>
    public static bool SetStroke(DesignSession session, uint stroke, int slot)
    {
        if (session.Doc.Brushes is not { } brushes || brushes.IndexOfStroke(stroke) < 0)
            return false;

        if (slot >= 0 && (session.Doc.Paint is not { } set || slot >= set.Slots.Count))
            return false;

        Change(session, data =>
        {
            data.Strokes.RemoveAll(s => s.Stroke == stroke);

            if (slot >= 0)
                data.Strokes.Add((stroke, (byte)slot));
        });
        return true;
    }

    private static void Change(DesignSession session, Action<HullPaintSet> mutate) =>
        session.Edit(s =>
        {
            var before = s.Doc.Paint;
            var after = before?.Clone() ?? new HullPaintSet();
            mutate(after);
            s.Apply(PlanOp.SetPaint(before, after.IsEmpty ? null : after));
        });
}
