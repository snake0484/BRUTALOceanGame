using Brutal.Numerics;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime;
using Xunit.Abstractions;

namespace BrutalGame.Terrain.Tests;

public sealed class PlanetLeafSelectorTests(ITestOutputHelper output)
{
    private static readonly Lazy<PlanetHeightBounds> Bounds = new(() =>
    {
        var bounds = new PlanetHeightBounds(TestPlanet.Generator, 64);
        bounds.Build();
        return bounds;
    });

    private static double3 Above(double altitude)
    {
        var d = double3.Normalize(new double3(0.3, 0.8, -0.5));
        return d * (TestPlanet.Radius + TestPlanet.Generator.Surface.Height(d.X, d.Y, d.Z) + altitude);
    }

    private static LeafSet Select(double3 camera, int minLod, double margin = 0.0)
    {
        var selector = new PlanetLeafSelector(Bounds.Value, new EditStore());
        var set = new LeafSet();
        selector.Select(camera, minLod, margin, set);
        return set;
    }

    [Fact]
    public void SpeedBiasFollowsTheFillSpeed()
    {
        Assert.Equal(0, PlanetLeafSelector.SpeedLod(30.0));
        Assert.Equal(0, PlanetLeafSelector.SpeedLod(300.0));
        Assert.Equal(3, PlanetLeafSelector.SpeedLod(3000.0));
        Assert.Equal(5, PlanetLeafSelector.SpeedLod(10_000.0));
        Assert.Equal(PlanetLeafSelector.MaxLeafLod, PlanetLeafSelector.SpeedLod(1e9));
    }

    /// <summary>Near the ground: LOD 0 under the camera, every leaf on the shell, none inside another, parents split.</summary>
    [Fact]
    public void LeavesTileTheShellWithoutOverlap()
    {
        var set = Select(Above(30.0), 0);
        output.WriteLine($"{set.Count} leaves, {set.Visits} visits, {set.Uniform} uniform, {set.Pruned} pruned, per LOD [{string.Join(' ', set.PerLod)}]");

        Assert.Equal(0, set.FinestLod);
        Assert.True(set.PerLod[0] > 50, "LOD 0 round the camera");
        Assert.Equal(set.Count, set.Leaves.Count);

        foreach (var leaf in set.Order)
        {
            Assert.InRange(leaf.Lod, 0, PlanetLeafSelector.MaxLeafLod);
            Assert.Equal(NodeClass.Surface, Bounds.Value.Classify(leaf));

            for (var a = leaf.Parent; a.Lod <= PlanetLeafSelector.MaxLeafLod; a = a.Parent)
            {
                Assert.DoesNotContain(a.Key, set.Leaves);
                Assert.Contains(a.Key, set.Internal);
            }
        }
    }

    [Fact]
    public void SpeedBiasCapsTheFinestLod()
    {
        var set = Select(Above(30.0), 3);

        Assert.Equal(3, set.FinestLod);
        Assert.Equal(0, set.PerLod[0] + set.PerLod[1] + set.PerLod[2]);
    }

    /// <summary>From 800 km the far side is pruned, and the margin only ever adds leaves.</summary>
    [Fact]
    public void HorizonPrunesTheFarSide()
    {
        var camera = Above(800_000.0);
        var set = Select(camera, 0);
        var wider = Select(camera, 0, 50_000.0);
        var up = double3.Normalize(camera);
        output.WriteLine($"{set.Count} leaves, {set.Pruned} pruned; with a 50 km margin {wider.Count}");

        Assert.True(set.Pruned > 0);
        Assert.True(wider.Count >= set.Count);

        foreach (var leaf in set.Order)
        {
            var centre = leaf.Origin + new double3(0.5 * leaf.SizeMetres);
            Assert.True(double3.Dot(centre, up) > 0.0, $"{leaf} is on the far side");
        }
    }

