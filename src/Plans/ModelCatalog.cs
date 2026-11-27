using System.Text.Json;
using BrutalGame.Ships;

namespace BrutalGame.Plans;

/// <summary>A brush model from game data: id, name and its mesh (+Y out of the hull, +Z along the stroke, origin on the surface).</summary>
public sealed record BrushModel(DefId Id, string Key, string Name, HullMesh Mesh);

/// <summary>
/// The models model brushes repeat, loaded from <c>data/models/models.json</c> (each naming a mesh JSON beside it); game
/// assets only. Missing data is an empty catalog; a model whose mesh does not load or is not a valid mesh is refused.
/// </summary>
public sealed class ModelCatalog
{
    public const string Folder = "models";
    public const string RelativePath = "models/models.json";

    private readonly Dictionary<DefId, int> _byId = new();
    private readonly List<BrushModel> _all = new();

    public IReadOnlyList<BrushModel> All => _all;

    public int IndexOf(DefId id) => _byId.TryGetValue(id, out var index) ? index : -1;

    /// <summary>The mesh of model <paramref name="id"/>, or null.</summary>
    public HullMesh? Mesh(uint id) => _byId.TryGetValue(new DefId(id), out var index) ? _all[index].Mesh : null;

    public static ModelCatalog Load(string dataFolder)
    {
        var path = Path.Combine(dataFolder, RelativePath);
        var catalog = new ModelCatalog();

        if (!File.Exists(path))
            return catalog;

        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var entry in doc.RootElement.GetProperty("models").EnumerateArray())
        {
            var key = entry.GetProperty("id").GetString() ?? throw new InvalidDataException("model without id");
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? key : key;
            var file = entry.GetProperty("mesh").GetString() ?? throw new InvalidDataException($"model {key}: no mesh");
            var mesh = ParseMesh(File.ReadAllText(Path.Combine(dataFolder, Folder, file)), key);
            var model = new BrushModel(DefId.FromName(key), key, name, mesh);

            if (!catalog._byId.TryAdd(model.Id, catalog._all.Count))
                throw new InvalidDataException($"model {key}: id hash collides with {catalog._all[catalog._byId[model.Id]].Key}");

            catalog._all.Add(model);
        }

        return catalog;
    }

    /// <summary>A model mesh JSON: <c>unitsPerMetre</c> (1024), <c>vertices</c> [x, y, z], <c>faces</c> [corner indices].</summary>
    public static HullMesh ParseMesh(string json, string key)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var units = root.TryGetProperty("unitsPerMetre", out var u) ? u.GetInt32() : HullSolid.PositionUnitsPerMetre;
        var scale = HullSolid.PositionUnitsPerMetre / (double)units;
        var builder = new MeshBuilder();

        foreach (var v in root.GetProperty("vertices").EnumerateArray())
            builder.AddVertex(new MeshVertex((int)Math.Round(v[0].GetDouble() * scale), (int)Math.Round(v[1].GetDouble() * scale), (int)Math.Round(v[2].GetDouble() * scale)));

        foreach (var f in root.GetProperty("faces").EnumerateArray())
            builder.AddFace(f.EnumerateArray().Select(c => c.GetInt32()).ToArray(), 0);

        var mesh = builder.Build();

        if (MeshValidator.Validate(mesh, out var value) is var issue && issue != MeshIssue.None)
            throw new InvalidDataException($"model {key}: mesh is not valid ({issue} {value})");

        return mesh;
    }
}
