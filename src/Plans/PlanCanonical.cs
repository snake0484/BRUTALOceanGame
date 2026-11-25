using BrutalGame.Ships;

namespace BrutalGame.Plans;

/// <summary>
/// Puts equivalent designs into one byte form so they hash the same. Run after validation (it leaves invalid fields
/// alone). Server flow: decode, validate, canonicalise, encode, hash; the designer canonicalises on save.
/// - Rotation: the smallest snap triple naming the same rotation.
/// - Unused fields: Param is zero for every v1 shape; a cylinder's half Z equals its radius.
/// - Material table: only entries some brush uses, deduplicated, in order of first use; brush indices remapped.
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

            if (!BrushGeometry.IsSupported(brush.Shape))
                continue;

            brush.Param = 0;

            if (brush.Shape == BrushShape.Cylinder)
                brush.HalfZ = brush.HalfX;
        }

        CompactMaterials(doc);
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
