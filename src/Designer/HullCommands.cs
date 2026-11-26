using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// The hull tool's commands, each one undo group of brush-list ops. Brush ids come from the document (monotonic, never
/// reused, so a redone insert gets its old id back). New and edited brushes are snapped by the session's settings.
/// With mirror planes on (<see cref="Symmetry"/>), each command also adds, edits or removes the brush's mirror partners
/// in the same group.
/// </summary>
public static class HullCommands
{
    public const double MinWallMetres = 0.5;

    /// <summary>A new brush at the end of the list, selected. Returns its id.</summary>
    public static uint AddBrush(DesignSession session, BrushShape shape, BrushOp op, int posX, int posY, int posZ, ushort halfX, ushort halfY, ushort halfZ, uint rotation, DefId material)
    {
        uint id = 0;

        session.Edit(s =>
        {
            var brush = new HullBrush
            {
                Shape = shape,
                Op = op,
                Material = s.EnsureMaterial(material),
                PosX = posX,
                PosY = posY,
                PosZ = posZ,
                Rotation = rotation,
                HalfX = halfX,
                HalfY = halfY,
                HalfZ = halfZ,
            };
            brush = s.Snap.SnapBrush(brush);
            brush.Id = s.Doc.AllocateId();
            id = brush.Id;
            s.Apply(PlanOp.Insert(s.Doc.Brushes.Count, brush));

            foreach (var (_, mirrored, mesh) in s.Symmetry.Reflections(s.Doc, brush))
                PlaceMirror(s, mirrored, mesh, s.Doc.Brushes.Count);
        });

        session.Selection.Set(id);
        return id;
    }

    /// <summary>Replaces brush <paramref name="id"/> with <paramref name="after"/> (id kept), optionally snapped. False if missing or unchanged.</summary>
    public static bool SetBrush(DesignSession session, uint id, HullBrush after, bool snap = true)
    {
        var index = session.Doc.Brushes.IndexOf(id);

        if (index < 0)
            return false;

        var before = session.Doc.Brushes[index];
        after.Id = id;

        if (snap)
            after = session.Snap.SnapBrush(after);

        if (after == before)
            return false;

        session.Edit(s => Modify(s, index, after));
        return true;
    }

    /// <summary>Inside a group: brush <paramref name="index"/> becomes <paramref name="after"/>, its mirror partners its reflections.</summary>
    public static void Modify(DesignSession s, int index, HullBrush after)
    {
        var before = s.Doc.Brushes[index];
        var partners = s.Symmetry.Partners(s.Doc, before);
        s.Apply(PlanOp.Modify(index, before, after));
        var mesh = Symmetry.MeshOf(s.Doc, after);

        foreach (var (mask, partnerId) in partners)
        {
            var partnerIndex = s.Doc.Brushes.IndexOf(partnerId);
            var partner = s.Doc.Brushes[partnerIndex];

            if (!BrushMirror.Mirror(after, mask, mesh, out var mirrored, out _))
                continue;

            mirrored.Id = partner.Id;

            if (mirrored.Shape == BrushShape.Mesh)
                mirrored.Param = partner.Param;

            if (mirrored != partner)
                s.Apply(PlanOp.Modify(partnerIndex, partner, mirrored));
        }
    }

    /// <summary>Inserts a reflection as a new brush at <paramref name="index"/>; a mesh reflection reuses an equal mesh entry or adds one.</summary>
    public static uint PlaceMirror(DesignSession s, HullBrush mirrored, HullMesh? mesh, int index)
    {
        if (mirrored.Shape == BrushShape.Mesh && mesh is not null)
        {
            var slot = s.Doc.Meshes.FindIndex(m => m.ContentEquals(mesh));

            if (slot < 0)
            {
                slot = s.Doc.Meshes.Count;
                s.Apply(PlanOp.AddMesh(slot, mesh));
            }

            mirrored.Param = (ushort)slot;
        }

        mirrored.Id = s.Doc.AllocateId();
        s.Apply(PlanOp.Insert(index, mirrored));
        return mirrored.Id;
    }

    public static bool SetMaterial(DesignSession session, uint id, DefId material)
    {
        var index = session.Doc.Brushes.IndexOf(id);

        if (index < 0)
            return false;

        var changed = false;

        session.Edit(s =>
        {
            var before = s.Doc.Brushes[index];
            var after = before;
            after.Material = s.EnsureMaterial(material);

            if (after == before)
                return;

            Modify(s, index, after);
            changed = true;
        });

        return changed;
    }

    /// <summary>Flips Add and Subtract.</summary>
    public static bool ToggleOp(DesignSession session, uint id)
    {
        if (!session.TryGetBrush(id, out var brush) || brush.Op is not (BrushOp.Add or BrushOp.Subtract))
            return false;

        brush.Op = brush.Op == BrushOp.Add ? BrushOp.Subtract : BrushOp.Add;
        return SetBrush(session, id, brush, snap: false);
    }

