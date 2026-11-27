using BrutalGame.Designer.Mesh;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

public class MeshTests
{
    private const int M = HullSolid.PositionUnitsPerMetre;

    private static HullMesh Box(int hx = 2 * M, int hy = M, int hz = 3 * M / 2)
    {
        var b = new MeshBuilder();
        MeshPrimitives.Box(b, hx, hy, hz);
        return b.Build();
    }

    /// <summary>Exact enclosed volume in m³ of the subdivided triangles.</summary>
    private static double Volume(HullMesh mesh)
    {
        var t = MeshSubdivision.Triangulate(mesh);
        double v = 0;

        for (var i = 0; i < t.Indices.Length; i += 3)
        {
            MeshVertex a = t.Vertices[t.Indices[i]], b = t.Vertices[t.Indices[i + 1]], c = t.Vertices[t.Indices[i + 2]];
            v += (double)a.X * ((double)b.Y * c.Z - (double)b.Z * c.Y) - (double)a.Y * ((double)b.X * c.Z - (double)b.Z * c.X) + (double)a.Z * ((double)b.X * c.Y - (double)b.Y * c.X);
        }

        return v / 6.0 / ((double)M * M * M);
    }

    private static void AssertValid(HullMesh mesh)
    {
        var issue = MeshValidator.Validate(mesh, out var where);
        Assert.True(issue == MeshIssue.None, $"{issue} at {where}");
    }

    private static HullMesh Run(HullMesh mesh, Func<MeshBuilder, bool> op)
    {
        var b = new MeshBuilder(mesh);
        Assert.True(op(b), MeshOps.Error);
        return b.Build();
    }

    private static int FaceWithNormal(HullMesh mesh, int axis, int sign)
    {
        var b = new MeshBuilder(mesh);

        for (var f = 0; f < b.Faces.Count; f++)
        {
            var n = MeshOps.Normal(b, b.Faces[f]);
            var c = axis == 0 ? n.X : axis == 1 ? n.Y : n.Z;

            if (c * sign > 0.99)
                return f;
        }

        throw new InvalidOperationException("no such face");
    }

    [Fact]
    public void Primitives_AreValidClosedSolids()
    {
        foreach (var shape in new[] { SolidShape.Box, SolidShape.Wedge, SolidShape.Cylinder })
        {
            var solid = new HullSolid { Shape = shape, HalfX = 512, HalfY = 768, HalfZ = 1024 };

            if (shape == SolidShape.Cylinder)
                solid.HalfZ = solid.HalfX;

            var mesh = MeshPrimitives.FromSolid(solid);
            AssertValid(mesh);
            var expected = SolidGeometry.Volume(solid);
            var tolerance = shape == SolidShape.Cylinder ? 0.03 : 1e-9;
            Assert.Equal(1.0, Volume(mesh) / expected, tolerance);
        }
    }

    [Fact]
    public void Subdivision_KeepsCreasedCubeAndRoundsAnUncreasedOne()
    {
        var cube = Box(M, M, M);

        foreach (var levels in new byte[] { 1, 2 })
        {
            var b = new MeshBuilder(cube) { Levels = levels };
            var creased = b.Build();
            AssertValid(creased);
            Assert.Equal(8.0, Volume(creased), 9);
            Assert.Equal(6 * (int)Math.Pow(4, levels) * 2, MeshSubdivision.Triangulate(creased).TriangleCount);

            b.Creases.Clear();
            var smooth = b.Build();
            AssertValid(smooth);
            Assert.InRange(Volume(smooth), 2.0, 7.5);
        }
    }

    [Fact]
    public void Subdivision_IsDeterministic()
    {
        var b = new MeshBuilder(Box()) { Levels = 2 };
        b.Creases.Clear();
        var mesh = b.Build();
        var a = MeshSubdivision.Triangulate(mesh);
        var c = MeshSubdivision.Triangulate(new MeshBuilder(mesh).Build());

        Assert.Equal(a.Vertices, c.Vertices);
        Assert.Equal(a.Indices, c.Indices);
    }

