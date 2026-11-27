using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.Tests;

/// <summary>Wall profiles: half a hallway's section, rooms cut as their walled shape, plan bytes, validation, commands.</summary>
public class WallTests
{
    private const double K = 1.0 / HullSolid.PositionUnitsPerMetre;
    private static readonly DefId Alloy = DefId.FromName("mat.hull.alloy");

    /// <summary>A 10 × 3 × 14 m subtract box room.</summary>
    private static uint Room(DesignSession session) =>
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 0, 0, 0, 5 * 256, (ushort)(1.5 * 256), 7 * 256, SolidRotation.Identity, Alloy);

    [Theory]
    [InlineData(WallPreset.Straight)]
    [InlineData(WallPreset.Chamfered)]
    [InlineData(WallPreset.Coved)]
    [InlineData(WallPreset.Sloped)]
    public void Presets_AreValidWalls(WallPreset preset) =>
        Assert.True(WallGeometry.IsValid(WallGeometry.Preset(preset, 2.4, 0.3)));

    [Fact]
    public void FromHallway_TakesTheRightHalf_WallAtZero()
    {
        // A 1.2 × 2.2 rectangle: straight up the side; its flat ceiling is the room's own.
        var rectangle = WallGeometry.FromHallway(HallwayGeometry.Preset(HallwayPreset.Rectangle, 1.2, 2.2))!;
        Assert.Equal([new ProfilePoint(0, 0), new ProfilePoint(0, (int)Math.Round(2.2 * 1024))], rectangle);

        // An arch: up the side, then curving in to its middle at the top.
        var arch = WallGeometry.FromHallway(HallwayGeometry.Preset(HallwayPreset.Arch, 1.2, 2.2))!;
        Assert.True(WallGeometry.IsValid(arch));
        Assert.Equal(0, arch[0].X);
        Assert.Equal(2.2, arch[^1].Y * K, 2);
        Assert.Equal(0.6, arch[^1].X * K, 2);
    }

    [Fact]
    public void Room_IsTheBoxInsetByTheWall_FloorAndCeilingHalvesKept()
    {
        var box = new HullSolid { Shape = SolidShape.Box, Op = SolidOp.Subtract, HalfX = 5 * 256, HalfY = (ushort)(1.5 * 256), HalfZ = 7 * 256, Rotation = SolidRotation.Identity };
        var wall = WallGeometry.Preset(WallPreset.Chamfered, 2.4, 0.3);
        var mesh = WallGeometry.Room(box, wall)!;

        Assert.Equal(MeshIssue.None, MeshValidator.Validate(mesh, out _));
        Assert.Equal(16, mesh.Vertices.Length);

        // Floor ring inset 0.3 at -1.5; the chamfers keep 0.3 m tall in a 3 m room (not stretched).
        var ys = mesh.Vertices.ToArray().Select(v => Math.Round(v.Y * K, 2)).Distinct().Order().ToArray();
        Assert.Equal([-1.5, -1.2, 1.2, 1.5], ys);
        Assert.Equal(4.7, mesh.Vertices.ToArray().Where(v => v.Y * K < -1.49).Max(v => v.X) * K, 3);
        Assert.Equal(5.0, mesh.Vertices.ToArray().Max(v => v.X) * K, 3);
    }

    [Fact]
    public void Room_LowerThanTheProfile_SqueezesIt()
    {
        var box = new HullSolid { Shape = SolidShape.Box, HalfX = 2 * 256, HalfY = 256, HalfZ = 2 * 256, Rotation = SolidRotation.Identity };
        var mesh = WallGeometry.Room(box, WallGeometry.Preset(WallPreset.Sloped, 4.0, 0.5))!;

        Assert.Equal(MeshIssue.None, MeshValidator.Validate(mesh, out _));
        Assert.Equal(1.0, mesh.Vertices.ToArray().Max(v => v.Y) * K, 3);
        Assert.Equal(-1.0, mesh.Vertices.ToArray().Min(v => v.Y) * K, 3);
    }

    [Fact]
    public void Commands_SetRoomWall_PlanRoundTrips_AndProfileInUseStays()
    {
        var session = new DesignSession();
        var room = Room(session);
        var wall = InteriorCommands.AddProfile(session, "Chamfer", WallGeometry.Preset(WallPreset.Chamfered, 2.4, 0.3), kind: ProfileKind.Wall);
        Assert.True(InteriorCommands.SetRoomWall(session, room, wall));
        Assert.Equal(wall, session.Doc.Interior!.WallOf(room));

        // A hallway never picks a wall profile.
        InteriorCommands.AddHallway(session, new Vector3d(0, 0, 0), new Vector3d(0, 0, 4), Alloy);
        Assert.Equal(ProfileKind.Hallway, session.Doc.Interior.ProfileOf(session.Doc.Interior.Hallways[0])!.Kind);

        Assert.False(InteriorCommands.RemoveProfile(session, wall));

        var doc = session.Doc;
        Assert.Equal(PlanFormat.WallFormat, PlanFormat.FormatOf(doc));
        Assert.True(PlanFormat.TryDecode(PlanFormat.Encode(doc), out var back, out var error), error.ToString());
        Assert.Equal(PlanFormat.HashBody(doc), PlanFormat.HashBody(back));
        Assert.Equal(ProfileKind.Wall, back.Interior!.Profiles[wall].Kind);
        Assert.Equal(wall, back.Interior.WallOf(room));

        var log = new ValidationLog();
        PlanValidator.Validate(back, log);
        Assert.DoesNotContain(log.Issues, i => i.Code is IssueCode.RoomWall or IssueCode.WallShape);

        // The bake swaps the box for its walled mesh, in place.
        var expansion = WallGeometry.Expand(back.Interior, back.Solids.AsSpan(), back.Meshes.Count);
        Assert.Single(expansion.Rooms);
        Assert.Equal((0, room), (expansion.Rooms[0].Index, expansion.Rooms[0].Solid.Id));
        Assert.Equal(SolidShape.Mesh, expansion.Rooms[0].Solid.Shape);

        Assert.True(InteriorCommands.SetRoomWall(session, room, -1));
        Assert.Equal(-1, session.Doc.Interior!.WallOf(room));
    }

    [Fact]
    public void PlansWithoutWalls_KeepTheRouteFormat()
    {
        var session = new DesignSession();
        InteriorCommands.AddHallway(session, new Vector3d(0, 0, 0), new Vector3d(0, 0, 4), Alloy);
        Assert.Equal(PlanFormat.RouteFormat, PlanFormat.FormatOf(session.Doc));
    }

    [Fact]
    public void Validator_FlagsARoomWallOnAHallwayProfile_OrAMissingSolid()
    {
        var doc = new PlanDocument();
        doc.Materials.Add(Alloy);
        doc.Solids.Add(new HullSolid { Id = 1, Shape = SolidShape.Box, Op = SolidOp.Subtract, HalfX = 512, HalfY = 512, HalfZ = 512, Rotation = SolidRotation.Identity });
        doc.Interior = new HullInterior();
        doc.Interior.Profiles.Add(InteriorCommands.Default());
        doc.Interior.RoomWalls.Add(new RoomWall(1, 0));
        doc.Interior.RoomWalls.Add(new RoomWall(9, 0));

        var log = new ValidationLog();
        PlanValidator.Validate(doc, log);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.RoomWall && i.Element.Id == 1);
        Assert.Contains(log.Issues, i => i.Code == IssueCode.RoomWall && i.Element.Id == 9);
    }
}
