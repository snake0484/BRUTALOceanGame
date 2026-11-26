using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>One section of the current cuts: its cell per axis, a point inside it (1/1024 m) and a label.</summary>
public readonly record struct SectionInfo(int Id, int X, int Y, int Z, int AnchorX, int AnchorY, int AnchorZ, string Label);

/// <summary>
/// Layered hull commands (skin, sections, depth stacks, roles), each one undo group. The layering is replaced whole on
/// every edit (a <see cref="OpKind.SetLayering"/> op holding before and after copies), so undo restores it exactly.
/// </summary>
public static class LayerCommands
{
    public const ushort DefaultOuterMm = 250;
    public const ushort DefaultInnerMm = 500;
    public const ushort DefaultWallMm = 250;
    private const double OutsideMetres = 16.0;

    public static string LastError { get; private set; } = "";

    /// <summary>Turns a solid brush hull into a layered one: a hull stack (outer, inner) and a wall stack for rooms.</summary>
    public static bool Enable(DesignSession session, DefId outer, DefId inner)
    {
        if (session.Doc.Layers is not null)
            return false;

        session.Edit(s =>
        {
            var layers = new HullLayering { DefaultStack = 0, RoomStack = 1, ComponentStack = 1 };
            var hull = new LayerStack();
            hull.Layers.Add(new HullLayer(s.EnsureMaterial(outer), DefaultOuterMm));
            hull.Layers.Add(new HullLayer(s.EnsureMaterial(inner), DefaultInnerMm));
            var walls = new LayerStack();
            walls.Layers.Add(new HullLayer(s.EnsureMaterial(inner), DefaultWallMm));
            layers.Stacks.Add(hull);
            layers.Stacks.Add(walls);
            s.Apply(PlanOp.SetLayering(null, layers));
        });

        return true;
    }

    /// <summary>Back to a solid brush hull; refused while any brush is a room, component or panel.</summary>
    public static bool Disable(DesignSession session)
    {
        if (session.Doc.Layers is null)
            return false;

        foreach (ref readonly var brush in session.Doc.Brushes.AsSpan())
        {
            if (BrushRoles.Of(brush) != BrushRole.Skin)
            {
                LastError = "Set every room, component and panel back to skin first";
                return false;
            }
        }

        var before = session.Doc.Layers;
        session.Edit(s => s.Apply(PlanOp.SetLayering(before, null)));
        return true;
    }

    /// <summary>Edits a copy of the layering; <paramref name="change"/> returns false to cancel. One undo group.</summary>
    public static bool Change(DesignSession session, Func<DesignSession, HullLayering, bool> change)
    {
        var before = session.Doc.Layers;

        if (before is null)
            return false;

        var changed = false;

        session.Edit(s =>
        {
            var after = before.Clone();

            if (!change(s, after) || after.ToBytes().AsSpan().SequenceEqual(before.ToBytes()))
                return;

            s.Apply(PlanOp.SetLayering(before, after));
            changed = true;
        });

        return changed;
    }

    public static bool SetLayer(DesignSession session, int stack, int layer, DefId material, ushort thicknessMm) =>
        Change(session, (s, l) =>
        {
            if (stack >= l.Stacks.Count || layer >= l.Stacks[stack].Layers.Count)
                return false;

            l.Stacks[stack].Layers[layer] = new HullLayer(s.EnsureMaterial(material), Math.Max((ushort)1, thicknessMm));
            return true;
        });

    public static bool AddLayer(DesignSession session, int stack, DefId material) =>
        Change(session, (s, l) =>
        {
            if (stack >= l.Stacks.Count || l.Stacks[stack].Layers.Count >= HullLayering.MaxLayers)
                return false;

            l.Stacks[stack].Layers.Add(new HullLayer(s.EnsureMaterial(material), DefaultWallMm));
            return true;
        });

    public static bool RemoveLayer(DesignSession session, int stack, int layer) =>
        Change(session, (_, l) =>
        {
            if (stack >= l.Stacks.Count || layer >= l.Stacks[stack].Layers.Count || l.Stacks[stack].Layers.Count == 1)
                return false;

            l.Stacks[stack].Layers.RemoveAt(layer);
            return true;
        });

    public static bool AddStack(DesignSession session, DefId material) =>
        Change(session, (s, l) =>
        {
            if (l.Stacks.Count >= HullLayering.MaxStacks)
                return false;

            var stack = new LayerStack();
            stack.Layers.Add(new HullLayer(s.EnsureMaterial(material), DefaultInnerMm));
            l.Stacks.Add(stack);
            return true;
        });

