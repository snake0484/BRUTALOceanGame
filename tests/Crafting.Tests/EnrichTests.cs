namespace BrutalGame.Crafting.Tests;

public class EnrichTests
{
    private static EnrichParams R(string key) => TestData.Data.Recipe(key).Enrich;

    private static ItemStack Run(ItemStack input, string recipe, long batchId = 1)
    {
        var status = EnrichMath.Balance(input, R(recipe), 42, batchId, out var product, out _, out _);
        Assert.Equal(EnrichStatus.Ok, status);
        return product;
    }

    private static double Kg(long mg) => mg / 1e6;

    private static double Q(in ItemStack s) => (double)s.ContentMg / s.MassMg;

    [Fact]
    public void TypicalChain_MatchesTheDocTable()
    {
        var s = TestData.EmStack(1.0 / 0.0006, 0.0006);
        (string Recipe, double Purity, double ContentKg)[] steps =
        [
            ("em.concentrate.t0", 0.029, 0.85),
            ("em.concentrate.t0", 0.20, 0.72),
            ("em.recrystallise.t1", 0.43, 0.61),
            ("em.recrystallise.t2", 0.75, 0.49),
            ("em.recrystallise.t2", 0.85, 0.39),
            ("em.recrystallise.t3", 0.96, 0.29),
            ("em.crystallise.t4", 0.995, 0.21),
        ];

        foreach (var (recipe, purity, content) in steps)
        {
            s = Run(s, recipe);
            Assert.InRange(Q(s), purity - 0.0015, purity + 0.0015);
            Assert.InRange(Kg(s.ContentMg), content - 0.006, content + 0.006);
        }

        Assert.True(Fixed.AtMost(s.ContentMg, s.MassMg, 995_000));
    }

    [Fact]
    public void CrudeChain_MatchesTheDoc_AndStaysWithinItsSpread()
    {
        var s = Run(TestData.EmStack(0.72 / 0.20, 0.20), "em.crude_still.t1c");
        Assert.InRange(Kg(s.ContentMg), 0.50, 0.51);
        Assert.InRange(Q(s), 0.332, 0.334);
        Assert.Equal(150_000, s.SpreadPpm);
        AssertWithinSpread(s);

        StackMath.Assay(ref s);
        s = Run(s, "em.crude_recrystallise.t2c");
        Assert.InRange(Kg(s.ContentMg), 0.325, 0.335);
        Assert.InRange(Q(s), 0.554, 0.556);
        Assert.Equal(200_000, s.SpreadPpm);
        AssertWithinSpread(s);
    }

    [Fact]
    public void CrudeError_IsSeededPerBatch()
    {
        var input = TestData.EmStack(10, 0.3);
        var a = Run(input, "em.crude_still.t1c", 7);
        var b = Run(input, "em.crude_still.t1c", 7);
        var c = Run(input, "em.crude_still.t1c", 8);

        Assert.Equal(a.NominalMg, b.NominalMg);
        Assert.NotEqual(a.NominalMg, c.NominalMg);
        Assert.Equal(a.ContentMg, c.ContentMg);
    }

    [Fact]
    public void Balance_IsExactInMg_AndResidueIsAlwaysPoorerThanInput()
    {
        var rng = new Random(21);
        var recipes = TestData.Data.Recipes.Where(r => r.Kind == RecipeKind.Enrich).ToArray();

        for (var n = 0; n < 20_000; n++)
        {
            var r = recipes[rng.Next(recipes.Length)].Enrich;
            var q = rng.Next(Math.Max(1, r.MinInPpm), r.CapPpm);
            var mass = rng.NextInt64(1_000, 100_000_000_000);
            var input = ItemStack.Assayed(TestData.Em, mass, Math.Max(1, Fixed.MulDiv(mass, q, Fixed.One)));

            if (!Fixed.AtLeast(input.ContentMg, mass, r.MinInPpm) || Fixed.AtLeast(input.ContentMg, mass, r.CapPpm))
                continue;

            Assert.Equal(EnrichStatus.Ok, EnrichMath.Balance(input, r, 1, n, out var p, out var res, out var burnt));
            Assert.Equal(input.MassMg, p.MassMg + res.MassMg + burnt);
            Assert.Equal(input.ContentMg, p.ContentMg + res.ContentMg + burnt);
            Assert.True(Fixed.AtMost(p.ContentMg, p.MassMg, r.CapPpm), "product above cap");

            if (p.MassMg > 0)
                Assert.True((Int128)p.ContentMg * input.MassMg >= (Int128)input.ContentMg * (p.MassMg - 1), "product poorer than input beyond 1 mg of rounding");

            if (res.MassMg > 0)
                Assert.True((Int128)res.ContentMg * input.MassMg < (Int128)input.ContentMg * res.MassMg, "residue not poorer than input");
        }
    }

    [Fact]
    public void InputsAtOrAboveTheCap_OrBelowMinIn_AreRefused()
    {
        var t2 = R("em.recrystallise.t2");

        Assert.Equal(EnrichStatus.AtOrAboveCap, EnrichMath.Balance(TestData.EmStack(1, 0.85), t2, 0, 0, out _, out _, out _));
        Assert.Equal(EnrichStatus.AtOrAboveCap, EnrichMath.Balance(TestData.EmStack(1, 0.98), t2, 0, 0, out _, out _, out _));
        Assert.Equal(EnrichStatus.BelowMinIn, EnrichMath.Balance(TestData.EmStack(1, 0.29), t2, 0, 0, out _, out _, out _));
        Assert.Equal(EnrichStatus.AtOrAboveCap, EnrichMath.Balance(TestData.Unassayed(1, 0.86, 0.80, 0.2), t2, 0, 0, out _, out _, out _));
        Assert.Equal(EnrichStatus.Ok, EnrichMath.Balance(TestData.EmStack(1, 0.30), t2, 0, 0, out _, out _, out _));
    }

    [Fact]
    public void Preview_MatchesOddsFormula()
    {
        var t1 = R("em.recrystallise.t1");
        Assert.Equal(428_571, EnrichMath.Preview(200_000, t1));
        Assert.Equal(600_000, EnrichMath.Preview(500_000, t1));
    }

    [Fact]
    public void BatchLength_IsMassOverThroughput()
    {
        Assert.Equal(180 * 60, EnrichMath.Ticks(Fixed.MgPerKg, R("em.recrystallise.t2").ThroughputMgPerHour, 60));
        Assert.Equal(3600 * 60, EnrichMath.Ticks(2000 * Fixed.MgPerKg, R("em.concentrate.t0").ThroughputMgPerHour, 60));
    }

    private static void AssertWithinSpread(in ItemStack s) =>
        Assert.True((Int128)Math.Abs(s.NominalMg - s.ContentMg) * Fixed.One <= (Int128)s.SpreadPpm * s.ContentMg + Fixed.One);
}
