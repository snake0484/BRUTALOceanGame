using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Hallways: humanoid clearance, expansion to closed room prisms and stairs, plan bytes, validation, undo.</summary>
public class HallwayTests
{
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    private static HullHallway Hallway(double width, double height, params (double X, double Y, double Z)[] nodes)
    {
        var h = new HullHallway { Id = 7 };
        h.Profile.AddRange(HallwayGeometry.Preset(HallwayPreset.Rectangle, width, height));

        foreach (var (x, y, z) in nodes)
            h.Nodes.Add(((int)Math.Round(x * 1024), (int)Math.Round(y * 1024), (int)Math.Round(z * 1024)));

        return h;
    }

    [Theory]
    [InlineData(0.8, 1.9, true, 1)]
    [InlineData(1.6, 2.0, true, 2)]
    [InlineData(2.5, 2.4, true, 3)]
    [InlineData(0.7, 2.2, false, 0)]
    [InlineData(1.2, 1.8, false, 0)]
    public void Clearance_NeedsRoomToStand_AndCountsPeopleAbreast(double width, double height, bool fits, int people)
    {
        var c = HallwayGeometry.Measure(HallwayGeometry.Preset(HallwayPreset.Rectangle, width, height));

        Assert.Equal(fits, c.Fits);
        Assert.Equal(people, c.People);
    }

    [Fact]
    public void Clearance_OfAnArch_IsTheWidthAtHeadHeight()
    {
        // 1.0 × 2.2 arch: springs at 1.7 m, so at 1.9 m the clear width is sqrt(1 - 0.4²) = 0.917 m.
        var c = HallwayGeometry.Measure(HallwayGeometry.Preset(HallwayPreset.Arch, 1.0, 2.2));

        Assert.True(c.Fits);
        Assert.Equal(1, c.People);
        Assert.InRange(c.Width, 0.88, 0.93);
    }

    [Fact]
    public void Expansion_IsClosedPrisms_CutAsRooms()
    {
        var interior = new HullInterior();
        interior.Hallways.Add(Hallway(1.2, 2.2, (0, 0, 0), (0, 0, 6), (5, 0, 6)));

        var plain = HallwayGeometry.Expand(interior, layered: false, meshBase: 3);
        Assert.Equal(2, plain.Solids.Count);
        Assert.All(plain.Solids, s => Assert.Equal(SolidOp.Subtract, s.Op));
        Assert.Equal(3, plain.Solids[0].Param);
        Assert.All(plain.Meshes, m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));

        var layered = HallwayGeometry.Expand(interior, layered: true, meshBase: 0);
        Assert.All(layered.Solids, s => Assert.Equal(SolidRole.Room, SolidRoles.Of(s)));
    }

    [Fact]
    public void SteepRun_GetsStairs_AndAShaftGetsNone()
    {
        // 3 m up over 4 m: 37 degrees, stairs of 17 risers (3 / 0.18).
        var interior = new HullInterior();
        interior.Hallways.Add(Hallway(1.2, 2.2, (0, 0, 0), (0, 3, 4)));
        var stairs = HallwayGeometry.Expand(interior, layered: false, meshBase: 0);

        var steps = stairs.Solids.Where(s => s.Op == SolidOp.Add).ToList();
        Assert.Equal(17, steps.Count);
        Assert.All(stairs.Meshes, m => Assert.Equal(MeshIssue.None, MeshValidator.Validate(m, out _)));
        Assert.All(steps, s => Assert.True(s.Param < stairs.Meshes.Count));

        // Cuts come before every stair, so no cut removes a step.
        var firstStair = stairs.Solids.FindIndex(s => s.Op == SolidOp.Add);
        Assert.DoesNotContain(stairs.Solids.Skip(firstStair), s => s.Op == SolidOp.Subtract);

        interior.Hallways[0] = Hallway(1.2, 2.2, (0, 0, 0), (0, 6, 0));
        var shaft = HallwayGeometry.Expand(interior, layered: false, meshBase: 0);
        Assert.Equal(1, shaft.Shafts);
        Assert.DoesNotContain(shaft.Solids, s => s.Op == SolidOp.Add);
    }

    [Fact]
    public void Gravity_SetsTheFloor()
    {
        // Gravity rolled 90 degrees: down is -X... the run along Z is level and its floor faces +X.
        var down = HallwayGeometry.Down(SolidRotation.FromSteps(0, 0, 6));
        Assert.Equal(0, HallwayGeometry.Slope(new Vector3d(0, 0, 0), new Vector3d(0, 0, 5), down), 6);
        Assert.Equal(RunKind.Shaft, HallwayGeometry.Kind(HallwayGeometry.Slope(new Vector3d(0, 0, 0), new Vector3d(5, 0, 0), down)));
    }

    [Fact]
    public void Plan_RoundTripsHallways_AndValidates()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);
        doc.Interior = new HullInterior();
        doc.Interior.Hallways.Add(Hallway(1.2, 2.2, (0, 0, 0), (0, 0, 6)));
        doc.Interior.Hallways.Add(Hallway(0.6, 2.2, (2, 0, 0), (2, 0, 6)));
        doc.Interior.Hallways[1].Id = 8;

        Assert.Equal(PlanFormat.InteriorFormat, PlanFormat.FormatOf(doc));
        var bytes = PlanFormat.Encode(doc);
        Assert.True(PlanFormat.TryDecode(bytes, out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(doc), PlanFormat.HashBody(back));
        Assert.Equal(2, back.Interior!.Hallways.Count);

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.HallwayTooSmall && i.Element.Id == 8);
        Assert.DoesNotContain(log.Issues, i => i.Code == IssueCode.HallwayTooSmall && i.Element.Id == 7);
    }

    [Fact]
    public void Commands_UndoAsOneStep_AndKeepTheFloorAtZero()
    {
        var session = new DesignSession();
        var id = InteriorCommands.AddHallway(session, new Vector3d(0, 0, 0), new Vector3d(0, 0, 4), Alloy);
        Assert.NotEqual(0u, id);

        var raised = HallwayGeometry.Preset(HallwayPreset.Octagon, 1.0, 2.0).Select(p => p with { Y = p.Y + 500 }).ToList();
        Assert.True(InteriorCommands.SetProfile(session, id, raised));
        Assert.Equal(0, session.Doc.Interior!.Hallways[0].Profile.Min(p => p.Y));

        session.UndoLast();
        Assert.Equal(HallwayGeometry.Preset(HallwayPreset.Rounded, InteriorCommands.DefaultWidth, InteriorCommands.DefaultHeight), session.Doc.Interior!.Hallways[0].Profile);
        session.UndoLast();
        Assert.Null(session.Doc.Interior);
    }
}