    /// <summary>Which stack the default sections, rooms or components use (0, 1, 2).</summary>
    public static bool SetRoleStack(DesignSession session, int which, int stack) =>
        Change(session, (_, l) =>
        {
            if (stack >= l.Stacks.Count)
                return false;

            if (which == 0)
                l.DefaultStack = (byte)stack;
            else if (which == 1)
                l.RoomStack = (byte)stack;
            else
                l.ComponentStack = (byte)stack;

            return true;
        });

    public static bool AddCut(DesignSession session, int axis, int position) =>
        Change(session, (_, l) =>
        {
            if (l.Cuts.Count >= HullLayering.MaxCuts || l.Cuts.Contains(new SectionCut((byte)axis, position)))
                return false;

            l.Cuts.Add(new SectionCut((byte)axis, position));
            return true;
        });

    public static bool MoveCut(DesignSession session, int index, int position) =>
        Change(session, (_, l) =>
        {
            if (index >= l.Cuts.Count)
                return false;

            l.Cuts[index] = l.Cuts[index] with { Position = position };
            return true;
        });

    public static bool RemoveCut(DesignSession session, int index) =>
        Change(session, (_, l) =>
        {
            if (index >= l.Cuts.Count)
                return false;

            l.Cuts.RemoveAt(index);
            return true;
        });

    /// <summary>The section holding <paramref name="section"/>'s anchor uses <paramref name="stack"/>.</summary>
    public static bool AssignSection(DesignSession session, SectionInfo section, int stack) =>
        Change(session, (_, l) =>
        {
            if (stack >= l.Stacks.Count)
                return false;

            l.Assignments.Add(new SectionStack(section.AnchorX, section.AnchorY, section.AnchorZ, (byte)stack));
            return true;
        });

    /// <summary>Every section of the current cuts, with a point inside each and its current stack.</summary>
    public static List<(SectionInfo Section, int Stack)> Sections(HullLayering layers)
    {
        var cuts = layers.CutMetres();
        var stacks = layers.SectionStacks(cuts);
        var result = new List<(SectionInfo, int)>();
        string[] names = ["X", "Y", "Z"];

        for (var iz = 0; iz <= cuts[2].Length; iz++)
            for (var iy = 0; iy <= cuts[1].Length; iy++)
                for (var ix = 0; ix <= cuts[0].Length; ix++)
                {
                    var id = HullLayering.SectionId(cuts, ix, iy, iz);
                    var label = string.Join(' ', new[] { (ix, cuts[0]), (iy, cuts[1]), (iz, cuts[2]) }
                        .Select((c, axis) => c.Item2.Length == 0 ? null : $"{names[axis]}{Range(c.Item2, c.Item1)}").Where(t => t is not null));
                    var info = new SectionInfo(id, ix, iy, iz, Centre(cuts[0], ix), Centre(cuts[1], iy), Centre(cuts[2], iz), label.Length == 0 ? "whole hull" : label);
                    result.Add((info, stacks[id]));
                }

        return result;
    }

    private static string Range(double[] cuts, int i) =>
        i == 0 ? $"<{cuts[0]:0.##}" : i == cuts.Length ? $">{cuts[^1]:0.##}" : $"{cuts[i - 1]:0.##}..{cuts[i]:0.##}";

    private static int Centre(double[] cuts, int i)
    {
        var lo = i == 0 ? (cuts.Length > 0 ? cuts[0] - OutsideMetres : -OutsideMetres) : cuts[i - 1];
        var hi = i == cuts.Length ? (cuts.Length > 0 ? cuts[^1] + OutsideMetres : OutsideMetres) : cuts[i];
        return (int)Math.Round((lo + hi) * 0.5 * HullBrush.PositionUnitsPerMetre);
    }

    /// <summary>Sets a brush's role (and its mirror partners'); roles need a layered hull.</summary>
    public static bool SetRole(DesignSession session, uint id, BrushRole role)
    {
        if (session.Doc.Layers is null && role != BrushRole.Skin)
        {
            LastError = "Turn on the layered hull first";
            return false;
        }

        if (!session.TryGetBrush(id, out var brush))
            return false;

        brush.Flags = BrushRoles.With(brush.Flags, role);
        return HullCommands.SetBrush(session, id, brush, snap: false);
    }
}
