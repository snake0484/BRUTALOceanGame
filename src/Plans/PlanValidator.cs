using BrutalGame.Ships;
using BrutalGame.Ships.Components;

namespace BrutalGame.Plans;

public enum Severity : byte { Info, Warning, Error }

public enum IssueCode : ushort
{
    UnsupportedLevel,
    TooManySolids,
    TooManyMaterials,
    ZeroId,
    DuplicateId,
    UnsupportedShape,
    UnsupportedOp,
    UnsupportedFlags,
    BevelUnsupported,
    MaterialIndex,
    UnknownMaterial,
    InvalidRotation,
    ExtentTooSmall,
    OutOfBounds,
    BrickOverfull,
    NoSolid,
    TooManyMeshes,
    MeshIndex,
    MeshBounds,
    MeshInvalid,
    MeshBudget,
    LayerStacks,
    LayerThickness,
    LayerMaterial,
    LayerRefs,
    LayerCuts,
    RoleWithoutLayers,
    BrushCount,
    BrushMesh,
    BrushProfile,
    BrushSpacing,
    StrokeBrush,
    StrokeShape,
    StrokeMaterial,
    PartCount,
    PartShape,
    PartMaterial,
    PipeShape,
    PipeEnd,
    PipeMaterial,
    PlacementPrefab,
    PrefabLevel,
    ComponentSize,
}

public readonly record struct ValidationIssue(Severity Severity, IssueCode Code, ElementRef Element, int Value);

/// <summary>Issues of one validation pass, reused between passes.</summary>
public sealed class ValidationLog
{
    public readonly List<ValidationIssue> Issues = new();

    public int Errors { get; private set; }

    public int Warnings { get; private set; }

    public void Clear()
    {
        Issues.Clear();
        Errors = 0;
        Warnings = 0;
    }

    public void Add(Severity severity, IssueCode code, ElementRef element, int value = 0)
    {
        Issues.Add(new ValidationIssue(severity, code, element, value));

        if (severity == Severity.Error)
            Errors++;
        else if (severity == Severity.Warning)
            Warnings++;
    }
}

/// <summary>
/// Schema and geometry checks of a hull plan, the same on the designer (every commit) and the server (import and save,
/// where nothing from the client is trusted). Bounded by <see cref="PlanLimits"/>: counts are checked before any
/// per-solid work, and binning runs only when every solid is in bounds.
/// </summary>
public static class PlanValidator
{
    private const SolidFlags AllowedFlags = SolidFlags.Interior | SolidFlags.NoCollide | SolidFlags.Mirrored | SolidRoles.Mask;

    /// <summary>Stacks: 1..16, each 1..16 layers of known materials, 0.25..8 m in total; stack references in range; ≤ 64 cuts.</summary>
    private static void ValidateBrushes(PlanDocument doc, ValidationLog log)
    {
        var set = doc.Brushes;

        if (set is null)
            return;

        if (set.Brushes.Count > HullBrushSet.MaxBrushes || set.Strokes.Count > HullBrushSet.MaxStrokes)
            log.Add(Severity.Error, IssueCode.BrushCount, default, set.Brushes.Count);

        for (var i = 0; i < set.Brushes.Count; i++)
        {
            var brush = set.Brushes[i];

            if (brush.Kind == BrushKind.Stamp)
            {
                if (brush.Mesh >= doc.Meshes.Count)
                    log.Add(Severity.Error, IssueCode.BrushMesh, default, i);

                if (brush.Spacing < HullBrush.MinSpacing || brush.Spacing * HullSolid.MetresPerPositionUnit > PlanLimits.MaxReachMetres)
                    log.Add(Severity.Error, IssueCode.BrushSpacing, default, i);
            }
            else if (brush.Kind != BrushKind.Sweep || brush.Profile.Count is < HullBrush.MinProfilePoints or > HullBrush.MaxProfilePoints
                || brush.Profile.Any(p => Math.Abs(p.X) > HullBrush.MaxProfileUnits || Math.Abs(p.Y) > HullBrush.MaxProfileUnits) || ProfileArea(brush.Profile) == 0)
            {
                log.Add(Severity.Error, IssueCode.BrushProfile, default, i);
            }
        }

        foreach (var stroke in set.Strokes)
        {
            var element = new ElementRef(ElementKind.Solid, stroke.Id);

            if (stroke.Brush >= set.Brushes.Count)
                log.Add(Severity.Error, IssueCode.StrokeBrush, element, stroke.Brush);

            if (stroke.Points.Count is < HullStroke.MinPoints or > HullStroke.MaxPoints
                || stroke.Scale is < 100 or > 10000 || Math.Abs(stroke.Offset * HullSolid.MetresPerPositionUnit) > 16.0
                || stroke.Points.Any(p => (p.NX == 0 && p.NY == 0 && p.NZ == 0) || p.Position.Length() > PlanLimits.MaxReachMetres))
                log.Add(Severity.Error, IssueCode.StrokeShape, element, stroke.Points.Count);

            if (stroke.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.StrokeMaterial, element, stroke.Material);
        }
    }