    /// <summary>Removes every selected brush, and their mirror partners, in one group.</summary>
    public static int DeleteSelected(DesignSession session)
    {
        var removed = 0;

        session.Edit(s =>
        {
            var ids = new List<uint>();

            foreach (var id in s.Selection.Ids)
            {
                if (!s.TryGetBrush(id, out var brush))
                    continue;

                if (!ids.Contains(id))
                    ids.Add(id);

                foreach (var (_, partner) in s.Symmetry.Partners(s.Doc, brush))
                {
                    if (!ids.Contains(partner))
                        ids.Add(partner);
                }
            }

            foreach (var id in ids)
            {
                var index = s.Doc.Brushes.IndexOf(id);

                if (index < 0)
                    continue;

                s.Apply(PlanOp.Remove(index, s.Doc.Brushes[index]));
                removed++;
            }
        });

        session.Selection.Clear();
        return removed;
    }

    /// <summary>Copies brush <paramref name="id"/> right after itself, offset by one grid step on X; selects the copy.</summary>
    public static uint Duplicate(DesignSession session, uint id)
    {
        var index = session.Doc.Brushes.IndexOf(id);

        if (index < 0)
            return 0;

        uint copyId = 0;

        session.Edit(s =>
        {
            var source = s.Doc.Brushes[index];
            var partners = s.Symmetry.Partners(s.Doc, source);
            var copy = source;
            copy.Id = s.Doc.AllocateId();
            copy.PosX += Math.Max(s.Snap.PositionStep, HullBrush.PositionUnitsPerMetre);
            copyId = copy.Id;
            s.Apply(PlanOp.Insert(index + 1, copy));
            MirrorAfterPartners(s, copy, partners);
        });

        session.Selection.Set(copyId);
        return copyId;
    }

    /// <summary>
    /// "Cut the interior" (ship-hulls Hollow): a Subtract brush flagged Interior right after an Add box or cylinder,
    /// inset by <paramref name="wallMetres"/> (≥ 0.5 m) on every side. Returns its id, or 0 if the brush is too thin.
    /// </summary>
    public static uint Hollow(DesignSession session, uint id, double wallMetres)
    {
        var index = session.Doc.Brushes.IndexOf(id);

        if (index < 0)
            return 0;

        var source = session.Doc.Brushes[index];

        if (source.Op != BrushOp.Add || source.Shape is not (BrushShape.Box or BrushShape.Cylinder))
            return 0;

        var wall = (int)Math.Round(Math.Max(wallMetres, MinWallMetres) * HullBrush.HalfUnitsPerMetre);
        var hx = source.HalfX - wall;
        var hy = source.HalfY - wall;
        var hz = source.HalfZ - wall;

        if (hx < PlanLimits.MinHalfUnits || hy < PlanLimits.MinHalfUnits || (source.Shape == BrushShape.Box && hz < PlanLimits.MinHalfUnits))
            return 0;

        uint innerId = 0;

        session.Edit(s =>
        {
            var inner = source;
            inner.Id = s.Doc.AllocateId();
            inner.Op = BrushOp.Subtract;
            inner.Flags |= BrushFlags.Interior;
            inner.HalfX = (ushort)hx;
            inner.HalfY = (ushort)hy;
            inner.HalfZ = (ushort)Math.Max(hz, PlanLimits.MinHalfUnits);
            innerId = inner.Id;
            var partners = s.Symmetry.Partners(s.Doc, source);
            s.Apply(PlanOp.Insert(index + 1, inner));
            MirrorAfterPartners(s, inner, partners);
        });

        session.Selection.Set(innerId);
        return innerId;
    }

    /// <summary>Inserts <paramref name="brush"/>'s reflection right after each partner of its source (Duplicate, Hollow).</summary>
    private static void MirrorAfterPartners(DesignSession s, in HullBrush brush, List<(int Mask, uint Id)> partners)
    {
        var mesh = Symmetry.MeshOf(s.Doc, brush);

        foreach (var (mask, partnerId) in partners)
        {
            if (BrushMirror.Mirror(brush, mask, mesh, out var mirrored, out var mirroredMesh))
                PlaceMirror(s, mirrored, mirroredMesh, s.Doc.Brushes.IndexOf(partnerId) + 1);
        }
    }

    /// <summary>Moves a brush one place earlier (-1) or later (+1) in evaluation order.</summary>
    public static bool Move(DesignSession session, uint id, int delta)
    {
        var index = session.Doc.Brushes.IndexOf(id);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= session.Doc.Brushes.Count)
            return false;

        session.Edit(s =>
        {
            var brush = s.Doc.Brushes[index];
            s.Apply(PlanOp.Remove(index, brush));
            s.Apply(PlanOp.Insert(target, brush));
        });

        return true;
    }

    /// <summary>Rotates by whole 15 degree steps about one Euler axis (0 yaw, 1 pitch, 2 roll).</summary>
    public static bool Rotate(DesignSession session, uint id, int axis, int steps)
    {
        if (!session.TryGetBrush(id, out var brush))
            return false;

        brush.Rotation = BrushRotation.AddSteps(brush.Rotation, axis, steps);
        return SetBrush(session, id, brush, snap: false);
    }
}
