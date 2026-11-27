using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Interior;

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
    HallwayCount,
    HallwayShape,
    HallwayFloor,
    HallwayTooSmall,
    HallwayMaterial,
    HallwayShaft,
    WindowCount,
    WindowShape,
    WindowMaterial,
    WallShape,
    RoomWall,
    DecalCount,
    DecalShape,
    PaintSlots,
    PaintRef,
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
            else if (brush.Kind == BrushKind.Paint)
            {
                if (!Enum.IsDefined(brush.Pattern) || brush.Width is < HullBrush.MinPaintUnits or > HullBrush.MaxPaintUnits
                    || brush.LineWidth is < HullBrush.MinPaintUnits or > HullBrush.MaxPaintUnits || brush.Spacing is < HullBrush.MinPaintUnits or > HullBrush.MaxPaintUnits)
                    log.Add(Severity.Error, IssueCode.BrushProfile, default, i);
            }
            else if (brush.Kind != BrushKind.Sweep || !IsBrushProfile(doc, brush.Outline)
                || Math.Abs(brush.Anchor.X) > HullBrush.MaxProfileUnits || Math.Abs(brush.Anchor.Y) > HullBrush.MaxProfileUnits)
            {
                log.Add(Severity.Error, IssueCode.BrushProfile, default, i);
            }

            if (brush.Scale is < HullBrush.MinScale or > HullBrush.MaxScale)
                log.Add(Severity.Error, IssueCode.BrushProfile, default, i);
        }

        foreach (var stroke in set.Strokes)
        {
            var element = new ElementRef(ElementKind.Solid, stroke.Id);

            if (stroke.Brush >= set.Brushes.Count)
                log.Add(Severity.Error, IssueCode.StrokeBrush, element, stroke.Brush);

            if (stroke.Points.Count is < HullStroke.MinPoints or > HullStroke.MaxPoints
                || stroke.Scale is < 100 or > 10000 || Math.Abs(stroke.Offset * HullSolid.MetresPerPositionUnit) > 16.0
                || stroke.Points.Any(p => (p.NX == 0 && p.NY == 0 && p.NZ == 0) || p.Position.Length() > PlanLimits.MaxReachMetres || p.Scale is < HullBrush.MinScale or > HullBrush.MaxScale))
                log.Add(Severity.Error, IssueCode.StrokeShape, element, stroke.Points.Count);

            if (stroke.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.StrokeMaterial, element, stroke.Material);
        }
    }

    /// <summary>Plan profile <paramref name="index"/> is a brush outline a sweep can use: 3-16 corners in range, enclosing some area.</summary>
    public static bool IsBrushProfile(PlanDocument doc, int index) =>
        doc.Interior is { } interior && index < interior.Profiles.Count && interior.Profiles[index] is { Kind: ProfileKind.Brush } profile
        && IsBrushOutline(profile.Points);

    public static bool IsBrushOutline(List<ProfilePoint> points) =>
        points.Count is >= HullBrush.MinProfilePoints and <= HullBrush.MaxProfilePoints && ProfileArea(points) != 0
        && !points.Any(p => Math.Abs(p.X) > HullBrush.MaxProfileUnits || Math.Abs(p.Y) > HullBrush.MaxProfileUnits);

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

            if (pipe.Nodes.Count is < 2 or > PipeRoute.MaxNodes || pipe.Runs.Count is 0 or > PipeRoute.MaxRuns
                || pipe.Runs.Any(run => run.A == run.B || run.A >= pipe.Nodes.Count || run.B >= pipe.Nodes.Count || run.InnerMm == 0 || run.InnerMm >= run.OuterMm))
                log.Add(Severity.Error, IssueCode.PipeShape, element, pipe.Nodes.Count);

            foreach (var (node, end) in pipe.Ports)
            {
                if (node >= pipe.Nodes.Count || end.IsOpen || !parts.TryGetValue(end.Part, out var part) || end.Port >= PartGeometry.Ports(part).Length)
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

    /// <summary>
    /// Hallways: counts, a real path and outline, the floor at Y = 0, the humanoid clearance (0.8 × 1.9 m; the value is
    /// the clear width in mm), the stair material, and a Warning per run too steep for stairs (ladders are not built yet).
    /// </summary>
    private static void ValidateInterior(PlanDocument doc, ValidationLog log)
    {
        var interior = doc.Interior;

        if (interior is null)
            return;

        if (interior.Hallways.Count > HullInterior.MaxHallways || interior.Profiles.Count > HullInterior.MaxProfiles)
            log.Add(Severity.Error, IssueCode.HallwayCount, default, interior.Hallways.Count);

        // Profiles: a real outline in range with its floor at Y = 0 (a wall: see WallGeometry.IsValid). The value is the profile's index.
        var badProfiles = new HashSet<int>();

        for (var i = 0; i < interior.Profiles.Count; i++)
        {
            var points = interior.Profiles[i].Points;

            if (interior.Profiles[i].Kind == ProfileKind.Wall)
            {
                if (!WallGeometry.IsValid(points))
                {
                    log.Add(Severity.Error, IssueCode.WallShape, default, i);
                    badProfiles.Add(i);
                }

                continue;
            }

            // Brush outlines are checked with the brushes that use them.
            if (interior.Profiles[i].Kind == ProfileKind.Brush)
                continue;

            if (points.Count is < HullProfile.MinPoints or > HullProfile.MaxPoints || HallwayGeometry.Area2(points) == 0
                || points.Any(p => Math.Abs(p.X) > HullProfile.MaxUnits || Math.Abs(p.Y) > HullProfile.MaxUnits))
            {
                log.Add(Severity.Error, IssueCode.HallwayShape, default, i);
                badProfiles.Add(i);
            }
            else if (points.Min(p => p.Y) != 0)
            {
                log.Add(Severity.Warning, IssueCode.HallwayFloor, default, i);
            }
        }

        foreach (var hallway in interior.Hallways)
        {
            var element = new ElementRef(ElementKind.Route, hallway.Id);

            if (hallway.Nodes.Count is < 2 or > HullHallway.MaxNodes || hallway.Runs.Count is 0 or > HullHallway.MaxRuns
                || hallway.Runs.Any(r => r.A == r.B || r.A >= hallway.Nodes.Count || r.B >= hallway.Nodes.Count)
                || hallway.Profile >= interior.Profiles.Count || badProfiles.Contains(hallway.Profile) || interior.Profiles[hallway.Profile].Kind != ProfileKind.Hallway
                || Enumerable.Range(0, hallway.Nodes.Count).Any(i => hallway.Node(i).Length() > PlanLimits.MaxReachMetres)
                || !SolidRotation.IsValid(hallway.Gravity))
            {
                log.Add(Severity.Error, IssueCode.HallwayShape, element, hallway.Nodes.Count);
                continue;
            }

            var clearance = HallwayGeometry.Measure(interior.Profiles[hallway.Profile].Points);

            if (!clearance.Fits)
                log.Add(Severity.Error, IssueCode.HallwayTooSmall, element, (int)Math.Round(clearance.Width * 1000));

            if (hallway.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.HallwayMaterial, element, hallway.Material);

            var down = HallwayGeometry.Down(hallway.Gravity);

            for (var r = 0; r < hallway.Runs.Count; r++)
            {
                var (a, b) = hallway.Runs[r];

                if (HallwayGeometry.Kind(HallwayGeometry.Slope(hallway.Node(a), hallway.Node(b), down)) == RunKind.Shaft)
                    log.Add(Severity.Warning, IssueCode.HallwayShaft, element, r);
            }
        }

        // Room walls: one per solid, a box room using a good wall profile. The value is the profile index.
        if (interior.RoomWalls.Count > HullInterior.MaxRoomWalls)
            log.Add(Severity.Error, IssueCode.RoomWall, default, interior.RoomWalls.Count);

        var seen = new HashSet<uint>();

        foreach (var wall in interior.RoomWalls)
        {
            var element = new ElementRef(ElementKind.Solid, wall.Solid);
            var index = doc.Solids.IndexOf(wall.Solid);

            if (!seen.Add(wall.Solid) || index < 0 || !WallGeometry.IsRoom(doc.Solids[index], doc.Meshes)
                || wall.Profile >= interior.Profiles.Count || interior.Profiles[wall.Profile].Kind != ProfileKind.Wall || badProfiles.Contains(wall.Profile))
                log.Add(Severity.Error, IssueCode.RoomWall, element, wall.Profile);
        }
    }

    /// <summary>Paints: slot count, each naming a finish; the hull's slot and strokes' slots in range, each stroke there.</summary>
    private static void ValidatePaint(PlanDocument doc, ValidationLog log)
    {
        if (doc.Paint is not { } paint)
            return;

        if (paint.Slots.Count > HullPaintSet.MaxSlots || paint.Slots.Exists(s => s.Finish == 0))
            log.Add(Severity.Error, IssueCode.PaintSlots, default, paint.Slots.Count);

        if (paint.Hull != HullPaintSet.None && paint.Hull >= paint.Slots.Count)
            log.Add(Severity.Error, IssueCode.PaintRef, default, paint.Hull);

        foreach (var (stroke, slot) in paint.Strokes)
        {
            if (slot >= paint.Slots.Count || doc.Brushes is not { } set || set.IndexOfStroke(stroke) < 0)
                log.Add(Severity.Error, IssueCode.PaintRef, new ElementRef(ElementKind.Solid, stroke), slot);
        }
    }

    /// <summary>Decals: count, a real normal, sizes in range, an image named, within reach.</summary>
    private static void ValidateDecals(PlanDocument doc, ValidationLog log)
    {
        if (doc.Decals is not { } set)
            return;

        if (set.Decals.Count > HullDecalSet.MaxDecals)
            log.Add(Severity.Error, IssueCode.DecalCount, default, set.Decals.Count);

        foreach (var d in set.Decals)
        {
            if ((d.NX == 0 && d.NY == 0 && d.NZ == 0) || d.Roll >= 24 || !Enum.IsDefined(d.Shape)
                || (d.Shape == DecalShape.Image ? d.Asset == 0 : d.Paint >= (doc.Paint?.Slots.Count ?? 0)) || d.Position.Length() > PlanLimits.MaxReachMetres
                || d.WidthMm is < HullDecal.MinSizeMm or > HullDecal.MaxSizeMm || d.HeightMm is < HullDecal.MinSizeMm or > HullDecal.MaxSizeMm
                || d.DepthMm is < HullDecal.MinSizeMm or > HullDecal.MaxSizeMm)
                log.Add(Severity.Error, IssueCode.DecalShape, new ElementRef(ElementKind.Placement, d.Id), d.WidthMm);
        }
    }

    /// <summary>Windows: count, a real outward normal, sizes in range (0.1-16 m across, pane at least 5 mm, cut at most 8 m deep), within reach, the pane material.</summary>
    private static void ValidateWindows(PlanDocument doc, ValidationLog log)
    {
        var windows = doc.Windows;

        if (windows is null)
            return;

        if (windows.Windows.Count > HullWindowSet.MaxWindows)
            log.Add(Severity.Error, IssueCode.WindowCount, default, windows.Windows.Count);

        foreach (var w in windows.Windows)
        {
            var element = new ElementRef(ElementKind.Placement, w.Id);

            if ((w.NX == 0 && w.NY == 0 && w.NZ == 0) || !Enum.IsDefined(w.Shape) || w.Roll >= 24
                || w.WidthMm is < HullWindow.MinSizeMm or > HullWindow.MaxSizeMm || w.HeightMm is < HullWindow.MinSizeMm or > HullWindow.MaxSizeMm
                || w.ThicknessMm < HullWindow.MinThicknessMm || w.DepthMm > HullWindow.MaxDepthMm || w.Position.Length() > PlanLimits.MaxReachMetres)
                log.Add(Severity.Error, IssueCode.WindowShape, element, w.WidthMm);

            if (w.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.WindowMaterial, element, w.Material);
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
        ValidateDecals(doc, log);
        ValidatePaint(doc, log);
        ValidateComponents(doc, log);
        ValidateInterior(doc, log);
        ValidateWindows(doc, log);
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
