namespace BrutalGame.Crafting.Tests;

public class AdditiveTests
{
    private const int GeneratorGate = 850_000;

    private static readonly CraftingData Data = TestData.Data;

    private static ItemStack Add(string key, double kg, double potency = 0.90) =>
        ItemStack.Assayed(Data.Id(key), Fixed.Mg(kg), Fixed.Mg(kg * potency));

    private static AdditiveCheck Check(in ItemStack em, bool useTrue, params ItemStack[] additives)
    {
        Span<ItemId> kinds = stackalloc ItemId[4];
        Span<int> effects = stackalloc int[4];
        return AdditiveMath.Check(Data, em, additives, GeneratorGate, useTrue, kinds, effects, out _);
    }

    [Theory]
    [InlineData(0.10, 0.929, 0.928)]
    [InlineData(0.05, 0.888, 0.887)]
    [InlineData(0.02, 0.865, 0.864)]
    public void Gate_CountsAdditivesAsImpurity_AtTheDocsPurities(double dose, double passes, double fails)
    {
        var x = 1.0;
        Assert.Equal(AdditiveCheck.Ok, Check(TestData.EmStack(x / passes, passes), true, Add("add.binder", dose)));
        Assert.Equal(AdditiveCheck.Purity, Check(TestData.EmStack(x / fails, fails), true, Add("add.binder", dose)));
    }

    [Fact]
    public void Gate_HoldsOnTrueContent_EvenWhenNominalPasses()
    {
        var em = TestData.Unassayed(1 / 0.93, 0.92, 0.93, 0.05);

        Assert.Equal(AdditiveCheck.Ok, Check(em, false, Add("add.binder", 0.09)));
        Assert.Equal(AdditiveCheck.Purity, Check(em, true, Add("add.binder", 0.09)));
    }

    [Fact]
    public void Limits_RefuseAThirdKind_AndMoreThanTenPercent()
    {
        var em = TestData.EmStack(1 / 0.96, 0.96);

        Assert.Equal(AdditiveCheck.TooManyKinds, Check(em, true, Add("add.binder", 0.02), Add("add.mask", 0.02), Add("add.catalyst", 0.02)));
        Assert.Equal(AdditiveCheck.Ok, Check(em, true, Add("add.binder", 0.02), Add("add.binder", 0.02), Add("add.mask", 0.02)));
        Assert.Equal(AdditiveCheck.TooMuchMass, Check(em, true, Add("add.binder", 0.06), Add("add.mask", 0.05)));
        Assert.Equal(AdditiveCheck.NotAdditive, Check(em, true, Add("agent.mix", 0.01, 0)));
    }

    [Fact]
    public void Effects_ScaleLinearlyToFullActive_AndStop()
    {
        var em = TestData.EmStack(1 / 0.96, 0.96);
        Span<ItemId> kinds = stackalloc ItemId[2];
        Span<int> effects = stackalloc int[2];
        ItemStack[] half = [Add("add.binder", 0.025), Add("add.mask", 0.075, 0.60)];

        Assert.Equal(AdditiveCheck.Ok, AdditiveMath.Check(Data, em, half, GeneratorGate, true, kinds, effects, out var count));
        Assert.Equal(2, count);
        Assert.Equal(500_000, effects[0]);
        Assert.Equal(1_000_000, effects[1]);

        ItemStack[] over = [Add("add.binder", 0.09)];
        AdditiveMath.Check(Data, em, over, GeneratorGate, true, kinds, effects, out _);
        Assert.Equal(1_000_000, effects[0]);
    }

    [Fact]
    public void SharedFactors_Multiply_AndAdditiveKeysSum()
    {
        Span<double> factors = stackalloc double[Data.FactorKeys.Count];
        ItemId[] kinds = [Data.Id("add.dilatant"), Data.Id("add.lattice")];
        AdditiveMath.Factors(Data, kinds, [1_000_000, 1_000_000], factors);
        Assert.Equal(1.15 * 1.2, factors[Data.FactorKey("mX")], 9);
        Assert.Equal(1.25, factors[Data.FactorKey("maxRadiusMul")], 9);

        AdditiveMath.Factors(Data, kinds, [500_000, 500_000], factors);
        Assert.Equal(1.075 * 1.1, factors[Data.FactorKey("mX")], 9);

        AdditiveMath.Factors(Data, [Data.Id("add.chiral")], [500_000], factors);
        Assert.Equal(0.25, factors[Data.FactorKey("flowBiasAdd")], 9);
        Assert.Equal(1.0, factors[Data.FactorKey("mX")], 9);
    }

    [Fact]
    public void EmConsumedByAnAdditiveRecipe_NeverReappearsAsEmContent()
    {
        var inv = TestData.Hold();
        inv.TryAdd(TestData.EmStack(2, 0.70), 0);
        inv.TryAdd(ItemStack.Plain(Data.Id("metal.rare"), Fixed.MgPerKg), 0);
        var queue = new ProductionQueue(8, 1);
        var recipe = Data.Recipe("make.add.binder");

        Assert.Equal(StartStatus.Ok, Production.StartFixed(Data, recipe, 1, inv, 0, queue, 0, out _));

        var emLeft = inv.Read(0, 0);
        Assert.Equal(TestData.Em, emLeft.Item);
        Assert.InRange(Fixed.Mg(1.4) - emLeft.ContentMg, Fixed.Mg(0.5), Fixed.Mg(0.5) + 1);

        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];
        Assert.True(queue.TryPopDue(long.MaxValue, out var batch, outputs, out var count));
        Assert.Equal(1, count);
        Assert.Equal(Data.Id("add.binder"), outputs[0].Item);
        Assert.Equal(ContentKind.Active, Data.Item(outputs[0].Item).Content);
        Assert.Equal(Fixed.Mg(0.9), outputs[0].ContentMg);
        Assert.Equal(1800L * 60, batch.EndTick);
    }

    [Fact]
    public void CrudeLab_MakesPotency060_AtHalfRate()
    {
        var crude = Data.Recipe("make.add.binder.crude");
        var lab = Data.Recipe("make.add.binder");

        Assert.Equal(600_000, crude.Outputs[0].ContentPpm);
        Assert.Equal(2 * lab.TicksPerUnit, crude.TicksPerUnit);
        Assert.Equal("fc.additive_lab.crude", crude.Station);
    }
}
