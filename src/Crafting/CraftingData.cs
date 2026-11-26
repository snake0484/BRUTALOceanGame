namespace BrutalGame.Crafting;

/// <summary>Every refusal the loader found, one line each (<c>file: reason</c>).</summary>
public sealed class CraftingDataException(IReadOnlyList<string> errors)
    : Exception("crafting data refused:\n" + string.Join('\n', errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// All crafting content, loaded from versioned JSON in <c>data/</c> and validated as a whole: items, voxel materials,
/// wormhole additives, recipes, the exotic-matter rules and the pricing knobs. Nothing is hard-coded; ids are dense and
/// assigned in ordinal key order, so they are identical on every machine that loads the same data.
/// </summary>
public sealed class CraftingData
{
    public const int Version = 1;
    public const string ExoticPath = "physics/exotic.json";
    public const string PricingPath = "economy/pricing.json";

    public int TickRateHz { get; internal init; }
    public long TicksPerDay => TickRateHz * 86_400L;
    public required PurityRules Purity { get; init; }
    public required AdditiveRules Additives { get; init; }
    public required PricingRules Pricing { get; init; }
    public DecayRule Crystal { get; internal init; }
    public double AmPerEm { get; internal init; }
    public double AmEnergyJPerKg { get; internal init; }
    public double AmProductionJPerKg { get; internal init; }
    public double CoreEfficiency { get; internal init; }
    public IReadOnlyList<ItemDef> Items => _itemList;
    public IReadOnlyList<MaterialDef> Materials => _materials;
    public IReadOnlyList<RecipeDef> Recipes => _recipes;
    public IReadOnlyList<AdditiveDef> AdditiveDefs => _additives;
    public IReadOnlyList<string> Families => _families;
    public IReadOnlyList<string> FactorKeys => _factorKeys;

    internal ItemDef[] _items = [];
    internal ItemDef[] _itemList = [];
    internal MaterialDef[] _materials = [];
    internal RecipeDef[] _recipes = [];
    internal AdditiveDef[] _additives = [];
    internal AdditiveDef?[] _additiveByItem = [];
    internal string[] _families = [];
    internal string[] _factorKeys = [];
    internal bool[] _factorAdditive = [];
    internal Dictionary<string, ItemDef> _itemsByKey = new(StringComparer.Ordinal);
    internal Dictionary<string, RecipeDef> _recipesByKey = new(StringComparer.Ordinal);

    public ItemDef Item(ItemId id) => _items[id.Value];

    public ItemDef Item(string key) => _itemsByKey.TryGetValue(key, out var def) ? def : throw new KeyNotFoundException($"item {key}");

    public ItemId Id(string key) => Item(key).Id;

    public RecipeDef Recipe(string key) => _recipesByKey.TryGetValue(key, out var def) ? def : throw new KeyNotFoundException($"recipe {key}");

    public MaterialDef Material(string key) => Array.Find(_materials, m => m.Key == key) ?? throw new KeyNotFoundException($"material {key}");

    public AdditiveDef? Additive(ItemId id) => id.Value < _additiveByItem.Length ? _additiveByItem[id.Value] : null;

    public int Family(string name) => Array.IndexOf(_families, name);

    public int FactorKey(string name) => Array.IndexOf(_factorKeys, name);

    public bool FactorIsAdditive(int key) => _factorAdditive[key];

    public static CraftingData Load(string dataFolder)
    {
        var files = new List<(string, string)>();

        foreach (var path in Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories))
            files.Add((Path.GetRelativePath(dataFolder, path).Replace('\\', '/'), File.ReadAllText(path)));

        return Load(files);
    }

    /// <summary>Loads from (relative path, JSON) pairs; throws <see cref="CraftingDataException"/> listing every refusal.</summary>
    public static CraftingData Load(IEnumerable<(string Path, string Json)> files) => new CraftingLoader(files).Build();
}
