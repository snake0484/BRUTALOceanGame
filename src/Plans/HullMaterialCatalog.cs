using System.Text.Json;

namespace BrutalGame.Plans;

/// <summary>A hull material from data: density (kg/m³, a structural composite, not solid metal) and a display colour.</summary>
public sealed record HullMaterial(DefId Id, string Key, string Name, int DensityKgM3, uint ColourRgb, int YieldMpa = 0);

/// <summary>Hull materials loaded from <c>data/materials/hull.json</c>; no content in code.</summary>
public sealed class HullMaterialCatalog
{
    public const string RelativePath = "materials/hull.json";

    private readonly Dictionary<DefId, HullMaterial> _byId = new();
    private readonly List<HullMaterial> _all = new();

    public IReadOnlyList<HullMaterial> All => _all;

    public bool TryGet(DefId id, out HullMaterial material) => _byId.TryGetValue(id, out material!);

    public static HullMaterialCatalog Load(string dataFolder) => Parse(File.ReadAllText(Path.Combine(dataFolder, RelativePath)));

    /// <summary>Refuses two keys hashing to one <see cref="DefId"/> (catalog loader rule) and bad densities.</summary>
    public static HullMaterialCatalog Parse(string json)
    {
        var catalog = new HullMaterialCatalog();
        using var doc = JsonDocument.Parse(json);

        foreach (var entry in doc.RootElement.GetProperty("materials").EnumerateArray())
        {
            var key = entry.GetProperty("id").GetString() ?? throw new InvalidDataException("material without id");
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
            var density = entry.GetProperty("densityKgM3").GetInt32();
            var colour = Convert.ToUInt32(entry.GetProperty("colour").GetString(), 16);

            if (density <= 0 || density > 30000)
                throw new InvalidDataException($"material {key}: density {density} kg/m³ out of range");

            var yield = entry.TryGetProperty("yieldMpa", out var y) ? y.GetInt32() : 0;
            var material = new HullMaterial(DefId.FromName(key), key, name, density, colour, yield);

            if (!catalog._byId.TryAdd(material.Id, material))
                throw new InvalidDataException($"material {key}: id hash collides with {catalog._byId[material.Id].Key}");

            catalog._all.Add(material);
        }

        return catalog;
    }

    /// <summary>kg/m³ per plan material index; 0 for ids the catalog lacks.</summary>
    public int[] Densities(PlanDocument doc)
    {
        var densities = new int[doc.Materials.Count];

        for (var i = 0; i < densities.Length; i++)
            densities[i] = TryGet(doc.Materials[i], out var m) ? m.DensityKgM3 : 0;

        return densities;
    }
}
