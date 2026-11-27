using System.Text.Json;

namespace BrutalGame.Plans;

/// <summary>
/// A paint finish from game data: the texture set under <c>data/paints/&lt;folder&gt;</c> (color, normal, roughness, and
/// optionally metalness and bare_mask, white where the paint is scratched through), the metres one tile covers, and a
/// scale on its metalness map (powder coat sold as metal is paint here).
/// </summary>
public sealed record PaintFinish(DefId Id, string Key, string Name, string Folder, float TileMetres, float Metalness);

/// <summary>The paint finishes, loaded from <c>data/paints/finishes.json</c>; game assets only. Missing data is an empty catalog.</summary>
public sealed class FinishCatalog
{
    public const string Folder = "paints";
    public const string RelativePath = "paints/finishes.json";

    private readonly Dictionary<DefId, int> _byId = new();
    private readonly List<PaintFinish> _all = new();

    public IReadOnlyList<PaintFinish> All => _all;

    public string DataFolder { get; private set; } = "";

    /// <summary>The finish's index in <see cref="All"/> (its texture layer), or -1.</summary>
    public int IndexOf(DefId id) => _byId.TryGetValue(id, out var index) ? index : -1;

    /// <summary>Map <paramref name="map"/> ("color", "normal", ...) of finish <paramref name="index"/>; it may not exist.</summary>
    public string MapPath(int index, string map) => Path.Combine(DataFolder, Folder, _all[index].Folder, map + ".jpg");

    public static FinishCatalog Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, RelativePath);
        var catalog = File.Exists(path) ? Parse(File.ReadAllText(path)) : new FinishCatalog();
        catalog.DataFolder = dataFolder;
        return catalog;
    }

    /// <summary>Refuses two keys hashing to one <see cref="DefId"/> and tiles outside 0.05-64 m.</summary>
    public static FinishCatalog Parse(string json)
    {
        var catalog = new FinishCatalog();
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.GetProperty("finishes").EnumerateArray())
        {
            var key = entry.GetProperty("id").GetString() ?? throw new InvalidDataException("finish without id");
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
            var folder = entry.GetProperty("folder").GetString() ?? throw new InvalidDataException($"finish {key}: no folder");
            var tile = entry.TryGetProperty("tileMetres", out var t) ? t.GetSingle() : 1f;
            var metalness = entry.TryGetProperty("metalness", out var m) ? Math.Clamp(m.GetSingle(), 0f, 1f) : 1f;

            if (tile is < 0.05f or > 64f)
                throw new InvalidDataException($"finish {key}: tile {tile} m out of range");

            var finish = new PaintFinish(DefId.FromName(key), key, name, folder, tile, metalness);

            if (!catalog._byId.TryAdd(finish.Id, catalog._all.Count))
                throw new InvalidDataException($"finish {key}: id hash collides with {catalog._all[catalog._byId[finish.Id]].Key}");

            catalog._all.Add(finish);
        }

        return catalog;
    }
}
