using System.Reflection;
using System.Text.Json;

namespace BrutalGame.Terrain;

/// <summary>
/// A planet kind (planet-kinds.md §1.1): the JSON recipe in <c>data/planets/kinds/&lt;id&gt;.json</c>. Holds the ordered
/// layer stack as parsed JSON; <see cref="PlanetKindCompiler"/> turns it into a <see cref="PlanetKindProgram"/> per body.
/// </summary>
public sealed class PlanetKind
{
    public const int CurrentSchema = 1;
    public const string CompatV1 = "v1";

    public required string Id { get; init; }
    public string? Compat { get; init; }
    public float ReliefMin { get; init; }
    public float ReliefMax { get; init; }
    public bool SeaPresent { get; init; }

    /// <summary>The channel the sea clamp reads; required with a sea.</summary>
    public string? Continent { get; init; }

    /// <summary>Material palette key (temperate, desert, barren); null draws it from the seed as v1 did.</summary>
    public string? Materials { get; init; }

    public JsonElement Layers { get; init; }

    public bool IsCompatV1 => Compat == CompatV1;

    public static PlanetKind Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement.Clone();
        var id = root.GetProperty("id").GetString() ?? throw new InvalidDataException("Planet kind without an id");

        if (root.GetProperty("schema").GetInt32() != CurrentSchema)
            throw new InvalidDataException($"Planet kind {id}: schema {root.GetProperty("schema").GetInt32()} is not {CurrentSchema}");

        var relief = root.GetProperty("relief");
        var sea = root.TryGetProperty("sea", out var s) && s.GetProperty("present").GetBoolean();

        var kind = new PlanetKind
        {
            Id = id,
            Compat = root.TryGetProperty("compat", out var c) ? c.GetString() : null,
            ReliefMin = relief.GetProperty("minM").GetSingle(),
            ReliefMax = relief.GetProperty("maxM").GetSingle(),
            SeaPresent = sea,
            Continent = root.TryGetProperty("continent", out var k) ? k.GetString() : null,
            Materials = root.TryGetProperty("materials", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
            Layers = root.GetProperty("layers"),
        };

        if (kind.ReliefMin >= kind.ReliefMax)
            throw new InvalidDataException($"Planet kind {id}: empty relief range");

        if (sea && kind.Continent is null && !kind.IsCompatV1)
            throw new InvalidDataException($"Planet kind {id}: a sea needs a continent channel");

        return kind;
    }

    public PlanetMaterials? MaterialSet => Materials switch
    {
        null => null,
        "temperate" => PlanetMaterials.Temperate,
        "desert" => PlanetMaterials.Desert,
        "barren" => PlanetMaterials.Barren,
        _ => throw new InvalidDataException($"Planet kind {Id}: unknown materials '{Materials}'"),
    };
}

/// <summary>The kinds by id: those shipped in the assembly (data/planets/kinds), overridden by any loaded from disk.</summary>
public static class PlanetKinds
{
    public const string Legacy = "legacy-rolling";

    private static readonly Dictionary<string, PlanetKind> Kinds = LoadEmbedded();
    private static readonly object Gate = new();

    public static PlanetKind Get(string id)
    {
        lock (Gate)
        {
            return Kinds.TryGetValue(id, out var kind) ? kind : throw new KeyNotFoundException($"No planet kind '{id}'");
        }
    }

    public static bool Has(string id)
    {
        lock (Gate)
        {
            return Kinds.ContainsKey(id);
        }
    }

    public static string[] Ids()
    {
        lock (Gate)
        {
            var ids = new string[Kinds.Count];
            Kinds.Keys.CopyTo(ids, 0);
            Array.Sort(ids, StringComparer.Ordinal);
            return ids;
        }
    }

    public static void Register(PlanetKind kind)
    {
        lock (Gate)
        {
            Kinds[kind.Id] = kind;
        }
    }

    /// <summary>Loads every <c>*.json</c> under a kinds folder, replacing shipped kinds of the same id (the viewer's hot path for tuning).</summary>
    public static int LoadDirectory(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        var count = 0;

        foreach (var file in Directory.GetFiles(path, "*.json"))
        {
            Register(PlanetKind.Parse(File.ReadAllText(file)));
            count++;
        }

        return count;
    }

    private static Dictionary<string, PlanetKind> LoadEmbedded()
    {
        var kinds = new Dictionary<string, PlanetKind>(StringComparer.Ordinal);
        var assembly = Assembly.GetExecutingAssembly();

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var kind = PlanetKind.Parse(reader.ReadToEnd());
            kinds[kind.Id] = kind;
        }

        return kinds;
    }
}
