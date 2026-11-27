using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>Rotors and hinges: plan bytes, validation, moving-part groups, the preview pose, and the edit commands.</summary>
public class JointTests
{
    private static readonly string DataFolder = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly HullMaterialCatalog Catalog = HullMaterialCatalog.Load(DataFolder);
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    private static DesignSession Demo()
    {
        var session = new DesignSession();
        SampleHulls.JointsDemo(session, Alloy);
        return session;
    }

    [Fact]
    public void Plan_RoundTrips_AndValidates()
    {
        var doc = Demo().Doc;

        Assert.Equal(PlanFormat.JointFormat, PlanFormat.FormatOf(doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(doc), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(doc), PlanFormat.HashBody(back));
        Assert.Equal(2, back.Joints!.Joints.Count);
        Assert.Equal(3, back.Joints.Members.Count);
        Assert.Equal(60, back.Joints.Joints.Single(j => j.Kind == JointKind.Rotor).Speed);

        var log = new ValidationLog();
        PlanValidator.Validate(back, log, Catalog);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.JointCount or IssueCode.JointShape or IssueCode.JointMember);
    }

    [Fact]
    public void Validator_FlagsBadLimits_AndMissingMembers()
    {
        var session = Demo();
        var hinge = session.Doc.Joints!.Joints.Single(j => j.Kind == JointKind.Hinge).Id;
        JointCommands.SetJoint(session, hinge, j => j.Target = 900);   // past its 30 degree upper limit
        var doc = session.Doc.Clone();
        doc.Joints!.Members.Add((9999, hinge));

        var log = new ValidationLog();
        PlanValidator.Validate(doc, log, Catalog);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.JointShape && i.Element.Id == hinge);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.JointMember && i.Element.Id == 9999);
    }

    [Fact]
    public void Groups_PutEachSolidWithItsJoint()
    {
        var doc = Demo().Doc;
        var groups = JointGroups.Build(doc);
        var set = doc.Joints!;

        Assert.Equal(3, groups.Count);

        foreach (var s in doc.Solids.AsSpan())
        {
            var joint = set.JointOf(s.Id);
            Assert.Equal(joint == 0 ? 0 : groups.GroupOfJoint(joint), groups.GroupOf(s));
        }

        // A point on the turret's top sits on the turret's part.
        var turret = set.Members[0].Solid;
        Assert.Equal(groups.SolidGroup(turret), groups.SolidGroup(JointGroups.SolidUnder(doc, new Vector3d(0, 6.5, -1), new Vector3d(0, 1, 0))));
    }

    [Fact]
    public void Pose_TurnsAboutThePivot_AndNests()
    {
        var rotor = new HullJoint { Id = 1, Kind = JointKind.Rotor, Speed = 600 };   // 60 rpm: a turn a second
        Assert.Equal(Math.PI, JointPose.Angle(rotor, 0.5), 9);

        // 10 deg/s over 30 deg, at rest on its target (0), then up to 20, down to -10.
        var hinge = new HullJoint { Id = 2, Kind = JointKind.Hinge, Min = -100, Max = 200, Speed = 100 };
        Assert.Equal(0, JointPose.Angle(hinge, 0), 9);
        Assert.Equal(20 * Math.PI / 180, JointPose.Angle(hinge, 2), 9);
        Assert.Equal(10 * Math.PI / 180, JointPose.Angle(hinge, 3), 9);
        Assert.Equal(-10 * Math.PI / 180, JointPose.Angle(hinge, 5), 9);

        // A rotor about +Y at (1, 0, 0) and a second, mounted on it, about +Y at (2, 0, 0): a half turn of each.
        var doc = new PlanDocument();
        var set = new HullJointSet();
        const int m = HullSolid.PositionUnitsPerMetre;
        set.Joints.Add(new HullJoint { Id = 1, Kind = JointKind.Rotor, Speed = 600, X = m });
        set.Joints.Add(new HullJoint { Id = 2, Kind = JointKind.Rotor, Speed = 600, X = 2 * m, Parent = 1 });
        doc.Joints = set;
        var groups = JointGroups.Build(doc);
        var rotations = new double[27];
        var translations = new Vector3d[3];
        JointPose.Transforms(groups, 0.5, rotations, translations);

        // Group 1: (2, 0, 0) turns to (0, 0, 0). Group 2: its own half turn takes (3, 0, 0) to (1, 0, 0), which its mount's half turn about (1, 0, 0) keeps.
        AssertNear(new Vector3d(0, 0, 0), Move(rotations, translations, 1, new Vector3d(2, 0, 0)));
        AssertNear(new Vector3d(1, 0, 0), Move(rotations, translations, 2, new Vector3d(3, 0, 0)));
        AssertNear(new Vector3d(5, 0, 0), Move(rotations, translations, 0, new Vector3d(5, 0, 0)));
    }

    [Fact]
    public void Remove_PassesSolidsAndJointsToItsMount()
    {
        var session = Demo();
        var rotor = session.Doc.Joints!.Joints.Single(j => j.Kind == JointKind.Rotor).Id;
        var turret = session.Doc.Joints.Members.First(x => x.Joint == rotor).Solid;
        var gun = JointCommands.AddJoint(session, JointKind.Hinge, new Vector3d(0, 5.5, -3), new Vector3d(1, 0, 0), turret);
        var barrel = session.Doc.Joints!.Members.Last(x => x.Joint == rotor).Solid;
        JointCommands.SetMember(session, barrel, gun);

        Assert.Equal(rotor, session.Doc.Joints!.Joints.Single(j => j.Id == gun).Parent);
        Assert.Equal(2, session.Doc.Joints.Depth(gun));

        JointCommands.RemoveJoint(session, rotor);
        Assert.Equal(0u, session.Doc.Joints!.Joints.Single(j => j.Id == gun).Parent);
        Assert.Equal(0u, session.Doc.Joints.JointOf(turret));
        Assert.Equal(gun, session.Doc.Joints.JointOf(barrel));

        JointCommands.SetMember(session, barrel, 0);
        Assert.Equal(0u, session.Doc.Joints!.JointOf(barrel));

        session.UndoLast();
        session.UndoLast();
        Assert.Equal(rotor, session.Doc.Joints!.JointOf(turret));
    }

    [Fact]
    public void Nesting_StopsAtThreeDeep()
    {
        var session = Demo();
        var mount = session.Doc.Joints!.Members[0].Solid;
        var last = session.Doc.Joints.Members[0].Joint;

        for (var depth = 2; depth <= HullJointSet.MaxDepth; depth++)
        {
            var next = JointCommands.AddJoint(session, JointKind.Rotor, new Vector3d(0, 6, -1), new Vector3d(0, 1, 0), mount);
            Assert.NotEqual(0u, next);
            JointCommands.SetMember(session, mount, next);
            last = next;
        }

        Assert.Equal(HullJointSet.MaxDepth, session.Doc.Joints!.Depth(last));
        Assert.Equal(0u, JointCommands.AddJoint(session, JointKind.Rotor, new Vector3d(0, 6, -1), new Vector3d(0, 1, 0), mount));
    }

    [Fact]
    public void Snap_PointsPlusYAlongTheAxis()
    {
        foreach (var axis in new[] { new Vector3d(1, 0, 0), new Vector3d(0, 0, -1), new Vector3d(0, 1, 0) })
            AssertNear(axis, new HullJoint { Rotation = JointCommands.Snap(axis) }.Axis);
    }

    private static Vector3d Move(double[] r, Vector3d[] t, int g, Vector3d p)
    {
        var q = JointPose.Apply(r.AsSpan(g * 9, 9), p);
        return new Vector3d(q.X + t[g].X, q.Y + t[g].Y, q.Z + t[g].Z);
    }

    private static void AssertNear(Vector3d expected, Vector3d actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }
}
