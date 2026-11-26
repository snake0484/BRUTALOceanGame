using BrutalGame.Ships;
using static BrutalGame.Plans.Tests.PlanFormatTests;

namespace BrutalGame.Plans.Tests;

public class PlanValidatorTests
{
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(Path.Combine(AppContext.BaseDirectory, "data"));

    private static ValidationLog Validate(PlanDocument doc, HullMaterialCatalog? catalog = null)
    {
        var log = new ValidationLog();
        PlanValidator.Validate(doc, log, catalog ?? Catalog);
        return log;
    }

    private static void AssertRefused(PlanDocument doc, IssueCode code)
    {
        var log = Validate(doc);
        Assert.Contains(log.Issues, i => i.Code == code && i.Severity == Severity.Error);
    }

    [Fact]
    public void Sample_IsValid()
    {
        var log = Validate(Sample());
        Assert.Equal(0, log.Errors);
        Assert.Empty(log.Issues);
    }

    [Fact]
    public void RefusesBadIds()
    {
        var zero = Sample();
        zero.Brushes[1].Id = 0;
        AssertRefused(zero, IssueCode.ZeroId);

        var duplicate = Sample();
        duplicate.Brushes[1].Id = duplicate.Brushes[0].Id;
        AssertRefused(duplicate, IssueCode.DuplicateId);
    }

    [Fact]
    public void RefusesFeaturesOutsideFormat1()
    {
        var shape = Sample();
        shape.Brushes[0].Shape = BrushShape.Sphere;
        AssertRefused(shape, IssueCode.UnsupportedShape);

        var op = Sample();
        op.Brushes[0].Op = BrushOp.Intersect;
        AssertRefused(op, IssueCode.UnsupportedOp);

        var flags = Sample();
        flags.Brushes[0].Flags = BrushFlags.Damage;
        AssertRefused(flags, IssueCode.UnsupportedFlags);

        var bevel = Sample();
        bevel.Brushes[0].Bevel = 64;
        AssertRefused(bevel, IssueCode.BevelUnsupported);
    }

    [Fact]
    public void RefusesBadMaterials()
    {
        var index = Sample();
        index.Brushes[0].Material = 2;
        AssertRefused(index, IssueCode.MaterialIndex);

        var unknown = Sample();
        unknown.Materials[1] = DefId.FromName("mat.not.in.data");
        AssertRefused(unknown, IssueCode.UnknownMaterial);
        Assert.Equal(0, Validate(unknown, new HullMaterialCatalog()).Issues.Count(i => i.Code == IssueCode.MaterialIndex));
    }

    [Fact]
    public void RefusesBadQuantisation()
    {
        var rotation = Sample();
        rotation.Brushes[0].Rotation = 1u << 20;
        AssertRefused(rotation, IssueCode.InvalidRotation);

        var step = Sample();
        step.Brushes[0].Rotation = 24;
        AssertRefused(step, IssueCode.InvalidRotation);

        var thin = Sample();
        thin.Brushes[0].HalfY = PlanLimits.MinHalfUnits - 1;
        AssertRefused(thin, IssueCode.ExtentTooSmall);
    }

    [Fact]
    public void RefusesOutOfBoundsAndTooMany()
    {
        var far = Sample();
        far.Brushes[0].PosX = (int)(PlanLimits.MaxReachMetres * HullBrush.PositionUnitsPerMetre);
        AssertRefused(far, IssueCode.OutOfBounds);

        var many = new PlanDocument();
        many.Materials.Add(Alloy);

        for (var i = 0; i <= PlanLimits.MaxBrushes; i++)
            many.Brushes.Add(Brush((uint)i + 1, BrushShape.Box, BrushOp.Add, 0, i * 4096, 0, 0, 256, 256, 256));

        AssertRefused(many, IssueCode.TooManyBrushes);
    }

    /// <summary>The bake cap: more than 256 brushes reaching one brick is refused (the build tool stops at it).</summary>
    [Fact]
    public void RefusesOverfullBrick()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);

        for (var i = 0; i < HullBins.MaxBrushesPerBrick + 1; i++)
            doc.Brushes.Add(Brush((uint)i + 1, BrushShape.Box, BrushOp.Add, 0, 4096, 4096, 4096, 128, 128, 128));

        AssertRefused(doc, IssueCode.BrickOverfull);

        doc.Brushes.RemoveAt(0);
        Assert.Equal(0, Validate(doc).Errors);
    }

    [Fact]
    public void WarnsWithoutSolid()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);
        doc.Brushes.Add(Brush(1, BrushShape.Box, BrushOp.Subtract, 0, 0, 0, 0, 256, 256, 256));
        var log = Validate(doc);

        Assert.Equal(0, log.Errors);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.NoSolid && i.Severity == Severity.Warning);
    }

    [Fact]
    public void Catalog_LoadsDataAndRefusesBadEntries()
    {
        Assert.True(Catalog.TryGet(Alloy, out var alloy));
        Assert.True(alloy.DensityKgM3 > 0);
        Assert.Throws<InvalidDataException>(() => HullMaterialCatalog.Parse("""{ "materials": [ { "id": "a", "densityKgM3": 0, "colour": "FFFFFF" } ] }"""));
        Assert.Throws<InvalidDataException>(() => HullMaterialCatalog.Parse("""{ "materials": [ { "id": "a", "densityKgM3": 5, "colour": "FFFFFF" }, { "id": "a", "densityKgM3": 5, "colour": "FFFFFF" } ] }"""));
    }
}
