using System.Buffers.Binary;

namespace BrutalGame.Ships;

/// <summary>What a brush is in a layered hull (BrushFlags bits 6-7).</summary>
public enum BrushRole : byte { Skin, Room, Component, Panel }

public static class BrushRoles
{
    public const BrushFlags Mask = BrushFlags.RoleRoom | BrushFlags.RoleComponent;

    public static BrushRole Of(in HullBrush brush) => (BrushRole)(((int)brush.Flags >> 6) & 3);

    public static BrushFlags With(BrushFlags flags, BrushRole role) => (flags & ~Mask) | (BrushFlags)((int)role << 6);
}

/// <summary>One layer of a stack: a plan material index and a thickness in millimetres.</summary>
public readonly record struct HullLayer(byte Material, ushort ThicknessMm);

/// <summary>Layers from the outside in. The skin is the outer face of the first layer.</summary>
public sealed class LayerStack
{
    public readonly List<HullLayer> Layers = new();

    public double TotalMetres
    {
        get
        {
            var mm = 0;

            foreach (var layer in Layers)
                mm += layer.ThicknessMm;

            return mm * 0.001;
        }
    }

    /// <summary>Material of the layer at <paramref name="depth"/> metres in from the outer face (the last layer past the end).</summary>
    public byte MaterialAt(double depth)
    {
        var edge = 0.0;

        foreach (var layer in Layers)
        {
            edge += layer.ThicknessMm * 0.001;

            if (depth < edge)
                return layer.Material;
        }

        return Layers.Count > 0 ? Layers[^1].Material : (byte)0;
    }
}

/// <summary>A section cut: the plane coordinate[<see cref="Axis"/>] = <see cref="Position"/> (1/1024 m, ship-local).</summary>
public readonly record struct SectionCut(byte Axis, int Position);

/// <summary>Section containing <see cref="Anchor"/> (1/1024 m) uses stack <see cref="Stack"/>; anchors survive moving cuts.</summary>
public readonly record struct SectionStack(int AnchorX, int AnchorY, int AnchorZ, byte Stack);

/// <summary>
/// A layered hull (ship design note "skin, sections and layers"): the skin brushes shape the outer shell; cut planes
/// split the skin and hull into sections; each section's stack fills the shell inward from the skin by depth, and
/// anything deeper is void. Room and component brushes are cut out and get their own wall stack outward from their
/// surface, inside the skin. Panel brushes are solid add-ons. Treated as immutable once in a plan: edits replace it.
/// </summary>
public sealed class HullLayering
{
    public const int MaxStacks = 16;
    public const int MaxLayers = 16;
    public const int MaxCuts = 64;
    public const int MaxAssignments = 1024;
    public const int MinStackMm = 250;
    public const int MaxStackMm = 8000;

    public readonly List<LayerStack> Stacks = new();
    public readonly List<SectionCut> Cuts = new();
    public readonly List<SectionStack> Assignments = new();

    /// <summary>Stack for sections without an assignment.</summary>
    public byte DefaultStack;
    public byte RoomStack;
    public byte ComponentStack;

    public HullLayering Clone()
    {
        var copy = new HullLayering { DefaultStack = DefaultStack, RoomStack = RoomStack, ComponentStack = ComponentStack };

        foreach (var stack in Stacks)
        {
            var s = new LayerStack();
            s.Layers.AddRange(stack.Layers);
            copy.Stacks.Add(s);
        }

        copy.Cuts.AddRange(Cuts);
        copy.Assignments.AddRange(Assignments);
        return copy;
    }

    /// <summary>Sorted, deduplicated cut positions per axis, in metres.</summary>
    public double[][] CutMetres()
    {
        var axes = new double[3][];

        for (var axis = 0; axis < 3; axis++)
            axes[axis] = Cuts.Where(c => c.Axis == axis).Select(c => c.Position * HullBrush.MetresPerPositionUnit).Distinct().Order().ToArray();

        return axes;
    }

