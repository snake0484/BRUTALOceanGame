using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Model brushes: game models repeated along strokes with rows, seeded jitter, alternate copies and end gaps.</summary>
public class ModelBrushTests
{
    private static readonly uint Nut = DefId.FromName("model.nut.hex").Value;

    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");

    private static HullMesh NutMesh()
    {
        var file = Path.Combine(Data, ModelCatalog.Folder, "nut_hex.json");
        return ModelCatalog.ParseMesh(File.ReadAllText(file), "model.nut.hex");
    }

    private static HullSolid Box() => new()
    {
        Id = 1, Shape = SolidShape.Box, Op = SolidOp.Add, HalfX = 4 * 256, HalfY = 3 * 256, HalfZ = 4 * 256,
    };

    /// <summary>A 6 m stroke along X on top of the box, drawn with <paramref name="brush"/>.</summary>
    private static ExpandedHull Expand(HullBrush brush, uint strokeId = 10)
    {
        var set = new HullBrushSet();
        set.Brushes.Add(brush);
        var stroke = new HullStroke { Id = strokeId, Brush = 0 };
        stroke.Points.Add(StrokePoint.From(new Vector3d(-3, 3.2, 0), new Vector3d(0, 1, 0)));
        stroke.Points.Add(StrokePoint.From(new Vector3d(3, 3.2, 0), new Vector3d(0, 1, 0)));
        set.Strokes.Add(stroke);
        var mesh = NutMesh();
        return StrokeExpansion.Expand([Box()], [], set, null, null, id => id == Nut ? mesh : null);
    }

    private static HullBrush Nuts() => new() { Kind = BrushKind.Model, Model = Nut, Spacing = 1024 };

    [Fact]
    public void NutHead_IsAValidMesh()
    {
        var mesh = NutMesh();
        Assert.Equal(MeshIssue.None, MeshValidator.Validate(mesh, out _));
        Assert.Equal(18, mesh.Vertices.Length);

        var catalog = ModelCatalog.Load(Data);
        Assert.NotNull(catalog.Mesh(Nut));
    }

    [Fact]
    public void Model_RepeatsAtTheSpacing_RowsMultiply_GapsTrimTheEnds()
    {
        Assert.Equal(7, Expand(Nuts()).Generated);

        var rows = Nuts();
        (rows.Rows, rows.RowSpacing) = (3, 256);
        var three = Expand(rows);
        Assert.Equal(21, three.Generated);

        // The three rows sit a quarter metre apart across the line (Z), all stuck to the top.
        var zs = three.Solids[1..].Select(s => Math.Round(s.PosZ / 1024.0, 2)).Distinct().Order().ToArray();
        Assert.Equal([-0.25, 0.0, 0.25], zs);
        Assert.All(three.Solids[1..], s => Assert.Equal(3.0, s.PosY / 1024.0, 1));

        var gapped = Nuts();
        (gapped.StartGap, gapped.EndGap) = (1536, 1536);
        Assert.Equal(4, Expand(gapped).Generated);
    }

    [Fact]
    public void Jitter_IsSeededPerStroke_AndAlternatesStayValid()
    {
        var brush = Nuts();
        (brush.TurnJitter, brush.SizeJitter, brush.Alternate) = (45, 300, RepeatAlternate.Mirror);
        var a = Expand(brush, 10);
        var b = Expand(brush, 10);
        var c = Expand(brush, 11);

        // Same stroke, same copies; another stroke, other turns and sizes.
        Assert.Equal(a.Solids.Select(s => (s.HalfX, s.HalfZ)), b.Solids.Select(s => (s.HalfX, s.HalfZ)));
        Assert.NotEqual(a.Solids.Select(s => (s.HalfX, s.HalfZ)), c.Solids.Select(s => (s.HalfX, s.HalfZ)));
        Assert.All(a.Meshes.Skip(0), m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));
    }

    [Fact]
    public void PlainStamps_KeepTheirFormat_ModelBrushesRoundTrip()
    {
        var session = new DesignSession();
        var nuts = BrushCommands.AddModel(session, new DefId(Nut), 0.3);
        Assert.True(BrushCommands.EditBrush(session, nuts, b => (b.Rows, b.TurnJitter, b.Alternate, b.EndGap) = (2, 20, RepeatAlternate.Turn, 300)));

        Assert.Equal(PlanFormat.RepeatFormat, PlanFormat.FormatOf(session.Doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(session.Doc), out var back, out var error), error.ToString());
        var brush = back.Brushes!.Brushes[nuts];
        Assert.Equal((BrushKind.Model, Nut, (byte)2, (byte)20, RepeatAlternate.Turn, 300), (brush.Kind, brush.Model, brush.Rows, brush.TurnJitter, brush.Alternate, brush.EndGap));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.BrushMesh or IssueCode.BrushSpacing);

        // A plain stamp brush needs none of it.
        var plain = new HullBrushSet();
        plain.Brushes.Add(new HullBrush { Kind = BrushKind.Stamp });
        Assert.False(plain.NeedsRepeats);
    }
}
