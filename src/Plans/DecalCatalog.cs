using System.Text.Json;

namespace BrutalGame.Plans;

/// <summary>A decal image from game data: id, name and the image file under <c>data/decals</c>.</summary>
public sealed record DecalAsset(DefId Id, string Key, string Name, string Image);

/// <summary>
/// The decal images players can pick, loaded from <c>data/decals/decals.json</c>; game assets only (mods add more by adding
/// to the data, never player files). Missing data is an empty catalog.
/// </summary>
public sealed class DecalCatalog
{
    public const string Folder = "decals";
    public const string RelativePath = "decals/decals.json";

    private readonly Dictionary<DefId, int> _byId = new();
    private readonly List<DecalAsset> _all = new();

    public IReadOnlyList<DecalAsset> All => _all;

    /// <summary>The asset's index in <see cref="All"/> (its texture layer), or -1.</summary>
    public int IndexOf(DefId id) => _byId.TryGetValue(id, out var index) ? index : -1;

    public static DecalCatalog Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, RelativePath);
        var catalog = File.Exists(path) ? Parse(File.ReadAllText(path)) : new DecalCatalog();
        catalog.DataFolder = dataFolder;
        return catalog;
    }

    public string DataFolder { get; private set; } = "";

    /// <summary>The image file of asset <paramref name="index"/>.</summary>
    public string ImagePath(int index) => Path.Combine(DataFolder, Folder, _all[index].Image);

    /// <summary>Refuses two keys hashing to one <see cref="DefId"/>.</summary>
    public static DecalCatalog Parse(string json)
    {
        var catalog = new DecalCatalog();
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.GetProperty("decals").EnumerateArray())
        {
            var key = entry.GetProperty("id").GetString() ?? throw new InvalidDataException("decal without id");
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
            var image = entry.GetProperty("image").GetString() ?? throw new InvalidDataException($"decal {key}: no image");
            var asset = new DecalAsset(DefId.FromName(key), key, name, image);

            if (!catalog._byId.TryAdd(asset.Id, catalog._all.Count))
                throw new InvalidDataException($"decal {key}: id hash collides with {catalog._all[catalog._byId[asset.Id]].Key}");

            catalog._all.Add(asset);
        }

        return catalog;
    }
}
