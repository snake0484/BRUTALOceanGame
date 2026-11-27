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
    public const float DefaultInlandStart = 0.06f;

    public required string Id { get; init; }
    public string? Compat { get; init; }
    public float ReliefMin { get; init; }
    public float ReliefMax { get; init; }
    public bool SeaPresent { get; init; }

    /// <summary>
    /// Continent value from which the sea clamp holds the ground above the protected band (D30). Layers that can carve
    /// should gate in no earlier, or their hollows in the coastal ramp flood.
    /// </summary>
    public float InlandStart { get; init; } = DefaultInlandStart;

    /// <summary>The channel the sea clamp reads; required with a sea.</summary>
    public string? Continent { get; init; }

    /// <summary>Material palette key (temperate, desert, barren); null draws it from the seed as v1 did.</summary>
    public string? Materials { get; init; }

    public JsonElement Layers { get; init; }

    public bool IsCompatV1 => Compat == CompatV1;

    private static readonly string[] Keys = ["id", "schema", "compat", "requires", "relief", "sea", "continent", "materials", "layers"];
    private static readonly string[] ReliefKeys = ["minM", "maxM"];
    private static readonly string[] SeaKeys = ["present", "inlandStart"];

    public static PlanetKind Parse(string json)
    {
        var id = "(no id)";

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement.Clone();
            id = Need(root, "id", id).GetString() ?? throw new InvalidDataException("Planet kind without an id");

            Known(root, Keys, id);

            if (Need(root, "schema", id).GetInt32() != CurrentSchema)
                throw new InvalidDataException($"Planet kind {id}: schema {root.GetProperty("schema").GetInt32()} is not {CurrentSchema}");

            var relief = Need(root, "relief", id);
            var layers = Need(root, "layers", id);
            var sea = root.TryGetProperty("sea", out var s) ? s : default;
            Known(relief, ReliefKeys, id);

            if (sea.ValueKind != JsonValueKind.Undefined)
                Known(sea, SeaKeys, id);

            var kind = new PlanetKind
            {
                Id = id,
                Compat = root.TryGetProperty("compat", out var c) ? c.GetString() : null,
                ReliefMin = Need(relief, "minM", id).GetSingle(),
                ReliefMax = Need(relief, "maxM", id).GetSingle(),
                SeaPresent = sea.ValueKind != JsonValueKind.Undefined && Need(sea, "present", id).GetBoolean(),
                InlandStart = sea.ValueKind != JsonValueKind.Undefined && sea.TryGetProperty("inlandStart", out var inland) ? inland.GetSingle() : DefaultInlandStart,
                Continent = root.TryGetProperty("continent", out var k) ? k.GetString() : null,
                Materials = root.TryGetProperty("materials", out var m) ? m.GetString() : null,
                Layers = layers,
            };

            if (layers.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Planet kind {id}: layers must be an array");

            if (kind.ReliefMin >= kind.ReliefMax)
                throw new InvalidDataException($"Planet kind {id}: empty relief range");

            if (kind.InlandStart is <= 0f or > 0.5f)
                throw new InvalidDataException($"Planet kind {id}: sea.inlandStart must be in (0, 0.5]");

            if (kind.SeaPresent && kind.Continent is null && !kind.IsCompatV1)
                throw new InvalidDataException($"Planet kind {id}: a sea needs a continent channel");

            _ = kind.MaterialSet;
            return kind;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException($"Planet kind {id}: malformed ({e.Message})", e);
        }
    }

    private static void Known(JsonElement obj, string[] keys, string id)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Planet kind {id}: malformed (an object was expected)");

        foreach (var property in obj.EnumerateObject())
        {
            if (Array.IndexOf(keys, property.Name) < 0)
                throw new InvalidDataException($"Planet kind {id}: unknown key '{property.Name}'");
        }
    }

    private static JsonElement Need(JsonElement obj, string name, string id) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? e : throw new InvalidDataException($"Planet kind {id}: missing '{name}'");

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
            if (!name.EndsWith(".json", StringComparison.Ordinal) || name.StartsWith(AuthoredWorld.AuthoredPrefix, StringComparison.Ordinal) || name.StartsWith(AuthoredWorld.SystemsPrefix, StringComparison.Ordinal))
                continue;

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var kind = PlanetKind.Parse(reader.ReadToEnd());
            kinds[kind.Id] = kind;
        }

        return kinds;
    }
}
