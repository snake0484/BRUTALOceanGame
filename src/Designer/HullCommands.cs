using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>
/// The hull tool's commands, each one undo group of solid-list ops. Solid ids come from the document (monotonic, never
/// reused, so a redone insert gets its old id back). New and edited solids are snapped by the session's settings.
/// With mirror planes on (<see cref="Symmetry"/>), each command also adds, edits or removes the solid's mirror partners
/// in the same group.
/// </summary>
public static class HullCommands
{
    public const double MinWallMetres = 0.5;

    /// <summary>A new solid at the end of the list, selected. Returns its id.</summary>
    public static uint AddSolid(DesignSession session, SolidShape shape, SolidOp op, int posX, int posY, int posZ, ushort halfX, ushort halfY, ushort halfZ, uint rotation, DefId material)
    {
        uint id = 0;

        session.Edit(s =>
        {
            var solid = new HullSolid
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
            solid = s.Snap.SnapSolid(solid);
            solid.Id = s.Doc.AllocateId();
            id = solid.Id;
            s.Apply(PlanOp.Insert(s.Doc.Solids.Count, solid));

            foreach (var (_, mirrored, mesh) in s.Symmetry.Reflections(s.Doc, solid))
                PlaceMirror(s, mirrored, mesh, s.Doc.Solids.Count);
        });

        session.Selection.Set(id);
        return id;
    }

    /// <summary>Replaces solid <paramref name="id"/> with <paramref name="after"/> (id kept), optionally snapped. False if missing or unchanged.</summary>
    public static bool SetSolid(DesignSession session, uint id, HullSolid after, bool snap = true)
    {
        var index = session.Doc.Solids.IndexOf(id);

        if (index < 0)
            return false;

        var before = session.Doc.Solids[index];
        after.Id = id;

        if (snap)
            after = session.Snap.SnapSolid(after);

        if (after == before)
            return false;

        session.Edit(s => Modify(s, index, after));
        return true;
    }

    /// <summary>Inside a group: solid <paramref name="index"/> becomes <paramref name="after"/>, its mirror partners its reflections.</summary>
    public static void Modify(DesignSession s, int index, HullSolid after)
    {
        var before = s.Doc.Solids[index];
        var partners = s.Symmetry.Partners(s.Doc, before);
        s.Apply(PlanOp.Modify(index, before, after));
        var mesh = Symmetry.MeshOf(s.Doc, after);

        foreach (var (mask, partnerId) in partners)
        {
            var partnerIndex = s.Doc.Solids.IndexOf(partnerId);
            var partner = s.Doc.Solids[partnerIndex];

            if (!SolidMirror.Mirror(after, mask, mesh, out var mirrored, out _))
                continue;

            mirrored.Id = partner.Id;

            if (mirrored.Shape == SolidShape.Mesh)
                mirrored.Param = partner.Param;

            if (mirrored != partner)
                s.Apply(PlanOp.Modify(partnerIndex, partner, mirrored));
        }
    }

