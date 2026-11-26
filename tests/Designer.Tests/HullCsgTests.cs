using BrutalGame.Ships;

namespace BrutalGame.Designer.Tests;

/// <summary>The exact drawn surface: analytic areas, closed (every edge shared once each way), coplanar faces resolved.</summary>
public class HullCsgTests
{
    private static HullSolid Solid(uint id, SolidShape shape, double x, double y, double z, double hx, double hy, double hz, SolidOp op = SolidOp.Add, uint rotation = 0, byte material = 0) => new()
    {
        Id = id,
        Shape = shape,
        Op = op,
        Material = material,
        Rotation = rotation,
        PosX = (int)Math.Round(x * 1024),
        PosY = (int)Math.Round(y * 1024),
        PosZ = (int)Math.Round(z * 1024),
        HalfX = (ushort)Math.Round(hx * 256),
        HalfY = (ushort)Math.Round(hy * 256),
        HalfZ = (ushort)Math.Round(hz * 256),
    };

    private static double Area(List<(Vector3d[] Points, byte Material)> polygons)
    {
        var area = 0.0;

        foreach (var (points, _) in polygons)
        {
            double x = 0, y = 0, z = 0;

            for (var i = 0; i < points.Length; i++)
            {
                var p = points[i];
                var q = points[(i + 1) % points.Length];
                x += (p.Y - q.Y) * (p.Z + q.Z);
                y += (p.Z - q.Z) * (p.X + q.X);
                z += (p.X - q.X) * (p.Y + q.Y);
            }

            area += Math.Sqrt(x * x + y * y + z * z) / 2.0;
        }

        return area;
    }

    /// <summary>Every directed edge has its reverse exactly once: closed, consistently wound, no T-junctions.</summary>
    private static void AssertClosed(List<(Vector3d[] Points, byte Material)> polygons)
    {
        var edges = new Dictionary<((long, long, long), (long, long, long)), int>();

        static (long, long, long) Key(Vector3d p) => ((long)Math.Round(p.X * 1e5), (long)Math.Round(p.Y * 1e5), (long)Math.Round(p.Z * 1e5));

        foreach (var (points, _) in polygons)
        {
            for (var i = 0; i < points.Length; i++)
            {
                var a = Key(points[i]);
                var b = Key(points[(i + 1) % points.Length]);

                if (a == b)
                    continue;

                edges[(a, b)] = edges.GetValueOrDefault((a, b)) + 1;
            }
        }

        foreach (var ((a, b), count) in edges)
        {
            Assert.Equal(1, count);
            Assert.True(edges.ContainsKey((b, a)), $"open edge {a} -> {b}");
        }
    }

    [Fact]
    public void Box_IsItsSixFaces()
    {
        var polygons = HullCsg.Polygons([Solid(1, SolidShape.Box, 4, 4, 4, 1, 2, 3)], [], false);

        Assert.Equal(6, polygons.Count);
        Assert.Equal(2 * (2 * 4 + 2 * 6 + 4 * 6), Area(polygons), 6);
        AssertClosed(polygons);
    }

    [Fact]
    public void ThroughHole_AddsItsWalls()
    {
        var polygons = HullCsg.Polygons([Solid(1, SolidShape.Box, 0, 0, 0, 2, 2, 2), Solid(2, SolidShape.Box, 0, 0, 0, 1, 1, 3, SolidOp.Subtract)], [], false);

        Assert.Equal(2 * (16 - 4) + 4 * 16 + 4 * 2 * 4, Area(polygons), 6);
        AssertClosed(polygons);
    }

    [Fact]
    public void FlushBoxes_ShowNoInnerFaceOrDuplicates()
    {
        // Side by side, sharing the x = 0 face and the top plane: the union is one 4 × 2 × 2 box.
        var polygons = HullCsg.Polygons([Solid(1, SolidShape.Box, -1, 0, 0, 1, 1, 1), Solid(2, SolidShape.Box, 1, 0, 0, 1, 1, 1)], [], false);

        Assert.Equal(2 * (4 * 2 + 4 * 2 + 2 * 2), Area(polygons), 6);
        AssertClosed(polygons);
    }

    [Fact]
    public void OverlappingAndRotated_StayClosed()
    {
        HullSolid[] solids =
        [
            Solid(1, SolidShape.Box, 0, 0, 0, 3, 2, 4),
            Solid(2, SolidShape.Cylinder, 0, 1.5, 3, 1.25, 2, 1.25, rotation: SolidRotation.FromSteps(0, 6, 0)),
            Solid(3, SolidShape.Wedge, 1, 2.5, -1, 1.5, 0.5, 2, rotation: SolidRotation.FromSteps(3, 0, 0)),
            Solid(4, SolidShape.Box, 3, 0, 0, 1, 1, 1, SolidOp.Subtract, SolidRotation.FromSteps(1, 2, 0)),
        ];

        AssertClosed(HullCsg.Polygons(solids, [], false));
    }

    [Fact]
    public void MeshSolids_CutAndJoinClosed()
    {
        var cube = MeshPrimitives.FromSolid(Solid(0, SolidShape.Box, 0, 0, 0, 1.5, 1.5, 1.5));
        var meshSolid = Solid(2, SolidShape.Mesh, 2, 0.5, 0, 1.5, 1.5, 1.5, rotation: SolidRotation.FromSteps(2, 1, 0));
        HullSolid[] solids = [Solid(1, SolidShape.Box, 0, 0, 0, 3, 2, 3), meshSolid, Solid(3, SolidShape.Box, 3, 1, 0, 0.5, 0.5, 0.5, SolidOp.Subtract)];

        AssertClosed(HullCsg.Polygons(solids, [cube], false));
    }

    [Fact]
    public void Paint_RecoloursOnlyItsRegion()
    {
        var polygons = HullCsg.Polygons([Solid(1, SolidShape.Box, 0, 0, 0, 2, 2, 2), Solid(2, SolidShape.Box, 0, 2, 0, 1, 0.5, 1, SolidOp.Paint, material: 1)], [], false);
        var painted = polygons.Where(p => p.Material == 1).ToList();

        Assert.Equal(4.0, Area(painted), 6);
        Assert.Equal(96.0, Area(polygons), 6);
        AssertClosed(polygons);
    }

    [Fact]
    public void Build_BinsIntoBricks()
    {
        var result = HullCsg.Build([Solid(1, SolidShape.Box, 0, 0, 0, 10, 2, 2)], [], false);

        Assert.Equal(16, result.Bricks.Count);
        Assert.True(result.Triangles >= 12);
    }
}
