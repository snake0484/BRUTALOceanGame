using System.Numerics;
using BrutalGame.Designer.Mesh;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

public class MirrorTests
{
    private const int M = HullBrush.PositionUnitsPerMetre;

    private static float WorldDistance(in HullBrush b, Vector3d p)
    {
        Span<double> m = stackalloc double[9];
        BrushRotation.Matrix(b.Rotation, m);
        double dx = p.X - b.PositionX, dy = p.Y - b.PositionY, dz = p.Z - b.PositionZ;
        var local = new Vector3((float)(m[0] * dx + m[3] * dy + m[6] * dz), (float)(m[1] * dx + m[4] * dy + m[7] * dz), (float)(m[2] * dx + m[5] * dy + m[8] * dz));
        return BrushGeometry.Distance(b.Shape, local, BrushGeometry.Half(b));
    }

    /// <summary>Every primitive under every plane combination: the mirrored brush's field at S·p is the original's at p.</summary>
    [Fact]
    public void PrimitiveReflections_AreTheMirrorImage()
    {
        var rng = new Random(3);

        foreach (var shape in new[] { BrushShape.Box, BrushShape.Wedge, BrushShape.Cylinder })
        {
            for (var trial = 0; trial < 12; trial++)
            {
                var brush = new HullBrush
                {
                    Shape = shape,
                    PosX = rng.Next(-8, 9) * 256,
                    PosY = rng.Next(-8, 9) * 256,
                    PosZ = rng.Next(-8, 9) * 256,
                    Rotation = BrushRotation.FromSteps(rng.Next(24), rng.Next(24), rng.Next(24)),
                    HalfX = (ushort)rng.Next(128, 900),
                    HalfY = (ushort)rng.Next(128, 900),
                    HalfZ = (ushort)rng.Next(128, 900),
                };

                for (var mask = 1; mask < 8; mask++)
                {
                    Assert.True(BrushMirror.Mirror(brush, mask, null, out var mirrored, out _));
                    var (sx, sy, sz) = BrushMirror.Signs(mask);

                    for (var s = 0; s < 40; s++)
                    {
                        var p = new Vector3d(rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8);
                        var q = new Vector3d(p.X * sx, p.Y * sy, p.Z * sz);
                        Assert.Equal(WorldDistance(brush, p), WorldDistance(mirrored, q), 3);
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
        var a = HullCommands.AddBrush(session, BrushShape.Wedge, BrushOp.Add, 4 * M, M, 2 * M, 512, 256, 768, BrushRotation.FromSteps(3, 0, 2), UndoTests.Alloy);
        Assert.Equal(2, session.Doc.Brushes.Count);
        var partner = session.Doc.Brushes[1];
        Assert.Equal(-4 * M, partner.PosX);

        // A brush on the plane is its own reflection: no copy.
        HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Add, 0, 6 * M, 0, 512, 512, 512, 0, UndoTests.Alloy);
        Assert.Equal(3, session.Doc.Brushes.Count);

        session.TryGetBrush(a, out var moved);
        moved.PosY += 2 * M;
        moved.Rotation = BrushRotation.AddSteps(moved.Rotation, 1, 4);
        Assert.True(HullCommands.SetBrush(session, a, moved));
        session.TryGetBrush(partner.Id, out var follower);
        Assert.Equal(3 * M, follower.PosY);
        Assert.True(BrushMirror.Mirror(moved, 1, null, out var expected, out _));
        Assert.Equal(BrushRotation.Canonical(expected.Rotation), BrushRotation.Canonical(follower.Rotation));

        session.Selection.Set(a);
        HullCommands.DeleteSelected(session);
        Assert.Single(session.Doc.Brushes.AsSpan().ToArray());

        Assert.True(session.UndoLast());
        Assert.Equal(3, session.Doc.Brushes.Count);
    }

    [Fact]
    public void TwoPlanes_MakeFourCopies_AndHollowFollows()
    {
        var session = new DesignSession();
        session.Symmetry.X = true;
        session.Symmetry.Z = true;
        var a = HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Add, 5 * M, 0, 7 * M, 768, 768, 768, 0, UndoTests.Alloy);
        Assert.Equal(4, session.Doc.Brushes.Count);

        HullCommands.Hollow(session, a, 0.5);
        Assert.Equal(8, session.Doc.Brushes.Count);
        Assert.Equal(4, session.Doc.Brushes.AsSpan().ToArray().Count(b => b.Op == BrushOp.Subtract));

        HullCommands.Duplicate(session, a);
        Assert.Equal(12, session.Doc.Brushes.Count);
        Assert.Equal(12, session.Doc.Brushes.AsSpan().ToArray().Select(b => (b.PosX, b.PosZ, b.Op)).Distinct().Count());
    }

    /// <summary>Mesh brushes: the partner carries the reflected mesh and follows mesh edits; its bake is the mirror image.</summary>
    [Fact]
    public void MeshEdits_FollowToThePartner()
    {
        var session = new DesignSession();
        session.Symmetry.X = true;
        var a = HullCommands.AddBrush(session, BrushShape.Box, BrushOp.Add, 6 * M, 2 * M, 0, 512, 512, 768, BrushRotation.FromSteps(2, 0, 0), UndoTests.Alloy);
        var b = session.Doc.Brushes[1].Id;
        Assert.True(MeshCommands.ConvertToMesh(session, a));

        var mesh = MeshCommands.MeshOf(session, a)!;
        var builder = new MeshBuilder(mesh);
        var face = Enumerable.Range(0, builder.Faces.Count).First(f => MeshOps.Normal(builder, builder.Faces[f]).X > 0.9 || MeshOps.Normal(builder, builder.Faces[f]).Z > 0.9);
        Assert.True(MeshCommands.Apply(session, a, x => MeshOps.ExtrudeFaces(x, [face], M)));

        var meshA = MeshCommands.MeshOf(session, a)!;
        var meshB = MeshCommands.MeshOf(session, b)!;
        Assert.True(BrushMirror.Mirror(meshA, 1).ContentEquals(meshB));
        Assert.Equal(MeshIssue.None, MeshValidator.Validate(meshB, out _));

        using var bakeA = Bake(session, a);
        using var bakeB = Bake(session, b);
        Assert.Equal(bakeA.Mass.VolumeM3, bakeB.Mass.VolumeM3, 1);
        Assert.Equal(-bakeA.Mass.CentreOfMass.X, bakeB.Mass.CentreOfMass.X, 1);
        Assert.Equal(bakeA.Mass.CentreOfMass.Z, bakeB.Mass.CentreOfMass.Z, 1);

        while (session.UndoLast())
        {
        }

        Assert.Empty(session.Doc.Brushes.AsSpan().ToArray());
    }

    private static HullBaker Bake(DesignSession session, uint id)
    {
        session.TryGetBrush(id, out var brush);
        var baker = new HullBaker(2);
        baker.Start([brush], session.Doc.Meshes, [800]);
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
