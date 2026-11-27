using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Doors and docking ports: openings in plan bytes, the doorway cut through the frame and the wall, placed doors' leaves as moving parts, sliders.</summary>
public class DoorTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    [Fact]
    public void Openings_RoundTrip_AndValidate()
    {
        var door = SampleComponents.SwingDoor(Alloy);

        Assert.Equal(PlanFormat.ProgramFormat, PlanFormat.FormatOf(door));   // it carries the Door programme too
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(door), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(door), PlanFormat.HashBody(back));
        var opening = Assert.Single(back.Openings!.Openings);
        Assert.Equal(OpeningKind.Door, opening.Kind);
        Assert.Equal(ProfileKind.Door, back.Interior!.Profiles[opening.Profile].Kind);

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Catalog);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.OpeningShape or IssueCode.OpeningCount or IssueCode.JointShape);

        // An opening on a profile that is not a door profile is an error.
        back.Openings.Openings[0].Profile = 9;
        log = new ValidationLog();
        PlanValidator.Validate(back, log, Catalog);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.OpeningShape);
    }

    [Fact]
    public void Doorway_CutsTheFrame_NotTheLeaf()
    {
        var door = SampleComponents.SwingDoor(Alloy);
        var solids = new List<HullSolid>(door.Solids.AsSpan().ToArray());
        var meshes = new List<HullMesh>(door.Meshes);
        Span<double> identity = [1, 0, 0, 0, 1, 0, 0, 0, 1];
        OpeningGeometry.Cuts(door.Openings, door.Interior!.Profiles, identity, default, 0, false, meshes.Count, solids, meshes);
        var frame = solids.Where(s => door.Joints!.JointOf(s.Id) == 0).ToArray();
        var surface = new HullSurface(frame, meshes);

        Assert.False(surface.Raycast(new Vector3d(0, 1, 1), new Vector3d(0, 0, -1), 4, out _, out _));
        Assert.True(surface.Raycast(new Vector3d(0.9, 1, 1), new Vector3d(0, 0, -1), 4, out _, out _));

        // The prefab's look: the frame and the leaf, each its own part.
        var shape = PrefabShape.Build(door, _ => 2700);
        Assert.Equal(2, shape.Parts.Count);
        Assert.NotEmpty(shape.Parts[1]);
    }

    [Fact]
    public void PlacedDoor_CutsThroughTheWall_AndItsLeafMoves()
    {
        var session = new DesignSession();
        SampleHulls.DoorsDemo(session, Alloy);
        var doc = session.Doc;
        var solids = new List<HullSolid>(doc.Solids.AsSpan().ToArray());
        var meshes = new List<HullMesh>(doc.Meshes);
        Span<double> m = stackalloc double[9];

        for (var i = 0; i < doc.Components!.Placements.Count; i++)
        {
            var placement = doc.Components.Placements[i];
            var prefab = doc.Prefabs[placement.Prefab];
            SolidRotation.Matrix(placement.Rotation, m);
            OpeningGeometry.Cuts(prefab.Openings, prefab.Interior?.Profiles, m, placement.Position, i + 1, false, meshes.Count, solids, meshes);
        }

        // Through the swing door in the +X wall, straight across the room to the far wall's inside.
        var surface = new HullSurface(solids.ToArray(), meshes);
        Assert.True(surface.Raycast(new Vector3d(6, 1.4, 2), new Vector3d(-1, 0, 0), 20, out var t, out _));
        Assert.InRange(t, 10.6, 10.8);

        // Beside it, the wall.
        Assert.True(surface.Raycast(new Vector3d(6, 1.4, -4.5), new Vector3d(-1, 0, 0), 20, out t, out _));
        Assert.InRange(t, 0.9, 1.1);

        // Each placed component's joint is a moving part of the ship, placed with it.
        var groups = JointGroups.Build(doc);
        Assert.Equal(5, groups.Count);   // the swing door's hinge, the sliding door's leaf and bolt, the port's hatch
        var hinge = groups.Joints[0];
        Assert.Equal(JointKind.Hinge, hinge.Kind);
        AssertNear(new Vector3d(5, 0.4, 2.5), hinge.Pivot);
        AssertNear(new Vector3d(0, 1, 0), hinge.Axis);
        var slider = groups.Joints[1];
        Assert.Equal(JointKind.Slider, slider.Kind);
        Assert.InRange(Math.Abs(slider.Axis.Z), 0.999, 1.001);
        Assert.Equal(groups.PlacementGroup(0), groups.GroupOf(solids.First(s => OpeningGeometry.Owner(s.Id) == 1)));
        Assert.Equal(1, groups.PlacementPartGroup(0, 1));
    }

    [Fact]
    public void Slider_SlidesAlongItsAxis()
    {
        var doc = new PlanDocument();
        var set = new HullJointSet();
        set.Joints.Add(new HullJoint { Id = 1, Kind = JointKind.Slider, Rotation = JointCommands.Snap(new Vector3d(1, 0, 0)), Min = 0, Max = 1000, Target = 0, Speed = 500 });
        doc.Joints = set;
        var groups = JointGroups.Build(doc);
        var rotations = new double[18];
        var translations = new Vector3d[2];

        Assert.Equal(0.5, JointPose.Angle(set.Joints[0], 1), 9);
        Assert.Equal(1.0, JointPose.Angle(set.Joints[0], 2), 9);
        Assert.Equal(0.5, JointPose.Angle(set.Joints[0], 3), 9);
        JointPose.Transforms(groups, 1, rotations, translations);
        AssertNear(new Vector3d(0.5, 0, 0), translations[1]);
        Assert.Equal(1, rotations[9], 9);

        var log = new ValidationLog();
        doc.Joints.Joints[0].Target = 2000;
        PlanValidator.Validate(doc, log, Catalog);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.JointShape);
    }

    [Fact]
    public void Ports_MateBySameShape()
    {
        var a = new HullProfile { Name = "A", Kind = ProfileKind.Door };
        a.Points.AddRange(HallwayGeometry.Preset(HallwayPreset.Octagon, 1.4, 1.4));
        var b = new HullProfile { Name = "Another name", Kind = ProfileKind.Door };
        b.Points.AddRange(a.Points);
        var c = new HullProfile { Kind = ProfileKind.Door };
        c.Points.AddRange(HallwayGeometry.Preset(HallwayPreset.Octagon, 1.6, 1.4));
        var lopsided = new HullProfile { Kind = ProfileKind.Door };
        lopsided.Points.AddRange([new ProfilePoint(-512, 0), new ProfilePoint(512, 0), new ProfilePoint(800, 2048)]);

        Assert.Equal(OpeningGeometry.PortType(a), OpeningGeometry.PortType(b));
        Assert.NotEqual(OpeningGeometry.PortType(a), OpeningGeometry.PortType(c));
        Assert.True(OpeningGeometry.Symmetric(a));
        Assert.False(OpeningGeometry.Symmetric(lopsided));
    }

    [Fact]
    public void Facing_PointsZOutAndYUp()
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(OpeningCommands.Facing(new Vector3d(1, 0, 0)), m);
        AssertNear(new Vector3d(1, 0, 0), new Vector3d(m[2], m[5], m[8]));
        AssertNear(new Vector3d(0, 1, 0), new Vector3d(m[1], m[4], m[7]));
    }

    private static void AssertNear(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }
}