    [Fact]
    public void Extrude_AddsTheSweptVolume()
    {
        var box = Box();
        var top = FaceWithNormal(box, 1, 1);
        var extruded = Run(box, b => MeshOps.ExtrudeFaces(b, [top], M));

        AssertValid(extruded);
        Assert.Equal(4 * 2 * 3 + 4 * 3 * 1, Volume(extruded), 9);
        Assert.Equal(10, extruded.FaceCount);
    }

    [Fact]
    public void InsetThenExtrudeInward_CutsAPocket()
    {
        var box = Box();
        var top = FaceWithNormal(box, 1, 1);
        var inset = Run(box, b => MeshOps.InsetFaces(b, [top], M / 2));
        AssertValid(inset);
        Assert.Equal(24.0, Volume(inset), 9);

        var pocket = Run(inset, b => MeshOps.ExtrudeFaces(b, [top], -M));
        AssertValid(pocket);
        Assert.True(Volume(pocket) < 24.0);
    }

    [Fact]
    public void LoopCut_SplitsTheRingAndKeepsVolume()
    {
        var box = Box();
        var b0 = new MeshBuilder(box);
        var xEdge = Enumerable.Range(0, b0.Vertices.Count).SelectMany(a => Enumerable.Range(0, b0.Vertices.Count).Select(c => (a, c)))
            .First(e => b0.Vertices[e.a].X < 0 && b0.Vertices[e.c].X > 0 && b0.Vertices[e.a].Y == b0.Vertices[e.c].Y && b0.Vertices[e.a].Z == b0.Vertices[e.c].Z);
        var cut = Run(box, b => MeshOps.LoopCut(b, xEdge, 0.5));

        AssertValid(cut);
        Assert.Equal(10, cut.FaceCount);
        Assert.Equal(12, cut.Vertices.Length);
        Assert.Equal(24.0, Volume(cut), 9);
        Assert.Equal(4, cut.Vertices.Count(v => v.X == 0));
    }

    [Fact]
    public void Bevel_ChamfersABoxEdge()
    {
        var box = Box();
        var b0 = new MeshBuilder(box);
        var edge = (0, 1);
        var bevelled = Run(box, b => MeshOps.BevelEdges(b, [edge], M / 4));

        AssertValid(bevelled);
        Assert.Equal(7, bevelled.FaceCount);
        MeshVertex p = b0.Vertices[0], q = b0.Vertices[1];
        var length = Math.Sqrt(Math.Pow(p.X - q.X, 2) + Math.Pow(p.Y - q.Y, 2) + Math.Pow(p.Z - q.Z, 2)) / M;
        Assert.Equal(24.0 - 0.25 * 0.25 / 2 * length, Volume(bevelled), 6);
    }

    [Fact]
    public void Bridge_DrillsATunnel()
    {
        var box = Box();
        var px = FaceWithNormal(box, 0, 1);
        var nx = FaceWithNormal(box, 0, -1);
        var inset = Run(box, b => MeshOps.InsetFaces(b, [px, nx], M / 2));
        var tunnel = Run(inset, b => MeshOps.BridgeFaces(b, px, nx));

        AssertValid(tunnel);
        var k = 1.0 - 0.5 / Math.Sqrt(1.0 + 1.5 * 1.5);
        Assert.Equal(24.0 - 4.0 * (2.0 * k) * (3.0 * k), Volume(tunnel), 2);
    }

    [Fact]
    public void KnifeThenMirror_IsSymmetricAndValid()
    {
        var b = new MeshBuilder();
        MeshPrimitives.Wedge(b, 2 * M, M, M);
        b.Vertices[1] = b.Vertices[1] with { X = 3 * M };
        var lopsided = b.Build();
        AssertValid(lopsided);

        var knifed = Run(lopsided, x => MeshOps.Knife(x, 0, 0));
        AssertValid(knifed);
        Assert.Equal(Volume(lopsided), Volume(knifed), 6);

        var mirrored = Run(knifed, MeshOps.MirrorX);
        AssertValid(mirrored);
        var xs = mirrored.Vertices.Select(v => v.X).Order().ToArray();
        Assert.Equal(xs, xs.Select(x => -x).Order().ToArray());
    }