    /// <summary>Parts, pipes and placements: counts, positive sizes, materials, pipe ends on real ports, placed prefabs present.</summary>
    private static void ValidateComponents(PlanDocument doc, ValidationLog log)
    {
        foreach (var (_, prefab) in doc.Prefabs)
        {
            if (prefab.Level != PlanLevel.Component)
                log.Add(Severity.Error, IssueCode.PrefabLevel, default, (int)prefab.Level);
        }

        var data = doc.Components;

        if (data is null)
            return;

        if (data.Parts.Count > ComponentData.MaxParts || data.Pipes.Count > ComponentData.MaxPipes || data.Placements.Count > ComponentData.MaxPlacements)
            log.Add(Severity.Error, IssueCode.PartCount, default, data.Parts.Count);

        var parts = new Dictionary<uint, ComponentPart>();

        foreach (var p in data.Parts)
        {
            var element = new ElementRef(ElementKind.Part, p.Id);
            parts[p.Id] = p;

            if (p.Kind > PartKind.Igniter || !SolidRotation.IsValid(p.Rotation) || p.P0 <= 0 || p.P1 <= 0 || (p.Kind is PartKind.Chamber or PartKind.Injector && p.P2 <= 0))
                log.Add(Severity.Error, IssueCode.PartShape, element, (int)p.Kind);
            else if (p.Kind == PartKind.Nozzle && !PartGeometry.Nozzle(p).IsValid)
                log.Add(Severity.Error, IssueCode.PartShape, element, (int)p.Kind);

            if (p.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.PartMaterial, element, p.Material);

            if (doc.Level == PlanLevel.Component && p.Position.Length() > PlanLimits.MaxComponentMetres)
                log.Add(Severity.Error, IssueCode.ComponentSize, element, 0);
        }

        foreach (var pipe in data.Pipes)
        {
            var element = new ElementRef(ElementKind.Route, pipe.Id);

            if (pipe.Nodes.Count is < 2 or > PipeRoute.MaxNodes || pipe.InnerMm == 0 || pipe.InnerMm >= pipe.OuterMm)
                log.Add(Severity.Error, IssueCode.PipeShape, element, pipe.Nodes.Count);

            foreach (var end in new[] { pipe.From, pipe.To })
            {
                if (!end.IsOpen && (!parts.TryGetValue(end.Part, out var part) || end.Port >= PartGeometry.Ports(part).Length))
                    log.Add(Severity.Error, IssueCode.PipeEnd, element, (int)end.Part);
            }

            if (pipe.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.PipeMaterial, element, pipe.Material);
        }

        foreach (var placement in data.Placements)
        {
            if (!doc.Prefabs.ContainsKey(placement.Prefab) || !SolidRotation.IsValid(placement.Rotation))
                log.Add(Severity.Error, IssueCode.PlacementPrefab, new ElementRef(ElementKind.Placement, placement.Id), 0);
        }
    }

