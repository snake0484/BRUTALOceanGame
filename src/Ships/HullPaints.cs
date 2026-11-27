using System.Buffers.Binary;
using System.Text;

namespace BrutalGame.Ships;

/// <summary>
/// A paint in one of a plan's slots: a finish (texture set from game data, by asset id) in a colour. <see cref="Source"/>
/// names the library file it was imported from or saved to; a change to that file reaches the plan only when it is
/// reimported on purpose.
/// </summary>
public sealed class HullPaint
{
    public const int MaxNameBytes = 64;
    public const int MaxSourceBytes = 255;

    public string Name = "";
    public string Source = "";

    /// <summary>The finish's asset id (<c>DefId</c> value of a finish in game data).</summary>
    public uint Finish;

    /// <summary>Colour as sRGB bytes, R in the low byte, alpha (opacity, for paint shapes) in the high one.</summary>
    public uint Colour = 0xFFB4B4B4;

    public bool SameLook(HullPaint other) => Finish == other.Finish && Colour == other.Colour;

    public HullPaint Clone() => (HullPaint)MemberwiseClone();
}

/// <summary>
/// The plan's paints, by slot: the hull's base paint and anything else painted (paint shapes, brush strokes with their
/// own paint) name a slot, so changing a slot, or importing another paint into it, repaints all of them. Brushes without
/// their own paint carry the hull's. Edits replace the whole set, so undo restores it exactly.
/// </summary>
public sealed class HullPaintSet
{
    public const int MaxSlots = 32;
    public const byte None = 255;

    public readonly List<HullPaint> Slots = new();

    /// <summary>The hull's base paint slot, or <see cref="None"/> (the structural materials' colours).</summary>
    public byte Hull = None;

    /// <summary>Brush strokes painted their own slot (by stroke id).</summary>
    public readonly List<(uint Stroke, byte Slot)> Strokes = new();

    public bool IsEmpty => Slots.Count == 0 && Strokes.Count == 0 && Hull == None;

    public int SlotOf(uint stroke)
    {
        foreach (var (s, slot) in Strokes)
        {
            if (s == stroke)
                return slot;
        }

        return -1;
    }

    public HullPaintSet Clone()
    {
        var copy = new HullPaintSet { Hull = Hull };

        foreach (var p in Slots)
            copy.Slots.Add(p.Clone());

        copy.Strokes.AddRange(Strokes);
        return copy;
    }

    /// <summary>
    /// <c>u8 slot count</c>, per slot <c>u8 name length, name (UTF-8), u8 source length, source, u32 finish, u32 colour</c>;
    /// <c>u8 hull slot, u16 stroke count</c>, per stroke <c>u32 id, u8 slot</c>.
    /// </summary>
    public byte[] ToBytes()
    {
        var names = Slots.Select(p => Clip(p.Name, HullPaint.MaxNameBytes)).ToList();
        var sources = Slots.Select(p => Clip(p.Source, HullPaint.MaxSourceBytes)).ToList();
        var b = new byte[1 + Slots.Select((_, i) => 10 + names[i].Length + sources[i].Length).Sum() + 3 + Strokes.Count * 5];
        var o = 0;
        b[o++] = (byte)Slots.Count;

        for (var i = 0; i < Slots.Count; i++)
        {
            b[o++] = (byte)names[i].Length;
            names[i].CopyTo(b, o);
            o += names[i].Length;
            b[o++] = (byte)sources[i].Length;
            sources[i].CopyTo(b, o);
            o += sources[i].Length;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), Slots[i].Finish);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), Slots[i].Colour);
            o += 8;
        }

        b[o++] = Hull;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Strokes.Count);
        o += 2;

        foreach (var (stroke, slot) in Strokes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), stroke);
            b[o + 4] = slot;
            o += 5;
        }

        return b;
    }

    public static HullPaintSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1 || s[0] > MaxSlots)
            return null;

        var set = new HullPaintSet();
        int count = s[0], o = 1;

        for (var i = 0; i < count; i++)
        {
            var paint = new HullPaint();

            if (!Text(s, ref o, out paint.Name) || !Text(s, ref o, out paint.Source) || s.Length < o + 8)
                return null;

            paint.Finish = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
            paint.Colour = BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 4)..]);
            o += 8;
            set.Slots.Add(paint);
        }

        if (s.Length < o + 3)
            return null;

        set.Hull = s[o++];
        int strokes = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;

        if (strokes > HullBrushSet.MaxStrokes || s.Length < o + strokes * 5)
            return null;

        for (var i = 0; i < strokes; i++, o += 5)
            set.Strokes.Add((BinaryPrimitives.ReadUInt32LittleEndian(s[o..]), s[o + 4]));

        consumed = o;
        return set;
    }

    private static byte[] Clip(string text, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length <= max ? bytes : bytes[..max];
    }

    private static bool Text(ReadOnlySpan<byte> s, ref int o, out string text)
    {
        text = "";

        if (s.Length < o + 1 || s.Length < o + 1 + s[o])
            return false;

        int length = s[o++];
        text = Encoding.UTF8.GetString(s.Slice(o, length));
        o += length;
        return true;
    }
}
