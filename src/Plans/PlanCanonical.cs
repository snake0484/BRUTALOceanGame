using BrutalGame.Ships;
using BrutalGame.Ships.Components;

namespace BrutalGame.Plans;

/// <summary>
/// Puts equivalent designs into one byte form so they hash the same. Run after validation (it leaves invalid fields
/// alone). Server flow: decode, validate, canonicalise, encode, hash; the designer canonicalises on save.
/// - Rotation: the smallest snap triple naming the same rotation.
/// - Unused fields: Param is zero for every v1 shape; a cylinder's half Z equals its radius.
/// - Material table: only entries some solid uses, deduplicated, in order of first use; solid indices remapped.
/// - Mesh table: the same for meshes (deduplicated by content); a mesh solid's half extents are its mesh's extent.
/// - Layers: cuts sorted and deduplicated; assignments in order, one per section (the last wins), anchors moved to the
///   section's centre point; layer materials join the material table.
/// Solid order and ids are design (evaluation order, identity) and are kept.
/// </summary>
public static class PlanCanonical
{
    public static void Canonicalise(PlanDocument doc)
    {
        var solids = doc.Solids.AsSpan();

        foreach (ref var solid in solids)
        {
            solid.Rotation = SolidRotation.Canonical(solid.Rotation);

            if (solid.Shape == SolidShape.Mesh)
            {
                if (solid.Param < doc.Meshes.Count)
                    (solid.HalfX, solid.HalfY, solid.HalfZ) = doc.Meshes[solid.Param].HalfUnits();

                continue;
            }

            if (!SolidGeometry.IsSupported(solid.Shape))
                continue;

            solid.Param = 0;

            if (solid.Shape == SolidShape.Cylinder)
                solid.HalfZ = solid.HalfX;
        }

        if (doc.Brushes is { IsEmpty: true })
            doc.Brushes = null;

        if (doc.Components is { IsEmpty: true })
            doc.Components = null;

        // Room walls of solids that are gone are dropped; the rest go in solid id order.
        if (doc.Interior is { RoomWalls.Count: > 0 } walls)
        {
            walls.RoomWalls.RemoveAll(w => doc.Solids.IndexOf(w.Solid) < 0);
            walls.RoomWalls.Sort((a, b) => a.Solid.CompareTo(b.Solid));
        }

        if (doc.Interior is { IsEmpty: true })
            doc.Interior = null;

        if (doc.Windows is { IsEmpty: true })
            doc.Windows = null;

        if (doc.Decals is { IsEmpty: true })
            doc.Decals = null;

        if (doc.Openings is { IsEmpty: true })
            doc.Openings = null;

        // Members of solids or joints that are gone are dropped; the rest go in solid id order.
        if (doc.Joints is { } joints)
        {
            joints.Members.RemoveAll(m => doc.Solids.IndexOf(m.Solid) < 0 || joints.IndexOf(m.Joint) < 0);
            joints.Members.Sort((a, b) => a.Solid.CompareTo(b.Solid));

            if (joints.IsEmpty)
                doc.Joints = null;
        }

        // Strokes' own paints for strokes that are gone are dropped; the rest go in stroke id order.
        if (doc.Paint is { } paint)
        {
            paint.Strokes.RemoveAll(s => doc.Brushes is not { } set || set.IndexOfStroke(s.Stroke) < 0);
            paint.Strokes.Sort((a, b) => a.Stroke.CompareTo(b.Stroke));

            if (paint.IsEmpty)
                doc.Paint = null;
        }

        // Prefabs no placement uses are dropped.
        var used = new HashSet<ulong>();

        if (doc.Components is not null)
        {
            foreach (var p in doc.Components.Placements)
                used.Add(p.Prefab);
        }

        foreach (var hash in doc.Prefabs.Keys.Where(h => !used.Contains(h)).ToList())
            doc.Prefabs.Remove(hash);

        // A prefab read from an older format re-encodes to new bytes: its key (and the placements') follow them.
        foreach (var (key, prefab) in doc.Prefabs.ToList())
        {
            var hash = PlanFormat.HashBody(prefab).Value;

            if (hash == key)
                continue;

            doc.Prefabs.Remove(key);
            doc.Prefabs[hash] = prefab;

            for (var i = 0; i < doc.Components!.Placements.Count; i++)
            {
                var placement = doc.Components.Placements[i];

                if (placement.Prefab == key)
                    doc.Components.Placements[i] = placement with { Prefab = hash };
            }
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
            int ix = HullLayering.Cell(metres[0], a.AnchorX * HullSolid.MetresPerPositionUnit);
            int iy = HullLayering.Cell(metres[1], a.AnchorY * HullSolid.MetresPerPositionUnit);
            int iz = HullLayering.Cell(metres[2], a.AnchorZ * HullSolid.MetresPerPositionUnit);
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
        return (int)Math.Round((lo + hi) * 0.5 * HullSolid.PositionUnitsPerMetre);
    }

    private static void CompactMeshes(PlanDocument doc)
    {
        var old = doc.Meshes;
        var remap = new int[old.Count];
        Array.Fill(remap, -1);
        var table = new List<HullMesh>(old.Count);

        foreach (ref var solid in doc.Solids.AsSpan())
        {
            if (solid.Shape != SolidShape.Mesh || solid.Param >= old.Count)
                continue;

            if (remap[solid.Param] < 0)
            {
                var mesh = old[solid.Param];
                var existing = table.FindIndex(m => m.ContentEquals(mesh));

                if (existing < 0)
                {
                    existing = table.Count;
                    table.Add(mesh);
                }

                remap[solid.Param] = existing;
            }

            solid.Param = (ushort)remap[solid.Param];
        }

        // Stamp brushes keep their meshes (after the solids' ones).
        if (doc.Brushes is not null)
        {
            foreach (var brush in doc.Brushes.Brushes)
            {
                if (brush.Kind != BrushKind.Stamp || brush.Mesh >= old.Count)
                    continue;

                if (remap[brush.Mesh] < 0)
                {
                    var mesh = old[brush.Mesh];
                    var existing = table.FindIndex(m => m.ContentEquals(mesh));

                    if (existing < 0)
                    {
                        existing = table.Count;
                        table.Add(mesh);
                    }

                    remap[brush.Mesh] = existing;
                }

                brush.Mesh = (ushort)remap[brush.Mesh];
            }
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

        foreach (ref var solid in doc.Solids.AsSpan())
        {
            if (solid.Material >= old.Count)
                continue;

            if (remap[solid.Material] < 0)
            {
                var id = old[solid.Material];
                var existing = table.IndexOf(id);

                if (existing < 0)
                {
                    existing = table.Count;
                    table.Add(id);
                }

                remap[solid.Material] = existing;
            }

            solid.Material = (byte)remap[solid.Material];
        }

        // Layer materials keep their entries too (after the solids' ones).
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

        // Part and pipe materials too.
        if (doc.Components is not null)
        {
            for (var i = 0; i < doc.Components.Parts.Count; i++)
            {
                var part = doc.Components.Parts[i];
                part.Material = Keep(part.Material);
                doc.Components.Parts[i] = part;
            }

            foreach (var pipe in doc.Components.Pipes)
                pipe.Material = Keep(pipe.Material);
        }

        // Hallway (stair) materials too.
        if (doc.Interior is not null)
        {
            foreach (var hallway in doc.Interior.Hallways)
                hallway.Material = Keep(hallway.Material);

            // Profiles no hallway uses stay (they are the plan's own library); nothing to remap.
        }

        // Window (pane) materials too.
        if (doc.Windows is not null)
        {
            foreach (var window in doc.Windows.Windows)
                window.Material = Keep(window.Material);
        }

        byte Keep(byte material)
        {
            if (material >= old.Count)
                return material;

            if (remap[material] < 0)
            {
                var id = old[material];
                var existing = table.IndexOf(id);

                if (existing < 0)
                {
                    existing = table.Count;
                    table.Add(id);
                }

                remap[material] = existing;
            }

            return (byte)remap[material];
        }

        // Stroke materials too.
        if (doc.Brushes is not null)
        {
            foreach (var stroke in doc.Brushes.Strokes)
            {
                if (stroke.Material >= old.Count)
                    continue;

                if (remap[stroke.Material] < 0)
                {
                    var id = old[stroke.Material];
                    var existing = table.IndexOf(id);

                    if (existing < 0)
                    {
                        existing = table.Count;
                        table.Add(id);
                    }

                    remap[stroke.Material] = existing;
                }

                stroke.Material = (byte)remap[stroke.Material];
            }
        }

        old.Clear();
        old.AddRange(table);
    }
}
