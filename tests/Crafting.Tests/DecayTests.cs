namespace BrutalGame.Crafting.Tests;

public class DecayTests
{
    private static readonly DecayRule Crystal = TestData.Data.Crystal;

    private static double PurityAt(Inventory inv, long tick) => inv.Read(0, tick).PurityPpm / 1e6;

    [Fact]
    public void CrystalOutsideStasis_FollowsTheDocTimes()
    {
        var day = TestData.TicksPerDay;
        var top = TestData.Hold();
        top.TryAdd(TestData.EmStack(1, 0.995), 0);
        var low = TestData.Hold();
        low.TryAdd(TestData.EmStack(1, 0.975), 0);

        Assert.True(top.Peek(0).Metastable);
        Assert.True(PurityAt(top, day * 5 / 2) >= 0.97);
        Assert.True(PurityAt(top, day * 5 / 2 + day / 100) < 0.97);
        Assert.Equal(0.96, PurityAt(top, day * 7 / 2), 6);
        Assert.Equal(0.96, PurityAt(top, day * 100), 6);
        Assert.True(PurityAt(low, day / 2) >= 0.97);
        Assert.True(PurityAt(low, day / 2 + day / 100) < 0.97);
    }

    [Fact]
    public void LeavingTheBand_KeepsDecayingToTheFloor_ThenIsStableForGood()
    {
        var day = TestData.TicksPerDay;
        var s = TestData.EmStack(1, 0.975);
        StackMath.MarkNew(ref s, 0, Crystal);

        StackMath.Settle(ref s, day, Crystal);
        Assert.True(s.Metastable);
        Assert.Equal(965_000, s.PurityPpm);

        StackMath.Settle(ref s, 3 * day, Crystal);
        Assert.False(s.Metastable);
        Assert.Equal(960_000, s.PurityPpm);

        StackMath.MarkNew(ref s, 3 * day, Crystal);
        Assert.False(s.Metastable);
    }

    [Fact]
    public void LazyDecay_EqualsPerTickEvaluation_AcrossStasisToggles()
    {
        var rng = new Random(31);
        var inv = TestData.Hold();
        var start = TestData.EmStack(3.7, 0.99);
        inv.TryAdd(start, 0);
        var mass = start.MassMg;
        var floor = StackMath.FloorContent(mass, Crystal);
        var denominator = (Int128)Fixed.One * Crystal.TicksPerDay;
        Int128 lossNumerator = 0;
        var nextToggle = rng.Next(1, 4000);

        for (long tick = 1; tick <= 400_000; tick++)
        {
            if (!inv.InStasis)
                lossNumerator += (Int128)mass * Crystal.RatePpmPerDay;

            var expected = Math.Max(floor, start.ContentMg - (long)(lossNumerator / denominator));
            Assert.Equal(expected, inv.Read(0, tick).ContentMg);

            if (tick == nextToggle)
            {
                inv.SetStasis(!inv.InStasis, tick);
                nextToggle += rng.Next(1, 4000);
            }
        }
    }

    [Fact]
    public void DecayAndShock_NeverGoBelowTheFloor_AndStasisBlocksShock()
    {
        var day = TestData.TicksPerDay;
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(1, 0.99), 0);

        for (var i = 0; i < 10; i++)
            inv.Shock(day / 10 * i);

        Assert.Equal(960_000, inv.Read(0, day).PurityPpm);
        Assert.False(inv.Peek(0).Metastable);

        var cased = TestData.Hold();
        cased.SetStasis(true, 0);
        cased.TryAdd(TestData.EmStack(1, 0.99), 0);
        cased.Shock(10);
        Assert.Equal(990_000, cased.Read(0, 30 * day).PurityPpm);

        cased.SetStasis(false, 30 * day);
        Assert.Equal(980_000, cased.Read(0, 31 * day).PurityPpm);
    }

    [Fact]
    public void SeedLattice_PotencyDecaysToItsFloor_Lazily()
    {
        var day = TestData.TicksPerDay;
        var lattice = TestData.Data.Id("add.lattice");
        var inv = TestData.Hold();
        inv.TryAdd(ItemStack.Assayed(lattice, Fixed.MgPerKg, 900_000), 0);

        Assert.True(inv.Peek(0).Metastable);
        Assert.Equal(700_000, inv.Read(0, 10 * day).PurityPpm);
        Assert.Equal(450_000, inv.Read(0, 30 * day).PurityPpm);
        Assert.Equal(450_000, inv.Read(0, 300 * day).PurityPpm);
    }

    [Fact]
    public void MergingDecayingStacks_SettlesBoth_AndConservesTheSettledContent()
    {
        var day = TestData.TicksPerDay;
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(1, 0.99), 0);
        var before = inv.Read(0, day / 2);
        var incoming = TestData.EmStack(1, 0.9853);

        Assert.Equal(AddResult.Merged, inv.TryAdd(incoming, day / 2));
        Assert.Equal(before.ContentMg + incoming.ContentMg, inv.Peek(0).ContentMg);
        Assert.Equal(before.MassMg + incoming.MassMg, inv.Peek(0).MassMg);
    }
}