    [Fact]
    public void Mirror_OfAMeshWithNoSeam_SplitsTheFacesAcrossTheMiddle()
    {
        // A plain box has no vertices at X = 0: its top, bottom, front and back faces cross the plane.
        var box = Box();
        var mirrored = Run(box, MeshOps.MirrorX);

        AssertValid(mirrored);
        Assert.Equal(Volume(box), Volume(mirrored), 6);
        Assert.Contains(mirrored.Vertices, v => v.X == 0);
        var xs = mirrored.Vertices.Select(v => v.X).Order().ToArray();
        Assert.Equal(xs, xs.Select(x => -x).Order().ToArray());
    }

    [Fact]
    public void MirroredDrag_KeepsTheSeamJoined()
    {
        var mirrored = Run(Run(Box(), x => MeshOps.Knife(x, 0, 0)), MeshOps.MirrorX);
        var top = mirrored.Vertices.Max(v => v.Y);
        var seam = Enumerable.Range(0, mirrored.Vertices.Length).Where(i => mirrored.Vertices[i].X == 0 && mirrored.Vertices[i].Y == top).ToArray();
        Assert.NotEmpty(seam);

        // Dragged along X and up: the seam vertices only rise, and the mesh stays closed.
        var b = new MeshBuilder(mirrored);
        MeshOps.TranslateMirrored(b, seam, M / 2, M / 4, 0);
        Assert.True(MeshOps.MirrorX(b));
        var raised = b.Build();
        AssertValid(raised);
        Assert.Contains(raised.Vertices, v => v.X == 0 && v.Y == top + M / 4);
        Assert.DoesNotContain(raised.Vertices, v => v.Y == top + M / 4 && v.X != 0);

        // A vertex picked on the -X side moves; its +X twin follows mirrored.
        var left = Array.FindIndex(raised.Vertices, v => v.X < 0);
        var p = raised.Vertices[left];
        b = new MeshBuilder(raised);
        MeshOps.TranslateMirrored(b, [left], -M / 4, 0, 0);
        Assert.True(MeshOps.MirrorX(b));
        var pulled = b.Build();
        AssertValid(pulled);
        Assert.Contains(pulled.Vertices, v => v == new MeshVertex(p.X - M / 4, p.Y, p.Z));
        Assert.Contains(pulled.Vertices, v => v == new MeshVertex(-p.X + M / 4, p.Y, p.Z));
    }

    [Fact]
    public void Validator_RefusesBrokenMeshes()
    {
        var b = new MeshBuilder(Box());
        b.Faces.RemoveAt(0);
        b.Panels.RemoveAt(0);
        Assert.Equal(MeshIssue.Open, MeshValidator.Validate(b.Build(), out _));

        b = new MeshBuilder(Box());
        b.Faces[2] = b.Faces[2].Reverse().ToArray();
        Assert.Equal(MeshIssue.NonManifold, MeshValidator.Validate(b.Build(), out _));

        b = new MeshBuilder(Box());

        for (var f = 0; f < b.Faces.Count; f++)
            b.Faces[f] = b.Faces[f].Reverse().ToArray();

        Assert.Equal(MeshIssue.InsideOut, MeshValidator.Validate(b.Build(), out _));

        // Two boxes in one mesh, overlapping: closed and oriented, but the shells cross.
        b = new MeshBuilder();
        MeshPrimitives.Box(b, M, M, M);
        var second = new MeshBuilder();
        MeshPrimitives.Box(second, M, M, M);
        var offset = b.Vertices.Count;

        foreach (var v in second.Vertices)
            b.AddVertex(new MeshVertex(v.X + M, v.Y + M / 2, v.Z + M / 3));

        foreach (var f in second.Faces)
            b.AddFace(f.Select(c => c + offset).ToArray());

        Assert.Equal(MeshIssue.SelfIntersection, MeshValidator.Validate(b.Build(), out _));

        b = new MeshBuilder(Box());
        b.Vertices[0] = b.Vertices[1] with { X = b.Vertices[1].X - 4 };
        Assert.Equal(MeshIssue.ShortEdge, MeshValidator.Validate(b.Build(), out _));
    }

    /// <summary>A box converted to a mesh solid bakes to the same solid as the box primitive: same mass and volume.</summary>
    [Fact]
    public void ConvertedBox_BakesLikeThePrimitive()
    {
        var session = new DesignSession();
        session.Snap.Step = GridStep.Eighth;
        var id = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 1152, 2048, -640, 4 * 256, 2 * 256, 3 * 256, SolidRotation.FromSteps(3, 0, 0), UndoTests.Alloy);
        HullCommands.Hollow(session, id, 0.5);
        int[] densities = [800];