    /// <summary>
    /// A dig 500 m down and a place 300 m up sit where the shell calls every node above LOD 0 uniform, and neither shows
    /// at LOD 1: the octree must still reach their LOD-0 leaf, which meshes, builds collision and counts for the hole check.
    /// </summary>
    [Theory]
    [InlineData(-500.0, BrushMode.Dig)]
    [InlineData(300.0, BrushMode.Place)]
    public void EditsFarFromTheGroundKeepTheirLeaf(double offset, BrushMode mode)
    {
        var p = TestPlanet.Inland(offset);
        var edits = new EditStore();
        edits.Add(EditOp.At(p.X, p.Y, p.Z, 1.5f, mode, VoxelMaterial.Rock));
        var leaf = ChunkCoord.FromPlanet(p.X, p.Y, p.Z, 0);
        var selector = new PlanetLeafSelector(Bounds.Value, edits);
        var set = new LeafSet();
        selector.Select(p, 0, 2000.0, set);

        Assert.NotEqual(NodeClass.Surface, Bounds.Value.Classify(leaf.Parent));
        Assert.False(edits.Reaches(leaf.Parent, 0.0));
        Assert.Contains(leaf.Key, set.Leaves);
        Assert.False(selector.ProvablyUnseen(leaf, p));
        Assert.True(selector.ProvablyUnseen(new ChunkCoord(leaf.X + 3, leaf.Y, leaf.Z), p));

        for (var a = leaf.Parent; a.Lod <= PlanetLeafSelector.MaxLeafLod; a = a.Parent)
            Assert.Contains(a.Key, set.Internal);

        using var shape = new StaticMeshShape();
        var job = new ChunkJob { Generator = TestPlanet.Generator, Shape = shape };
        job.Chunk.Coord = leaf;
        job.GatherOps(edits, 0.0);
        job.Run();
        output.WriteLine($"{offset} m: {set.Count} leaves, leaf {leaf} meshed {job.Mesh.IndexCount / 3} triangles, collision {shape.TriangleCount}");

        Assert.True(job.Mesh.IndexCount > 0, "the edit meshes");
        Assert.True(shape.IsBuilt, "the edit collides");
    }

    /// <summary>Without edits nothing changes: the same camera selects the same leaves as an empty store.</summary>
    [Fact]
    public void AnEditElsewhereLeavesTheSelectionAlone()
    {
        var camera = Above(30.0);
        var far = -camera;
        var edits = new EditStore();
        edits.Add(EditOp.At(far.X, far.Y, far.Z, 1.5f, BrushMode.Dig, VoxelMaterial.Rock));
        var set = new LeafSet();
        new PlanetLeafSelector(Bounds.Value, edits).Select(camera, 0, 0.0, set);

        Assert.Equal(Select(camera, 0).Order, set.Order);
    }

    /// <summary>The hole check's cover (review F7): finer leaves cover a coarse node only when they cover all of it that could show, not when any one of them is drawn.</summary>
    [Fact]
    public void DescendantsCoverNeedsEveryShowingLeaf()
    {
        var camera = Above(30.0);
        var ground = Above(0.0);
        var selector = new PlanetLeafSelector(Bounds.Value, new EditStore());
        var set = new LeafSet();
        selector.Select(camera, 0, 2000.0, set);
        var node = ChunkCoord.FromPlanet(ground.X, ground.Y, ground.Z, 2);
        var drawable = new HashSet<ulong>(set.Leaves);
        var ancestors = new HashSet<ulong>(set.Internal);
        var showing = default(ChunkCoord);
        var under = 0;

        foreach (var leaf in set.Order)
        {
            if (leaf.Lod != 0 || leaf.Parent.Parent != node)
                continue;

            under++;

            if (!selector.ProvablyUnseen(leaf, camera))
                showing = leaf;
        }

        Assert.Contains(node.Key, set.Internal);
        Assert.True(under > 1 && showing.Parent.Parent == node, $"{under} leaves under {node}");
        Assert.True(TerrainWorld.DescendantsCover(node, drawable, ancestors, selector, camera));

        drawable.Remove(showing.Key);
        Assert.False(TerrainWorld.DescendantsCover(node, drawable, ancestors, selector, camera));

        drawable.Clear();
        drawable.Add(showing.Key);
        Assert.True(TerrainWorld.DescendantsCover(showing, drawable, ancestors, selector, camera));
        Assert.Equal(under == 1, TerrainWorld.DescendantsCover(node, drawable, ancestors, selector, camera));
    }

    [Fact]
    public void SelectionIsAllocationFree()
    {
        var selector = new PlanetLeafSelector(Bounds.Value, new EditStore());
        var set = new LeafSet();
        var camera = Above(30.0);
        selector.Select(camera, 0, 2000.0, set);
        selector.Select(camera + new double3(40.0, 0.0, 0.0), 0, 2000.0, set);

        var before = GC.GetAllocatedBytesForCurrentThread();
        selector.Select(camera + new double3(80.0, 0.0, 0.0), 0, 2000.0, set);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
