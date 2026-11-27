using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Windows: the pane takes the hull's shape, the opening cuts through, plan bytes, validation, glazing materials.</summary>
public class WindowTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");
    private static readonly DefId Glass = DefId.FromName("mat.glazing.soda");

    private static DesignSession Demo()
    {
        var session = new DesignSession();
        SampleHulls.HollowBoxWithCylinder(session, Alloy, Alloy);
        return session;
    }

    [Fact]
    public void Pane_OnACylinder_FollowsItsCurve()
    {
        var session = Demo();

        // On top of the 1.5 m cylinder along Z at (0, 3, 7): the pane's outer points sit on the curve, not on a flat plate.
        WindowCommands.AddWindow(session, new Vector3d(0, 4.5, 7), new Vector3d(0, 1, 0), Glass, new HullWindow { Shape = WindowShape.Rectangle, WidthMm = 1600, HeightMm = 1200 });
        var doc = session.Doc;
        var surface = new HullSurface(doc.Solids.AsSpan(), doc.Meshes);
        var result = WindowGeometry.Expand(doc.Windows, surface, layered: false, meshBase: doc.Meshes.Count);

        Assert.Single(result.Cuts);
        Assert.Single(result.Panes);
        Assert.Equal(SolidOp.Subtract, result.Cuts[0].Op);
        Assert.Equal(0, result.Unstuck);
        Assert.All(result.Meshes, m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));

        var w = doc.Windows!.Windows[0];
        var (across, up, n) = WindowGeometry.Frame(w);
        var outline = WindowGeometry.Outline(w.Shape, 1.6, 1.2);
        var unstuck = 0;
        var rings = WindowGeometry.PaneRings(w, surface, WindowGeometry.Centre(w, surface), across, up, n, outline, ref unstuck);

        foreach (var p in rings.SelectMany(r => r))
        {
            var radius = Math.Sqrt(p.X * p.X + (p.Y - 3) * (p.Y - 3));
            Assert.InRange(radius, 1.495, 1.505);
        }

        // The rim drops below the crown: a flat plate would not.
        Assert.True(rings[^1].Min(p => p.Y) < 4.3);
    }

    [Fact]
    public void Window_OnLayeredHull_IsARoomCut_AndAPanelPane()
    {
        var session = Demo();
        WindowCommands.AddWindow(session, new Vector3d(-6, 3, 0), new Vector3d(-1, 0, 0), Glass);
        var doc = session.Doc;
        var result = WindowGeometry.Expand(doc.Windows, new HullSurface(doc.Solids.AsSpan(), doc.Meshes), layered: true, meshBase: 0);

        Assert.Equal(SolidRole.Room, SolidRoles.Of(result.Cuts[0]));
        Assert.Equal(SolidRole.Panel, SolidRoles.Of(result.Panes[0]));
        Assert.All(result.Meshes, m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));
    }

    [Fact]
    public void Plan_RoundTripsWindows_AndValidates()
    {
        var session = Demo();
        var good = WindowCommands.AddWindow(session, new Vector3d(-6, 3, 0), new Vector3d(-1, 0, 0), Glass);
        var bad = WindowCommands.AddWindow(session, new Vector3d(-6, 3, 2), new Vector3d(-1, 0, 0), Glass);
        WindowCommands.SetWindow(session, bad, w => w.WidthMm = 20);
        var doc = session.Doc;

        Assert.Equal(PlanFormat.WindowFormat, PlanFormat.FormatOf(doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(doc), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(doc), PlanFormat.HashBody(back));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Catalog);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.WindowShape && i.Element.Id == bad);
        Assert.DoesNotContain(log.Issues, i => i.Element.Id == good);

        session.UndoLast();
        session.UndoLast();
        session.UndoLast();
        Assert.Null(session.Doc.Windows);
    }

    [Fact]
    public void Glazings_AreSeeThrough_AndHullAlloyIsNot()
    {
        Assert.True(Catalog.TryGet(Glass, out var glass) && glass.Transparent);
        Assert.True(Catalog.TryGet(DefId.FromName("mat.glazing.alon"), out var alon) && alon.Transparent && alon.YieldMpa > glass.YieldMpa);
        Assert.True(Catalog.TryGet(Alloy, out var alloy) && !alloy.Transparent);
    }
}
