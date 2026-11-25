using BrutalGame.Ships;

namespace BrutalGame.Plans;

public enum Severity : byte { Info, Warning, Error }

public enum IssueCode : ushort
{
    UnsupportedLevel,
    TooManyBrushes,
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
/// per-brush work, and binning runs only when every brush is in bounds.
/// </summary>
public static class PlanValidator
{
    private const BrushFlags AllowedFlags = BrushFlags.Interior | BrushFlags.NoCollide | BrushFlags.Mirrored;

    /// <param name="catalog">Known materials; null skips the check (a file opened without data).</param>
    public static void Validate(PlanDocument doc, ValidationLog log, HullMaterialCatalog? catalog = null, HullBins? bins = null)
    {
        log.Clear();

        if (doc.Level is not (PlanLevel.Ship or PlanLevel.HullChunk))
        {
            log.Add(Severity.Error, IssueCode.UnsupportedLevel, default, (int)doc.Level);
            return;
        }

        var brushes = doc.Brushes.AsSpan();

        if (brushes.Length > PlanLimits.MaxBrushes)
        {
            log.Add(Severity.Error, IssueCode.TooManyBrushes, default, brushes.Length);
            return;
        }

        if (doc.Materials.Count > PlanLimits.MaxMaterials)
        {
            log.Add(Severity.Error, IssueCode.TooManyMaterials, default, doc.Materials.Count);
            return;
        }

        for (var m = 0; m < doc.Materials.Count; m++)
        {
            if (catalog is not null && !catalog.TryGet(doc.Materials[m], out _))
                log.Add(Severity.Error, IssueCode.UnknownMaterial, new ElementRef(ElementKind.Material, (uint)m), (int)doc.Materials[m].Value);
        }

        var ids = new HashSet<uint>(brushes.Length);
        var inBounds = true;
        var anyAdd = false;

        foreach (ref readonly var brush in brushes)
        {
            var element = new ElementRef(ElementKind.Brush, brush.Id);

            if (brush.Id == 0)
                log.Add(Severity.Error, IssueCode.ZeroId, element);
            else if (!ids.Add(brush.Id))
                log.Add(Severity.Error, IssueCode.DuplicateId, element);

            if (!BrushGeometry.IsSupported(brush.Shape))
            {
                log.Add(Severity.Error, IssueCode.UnsupportedShape, element, (int)brush.Shape);
                inBounds = false;
                continue;
            }

            if (brush.Op is not (BrushOp.Add or BrushOp.Subtract or BrushOp.Paint))
                log.Add(Severity.Error, IssueCode.UnsupportedOp, element, (int)brush.Op);

            anyAdd |= brush.Op == BrushOp.Add;

            if ((brush.Flags & ~AllowedFlags) != 0)
                log.Add(Severity.Error, IssueCode.UnsupportedFlags, element, (int)brush.Flags);

            if (brush.Bevel != 0)
                log.Add(Severity.Error, IssueCode.BevelUnsupported, element, brush.Bevel);

            if (brush.Material >= doc.Materials.Count)
                log.Add(Severity.Error, IssueCode.MaterialIndex, element, brush.Material);

            if (!BrushRotation.IsValid(brush.Rotation))
            {
                log.Add(Severity.Error, IssueCode.InvalidRotation, element, (int)brush.Rotation);
                inBounds = false;
                continue;
            }

            var thinnest = brush.Shape == BrushShape.Cylinder ? Math.Min(brush.HalfX, brush.HalfY) : Math.Min(brush.HalfX, Math.Min(brush.HalfY, brush.HalfZ));

            if (thinnest < PlanLimits.MinHalfUnits)
                log.Add(Severity.Error, IssueCode.ExtentTooSmall, element, thinnest);

            BrushGeometry.Bounds(brush, out var min, out var max);
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
        bins.Build(brushes);

        if (bins.MaxPerBrick > HullBins.MaxBrushesPerBrick)
            log.Add(Severity.Error, IssueCode.BrickOverfull, default, bins.MaxPerBrick);
    }
}
