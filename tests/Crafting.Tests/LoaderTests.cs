using System.Text.Json.Nodes;

namespace BrutalGame.Crafting.Tests;

public class LoaderTests
{
    [Fact]
    public void RealData_Loads_WithStableIds()
    {
        var data = TestData.Data;
        var again = CraftingData.Load(TestData.Folder);

        Assert.Equal(20, data.Items.Count);
        Assert.Equal(7, data.AdditiveDefs.Count);
        Assert.Equal(8, data.Materials.Count);
        Assert.Contains(data.Recipes, r => r.Key == "make.add.binder.crude");
        Assert.DoesNotContain(data.Recipes, r => r.Key == "make.add.dilatant.crude");

        for (var i = 0; i < data.Items.Count; i++)
            Assert.Equal(data.Items[i].Id, again.Items[i].Id);

        Assert.Equal(995_000, data.Purity.QMaxPpm);
        Assert.Equal(2000, data.Recipe("am.production").Am.JPerNg);
    }

    [Fact]
    public void BucketEdges_AreDerivedFromBandsAndGates_AsTheDocLists()
    {
        int[] doc = [100_000, 150_000, 170_000, 250_000, 300_000, 400_000, 600_000, 850_000, 900_000, 970_000];
        Assert.Equal(doc, TestData.Data.Purity.GatesPpm);
    }

    [Fact]
    public void NoRecipeOrSource_ExceedsQMax()
    {
        var data = TestData.Data;
        var qMax = data.Purity.QMaxPpm;

        foreach (var r in data.Recipes)
        {
            if (r.Kind == RecipeKind.Enrich)
                Assert.True(r.Enrich.CapPpm <= qMax, r.Key);

            if (r.Kind == RecipeKind.Blend)
                Assert.True(r.Blend.TargetPpm <= qMax, r.Key);

            foreach (var o in r.Outputs)
                Assert.True(o.ContentPpm <= qMax, r.Key);
        }

        foreach (var m in data.Materials)
            Assert.True(m.PurityMaxPpm <= qMax, m.Key);
    }

    public static TheoryData<string, string, string> Refusals() => new()
    {
        { "recipes/em_recrystallise_t2.json", "purityCap=0.999", "above qMax" },
        { "recipes/em_recrystallise_t2.json", "alpha=1.0", "alpha must exceed 1" },
        { "recipes/em_recrystallise_t2.json", "yield=0.98", "yield + loss" },
        { "recipes/em_recrystallise_t2.json", "loss=0", "every loop lossy" },
        { "recipes/em_recrystallise_t2.json", "input.minPurity=0.9", "below purityCap" },
        { "recipes/em_recrystallise_t2.json", "consumes.0.item=solvent.unobtainium", "unknown item" },
        { "recipes/em_recrystallise_t2.json", "version=2", "version" },
        { "recipes/em_recrystallise_t2.json", "kind=Transmute", "unknown kind" },
        { "recipes/em_slurry_blend.json", "targetPurity=0.999", "targetPurity" },
        { "recipes/weapon_agcharge.json", "inputs.0.minPurity=0.999", "no stock can meet" },
        { "recipes/weapon_agcharge.json", "outputs.0.item=em", "cannot output EM" },
        { "em_additives/binder.json", "recipe.outputs=[{\"item\":\"add.binder\",\"kg\":1,\"potency\":0.999}]", "potency" },
        { "em_additives/lattice.json", "item.metastable.floorPotency=1.0", "metastable floor" },
        { "materials/voxel/exotic_ore.json", "placeable=true", "placeable material can never yield EM" },
        { "materials/voxel/remnant_geode.json", "yields.purityRange=[0.97,0.999]", "above qMax" },
        { "physics/exotic.json", "crystal.floor=0.98", "floor must lie below" },
        { "physics/exotic.json", "qMax=1.0", "complete purity" },
        { "physics/exotic.json", "amEnergyJPerKg=6e15", "free energy" },
        { "items/ice.json", "id=em", "duplicate item id em" },
        { "economy/pricing.json", "buyback.itemOfWholesale=1.2", "buyback" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ImpossibleData_IsRefusedAtLoad(string path, string edit, string expected)
    {
        var refusal = TestData.LoadEdited(path, root => Apply(root, edit));

        Assert.NotNull(refusal);
        Assert.Contains(refusal.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void UnmodifiedFiles_LoadThroughTheEditPath() => Assert.Null(TestData.LoadEdited("items/ice.json", _ => { }));

    /// <summary><c>a.b.0.c=value</c>: walks objects and array indices, then sets the JSON value.</summary>
    private static void Apply(JsonObject root, string edit)
    {
        var eq = edit.IndexOf('=');
        var path = edit[..eq].Split('.');
        var value = JsonNode.Parse(edit[(eq + 1)..].StartsWith('[') || char.IsDigit(edit[eq + 1]) || edit[(eq + 1)..] is "true" or "false"
            ? edit[(eq + 1)..]
            : $"\"{edit[(eq + 1)..]}\"");
        JsonNode node = root;

        for (var i = 0; i < path.Length - 1; i++)
            node = int.TryParse(path[i], out var index) ? node[index]! : node[path[i]]!;

        if (int.TryParse(path[^1], out var last))
            node[last] = value;
        else
            node[path[^1]] = value;
    }
}
