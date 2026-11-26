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
        zero.Solids[1].Id = 0;
        AssertRefused(zero, IssueCode.ZeroId);

        var duplicate = Sample();
        duplicate.Solids[1].Id = duplicate.Solids[0].Id;
        AssertRefused(duplicate, IssueCode.DuplicateId);
    }

    [Fact]
    public void RefusesFeaturesOutsideFormat1()
    {
        var shape = Sample();
        shape.Solids[0].Shape = SolidShape.Sphere;
        AssertRefused(shape, IssueCode.UnsupportedShape);

        var op = Sample();
        op.Solids[0].Op = SolidOp.Intersect;
        AssertRefused(op, IssueCode.UnsupportedOp);

        var flags = Sample();
        flags.Solids[0].Flags = SolidFlags.Damage;
        AssertRefused(flags, IssueCode.UnsupportedFlags);

        var bevel = Sample();
        bevel.Solids[0].Bevel = 64;
        AssertRefused(bevel, IssueCode.BevelUnsupported);
    }

    [Fact]
    public void RefusesBadMaterials()
    {
        var index = Sample();
        index.Solids[0].Material = 2;
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
        rotation.Solids[0].Rotation = 1u << 20;
        AssertRefused(rotation, IssueCode.InvalidRotation);

        var step = Sample();
        step.Solids[0].Rotation = 24;
        AssertRefused(step, IssueCode.InvalidRotation);

        var thin = Sample();
        thin.Solids[0].HalfY = PlanLimits.MinHalfUnits - 1;
        AssertRefused(thin, IssueCode.ExtentTooSmall);
    }

    [Fact]
    public void RefusesOutOfBoundsAndTooMany()
    {
        var far = Sample();
        far.Solids[0].PosX = (int)(PlanLimits.MaxReachMetres * HullSolid.PositionUnitsPerMetre);
        AssertRefused(far, IssueCode.OutOfBounds);

        var many = new PlanDocument();
        many.Materials.Add(Alloy);

        for (var i = 0; i <= PlanLimits.MaxSolids; i++)
            many.Solids.Add(Solid((uint)i + 1, SolidShape.Box, SolidOp.Add, 0, i * 4096, 0, 0, 256, 256, 256));

        AssertRefused(many, IssueCode.TooManySolids);
    }

    /// <summary>The bake cap: more than 256 solids reaching one brick is refused (the build tool stops at it).</summary>
    [Fact]
    public void RefusesOverfullBrick()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);

        for (var i = 0; i < HullBins.MaxSolidsPerBrick + 1; i++)
            doc.Solids.Add(Solid((uint)i + 1, SolidShape.Box, SolidOp.Add, 0, 4096, 4096, 4096, 128, 128, 128));

        AssertRefused(doc, IssueCode.BrickOverfull);

        doc.Solids.RemoveAt(0);
        Assert.Equal(0, Validate(doc).Errors);
    }

    [Fact]
    public void WarnsWithoutSolid()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);
        doc.Solids.Add(Solid(1, SolidShape.Box, SolidOp.Subtract, 0, 0, 0, 0, 256, 256, 256));
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