    /// <summary>Section index along one axis: the number of cuts at or below the coordinate.</summary>
    public static int Cell(double[] cuts, double v)
    {
        int lo = 0, hi = cuts.Length;

        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;

            if (cuts[mid] <= v)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    public static int SectionId(double[][] cuts, int ix, int iy, int iz) => ix + (cuts[0].Length + 1) * (iy + (cuts[1].Length + 1) * iz);

    /// <summary>Stack index per section id under the current cuts (later assignments to one section win).</summary>
    public int[] SectionStacks(double[][] cuts)
    {
        var count = (cuts[0].Length + 1) * (cuts[1].Length + 1) * (cuts[2].Length + 1);
        var stacks = new int[count];
        Array.Fill(stacks, DefaultStack);

        foreach (var a in Assignments)
        {
            var id = SectionId(cuts, Cell(cuts[0], a.AnchorX * HullBrush.MetresPerPositionUnit), Cell(cuts[1], a.AnchorY * HullBrush.MetresPerPositionUnit), Cell(cuts[2], a.AnchorZ * HullBrush.MetresPerPositionUnit));
            stacks[id] = a.Stack;
        }

        return stacks;
    }

    public double MaxStackMetres => Stacks.Count == 0 ? 0 : Stacks.Max(s => s.TotalMetres);

    // ---- bytes: the BODY's layering block (format 3) and part of the brick cache key ----

    /// <summary>
    /// u8 default, room, component stack; u8 stack count, then per stack u8 layer count and (u8 material, u16 mm) each;
    /// u16 cut count, (u8 axis, i32 position) each; u16 assignment count, (i32 x, y, z, u8 stack) each.
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 4 + Stacks.Sum(s => 1 + s.Layers.Count * 3) + 2 + Cuts.Count * 5 + 2 + Assignments.Count * 13;
        var b = new byte[size];
        var o = 0;
        b[o++] = DefaultStack;
        b[o++] = RoomStack;
        b[o++] = ComponentStack;
        b[o++] = (byte)Stacks.Count;

        foreach (var stack in Stacks)
        {
            b[o++] = (byte)stack.Layers.Count;

            foreach (var layer in stack.Layers)
            {
                b[o++] = layer.Material;
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), layer.ThicknessMm);
                o += 2;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Cuts.Count);
        o += 2;

        foreach (var cut in Cuts)
        {
            b[o++] = cut.Axis;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), cut.Position);
            o += 4;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Assignments.Count);
        o += 2;

        foreach (var a in Assignments)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), a.AnchorX);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), a.AnchorY);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), a.AnchorZ);
            b[o + 12] = a.Stack;
            o += 13;
        }

        return b;
    }

    /// <summary>Reads the block at the start of <paramref name="s"/>, structurally checked (counts within limits); null on failure.</summary>
    public static HullLayering? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 4)
            return null;

        var l = new HullLayering { DefaultStack = s[0], RoomStack = s[1], ComponentStack = s[2] };
        int stacks = s[3], o = 4;

        if (stacks > MaxStacks)
            return null;

        for (var i = 0; i < stacks; i++)
        {
            if (s.Length < o + 1)
                return null;

            int layers = s[o++];

            if (layers > MaxLayers || s.Length < o + layers * 3)
                return null;

            var stack = new LayerStack();

            for (var j = 0; j < layers; j++, o += 3)
                stack.Layers.Add(new HullLayer(s[o], BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 1)..])));

            l.Stacks.Add(stack);
        }

        if (s.Length < o + 2)
            return null;

        int cuts = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;

        if (cuts > MaxCuts || s.Length < o + cuts * 5 + 2)
            return null;

        for (var i = 0; i < cuts; i++, o += 5)
            l.Cuts.Add(new SectionCut(s[o], BinaryPrimitives.ReadInt32LittleEndian(s[(o + 1)..])));

        int assignments = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;

        if (assignments > MaxAssignments || s.Length < o + assignments * 13)
            return null;

        for (var i = 0; i < assignments; i++, o += 13)
            l.Assignments.Add(new SectionStack(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..]), s[o + 12]));

        consumed = o;
        return l;
    }

    public ulong Hash() => XxHash64.Hash(ToBytes());
}

/// <summary>A layering prepared for one bake (cuts in metres, stack per section, depths): read-only, shared by workers.</summary>
public sealed class LayerContext
{
    public LayerContext(HullLayering layering)
    {
        Source = layering;
        Cuts = layering.CutMetres();
        SectionStack = layering.SectionStacks(Cuts);
        Stacks = layering.Stacks.ToArray();
        Totals = Stacks.Select(s => (float)s.TotalMetres).ToArray();
        MaxDepth = Totals.Length > 0 ? Totals.Max() : 0f;
        RoomLayers = Stacks[Math.Min(layering.RoomStack, Stacks.Length - 1)];
        ComponentLayers = Stacks[Math.Min(layering.ComponentStack, Stacks.Length - 1)];
        RoomWall = (float)RoomLayers.TotalMetres;
        ComponentWall = (float)ComponentLayers.TotalMetres;

        for (var i = 0; i < SectionStack.Length; i++)
            SectionStack[i] = Math.Min(SectionStack[i], Stacks.Length - 1);
    }

    public readonly HullLayering Source;
    public readonly double[][] Cuts;
    public readonly int[] SectionStack;
    public readonly LayerStack[] Stacks;
    public readonly float[] Totals;
    public readonly float MaxDepth;
    public readonly LayerStack RoomLayers;
    public readonly LayerStack ComponentLayers;
    public readonly float RoomWall;
    public readonly float ComponentWall;
}
