using System.Runtime.InteropServices;
using BrutalGame.Designer.Tools;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

public class HullBakeTests
{
    private const int Density = 800;
    private static readonly int[] Densities = [Density, 1100];

    private static HullBaker Bake(PlanDocument doc, int workers)
    {
        var baker = new HullBaker(workers);
        baker.Start(doc.Solids.AsSpan(), Densities);
        baker.Wait();
        return baker;
    }

    private static byte[] MeshBytes(HullBaker baker)
    {
        var bytes = new List<byte>();

        foreach (var brick in baker.Bricks)
        {
            bytes.AddRange(MemoryMarshal.AsBytes([brick.Key.X, brick.Key.Y, brick.Key.Z]));
            bytes.AddRange(MemoryMarshal.AsBytes(brick.VertexSpan));
            bytes.AddRange(MemoryMarshal.AsBytes(brick.IndexSpan));
        }

        return bytes.ToArray();
    }

    private static DesignSession Demo()
    {
        var session = new DesignSession();
        SampleHulls.HollowBoxWithCylinder(session, UndoTests.Alloy, UndoTests.Titanium);
        return session;
    }

    [Fact]
    public void SameSolids_GiveIdenticalMeshBytes()
    {
        var doc = Demo().Doc;
        using var a = Bake(doc, 1);
        using var b = Bake(doc, 6);
        using var c = Bake(doc.Clone(), 3);
        var bytes = MeshBytes(a);

        Assert.True(a.TriangleCount > 1000);
        Assert.Equal(bytes, MeshBytes(b));
        Assert.Equal(bytes, MeshBytes(c));
        Assert.Equal(a.Mass, b.Mass);
    }

    /// <summary>An edit re-bakes only the touched bricks, and the result is byte-identical to a cold bake of the new list.</summary>
    [Fact]
    public void IncrementalBake_MatchesColdBake()
    {
        var session = Demo();
        using var warm = Bake(session.Doc, 4);
        var total = warm.Bricks.Count;
        var last = session.Doc.Solids[session.Doc.Solids.Count - 1];
        last.PosY += 1024;
        HullCommands.SetSolid(session, last.Id, last);

        warm.Start(session.Doc.Solids.AsSpan(), Densities);
        warm.Wait();
        using var cold = Bake(session.Doc, 4);

        Assert.True(warm.LastRebaked < total, $"{warm.LastRebaked} of {total} bricks re-baked");
        Assert.True(warm.LastReused > 0);
        Assert.Equal(MeshBytes(cold), MeshBytes(warm));
        Assert.Equal(cold.Mass, warm.Mass);
    }

    /// <summary>
    /// A box hollowed by Hollow (0.5 m walls), faces on the 1/8 m grid: occupancy is exact for faces normal to an axis,
    /// so mass and volume equal the analytic values, and the centre of mass is the box centre.
    /// </summary>
    [Fact]
    public void HollowBox_MassMatchesAnalytic()
    {
        var session = new DesignSession();
        session.Snap.Step = GridStep.Eighth;
        var box = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 1152, 2048, -640, 4 * 256, 2 * 256, 3 * 256, 0, UndoTests.Alloy);
        HullCommands.Hollow(session, box, 0.5);
        using var baker = Bake(session.Doc, 4);
        var mass = baker.Mass;

        const double outer = 8.0 * 4.0 * 6.0;
        const double inner = 7.0 * 3.0 * 5.0;
        Assert.Equal(outer - inner, mass.VolumeM3, 9);
        Assert.Equal((outer - inner) * Density, mass.MassKg, 6);
        Assert.Equal((long)((outer - inner) * Density * 1000), mass.MassGrams);

        var com = mass.CentreOfMass;
        Assert.Equal(1.125, com.X, 9);
        Assert.Equal(2.0, com.Y, 9);
        Assert.Equal(-0.625, com.Z, 9);

