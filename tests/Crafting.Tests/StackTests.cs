namespace BrutalGame.Crafting.Tests;

public class StackTests
{
    private static ItemStack RandomStack(Random rng)
    {
        var mass = rng.NextInt64(1, 50_000_000_000);
        var content = rng.NextInt64(0, mass + 1);
        var spread = rng.Next(3) == 0 ? 0 : rng.Next(1, 200_001);
        var error = spread == 0 ? 0 : (long)((Int128)content * rng.NextInt64(-spread, spread + 1) / Fixed.One);
        return ItemStack.Unassayed(TestData.Em, mass, content, content + error, spread);
    }

    [Fact]
    public void SplitAndCombine_ConserveMassTrueAndNominalContentExactly()
    {
        var rng = new Random(11);

        for (var n = 0; n < 20_000; n++)
        {
            var original = RandomStack(rng);
            var stack = original;
            StackMath.Split(ref stack, rng.NextInt64(1, original.MassMg), out var part);

            Assert.Equal(original.MassMg, stack.MassMg + part.MassMg);
            Assert.Equal(original.ContentMg, stack.ContentMg + part.ContentMg);
            Assert.Equal(original.NominalMg, stack.NominalMg + part.NominalMg);

            StackMath.Combine(ref stack, part);
            Assert.Equal(original.MassMg, stack.MassMg);
            Assert.Equal(original.ContentMg, stack.ContentMg);
            Assert.Equal(original.NominalMg, stack.NominalMg);
        }
    }

    [Fact]
    public void Blend_ConservesExactly_AndItsSpreadBoundsTheMergedError()
    {
        var rng = new Random(12);
        Span<ItemStack> parts = stackalloc ItemStack[5];

        for (var n = 0; n < 20_000; n++)
        {
            long mass = 0, content = 0, nominal = 0;

            for (var i = 0; i < parts.Length; i++)
            {
                parts[i] = RandomStack(rng);
                mass += parts[i].MassMg;
                content += parts[i].ContentMg;
                nominal += parts[i].NominalMg;
            }

            var blend = StackMath.Blend(parts, TestData.Em);

            Assert.Equal(mass, blend.MassMg);
            Assert.Equal(content, blend.ContentMg);
            Assert.Equal(nominal, blend.NominalMg);
            Assert.True((Int128)Math.Abs(blend.NominalMg - blend.ContentMg) * Fixed.One <= (Int128)blend.SpreadPpm * blend.ContentMg,
                $"|NomErr| above Spread at n={n}");
        }
    }

    [Fact]
    public void Assay_ZeroesTheError_AndShowsTruePurity()
    {
        var s = TestData.Unassayed(10, 0.30, 0.345, 0.15);
        StackMath.Assay(ref s);

        Assert.True(s.IsAssayed);
        Assert.Equal(s.ContentMg, s.NominalMg);
        Assert.Equal(300_000, s.NominalPurityPpm);
    }

    [Fact]
    public void AutoMerge_NeverMovesContentAcrossAGate_NorMergesAssayedWithUnassayed()
    {
        var data = TestData.Data;
        var gates = data.Purity.GatesPpm;
        var rng = new Random(13);
        var inv = TestData.Hold(4096);
        var sides = new Dictionary<int, (int Segment, bool Assayed)>();
        var inputs = new Dictionary<int, int>();

        for (var n = 0; n < 6000; n++)
        {
            var gate = gates[rng.Next(gates.Count)];
            var q = Math.Clamp(gate + rng.Next(-12_000, 12_000), 1, 994_000);
            var mass = rng.NextInt64(1_000, 10_000_000);
            var content = Fixed.MulDiv(mass, q, Fixed.One);
            var assayed = rng.Next(2) == 0;
            var stack = assayed
                ? ItemStack.Assayed(TestData.Em, mass, content)
                : ItemStack.Unassayed(TestData.Em, mass, Fixed.MulDiv(content, 1_000_000 - rng.Next(0, 50_000), Fixed.One), content, 150_000);
            var segment = data.Purity.Segment(stack.NominalPurityPpm);

            Assert.True(inv.TryAdd(stack, 0, out var slot) is AddResult.Added or AddResult.Merged);

            if (sides.TryGetValue(slot, out var side))
            {
                Assert.Equal(side.Segment, segment);

                if (stack.NominalPurityPpm >= data.Purity.OreTopPpm)
                    Assert.Equal(side.Assayed, assayed);

                inputs[slot]++;
            }
            else
            {
                sides[slot] = (segment, assayed);
                inputs[slot] = 1;
            }

            Assert.Equal(sides[slot].Segment, data.Purity.Segment(inv.Peek(slot).NominalPurityPpm));
        }

        Assert.Contains(inputs.Values, v => v > 5);
    }

    [Fact]
    public void OreBand_MergesWhole_ButStillSplitsAtTheT1Gate()
    {
        var inv = TestData.Hold();

        Assert.Equal(AddResult.Added, inv.TryAdd(TestData.EmStack(1000, 0.0006), 0));
        Assert.Equal(AddResult.Merged, inv.TryAdd(TestData.Unassayed(100, 0.05, 0.06, 0.15), 0));
        Assert.Equal(AddResult.Added, inv.TryAdd(TestData.EmStack(10, 0.12), 0));
        Assert.Equal(AddResult.Merged, inv.TryAdd(TestData.EmStack(10, 0.14), 0));
        Assert.Equal(AddResult.Added, inv.TryAdd(TestData.EmStack(10, 0.16), 0));
        Assert.Equal(AddResult.Added, inv.TryAdd(TestData.Unassayed(10, 0.16, 0.16, 0.15), 0));
        Assert.Equal(4, inv.Count);
    }
}