    private static long ProfileArea(List<ProfilePoint> profile)
    {
        var area = 0L;

        for (var j = 0; j < profile.Count; j++)
        {
            var a = profile[j];
            var b = profile[(j + 1) % profile.Count];
            area += (long)a.X * b.Y - (long)b.X * a.Y;
        }

        return area;
    }

    private static void ValidateLayers(PlanDocument doc, ValidationLog log)
    {
        var layers = doc.Layers;

        if (layers is null)
            return;

        if (layers.Stacks.Count is 0 or > HullLayering.MaxStacks)
        {
            log.Add(Severity.Error, IssueCode.LayerStacks, default, layers.Stacks.Count);
            return;
        }

        for (var s = 0; s < layers.Stacks.Count; s++)
        {
            var stack = layers.Stacks[s];
            var mm = stack.Layers.Sum(l => l.ThicknessMm);

            if (stack.Layers.Count is 0 or > HullLayering.MaxLayers || mm < HullLayering.MinStackMm || mm > HullLayering.MaxStackMm || stack.Layers.Any(l => l.ThicknessMm == 0))
                log.Add(Severity.Error, IssueCode.LayerThickness, default, s);

            if (stack.Layers.Any(l => l.Material >= doc.Materials.Count))
                log.Add(Severity.Error, IssueCode.LayerMaterial, default, s);
        }

        var count = layers.Stacks.Count;

        if (layers.DefaultStack >= count || layers.RoomStack >= count || layers.ComponentStack >= count || layers.Assignments.Any(a => a.Stack >= count))
            log.Add(Severity.Error, IssueCode.LayerRefs, default);

        if (layers.Cuts.Count > HullLayering.MaxCuts || layers.Cuts.Any(c => c.Axis > 2 || Math.Abs(c.Position * HullSolid.MetresPerPositionUnit) > PlanLimits.MaxReachMetres))
            log.Add(Severity.Error, IssueCode.LayerCuts, default, layers.Cuts.Count);
    }

