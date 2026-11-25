using BrutalGame.Ships;

namespace BrutalGame.Plans;

/// <summary>
/// Puts equivalent designs into one byte form so they hash the same. Run after validation (it leaves invalid fields
/// alone). Server flow: decode, validate, canonicalise, encode, hash; the designer canonicalises on save.
/// - Rotation: the smallest snap triple naming the same rotation.
/// - Unused fields: Param is zero for every v1 shape; a cylinder's half Z equals its radius.
/// - Material table: only entries some brush uses, deduplicated, in order of first use; brush indices remapped.
/// - Mesh table: the same for meshes (deduplicated by content); a mesh brush's half extents are its mesh's extent.
/// Brush order and ids are design (evaluation order, identity) and are kept.
/// </summary>
public static class PlanCanonical
{
    public static void Canonicalise(PlanDocument doc)
    {
        var brushes = doc.Brushes.AsSpan();

        foreach (ref var brush in brushes)
        {
            brush.Rotation = BrushRotation.Canonical(brush.Rotation);

            if (brush.Shape == BrushShape.Mesh)
            {
                if (brush.Param < doc.Meshes.Count)
                    (brush.HalfX, brush.HalfY, brush.HalfZ) = doc.Meshes[brush.Param].HalfUnits();

                continue;
            }

            if (!BrushGeometry.IsSupported(brush.Shape))
                continue;

            brush.Param = 0;

            if (brush.Shape == BrushShape.Cylinder)
                brush.HalfZ = brush.HalfX;
        }

        CompactMaterials(doc);
        CompactMeshes(doc);
    }

    private static void CompactMeshes(PlanDocument doc)
    {
        var old = doc.Meshes;
        var remap = new int[old.Count];
        Array.Fill(remap, -1);
        var table = new List<HullMesh>(old.Count);

        foreach (ref var brush in doc.Brushes.AsSpan())
        {
            if (brush.Shape != BrushShape.Mesh || brush.Param >= old.Count)
                continue;

            if (remap[brush.Param] < 0)
            {
                var mesh = old[brush.Param];
                var existing = table.FindIndex(m => m.ContentEquals(mesh));

                if (existing < 0)
                {
                    existing = table.Count;
                    table.Add(mesh);
                }

                remap[brush.Param] = existing;
            }

            brush.Param = (ushort)remap[brush.Param];
        }

        old.Clear();
        old.AddRange(table);
    }

    private static void CompactMaterials(PlanDocument doc)
    {
        var old = doc.Materials;
        var remap = new int[PlanLimits.MaxMaterials];
        remap.AsSpan().Fill(-1);
        var table = new List<DefId>(old.Count);

        foreach (ref var brush in doc.Brushes.AsSpan())
        {
            if (brush.Material >= old.Count)
                continue;

            if (remap[brush.Material] < 0)
            {
                var id = old[brush.Material];
                var existing = table.IndexOf(id);

                if (existing < 0)
                {
                    existing = table.Count;
                    table.Add(id);
                }

                remap[brush.Material] = existing;
            }

            brush.Material = (byte)remap[brush.Material];
        }

        old.Clear();
        old.AddRange(table);
    }
}
