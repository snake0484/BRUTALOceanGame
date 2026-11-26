using System.Text.Json.Nodes;

namespace BrutalGame.Crafting.Tests;

internal static class TestData
{
    public static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data");
    public static readonly CraftingData Data = CraftingData.Load(Folder);
    public static readonly ItemId Em = Data.Id("em");
    public static long TicksPerDay => Data.TicksPerDay;

    public static ItemStack EmStack(double kg, double purity) => ItemStack.Assayed(Em, Fixed.Mg(kg), Fixed.Mg(kg * purity));

    public static ItemStack Unassayed(double kg, double truePurity, double nominalPurity, double spread) =>
        ItemStack.Unassayed(Em, Fixed.Mg(kg), Fixed.Mg(kg * truePurity), Fixed.Mg(kg * nominalPurity), Fixed.Ppm(spread));

    public static Inventory Hold(int slots = 64) => new(Data, slots, long.MaxValue / 4, long.MaxValue / 4);

    public static List<(string Path, string Json)> Files()
    {
        var files = new List<(string, string)>();

        foreach (var path in Directory.EnumerateFiles(Folder, "*.json", SearchOption.AllDirectories))
            files.Add((Path.GetRelativePath(Folder, path).Replace('\\', '/'), File.ReadAllText(path)));

        return files;
    }

    /// <summary>Loads the real data with one file's JSON edited; returns the refusal or null when it loaded.</summary>
    public static CraftingDataException? LoadEdited(string path, Action<JsonObject> edit)
    {
        var files = Files();
        var index = files.FindIndex(f => f.Path == path);
        Assert.True(index >= 0, $"no data file {path}");
        var node = JsonNode.Parse(files[index].Json)!.AsObject();
        edit(node);
        files[index] = (path, node.ToJsonString());

        try
        {
            CraftingData.Load(files);
            return null;
        }
        catch (CraftingDataException e)
        {
            return e;
        }
    }
}
