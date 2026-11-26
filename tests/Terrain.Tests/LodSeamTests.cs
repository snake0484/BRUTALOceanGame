using Brutal.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

/// <summary>
/// LOD seams (review F1): where a fine leaf meets a coarse one, the two meshes as the GPU draws them (packed vertices,
/// skirts, back faces culled) must leave nothing to see through. Rays are cast at the strip of ground running from the
/// fine leaf's border vertices towards the coarse leaf: straight down (the plan view) and at 35° pitch from eight
/// headings, along the seam included. A ray that meets no front face from well above the strip to
/// <see cref="RayReach"/> coarse voxels past it went through a crack.
/// </summary>
public sealed class LodSeamTests(ITestOutputHelper output)
{
    private const int Sites = 3;
    private const int VertexStride = 2;
    private const int Offsets = 6;
    private const double OffsetStep = 0.25;
    private const int Headings = 8;
    private const double ObliquePitch = 35.0;

    /// <summary>Probed vertices keep this far (own voxels) from the leaves' other faces, whose neighbours are not meshed here.</summary>
    private const double FineMargin = 6.0;
    private const double CoarseMargin = 3.0;

    /// <summary>Coarse voxels a ray runs before and after its target, and how far from the seam triangles are kept.</summary>
    private const double RayLead = 4.0;
    private const double RayReach = 2.5;
    private const double SoupReach = 8.0;

    private sealed class Soup
    {
        private readonly List<double3> _triangles = new();

        /// <summary>
        /// Appends the mesh's triangles, as terrain.vert decodes them, relative to <paramref name="origin"/>; only those
        /// with a corner within <paramref name="reach"/> of the plane <paramref name="axis"/> = <paramref name="plane"/>.
        /// </summary>
        public void Add(ChunkMesh mesh, ChunkCoord coord, double3 origin, int axis, double plane, double reach)
        {
            var shift = coord.Origin - origin;
            Span<double3> corners = stackalloc double3[3];

            for (var i = 0; i < mesh.IndexCount; i += 3)
            {
                var near = false;

                for (var k = 0; k < 3; k++)
                {
                    var p = mesh.Packed[mesh.Indices[i + k]].DecodePosition(coord.VoxelScale);
                    corners[k] = shift + new double3(p.X, p.Y, p.Z);
                    near |= Math.Abs(Axis(corners[k], axis) - plane) <= reach;
                }

                if (near)
                    _triangles.AddRange(corners);
            }
        }

        /// <summary>True if the ray meets a front face (counter-clockwise seen from its origin, as the terrain pipeline culls) at 0 ≤ t ≤ <paramref name="reach"/>.</summary>
        public bool Hit(double3 from, double3 dir, double reach)
        {
            for (var i = 0; i < _triangles.Count; i += 3)
            {
                var a = _triangles[i];
                var e1 = _triangles[i + 1] - a;
                var e2 = _triangles[i + 2] - a;
                var n = double3.Cross(e1, e2);
                var facing = double3.Dot(n, dir);

                if (facing >= 0.0)
                    continue;

                var s = from - a;
                var t = double3.Dot(n, s) / -facing;

                if (t < 0.0 || t > reach)
                    continue;

                var q = double3.Cross(s, dir);
                var u = double3.Dot(e2, q) / -facing;
                var v = -double3.Dot(e1, q) / -facing;

                if (u >= 0.0 && v >= 0.0 && u + v <= 1.0)
                    return true;
            }

            return false;
        }
    }

    /// <param name="PlanGap">The widest run of plan rays through at one vertex, in fine voxels.</param>
    private readonly record struct SeamResult(int Rays, int Through, int PlanRays, int PlanThrough, double PlanGap)
    {
        public SeamResult Plus(SeamResult o) => new(Rays + o.Rays, Through + o.Through, PlanRays + o.PlanRays, PlanThrough + o.PlanThrough, Math.Max(PlanGap, o.PlanGap));
    }