        using var primitive = new HullBaker(4);
        primitive.Start(session.Doc.Solids.AsSpan(), session.Doc.Meshes, densities);
        primitive.Wait();

        Assert.True(MeshCommands.ConvertToMesh(session, id));
        Assert.True(MeshCommands.ConvertToMesh(session, session.Doc.Solids[1].Id));
        using var mesh = new HullBaker(4);
        mesh.Start(session.Doc.Solids.AsSpan(), session.Doc.Meshes, densities);
        mesh.Wait();

        Assert.Equal(primitive.Mass.VolumeM3, mesh.Mass.VolumeM3, 2);
        Assert.InRange(mesh.Mass.VolumeM3, 86.0, 88.0);
        Assert.InRange(mesh.TriangleCount, primitive.TriangleCount * 0.8, primitive.TriangleCount * 1.2);

        var log = new ValidationLog();
        PlanValidator.Validate(session.Doc, log);
        Assert.Equal(0, log.Errors);
    }

    [Fact]
    public void MeshEdits_UndoToExactBytesAndRoundTripTheFile()
    {
        var session = new DesignSession();
        var id = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 1024, 512, 768, 0, UndoTests.Alloy);
        var start = PlanFormat.EncodeBody(session.Doc);

        Assert.True(MeshCommands.ConvertToMesh(session, id));
        var top = FaceWithNormal(MeshCommands.MeshOf(session, id)!, 1, 1);
        Assert.True(MeshCommands.Apply(session, id, b => MeshOps.ExtrudeFaces(b, [top], M)));
        Assert.True(MeshCommands.Apply(session, id, b => MeshOps.InsetFaces(b, [top], M / 4)));
        Assert.True(MeshCommands.SetLevels(session, id, 1));
        var end = PlanFormat.EncodeBody(session.Doc);
        Assert.True(session.TryGetSolid(id, out var solid));
        Assert.True(solid.HalfY >= 2 * 256 * 3 / 2);

        var copy = session.Doc.Clone();
        PlanCanonical.Canonicalise(copy);
        var bytes = PlanFormat.Encode(copy);
        Assert.True(PlanFormat.TryDecode(bytes, out var loaded, out var error), error.ToString());
        Assert.Equal(bytes, PlanFormat.Encode(loaded));
        Assert.Equal(PlanFormat.HashBody(copy), PlanFormat.HashBody(loaded));

        while (session.UndoLast())
        {
        }

        Assert.Equal(PlanFormat.EncodeBody(new PlanDocument()), PlanFormat.EncodeBody(session.Doc));

        while (session.RedoNext())
        {
        }

        Assert.Equal(end, PlanFormat.EncodeBody(session.Doc));
        Assert.NotEqual(start, end);
    }

    [Fact]
    public void SharedMeshes_DedupeInTheCanonicalTable()
    {
        var session = new DesignSession();
        var a = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 0, 0, 0, 512, 512, 512, 0, UndoTests.Alloy);
        var c = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 8 * M, 0, 0, 512, 512, 512, 0, UndoTests.Alloy);
        MeshCommands.ConvertToMesh(session, a);
        MeshCommands.ConvertToMesh(session, c);
        Assert.Equal(2, session.Doc.Meshes.Count);

        var copy = session.Doc.Clone();
        PlanCanonical.Canonicalise(copy);
        Assert.Single(copy.Meshes);
        Assert.Equal(copy.Solids[0].Param, copy.Solids[1].Param);
    }

    [Fact]
    public void MeshSolid_Picks()
    {
        var session = new DesignSession();
        var id = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, 0, 512, 1024, 512, 0, UndoTests.Alloy);
        MeshCommands.ConvertToMesh(session, id);

        Assert.Equal(id, Tools.HullPicking.Pick(session.Doc, new Vector3d(10, 0, 0), new Vector3d(-1, 0, 0), out var d));
        Assert.Equal(8.0, d, 1);
        Assert.Equal(0u, Tools.HullPicking.Pick(session.Doc, new Vector3d(10, 5, 0), new Vector3d(-1, 0, 0), out _));
    }
}