    /// <summary>Inserts a reflection as a new solid at <paramref name="index"/>; a mesh reflection reuses an equal mesh entry or adds one.</summary>
    public static uint PlaceMirror(DesignSession s, HullSolid mirrored, HullMesh? mesh, int index)
    {
        if (mirrored.Shape == SolidShape.Mesh && mesh is not null)
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
        var index = session.Doc.Solids.IndexOf(id);

        if (index < 0)
            return false;

        var changed = false;

        session.Edit(s =>
        {
            var before = s.Doc.Solids[index];
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
        if (!session.TryGetSolid(id, out var solid) || solid.Op is not (SolidOp.Add or SolidOp.Subtract))
            return false;

        solid.Op = solid.Op == SolidOp.Add ? SolidOp.Subtract : SolidOp.Add;
        return SetSolid(session, id, solid, snap: false);
    }

    /// <summary>Removes every selected solid, and their mirror partners, in one group.</summary>
    public static int DeleteSelected(DesignSession session)
    {
        var removed = 0;

        session.Edit(s =>
        {
            var ids = new List<uint>();

            foreach (var id in s.Selection.Ids)
            {
                if (!s.TryGetSolid(id, out var solid))
                    continue;

                if (!ids.Contains(id))
                    ids.Add(id);

                foreach (var (_, partner) in s.Symmetry.Partners(s.Doc, solid))
                {
                    if (!ids.Contains(partner))
                        ids.Add(partner);
                }
            }

            foreach (var id in ids)
            {
                var index = s.Doc.Solids.IndexOf(id);

                if (index < 0)
                    continue;

                s.Apply(PlanOp.Remove(index, s.Doc.Solids[index]));
                removed++;
            }
        });

        session.Selection.Clear();
        return removed;
    }

    /// <summary>Copies solid <paramref name="id"/> right after itself, offset by one grid step on X; selects the copy.</summary>
    public static uint Duplicate(DesignSession session, uint id)
    {
        var index = session.Doc.Solids.IndexOf(id);

        if (index < 0)
            return 0;

        uint copyId = 0;

        session.Edit(s =>
        {
            var source = s.Doc.Solids[index];
            var partners = s.Symmetry.Partners(s.Doc, source);
            var copy = source;
            copy.Id = s.Doc.AllocateId();
            copy.PosX += Math.Max(s.Snap.PositionStep, HullSolid.PositionUnitsPerMetre);
            copyId = copy.Id;
            s.Apply(PlanOp.Insert(index + 1, copy));
            MirrorAfterPartners(s, copy, partners);
        });

        session.Selection.Set(copyId);
        return copyId;
    }

    /// <summary>
    /// "Cut the interior" (ship-hulls Hollow): a Subtract solid flagged Interior right after an Add box or cylinder,
    /// inset by <paramref name="wallMetres"/> (≥ 0.5 m) on every side. Returns its id, or 0 if the solid is too thin.
    /// </summary>
    public static uint Hollow(DesignSession session, uint id, double wallMetres)
    {
        var index = session.Doc.Solids.IndexOf(id);

        if (index < 0)
            return 0;

        var source = session.Doc.Solids[index];

        if (source.Op != SolidOp.Add || source.Shape is not (SolidShape.Box or SolidShape.Cylinder))
            return 0;

        var wall = (int)Math.Round(Math.Max(wallMetres, MinWallMetres) * HullSolid.HalfUnitsPerMetre);
        var hx = source.HalfX - wall;
        var hy = source.HalfY - wall;
        var hz = source.HalfZ - wall;

        if (hx < PlanLimits.MinHalfUnits || hy < PlanLimits.MinHalfUnits || (source.Shape == SolidShape.Box && hz < PlanLimits.MinHalfUnits))
            return 0;

        uint innerId = 0;

        session.Edit(s =>
        {
            var inner = source;
            inner.Id = s.Doc.AllocateId();
            inner.Op = SolidOp.Subtract;
            inner.Flags |= SolidFlags.Interior;
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

    /// <summary>Inserts <paramref name="solid"/>'s reflection right after each partner of its source (Duplicate, Hollow).</summary>
    private static void MirrorAfterPartners(DesignSession s, in HullSolid solid, List<(int Mask, uint Id)> partners)
    {
        var mesh = Symmetry.MeshOf(s.Doc, solid);

        foreach (var (mask, partnerId) in partners)
        {
            if (SolidMirror.Mirror(solid, mask, mesh, out var mirrored, out var mirroredMesh))
                PlaceMirror(s, mirrored, mirroredMesh, s.Doc.Solids.IndexOf(partnerId) + 1);
        }
    }

    /// <summary>Moves a solid one place earlier (-1) or later (+1) in evaluation order.</summary>
    public static bool Move(DesignSession session, uint id, int delta)
    {
        var index = session.Doc.Solids.IndexOf(id);
        var target = index + delta;

        if (index < 0 || target < 0 || target >= session.Doc.Solids.Count)
            return false;

        session.Edit(s =>
        {
            var solid = s.Doc.Solids[index];
            s.Apply(PlanOp.Remove(index, solid));
            s.Apply(PlanOp.Insert(target, solid));
        });

        return true;
    }

    /// <summary>Rotates by whole 15 degree steps about one Euler axis (0 yaw, 1 pitch, 2 roll).</summary>
    public static bool Rotate(DesignSession session, uint id, int axis, int steps)
    {
        if (!session.TryGetSolid(id, out var solid))
            return false;

        solid.Rotation = SolidRotation.AddSteps(solid.Rotation, axis, steps);
        return SetSolid(session, id, solid, snap: false);
    }
}
