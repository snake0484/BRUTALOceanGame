using System.Numerics;
using BrutalGame.Designer.Mesh;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

public class MirrorTests
{
    private const int M = HullSolid.PositionUnitsPerMetre;

    private static float WorldDistance(in HullSolid b, Vector3d p)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(b.Rotation, m);
        double dx = p.X - b.PositionX, dy = p.Y - b.PositionY, dz = p.Z - b.PositionZ;
        var local = new Vector3((float)(m[0] * dx + m[3] * dy + m[6] * dz), (float)(m[1] * dx + m[4] * dy + m[7] * dz), (float)(m[2] * dx + m[5] * dy + m[8] * dz));
        return SolidGeometry.Distance(b.Shape, local, SolidGeometry.Half(b));
    }

    /// <summary>Every primitive under every plane combination: the mirrored solid's field at S·p is the original's at p.</summary>
    [Fact]
    public void PrimitiveReflections_AreTheMirrorImage()
    {
        var rng = new Random(3);

        foreach (var shape in new[] { SolidShape.Box, SolidShape.Wedge, SolidShape.Cylinder })
        {
            for (var trial = 0; trial < 12; trial++)
            {
                var solid = new HullSolid
                {
                    Shape = shape,
                    PosX = rng.Next(-8, 9) * 256,
                    PosY = rng.Next(-8, 9) * 256,
                    PosZ = rng.Next(-8, 9) * 256,
                    Rotation = SolidRotation.FromSteps(rng.Next(24), rng.Next(24), rng.Next(24)),
                    HalfX = (ushort)rng.Next(128, 900),
                    HalfY = (ushort)rng.Next(128, 900),
                    HalfZ = (ushort)rng.Next(128, 900),
                };

                for (var mask = 1; mask < 8; mask++)
                {
                    Assert.True(SolidMirror.Mirror(solid, mask, null, out var mirrored, out _));
                    var (sx, sy, sz) = SolidMirror.Signs(mask);

                    for (var s = 0; s < 40; s++)
                    {
                        var p = new Vector3d(rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8);
                        var q = new Vector3d(p.X * sx, p.Y * sy, p.Z * sz);
                        Assert.Equal(WorldDistance(solid, p), WorldDistance(mirrored, q), 3);
                    }
                }
            }
        }
    }

    [Fact]
    public void AddMoveDelete_KeepMirrorPartners()
    {
        var session = new DesignSession();
        session.Symmetry.X = true;
        var a = HullCommands.AddSolid(session, SolidShape.Wedge, SolidOp.Add, 4 * M, M, 2 * M, 512, 256, 768, SolidRotation.FromSteps(3, 0, 2), UndoTests.Alloy);
        Assert.Equal(2, session.Doc.Solids.Count);
        var partner = session.Doc.Solids[1];
        Assert.Equal(-4 * M, partner.PosX);

        // A solid on the plane is its own reflection: no copy.
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 6 * M, 0, 512, 512, 512, 0, UndoTests.Alloy);
        Assert.Equal(3, session.Doc.Solids.Count);

        session.TryGetSolid(a, out var moved);
        moved.PosY += 2 * M;
        moved.Rotation = SolidRotation.AddSteps(moved.Rotation, 1, 4);
        Assert.True(HullCommands.SetSolid(session, a, moved));
        session.TryGetSolid(partner.Id, out var follower);
        Assert.Equal(3 * M, follower.PosY);
        Assert.True(SolidMirror.Mirror(moved, 1, null, out var expected, out _));
        Assert.Equal(SolidRotation.Canonical(expected.Rotation), SolidRotation.Canonical(follower.Rotation));

        session.Selection.Set(a);
        HullCommands.DeleteSelected(session);
        Assert.Single(session.Doc.Solids.AsSpan().ToArray());

        Assert.True(session.UndoLast());
        Assert.Equal(3, session.Doc.Solids.Count);
    }

    [Fact]
    public void TwoPlanes_MakeFourCopies_AndHollowFollows()
    {
        var session = new DesignSession();
        session.Symmetry.X = true;
        session.Symmetry.Z = true;
        var a = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 5 * M, 0, 7 * M, 768, 768, 768, 0, UndoTests.Alloy);
        Assert.Equal(4, session.Doc.Solids.Count);

        HullCommands.Hollow(session, a, 0.5);
        Assert.Equal(8, session.Doc.Solids.Count);
        Assert.Equal(4, session.Doc.Solids.AsSpan().ToArray().Count(b => b.Op == SolidOp.Subtract));

        HullCommands.Duplicate(session, a);
        Assert.Equal(12, session.Doc.Solids.Count);
        Assert.Equal(12, session.Doc.Solids.AsSpan().ToArray().Select(b => (b.PosX, b.PosZ, b.Op)).Distinct().Count());
    }

    /// <summary>Mesh solids: the partner carries the reflected mesh and follows mesh edits; its bake is the mirror image.</summary>
    [Fact]
    public void MeshEdits_FollowToThePartner()
    {
        var session = new DesignSession();
        session.Symmetry.X = true;
        var a = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 6 * M, 2 * M, 0, 512, 512, 768, SolidRotation.FromSteps(2, 0, 0), UndoTests.Alloy);
        var b = session.Doc.Solids[1].Id;
        Assert.True(MeshCommands.ConvertToMesh(session, a));

        var mesh = MeshCommands.MeshOf(session, a)!;
        var builder = new MeshBuilder(mesh);
        var face = Enumerable.Range(0, builder.Faces.Count).First(f => MeshOps.Normal(builder, builder.Faces[f]).X > 0.9 || MeshOps.Normal(builder, builder.Faces[f]).Z > 0.9);
        Assert.True(MeshCommands.Apply(session, a, x => MeshOps.ExtrudeFaces(x, [face], M)));

        var meshA = MeshCommands.MeshOf(session, a)!;
        var meshB = MeshCommands.MeshOf(session, b)!;
        Assert.True(SolidMirror.Mirror(meshA, 1).ContentEquals(meshB));
        Assert.Equal(MeshIssue.None, MeshValidator.Validate(meshB, out _));

        using var bakeA = Bake(session, a);
        using var bakeB = Bake(session, b);
        Assert.Equal(bakeA.Mass.VolumeM3, bakeB.Mass.VolumeM3, 1);
        Assert.Equal(-bakeA.Mass.CentreOfMass.X, bakeB.Mass.CentreOfMass.X, 1);
        Assert.Equal(bakeA.Mass.CentreOfMass.Z, bakeB.Mass.CentreOfMass.Z, 1);

        while (session.UndoLast())
        {
        }

        Assert.Empty(session.Doc.Solids.AsSpan().ToArray());
    }

    private static HullBaker Bake(DesignSession session, uint id)
    {
        session.TryGetSolid(id, out var solid);
        var baker = new HullBaker(2);
        baker.Start([solid], session.Doc.Meshes, [800]);
        baker.Wait();
        return baker;
    }

    [Fact]
    public void Front_IsPlusZ()
    {
        Assert.Equal(2, ShipAxes.FrontAxis);
        Assert.Equal(1, ShipAxes.FrontSign);
    }
}
