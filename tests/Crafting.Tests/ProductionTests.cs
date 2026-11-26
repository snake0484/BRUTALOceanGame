namespace BrutalGame.Crafting.Tests;

public class ProductionTests
{
    private static readonly CraftingData Data = TestData.Data;

    [Fact]
    public void Queue_PopsInEndTickOrder_OnlyWhenDue()
    {
        var q = new ProductionQueue(8, 2);
        var recipe = new RecipeId(1);
        q.TrySchedule(0, recipe, 0, 300, [], out var late);
        q.TrySchedule(1, recipe, 0, 100, [], out var early);
        q.TrySchedule(0, recipe, 50, 50, [], out var tied);
        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];

        Assert.False(q.TryPopDue(99, out _, outputs, out _));
        Assert.True(q.TryPopDue(100, out var a, outputs, out _));
        Assert.True(q.TryPopDue(100, out var b, outputs, out _));
        Assert.Equal(early, a.Id);
        Assert.Equal(tied, b.Id);
        Assert.Equal(300, q.NextDueTick);
        Assert.True(q.TryPopDue(1000, out var c, outputs, out _));
        Assert.Equal(late, c.Id);
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void StarvedTicks_DelayTheBatch_Lazily()
    {
        var q = new ProductionQueue(4, 1);
        q.TrySchedule(0, new RecipeId(1), 0, 100, [], out _);
        q.AddStarved(0, 30);
        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];