    /// <param name="catalog">Known materials; null skips the check (a file opened without data).</param>
    public static void Validate(PlanDocument doc, ValidationLog log, HullMaterialCatalog? catalog = null, HullBins? bins = null)
    {
        log.Clear();

        if (doc.Level is not (PlanLevel.Ship or PlanLevel.HullChunk or PlanLevel.Component))
        {
            log.Add(Severity.Error, IssueCode.UnsupportedLevel, default, (int)doc.Level);
            return;
        }

        var solids = doc.Solids.AsSpan();

        if (solids.Length > PlanLimits.MaxSolids)
        {
            log.Add(Severity.Error, IssueCode.TooManySolids, default, solids.Length);
            return;
        }

        if (doc.Materials.Count > PlanLimits.MaxMaterials)
        {
            log.Add(Severity.Error, IssueCode.TooManyMaterials, default, doc.Materials.Count);
            return;
        }

        if (doc.Meshes.Count > PlanLimits.MaxMeshes)
        {
            log.Add(Severity.Error, IssueCode.TooManyMeshes, default, doc.Meshes.Count);
            return;
        }

        var meshValid = new bool[doc.Meshes.Count];
        var meshVertices = 0;

        for (var m = 0; m < doc.Meshes.Count; m++)
        {
            var mesh = doc.Meshes[m];
            meshVertices += mesh.Vertices.Length;
            var issue = MeshValidator.ValidateCached(mesh, out var where);
            meshValid[m] = issue == MeshIssue.None;

            if (!meshValid[m])
                log.Add(Severity.Error, IssueCode.MeshInvalid, new ElementRef(ElementKind.Mesh, (uint)m), ((int)issue << 24) | (where & 0xFFFFFF));
        }

        if (meshVertices > PlanLimits.MaxMeshVertices)
            log.Add(Severity.Error, IssueCode.MeshBudget, default, meshVertices);

        for (var m = 0; m < doc.Materials.Count; m++)
        {
            if (catalog is not null && !catalog.TryGet(doc.Materials[m], out _))
                log.Add(Severity.Error, IssueCode.UnknownMaterial, new ElementRef(ElementKind.Material, (uint)m), (int)doc.Materials[m].Value);
        }

        ValidateLayers(doc, log);
        ValidateBrushes(doc, log);
        ValidateComponents(doc, log);
        var ids = new HashSet<uint>(solids.Length);
        var inBounds = true;
        var anyAdd = false;

        foreach (ref readonly var solid in solids)
        {
            var element = new ElementRef(ElementKind.Solid, solid.Id);

            if (solid.Id == 0)
                log.Add(Severity.Error, IssueCode.ZeroId, element);
            else if (!ids.Add(solid.Id))
                log.Add(Severity.Error, IssueCode.DuplicateId, element);

            if (solid.Shape == SolidShape.Mesh)
            {
                if (solid.Param >= doc.Meshes.Count)
                {
                    log.Add(Severity.Error, IssueCode.MeshIndex, element, solid.Param);
                    inBounds = false;
                    continue;
                }

                var (hx, hy, hz) = doc.Meshes[solid.Param].HalfUnits();

                if (solid.HalfX < hx || solid.HalfY < hy || solid.HalfZ < hz)
                    log.Add(Severity.Error, IssueCode.MeshBounds, element, solid.Param);
            }
            else if (!SolidGeometry.IsSupported(solid.Shape))
            {
                log.Add(Severity.Error, IssueCode.UnsupportedShape, element, (int)solid.Shape);
                inBounds = false;
                continue;
            }

            if (solid.Op is not (SolidOp.Add or SolidOp.Subtract or SolidOp.Paint))
                log.Add(Severity.Error, IssueCode.UnsupportedOp, element, (int)solid.Op);

            anyAdd |= solid.Op == SolidOp.Add;

            if ((solid.Flags & ~AllowedFlags) != 0)
                log.Add(Severity.Error, IssueCode.UnsupportedFlags, element, (int)solid.Flags);

            if (SolidRoles.Of(solid) != SolidRole.Skin && doc.Layers is null)
                log.Add(Severity.Error, IssueCode.RoleWithoutLayers, element, (int)SolidRoles.Of(solid));

            if (solid.Bevel != 0)
                log.Add(Severity.Error, IssueCode.BevelUnsupported, element, solid.Bevel);

            if (solid.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.MaterialIndex, element, solid.Material);

            if (!SolidRotation.IsValid(solid.Rotation))
            {
                log.Add(Severity.Error, IssueCode.InvalidRotation, element, (int)solid.Rotation);
                inBounds = false;
                continue;
            }

            var thinnest = solid.Shape == SolidShape.Mesh ? PlanLimits.MinHalfUnits : solid.Shape == SolidShape.Cylinder ? Math.Min(solid.HalfX, solid.HalfY) : Math.Min(solid.HalfX, Math.Min(solid.HalfY, solid.HalfZ));

            if (thinnest < PlanLimits.MinHalfUnits)
                log.Add(Severity.Error, IssueCode.ExtentTooSmall, element, thinnest);

            SolidGeometry.Bounds(solid, out var min, out var max);
            var reach = Math.Max(Math.Max(Math.Abs(min.X), Math.Abs(max.X)), Math.Max(Math.Max(Math.Abs(min.Y), Math.Abs(max.Y)), Math.Max(Math.Abs(min.Z), Math.Abs(max.Z))));

            if (reach > PlanLimits.MaxReachMetres)
            {
                log.Add(Severity.Error, IssueCode.OutOfBounds, element, (int)Math.Ceiling(reach));
                inBounds = false;
            }
        }

        if (!anyAdd)
            log.Add(Severity.Warning, IssueCode.NoSolid, default);

        if (!inBounds)
            return;

        bins ??= new HullBins();
        bins.Build(solids);

        if (bins.MaxPerBrick > HullBins.MaxSolidsPerBrick)
            log.Add(Severity.Error, IssueCode.BrickOverfull, default, bins.MaxPerBrick);
    }
}