        // Thin-walled box about its centre: Ixx = m_o (b² + c²) / 12 − m_i (b'² + c'²) / 12.
        static double Ixx(double b, double c, double rhoV) => rhoV * (b * b + c * c) / 12.0;
        var expected = Ixx(4, 6, outer * Density) - Ixx(3, 5, inner * Density);
        Assert.Equal(1.0, mass.Inertia.Xx / expected, 2);
    }

    [Fact]
    public void Primitives_VolumeNearAnalytic()
    {
        var session = new DesignSession();
        session.Snap.Step = GridStep.Eighth;
        var cylinder = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, 0, 3 * 256, 4 * 256, 3 * 256, SolidRotation.FromSteps(0, 6, 0), UndoTests.Alloy);
        var wedge = HullCommands.AddSolid(session, SolidShape.Wedge, SolidOp.Add, 40 * 1024, 0, 0, 2 * 256, 3 * 256, 4 * 256, 0, UndoTests.Alloy);
        using var baker = Bake(session.Doc, 4);
        session.TryGetSolid(cylinder, out var c);
        session.TryGetSolid(wedge, out var w);
        var expected = SolidGeometry.Volume(c) + SolidGeometry.Volume(w);

        Assert.Equal(1.0, baker.Mass.VolumeM3 / expected, 2);
    }

    /// <summary>
    /// Dual contouring puts a box's corners on the corners (within the 1/256-cell vertex snap and the 16-bit packing),
    /// even off the sample lattice; surface nets rounds them off by about a third of a cell.
    /// </summary>
    [Fact]
    public void DualContouring_KeepsBoxCornersSharp()
    {
        var session = new DesignSession();
        session.Snap.Enabled = false;
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 333, 1100, -77, 539, 300, 777, 0, UndoTests.Alloy);
        session.TryGetSolid(session.Selection.Primary, out var box);
        var half = SolidGeometry.Half(box);

        foreach (var mesher in new[] { HullMesher.DualContouring, HullMesher.SurfaceNets })
        {
            using var baker = new HullBaker(2) { Mesher = mesher, ExactSurfaces = false };
            baker.Start(session.Doc.Solids.AsSpan(), Densities);
            baker.Wait();
            var worst = 0.0;

            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3d(box.PositionX + ((c & 1) != 0 ? half.X : -half.X), box.PositionY + ((c & 2) != 0 ? half.Y : -half.Y), box.PositionZ + ((c & 4) != 0 ? half.Z : -half.Z));
                var nearest = double.MaxValue;

                foreach (var brick in baker.Bricks)
                {
                    foreach (var v in brick.VertexSpan)
                    {
                        var p = v.DecodePosition(HullBins.CellMetres);
                        double dx = brick.Origin.X + p.X - corner.X, dy = brick.Origin.Y + p.Y - corner.Y, dz = brick.Origin.Z + p.Z - corner.Z;
                        nearest = Math.Min(nearest, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    }
                }

                worst = Math.Max(worst, nearest);
            }

            if (mesher == HullMesher.DualContouring)
                Assert.True(worst < 0.005, $"dual contouring corner off by {worst:F4} m");
            else
                Assert.True(worst > 0.03, $"surface nets corner off by {worst:F4} m");
        }
    }

    /// <summary>A solid nothing else reaches is drawn from its own polygons: every vertex lies on its surface.</summary>
    [Fact]
    public void ExactSurfaces_LieOnTheSolid()
    {
        var session = new DesignSession();
        session.Snap.Enabled = false;
        var cylinder = HullCommands.AddSolid(session, SolidShape.Cylinder, SolidOp.Add, 0, 0, 0, 700, 1500, 700, SolidRotation.FromSteps(2, 5, 1), UndoTests.Alloy);
        var box = HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Add, 40 * 1024, 333, -77, 1100, 500, 900, SolidRotation.FromSteps(3, 0, 0), UndoTests.Alloy);
        Mesh.MeshCommands.ConvertToMesh(session, box);
        Mesh.MeshCommands.Apply(session, box, b =>
        {
            b.Creases.Clear();
            b.Levels = 1;
            return true;
        });

        using var baker = new HullBaker(2);
        baker.Start(session.Doc.Solids.AsSpan(), session.Doc.Meshes, Densities);
        baker.Wait();
        Assert.Equal(baker.Bricks.Count, baker.ExactBricks);

        session.TryGetSolid(cylinder, out var c);
        session.TryGetSolid(box, out var m);
        var mesh = MeshCache.Get(session.Doc.Meshes[m.Param]);
        var rotation = new double[9];
        var worst = 0.0;
        var vertices = 0;

        foreach (var brick in baker.Bricks)
        {
            foreach (var v in brick.VertexSpan)
            {
                var p = v.DecodePosition(HullBins.CellMetres);
                var ship = new Vector3d(brick.Origin.X + p.X, brick.Origin.Y + p.Y, brick.Origin.Z + p.Z);
                var near = ship.X < 20 ? c : m;
                SolidRotation.Matrix(near.Rotation, rotation);
                double dx = ship.X - near.PositionX, dy = ship.Y - near.PositionY, dz = ship.Z - near.PositionZ;
                var local = new System.Numerics.Vector3(
                    (float)(rotation[0] * dx + rotation[3] * dy + rotation[6] * dz),
                    (float)(rotation[1] * dx + rotation[4] * dy + rotation[7] * dz),
                    (float)(rotation[2] * dx + rotation[5] * dy + rotation[8] * dz));
                var distance = near.Shape == SolidShape.Mesh ? mesh.Distance(local, 1f) : Math.Abs(SolidGeometry.Distance(SolidShape.Cylinder, local, SolidGeometry.Half(near)));
                worst = Math.Max(worst, distance);
                vertices++;
            }
        }

        Assert.True(vertices > 100);
        Assert.True(worst < 0.002, $"exact vertex off the surface by {worst:F4} m");
    }

    /// <summary>Bricks a cut reaches fall back to the grid mesh; the rest of the hull stays exact.</summary>
    [Fact]
    public void ExactSurfaces_FallBackToTheGridWhereCut()
    {
        var session = new DesignSession();
        SampleHulls.MeshHull(session, UndoTests.Alloy, UndoTests.Titanium);
        using var baker = new HullBaker(2);
        baker.Start(session.Doc.Solids.AsSpan(), session.Doc.Meshes, Densities);
        baker.Wait();

        Assert.InRange(baker.ExactBricks, 1, baker.Bricks.Count - 1);

        using var grid = new HullBaker(2) { ExactSurfaces = false };
        grid.Start(session.Doc.Solids.AsSpan(), session.Doc.Meshes, Densities);
        grid.Wait();
        Assert.Equal(grid.Mass, baker.Mass);
        Assert.Equal(0, grid.ExactBricks);
    }

    [Fact]
    public void SubtractOnly_HasNoBricks()
    {
        var session = new DesignSession();
        HullCommands.AddSolid(session, SolidShape.Box, SolidOp.Subtract, 0, 0, 0, 512, 512, 512, 0, UndoTests.Alloy);
        using var baker = Bake(session.Doc, 2);

        Assert.Empty(baker.Bricks);
        Assert.Equal(0, baker.Mass.MassKg);
    }

    [Fact]
    public void Picking_FindsTheNearestSolid()
    {
        var session = Demo();
        var cylinder = session.Doc.Solids[2].Id;

        var hit = HullPicking.Pick(session.Doc, new Vector3d(0, 3, 30), new Vector3d(0, 0, -1), out var distance);
        Assert.Equal(cylinder, hit);
        Assert.Equal(20.0, distance, 2);

        Assert.Equal(0u, HullPicking.Pick(session.Doc, new Vector3d(0, 50, 30), new Vector3d(0, 0, -1), out _));
    }

    [Fact]
    public void ClosestOnAxis_IsTheCommonPerpendicular()
    {
        Assert.True(HullPicking.ClosestOnAxis(new Vector3d(5, 3, 10), new Vector3d(0, 0, -1), new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), out var along));
        Assert.Equal(5.0, along, 9);
        Assert.False(HullPicking.ClosestOnAxis(new Vector3d(0, 1, 0), new Vector3d(1, 0, 0), new Vector3d(0, 0, 0), new Vector3d(1, 0, 0), out _));
    }

    /// <summary>Wall time for a 100-solid hull: printed for the report, bounded loosely so a regression fails.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Bake100Solids_Timing()
    {
        var session = new DesignSession();
        SampleHulls.Bench(session, 100, UndoTests.Alloy, UndoTests.Titanium);
        var times = new List<float>();

        for (var i = 0; i < 5; i++)
        {
            using var baker = Bake(session.Doc, Math.Max(2, Environment.ProcessorCount - 2));
            times.Add(baker.LastBakeMs);
        }

        times.Sort();
        Assert.True(times[2] < 2000f, $"median bake {times[2]:F1} ms");
    }
}
