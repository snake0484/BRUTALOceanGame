namespace BrutalGame.Crafting.Tests;

public class InventoryTests
{
    private static readonly CraftingData Data = TestData.Data;

    [Fact]
    public void Capacity_IsBySlotsMassAndVolume()
    {
        var ice = Data.Id("ice");
        var inv = new Inventory(Data, 2, Fixed.Mg(1100), 1_000_000);

        Assert.Equal(AddResult.Added, inv.TryAdd(ItemStack.Plain(ice, Fixed.Mg(900)), 0));
        Assert.Equal(978_261, inv.VolumeMl);
        Assert.Equal(AddResult.OverVolume, inv.TryAdd(ItemStack.Plain(ice, Fixed.Mg(50)), 0));
        Assert.Equal(AddResult.OverMass, inv.TryAdd(ItemStack.Plain(Data.Id("metal.heavy"), Fixed.Mg(201)), 0));
        Assert.Equal(AddResult.Added, inv.TryAdd(ItemStack.Plain(Data.Id("metal.heavy"), Fixed.Mg(100)), 0));
        Assert.Equal(AddResult.NoSlot, inv.TryAdd(TestData.EmStack(0.001, 0.5), 0));
        Assert.Equal(AddResult.Merged, inv.TryAdd(ItemStack.Plain(ice, Fixed.Mg(10)), 0));
        Assert.Equal(Fixed.Mg(1010), inv.MassMg);
    }

    [Fact]
    public void Take_SplitsExactly_RemovesEmptySlots_AndKeepsWholeUnits()
    {
        var casing = Data.Id("part.ag_casing.s");
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(10, 0.5), 0);
        inv.TryAdd(ItemStack.Plain(casing, 3 * Fixed.Mg(20)), 0);

        Assert.False(inv.TryTake(1, Fixed.Mg(30), 0, out _));
        Assert.True(inv.TryTake(1, Fixed.Mg(40), 0, out var two));
        Assert.Equal(Fixed.Mg(40), two.MassMg);

        Assert.True(inv.TryTake(0, Fixed.Mg(10), 0, out var all));
        Assert.Equal(Fixed.Mg(5), all.ContentMg);
        Assert.Equal(1, inv.Count);
        Assert.Equal(casing, inv.ItemAt(0));
        Assert.Equal(Fixed.Mg(20), inv.MassMg);
    }

    [Fact]
    public void HotInventoryOps_AllocateNothing()
    {
        var inv = TestData.Hold(256);
        var ice = Data.Id("ice");
        var crystal = TestData.EmStack(0.5, 0.99);
        var unassayed = TestData.Unassayed(2, 0.31, 0.33, 0.15);

        void Cycle(long tick)
        {
            inv.TryAdd(TestData.EmStack(1, 0.42), tick);
            inv.TryAdd(unassayed, tick);
            inv.TryAdd(crystal, tick);
            inv.TryAdd(ItemStack.Plain(ice, 5000), tick);
            inv.TryTake(0, 1000, tick, out _);
            inv.TryTakeNominal(1, 500, tick, out _);
            _ = inv.Read(2, tick);
            inv.SetStasis(tick % 2 == 0, tick);
            inv.Shock(tick);
        }

        Cycle(1);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (long t = 2; t < 2000; t++)
            Cycle(t * 97);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