        Assert.False(q.TryPopDue(100, out _, outputs, out _));
        Assert.Equal(130, q.NextDueTick);
        Assert.True(q.TryPopDue(130, out var batch, outputs, out _));
        Assert.Equal(130, batch.EndTick);
    }

    [Fact]
    public void Queue_ScheduleAndPop_AllocateNothing()
    {
        var q = new ProductionQueue(64, 4);
        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        Span<ItemStack> made = stackalloc ItemStack[2];
        made[0] = TestData.EmStack(1, 0.5);
        made[1] = TestData.EmStack(1, 0.2);
        q.TrySchedule(0, new RecipeId(1), 0, 10, made, out _);
        q.TryPopDue(10, out _, outputs, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var t = 0; t < 10_000; t++)
        {
            q.TrySchedule(t % 4, new RecipeId(1), t, t % 7, made, out _);
            q.AddStarved(t % 4, 1);
            q.TryPopDue(t, out _, outputs, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void StartEnrich_DebitsInputSolventAndAm_AndDeliversProductAndResidue()
    {
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(3, 0.5), 0);
        inv.TryAdd(ItemStack.Plain(Data.Id("solvent.cryo"), Fixed.Mg(2)), 0);
        var q = new ProductionQueue(4, 1);
        long am = 0;
        var t2 = Data.Recipe("em.recrystallise.t2");

        Assert.Equal(StartStatus.Ok, Production.StartEnrich(Data, t2, inv, 0, Fixed.Mg(2), -1, ref am, 0, 9, q, 0, out _));
        Assert.Equal(Fixed.Mg(1), inv.MassOf(TestData.Em));
        Assert.Equal(Fixed.Mg(1), inv.MassOf(Data.Id("solvent.cryo")));

        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        Assert.True(q.TryPopDue(long.MaxValue, out var batch, outputs, out var count));
        Assert.Equal(2, count);
        Assert.Equal(2 * 180 * 60L, batch.EndTick);
        Assert.Equal(Fixed.Mg(0.8), outputs[0].ContentMg);
        Assert.Equal(Fixed.Mg(2), outputs[0].MassMg + outputs[1].MassMg + Fixed.Mg(0.03));
    }

    [Fact]
    public void StartEnrich_RefusesWithoutSpendingAnything()
    {
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(3, 0.7), 0);
        var q = new ProductionQueue(4, 1);
        long am = 0;
        var t3 = Data.Recipe("em.recrystallise.t3");

        Assert.Equal(StartStatus.NoSolvent, Production.StartEnrich(Data, t3, inv, 0, Fixed.Mg(1), -1, ref am, 0, 0, q, 0, out _));
        inv.TryAdd(ItemStack.Plain(Data.Id("solvent.cryo"), Fixed.Mg(1)), 0);
        Assert.Equal(StartStatus.NoAntimatter, Production.StartEnrich(Data, t3, inv, 0, Fixed.Mg(1), -1, ref am, 0, 0, q, 0, out _));
        Assert.Equal(StartStatus.BelowMinIn, Production.StartEnrich(Data, Data.Recipe("em.crystallise.t4"), inv, 0, Fixed.Mg(1), 1, ref am, 0, 0, q, 0, out _));
        Assert.Equal(Fixed.Mg(3), inv.MassOf(TestData.Em));
        Assert.Equal(0, q.Count);

        am = 50_000_000;
        Assert.Equal(StartStatus.Ok, Production.StartEnrich(Data, t3, inv, 0, Fixed.Mg(1), -1, ref am, 0, 0, q, 0, out _));
        Assert.Equal(0, am);
    }

    [Fact]
    public void T4_NeedsASeedOfTwoPercent_ConsumesATenthOfIt_AndDeliversDecayingCrystal()
    {
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(1, 0.96), 0);
        inv.TryAdd(TestData.EmStack(0.01, 0.965), 0);
        var q = new ProductionQueue(4, 1);
        long am = 1_000_000_000;
        var t4 = Data.Recipe("em.crystallise.t4");

        Assert.Equal(StartStatus.NoSeed, Production.StartEnrich(Data, t4, inv, 0, Fixed.Mg(1), 1, ref am, 0, 0, q, 0, out _));
        Assert.Equal(StartStatus.NoSeed, Production.StartEnrich(Data, t4, inv, 0, Fixed.Mg(1), -1, ref am, 0, 0, q, 0, out _));

        inv.TryAdd(TestData.EmStack(0.05, 0.99), 0);
        Assert.Equal(StartStatus.Ok, Production.StartEnrich(Data, t4, inv, 0, Fixed.Mg(1), 2, ref am, 0, 0, q, 0, out _));
        Assert.Equal(500_000_000, am);

        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        Assert.True(q.TryPopDue(long.MaxValue, out var batch, outputs, out var count));
        Assert.Equal(3, count);
        var seedTaken = StackMath.MassForNominal(TestData.EmStack(0.05, 0.99), Fixed.MulDivCeil(Fixed.Mg(0.96), 20_000, Fixed.One));
        Assert.Equal(seedTaken - Fixed.MulDivCeil(seedTaken, 100_000, Fixed.One), outputs[2].MassMg);

        var store = TestData.Hold();
        Production.Deliver(store, outputs[..count], batch.EndTick);
        Assert.True(store.Peek(0).Metastable);
        Assert.InRange(store.Read(0, batch.EndTick).PurityPpm, 994_990, 995_000);
    }

    [Fact]
    public void SlurryBlend_AddsAgentToTheFractionOrTarget_AndConservesContent()
    {
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(3, 0.30), 0);
        inv.TryAdd(ItemStack.Plain(Data.Id("agent.mix"), Fixed.Mg(5)), 0);
        var q = new ProductionQueue(4, 1);
        var blend = Data.Recipe("em.slurry.blend");

        Assert.Equal(StartStatus.Ok, Production.StartBlend(Data, blend, inv, 0, Fixed.Mg(3), 0, q, 0, out _));
        Assert.Equal(Fixed.Mg(3), inv.MassOf(Data.Id("agent.mix")));

        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        q.TryPopDue(long.MaxValue, out _, outputs, out _);
        Assert.Equal(Data.Id("em.slurry"), outputs[0].Item);
        Assert.Equal(Fixed.Mg(5), outputs[0].MassMg);
        Assert.Equal(Fixed.Mg(0.9), outputs[0].ContentMg);

        inv.TryAdd(TestData.EmStack(1, 0.45), 0);
        Assert.Equal(StartStatus.OutsideGate, Production.StartBlend(Data, blend, inv, inv.Count - 1, Fixed.Mg(1), 0, q, 0, out _));
    }

    [Fact]
    public void AgCharge_TakesEmAmAndCasing_AllOrNothing()
    {
        var inv = TestData.Hold();
        var cell = Data.Id("am.cell");
        inv.TryAdd(TestData.EmStack(5, 0.25), 0);
        inv.TryAdd(ItemStack.Plain(Data.Id("part.ag_casing.s"), Fixed.Mg(20)), 0);
        inv.TryAdd(ItemStack.Assayed(cell, Fixed.Mg(0.7), 4_000), 0);
        var q = new ProductionQueue(4, 1);
        var recipe = Data.Recipe("weapon.agcharge.1kg");

        Assert.Equal(StartStatus.NotEnough, Production.StartFixed(Data, recipe, 1, inv, 0, q, 0, out _));
        Assert.Equal(Fixed.Mg(5), inv.MassOf(TestData.Em));

        inv.TryAdd(ItemStack.Assayed(cell, Fixed.Mg(0.7), 10_000), 0);
        Assert.Equal(StartStatus.Ok, Production.StartFixed(Data, recipe, 1, inv, 0, q, 0, out _));
        Assert.Equal(Fixed.Mg(4), Fixed.Mg(5) - inv.MassOf(TestData.Em));
        Assert.Equal(0, inv.MassOf(Data.Id("part.ag_casing.s")));

        long am = 0;

        for (var i = 0; i < inv.Count; i++)
        {
            if (inv.ItemAt(i) == cell)
                am += inv.Peek(i).ContentMg;
        }

        Assert.Equal(9_000, am);
        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        Assert.True(q.TryPopDue(120 * 60, out _, outputs, out _));
        Assert.Equal(Data.Id("weapon.agcharge"), outputs[0].Item);
    }
}