    private static double Axis(double3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static double3 WithAxis(double3 v, int axis, double value) => new(axis == 0 ? value : v.X, axis == 1 ? value : v.Y, axis == 2 ? value : v.Z);

    private static double3 Unit(int axis) => WithAxis(default, axis, 1.0);

    /// <summary>The two coordinates other than <paramref name="axis"/> lie in [lo, hi] (voxels).</summary>
    private static bool Inside(double3 voxels, int axis, double lo, double hi)
    {
        for (var k = 0; k < 3; k++)
        {
            if (k != axis && (Axis(voxels, k) < lo || Axis(voxels, k) > hi))
                return false;
        }

        return true;
    }

    /// <summary>Meshes the leaf with skirts; returns its surface vertices (the skirts' follow them), 0 if it is empty.</summary>
    private static int Mesh(ChunkCoord coord, ChunkMesh mesh)
    {
        var chunk = new VoxelChunk { Coord = coord };
        var mesher = new SurfaceNetsMesher();
        TestPlanet.Generator.Generate(chunk, new PlanetChunkScratch());
        mesh.Clear();

        if (chunk.HasSurface)
            mesher.Mesh(chunk, mesh);

        return mesh.IndexCount == 0 ? 0 : mesh.VertexCount - mesher.LastSkirtTriangles;
    }

    /// <summary>The fine leaf at the site whose face on <paramref name="axis"/> is also a coarse border, with the ground through its middle.</summary>
    private static ChunkCoord FineLeaf(double3 site, int lod, int axis, bool coarsePlus)
    {
        var fine = ChunkCoord.FromPlanet(site.X, site.Y, site.Z, lod);
        var index = axis == 0 ? fine.X : axis == 1 ? fine.Y : fine.Z;

        if ((index & 1) != (coarsePlus ? 1 : 0))
            index++;

        for (var pass = 0; pass < 2; pass++)
        {
            var centre = double3.Normalize(fine.Origin + new double3(0.5 * fine.SizeMetres));
            var ground = centre * (TestPlanet.Radius + TestPlanet.Generator.Surface.Height(centre.X, centre.Y, centre.Z, lod));
            var at = ChunkCoord.FromPlanet(ground.X, ground.Y, ground.Z, lod);
            fine = new ChunkCoord(axis == 0 ? index : at.X, axis == 1 ? index : at.Y, axis == 2 ? index : at.Z, lod);
        }

        return fine;
    }

    private static SeamResult Measure(double3 site, int lod, int axis, bool coarsePlus)
    {
        var fine = FineLeaf(site, lod, axis, coarsePlus);
        var fineMesh = new ChunkMesh();
        var surfaceVertices = Mesh(fine, fineMesh);

        if (surfaceVertices == 0)
            return default;

        var sf = fine.VoxelSize;
        var sc = 2.0 * sf;
        var size = ChunkConstants.Size * sf;
        var origin = fine.Origin;
        var plane = coarsePlus ? size : 0.0;
        var outward = Unit(axis) * (coarsePlus ? 1.0 : -1.0);
        var up = double3.Normalize(origin + new double3(0.5 * size));
        var east = double3.Normalize(double3.Cross(up, Math.Abs(up.X) < 0.9 ? new double3(1.0, 0.0, 0.0) : new double3(0.0, 1.0, 0.0)));
        var north = double3.Cross(up, east);
        var pitch = ObliquePitch * Math.PI / 180.0;
        var soups = new Dictionary<ulong, Soup?>();
        var coarseMesh = new ChunkMesh();
        var result = default(SeamResult);
        var seen = 0;

        for (var i = 0; i < surfaceVertices; i++)
        {
            ref readonly var vertex = ref fineMesh.Vertices[i];
            var v = new double3(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            var a = Axis(v, axis) / sf;

            if ((coarsePlus ? a < ChunkConstants.Size - 1 : a > 0.0) || seen++ % VertexStride != 0)
                continue;

            var normal = new double3(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
            var along = outward - normal * double3.Dot(outward, normal);
            var world = origin + v;
            var across = WithAxis(world, axis, Axis(origin, axis) + plane + Axis(outward, axis) * 0.5 * sc);
            var coarse = ChunkCoord.FromPlanet(across.X, across.Y, across.Z, lod + 1);

            if (double3.Dot(along, along) < 0.04 || !Inside(v / sf, axis, FineMargin, ChunkConstants.Size - FineMargin)
                || !Inside((world - coarse.Origin) / sc, axis, CoarseMargin, ChunkConstants.Size - CoarseMargin))
                continue;

            if (!soups.TryGetValue(coarse.Key, out var soup))
            {
                if (Mesh(coarse, coarseMesh) > 0)
                {
                    soup = new Soup();
                    soup.Add(fineMesh, fine, origin, axis, plane, SoupReach * sc);
                    soup.Add(coarseMesh, coarse, origin, axis, plane, SoupReach * sc);
                }

                soups.Add(coarse.Key, soup);
            }

            if (soup is null)
                continue;

            along = double3.Normalize(along);
            var open = 0;

            for (var o = 1; o <= Offsets; o++)
            {
                var target = v + along * (o * OffsetStep * sf);
                var plan = soup.Hit(target + up * (RayLead * sc), -up, (RayLead + RayReach) * sc);
                open += plan ? 0 : 1;
                result = result.Plus(new SeamResult(1, plan ? 0 : 1, 1, plan ? 0 : 1, 0.0));

                for (var h = 0; h < Headings; h++)
                {
                    var heading = h * 2.0 * Math.PI / Headings;
                    var dir = up * -Math.Sin(pitch) + (east * Math.Cos(heading) + north * Math.Sin(heading)) * Math.Cos(pitch);
                    var hit = soup.Hit(target - dir * (RayLead * sc), dir, (RayLead + RayReach) * sc);
                    result = result.Plus(new SeamResult(1, hit ? 0 : 1, 0, 0, 0.0));
                }
            }

            result = result with { PlanGap = Math.Max(result.PlanGap, open * OffsetStep) };
        }

        return result;
    }

    /// <summary>Flanges and all, a leaf's mesh stays inside the margin culling and the packed vertex allow.</summary>
    [Fact]
    public void MeshStaysInsideTheCullMargin()
    {
        var mesh = new ChunkMesh();
        var skirts = 0;
        var worst = 0f;

        for (var lod = 0; lod <= PlanetGenerator.MaxLod; lod += 3)
        for (var site = 0; site < Sites; site++)
        {
            var fine = FineLeaf(TestPlanet.Inland(0.0, 7 + site), lod, 0, false);
            var surface = Mesh(fine, mesh);
            skirts += mesh.VertexCount - surface;

            foreach (ref readonly var vertex in mesh.VertexSpan)
            {
                var p = vertex.Position / fine.VoxelScale;
                worst = MathF.Max(worst, MathF.Max(MathF.Max(-p.X, -p.Y), -p.Z));
                worst = MathF.Max(worst, MathF.Max(MathF.Max(p.X, p.Y), p.Z) - ChunkConstants.Size);
            }
        }

        output.WriteLine($"{skirts} skirt vertices, furthest {worst:F3} voxels outside the cube");
        Assert.True(skirts > 1000, $"only {skirts} skirt vertices");
        Assert.True(worst <= SurfaceNetsMesher.MarginVoxels, $"{worst} voxels outside the cube");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void CoarseAndFineNeighboursLeaveNoGap(int lod)
    {
        Span<SeamResult> sides = stackalloc SeamResult[2];

        for (var site = 0; site < Sites; site++)
        for (var axis = 0; axis < 3; axis++)
        for (var side = 0; side < 2; side++)
            sides[side] = sides[side].Plus(Measure(TestPlanet.Inland(0.0, 7 + site), lod, axis, side == 1));

        for (var side = 0; side < 2; side++)
        {
            var r = sides[side];
            output.WriteLine($"L{lod}/L{lod + 1} coarse on the {(side == 1 ? "plus" : "minus")} side: plan {r.PlanThrough} of {r.PlanRays} rays through, widest gap {r.PlanGap:F2} fine voxels; all rays {r.Through} of {r.Rays} through");
        }

        for (var side = 0; side < 2; side++)
        {
            var r = sides[side];
            Assert.True(r.PlanRays > 100, $"side {side}: only {r.PlanRays} plan rays reached a seam");
            Assert.True(r.PlanThrough == 0, $"side {side}: plan gap {r.PlanGap:F2} fine voxels, {r.PlanThrough} of {r.PlanRays} rays through");
            Assert.True(r.Through == 0, $"side {side}: {r.Through} of {r.Rays} rays through");
        }
    }
}
