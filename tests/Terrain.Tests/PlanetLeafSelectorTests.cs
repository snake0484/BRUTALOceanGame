using Brutal.Numerics;
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
