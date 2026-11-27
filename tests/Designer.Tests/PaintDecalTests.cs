using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Paint brushes (bands of pattern as projector boxes) and decals (game images projected onto the hull).</summary>
public class PaintDecalTests
{
    private static HullSolid Box() => new()
    {
        Id = 1, Shape = SolidShape.Box, Op = SolidOp.Add, PosX = 0, PosY = 0, PosZ = 0, HalfX = 4 * 256, HalfY = 3 * 256, HalfZ = 4 * 256,
    };

    private static HullBrushSet PaintAcrossTop(params Vector3d[] points)
    {
        var set = new HullBrushSet();
        set.Brushes.Add(new HullBrush { Kind = BrushKind.Paint, Width = 1024, Spacing = 256, LineWidth = 40, Colour = 0xFF0000FF });
        var stroke = new HullStroke { Id = 10, Brush = 0 };

        foreach (var p in points)
            stroke.Points.Add(StrokePoint.From(p, new Vector3d(0, 1, 0)));

        set.Strokes.Add(stroke);
        return set;
    }

    [Fact]
    public void Paint_IsBoxesAlongTheBand_NoSolids()
    {
        var expanded = StrokeExpansion.Expand([Box()], [], PaintAcrossTop(new Vector3d(-3, 3.2, 0), new Vector3d(3, 3.2, 0)), null);

        // A straight stroke on a flat top merges into one box, 1 m wide, reaching over flair.
        Assert.Equal(0, expanded.Generated);
        var box = Assert.Single(expanded.Projectors);
        Assert.Equal(ProjectorKind.Paint, box.Kind);
        Assert.Equal(0.5, box.HalfV, 3);
        Assert.True(box.HalfU >= 3.0);
        Assert.Equal(1.0, box.N.Y, 3);
        Assert.Equal(3.0, box.Along, 3);
        Assert.Equal(0.25f, box.Spacing, 3);
    }

    [Fact]
    public void Paint_TurnsWithTheStroke_AndKeepsTheDistanceAlong()
    {
        var expanded = StrokeExpansion.Expand([Box()], [], PaintAcrossTop(new Vector3d(-3, 3.2, 0), new Vector3d(0, 3.2, 0), new Vector3d(0, 3.2, 3)), null);

        Assert.Equal(2, expanded.Projectors.Count);
        Assert.Equal(1.5, expanded.Projectors[0].Along, 3);
        Assert.Equal(4.5, expanded.Projectors[1].Along, 3);
        Assert.Equal(1.0, Math.Abs(expanded.Projectors[1].U.Z), 3);
    }

    [Fact]
    public void Decal_ProjectsFromItsBox_UprightOnAWall()
    {
        var decal = new HullDecal { WidthMm = 2000, HeightMm = 1000, DepthMm = 500, Opacity = 128 };
        decal.SetNormal(new Vector3d(1, 0, 0));
        var box = decal.Projector(3);

        // On a wall facing +X the image's up is ship up and its right is -Z (seen from outside, looking back along -X).
        Assert.Equal(ProjectorKind.Decal, box.Kind);
        Assert.Equal(1.0, box.V.Y, 3);
        Assert.Equal(-1.0, box.U.Z, 3);
        Assert.Equal((1.0, 0.5, 0.25), (box.HalfU, box.HalfV, box.HalfN));
        Assert.Equal(3, box.Layer);
        Assert.Equal(128u, box.Colour >> 24);

        // A quarter turn puts the image's up along +Z... or -Z: either way, off ship up.
        decal.Roll = 6;
        Assert.Equal(0.0, decal.Projector(0).V.Y, 3);
    }

    [Fact]
    public void Decals_RoundTripAndValidate()
    {
        var session = new DesignSession();
        var id = DecalCommands.AddDecal(session, new Vector3d(0, 3, 0), new Vector3d(0, 1, 0), DefId.FromName("decal.emblem"));
        Assert.NotEqual(0u, id);
        Assert.True(DecalCommands.SetDecal(session, id, d => (d.Roll, d.Opacity) = (5, 200)));

        Assert.Equal(PlanFormat.BrushProfileFormat, PlanFormat.FormatOf(session.Doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(session.Doc), out var back, out var error), error.ToString());
        var decal = Assert.Single(back.Decals!.Decals);
        Assert.Equal((5, 200, DefId.FromName("decal.emblem").Value), (decal.Roll, decal.Opacity, decal.Asset));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.DecalShape or IssueCode.DecalCount);

        back.Decals.Decals[0].Asset = 0;
        log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.DecalShape);

        session.UndoLast();
        session.UndoLast();
        Assert.Null(session.Doc.Decals);
    }

    [Fact]
    public void PaintBrush_RoundTrips()
    {
        var session = new DesignSession();
        var paint = BrushCommands.AddPaint(session, PaintPattern.LineCross, 0x80112233, 0.8, 0.3, 0.05);
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(session.Doc), out var back, out var error), error.ToString());
        var brush = back.Brushes!.Brushes[paint];
        Assert.Equal((BrushKind.Paint, 0x80112233u, 819, 307, 51), (brush.Kind, brush.Colour, brush.Width, brush.Spacing, brush.LineWidth));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.DoesNotContain(log.Issues, i => i.Code == IssueCode.BrushProfile);
    }

    [Fact]
    public void DecalCatalog_ReadsGameData()
    {
        var catalog = DecalCatalog.Parse("""{ "decals": [ { "id": "decal.a", "name": "A", "image": "a.png" }, { "id": "decal.b", "image": "b.png" } ] }""");
        Assert.Equal(2, catalog.All.Count);
        Assert.Equal(1, catalog.IndexOf(DefId.FromName("decal.b")));
        Assert.Equal(-1, catalog.IndexOf(DefId.FromName("decal.c")));
        Assert.Equal("decal.b", catalog.All[1].Name);
    }
}
