using BrutalGame.Ships;

namespace BrutalGame.Plans;

/// <summary>
/// Puts equivalent designs into one byte form so they hash the same. Run after validation (it leaves invalid fields
/// alone). Server flow: decode, validate, canonicalise, encode, hash; the designer canonicalises on save.
/// - Rotation: the smallest snap triple naming the same rotation.
/// - Unused fields: Param is zero for every v1 shape; a cylinder's half Z equals its radius.
/// - Material table: only entries some brush uses, deduplicated, in order of first use; brush indices remapped.
/// - Mesh table: the same for meshes (deduplicated by content); a mesh brush's half extents are its mesh's extent.
/// - Layers: cuts sorted and deduplicated; assignments in order, one per section (the last wins), anchors moved to the
///   section's centre point; layer materials join the material table.
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
        CanonicaliseLayers(doc.Layers);
    }

    private static void CanonicaliseLayers(HullLayering? layers)
    {
        if (layers is null)
            return;

        var cuts = layers.Cuts.Distinct().OrderBy(c => c.Axis).ThenBy(c => c.Position).ToList();
        layers.Cuts.Clear();
        layers.Cuts.AddRange(cuts);
        var metres = layers.CutMetres();
        var bySection = new SortedDictionary<int, SectionStack>();

        foreach (var a in layers.Assignments)
        {
            int ix = HullLayering.Cell(metres[0], a.AnchorX * HullBrush.MetresPerPositionUnit);
            int iy = HullLayering.Cell(metres[1], a.AnchorY * HullBrush.MetresPerPositionUnit);
            int iz = HullLayering.Cell(metres[2], a.AnchorZ * HullBrush.MetresPerPositionUnit);
            bySection[HullLayering.SectionId(metres, ix, iy, iz)] = new SectionStack(Centre(metres[0], ix), Centre(metres[1], iy), Centre(metres[2], iz), a.Stack);
        }

        layers.Assignments.Clear();

        foreach (var a in bySection.Values)
        {
            if (a.Stack != layers.DefaultStack)
                layers.Assignments.Add(a);
        }
    }

    /// <summary>A point inside section cell <paramref name="i"/> along one axis, on the 1/1024 m grid.</summary>
    private static int Centre(double[] cuts, int i)
    {
        const double outside = 16.0;
        var lo = i == 0 ? (cuts.Length > 0 ? cuts[0] - outside : -outside) : cuts[i - 1];
        var hi = i == cuts.Length ? (cuts.Length > 0 ? cuts[^1] + outside : outside) : cuts[i];
        return (int)Math.Round((lo + hi) * 0.5 * HullBrush.PositionUnitsPerMetre);
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

        // Layer materials keep their entries too (after the brushes' ones).
        if (doc.Layers is not null)
        {
            foreach (var stack in doc.Layers.Stacks)
            {
                for (var i = 0; i < stack.Layers.Count; i++)
                {
                    var layer = stack.Layers[i];

                    if (layer.Material >= old.Count)
                        continue;

                    if (remap[layer.Material] < 0)
                    {
                        var id = old[layer.Material];
                        var existing = table.IndexOf(id);

                        if (existing < 0)
                        {
                            existing = table.Count;
                            table.Add(id);
                        }

                        remap[layer.Material] = existing;
                    }

                    stack.Layers[i] = layer with { Material = (byte)remap[layer.Material] };
                }
            }
        }

        old.Clear();
        old.AddRange(table);
    }
}
