using Brutal.Numerics;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Tests;

/// <summary>Coast region keys and window frames (breaking-waves-brief.md step 1): the lattice on a sea tile, and depth sampled through it.</summary>
public sealed class CoastWindowTests
{
    private const double SeaRadius = 1_000_150.0;

    private static readonly double3 Spin = double3.Normalize(new double3(0.2, 1.0, -0.1));
    private static readonly SeaTiles Tiles = new(SeaRadius, Spin);

    /// <summary>A depth that is a known function of the direction.</summary>
    private sealed class Ramp : ICoastDepth
    {
        public float GeneratedDepth(in double3 d) => (float)(40.0 + 3000.0 * d.X - 1700.0 * d.Z);
    }

    [Fact]
    public void Window_RoundTripsThroughTheTilePlane()
    {
        var window = new CoastWindow(Tiles, tile: 17, x: -9, z: 23);

        for (var i = 0; i < 16; i++)
        {
            var x = 61.7 * i;
            var z = 1003.0 - 55.1 * i;
            var dir = window.Direction(x, z);
            Tiles.Plane(17, dir, out var u, out var v);
            window.ToLocal(dir, out var bx, out var bz);

            Assert.Equal(window.OriginU + x, u, 6);
            Assert.Equal(window.OriginV + z, v, 6);
            Assert.Equal(x, bx, 6);
            Assert.Equal(z, bz, 6);
        }
    }

    [Fact]
    public void FillDepth_MatchesTheSourceAtCellCentres()
    {
        var window = new CoastWindow(Tiles, tile: 5, x: 3, z: -2);
        var source = new Ramp();
        const int n = 32;
        var depth = new float[n * n];
        window.FillDepth(source, depth, -64.0, 128.0, 16.0, n, 0, n);

        for (var z = 0; z < n; z += 5)
        {
            for (var x = 0; x < n; x += 3)
                Assert.Equal(source.GeneratedDepth(window.Direction(-64.0 + (x + 0.5) * 16.0, 128.0 + (z + 0.5) * 16.0)), depth[x + z * n]);
        }
    }

    [Fact]
    public void NeighbourWeights_SumToOneAcrossTheSharedCells()
    {
        for (var c = 0f; c <= CoastWindow.Blend; c += 0.37f)
        {
            var mine = CoastWindow.Weight(CoastWindow.Pitch + c, 100f);
            var next = CoastWindow.Weight(c, 100f);
            Assert.Equal(1f, mine + next, 5);
        }

        Assert.Equal(1f, CoastWindow.Weight(128f, 128f));
        Assert.Equal(0f, CoastWindow.Weight(0f, 128f));
        Assert.Equal(0f, CoastWindow.Weight(128f, CoastWindow.Cells));
    }

    [Theory]
    [InlineData(64.0, 0)]
    [InlineData(63.9, -1)]
    [InlineData(959.9, 0)]
    [InlineData(960.0, 1)]
    [InlineData(-5000.0, -6)]
    public void Index_IsTheWindowWhoseOwnSpanHoldsThePoint(double u, int expected) => Assert.Equal(expected, CoastWindow.Index(u));

    [Fact]
    public void Carrier_QuantisesAndWrapsItsDirection()
    {
        var a = CoastCarrier.From(0.3f, 0.48f, 1.2f);
        var b = CoastCarrier.From(0.3f + MathF.Tau, 0.48f, 1.2f);
        var c = CoastCarrier.From(-MathF.Tau + 0.3f, 0.48f, 1.2f);

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(0.3f, a.Direction, 0.001f);
        Assert.Equal(0.48f, a.Omega, 0.001f);
        Assert.Equal(1.2f, a.Height, 0.005f);
        Assert.True(CoastCarrier.From(1f, 0f, 1f).Calm);
        Assert.NotEqual(new CoastRegionKey(1, 2, 3, a), new CoastRegionKey(1, 2, 3, CoastCarrier.From(0.5f, 0.48f, 1.2f)));
    }
}
