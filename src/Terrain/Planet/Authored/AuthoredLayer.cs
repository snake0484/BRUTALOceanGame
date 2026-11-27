using System.Text;
using System.Text.Json;
using Brutal.Numerics;

namespace BrutalGame.Terrain;

/// <summary>How a painted map enters the planet function.</summary>
public enum AuthoredMapMode : byte
{
    /// <summary>Metres added to the height after the relief knee, then clamped to the relief range.</summary>
    Height,
}

public sealed record AuthoredMapBinding(AuthoredMap Map, AuthoredMapMode Mode);

/// <summary>
/// A site stamp (planet-designer-brief.md §2): ground inside <see cref="CoreM"/> of the site's direction is set to
/// <see cref="HeightM"/>, eased back to the generated ground over <see cref="BlendM"/>. <see cref="Since"/> is the
/// content version that added or last moved it (D85: player edits in its core from older saves are dropped).
/// </summary>
public record struct AuthoredSite(string Id, long X, long Y, long Z, float CoreM, float BlendM, float HeightM, int Since)
{
    /// <summary>The planet-frame point (m); positions persist as int64 at 1/1024 m like <see cref="EditOp"/>.</summary>
    public readonly double3 Position => new(X * EditOp.Unit, Y * EditOp.Unit, Z * EditOp.Unit);

    public static AuthoredSite At(string id, double3 point, float coreM, float blendM, float heightM, int since) =>
        new(id, EditOp.Quantise(point.X), EditOp.Quantise(point.Y), EditOp.Quantise(point.Z), coreM, blendM, heightM, since);
}

/// <summary>A named place (city, outpost) for the HUD, spawn and points of interest; not terrain.</summary>
public record struct AuthoredMarker(string Id, string Type, string Name, string? Site);

/// <summary>
/// One body's authored layer (D83, D85): what we change by hand on top of the seeded layout, shipped in
/// <c>data/planets/&lt;body&gt;/</c>. It applies only to the body it was made on (seed, kind and kind seed must match)
/// and is part of the planet function, so every LOD, the impostor and every peer see it. Player edits are a separate
/// layer and never live here.
/// </summary>
public sealed class AuthoredLayer
{
    public const int CurrentSchema = 1;
    public const string BodyFile = "body.json";
    public const string MapFolder = "maps";

    private static readonly string[] Keys = ["id", "schema", "seed", "kind", "kindSeed", "version", "maps", "sites", "markers"];
    private static readonly string[] MapKeys = ["name", "mode"];
    private static readonly string[] SiteKeys = ["id", "type", "pos", "coreM", "blendM", "heightM", "since"];
    private static readonly string[] MarkerKeys = ["id", "type", "name", "site"];

    public required string Id { get; init; }
    public ulong Seed { get; init; }
    public required string Kind { get; init; }
    public ulong KindSeed { get; init; }
    public int Version { get; set; }

    public List<AuthoredMapBinding> Maps { get; init; } = [];
    public List<AuthoredSite> Sites { get; init; } = [];
    public List<AuthoredMarker> Markers { get; init; } = [];

    /// <summary>Nothing that changes terrain: the program is the generated one, bit for bit.</summary>
    public bool ChangesTerrain
    {
        get
        {
            if (Sites.Count > 0)
                return true;

            foreach (var m in Maps)
            {
                if (!m.Map.IsEmpty)
                    return true;
            }

            return false;
        }
    }

    public bool Matches(in PlanetParams p) => p.Seed == Seed && p.KindId == Kind && p.KindSeed == KindSeed;

    public AuthoredMap? Map(string name)
    {
        foreach (var m in Maps)
        {
            if (m.Map.Name == name)
                return m.Map;
        }

        return null;
    }

    /// <summary>FNV-1a over the canonical body file and every map file: what saves and the handshake compare.</summary>
    public ulong Hash()
    {
        var hash = PlanetKindCompiler.Fnv(Encoding.UTF8.GetBytes(ToJson()));

        foreach (var m in Maps)
            hash = PlanetKindCompiler.Fnv(m.Map.Write(), hash);

        return hash;
    }

    /// <summary>Frozen copies of the maps (jobs read them while the designer keeps painting its own).</summary>
    public AuthoredLayer Snapshot()
    {
        var maps = new List<AuthoredMapBinding>(Maps.Count);

        foreach (var m in Maps)
        {
            var map = m.Map.Clone();
            map.Compact();
            maps.Add(m with { Map = map.Freeze() });
        }

        return new AuthoredLayer
        {
            Id = Id, Seed = Seed, Kind = Kind, KindSeed = KindSeed, Version = Version,
            Maps = maps, Sites = [.. Sites], Markers = [.. Markers],
        };
    }

    public static AuthoredLayer Parse(string json, Func<string, byte[]> readMap)
    {
        var id = "(no id)";

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            id = Need(root, "id", id).GetString() ?? throw new InvalidDataException("Authored layer without an id");
            Known(root, Keys, id, "the body");

            if (Need(root, "schema", id).GetInt32() != CurrentSchema)
                throw new InvalidDataException($"Authored layer {id}: schema is not {CurrentSchema}");

            var layer = new AuthoredLayer
            {
                Id = id,
                Seed = Need(root, "seed", id).GetUInt64(),
                Kind = Need(root, "kind", id).GetString() ?? throw new InvalidDataException($"Authored layer {id}: no kind"),
                KindSeed = Need(root, "kindSeed", id).GetUInt64(),
                Version = Need(root, "version", id).GetInt32(),
            };

            if (root.TryGetProperty("maps", out var maps))
            {
                foreach (var m in maps.EnumerateArray())
                {
                    Known(m, MapKeys, id, "a map");
                    var name = Need(m, "name", id).GetString()!;
                    var mode = Need(m, "mode", id).GetString() switch
                    {
                        "height" => AuthoredMapMode.Height,
                        var other => throw new InvalidDataException($"Authored layer {id}: map {name} has unknown mode '{other}'"),
                    };

                    layer.Maps.Add(new AuthoredMapBinding(AuthoredMap.Read(name, readMap(name)), mode));
                }
            }

            if (root.TryGetProperty("sites", out var sites))
            {
                foreach (var s in sites.EnumerateArray())
                {
                    Known(s, SiteKeys, id, "a site");
                    var siteId = Need(s, "id", id).GetString()!;

                    if (Need(s, "type", id).GetString() != "flatten")
                        throw new InvalidDataException($"Authored layer {id}: site {siteId} has an unknown type");

                    var pos = Need(s, "pos", id);

                    if (pos.GetArrayLength() != 3)
                        throw new InvalidDataException($"Authored layer {id}: site {siteId} pos needs three numbers");

                    var site = new AuthoredSite(siteId, pos[0].GetInt64(), pos[1].GetInt64(), pos[2].GetInt64(),
                        Need(s, "coreM", id).GetSingle(), Need(s, "blendM", id).GetSingle(), Need(s, "heightM", id).GetSingle(),
                        s.TryGetProperty("since", out var since) ? since.GetInt32() : layer.Version);

                    if (!(site.CoreM >= 0f) || !(site.BlendM > 0f) || site.Position.Length() < 1.0)
                        throw new InvalidDataException($"Authored layer {id}: site {siteId} needs a position, coreM >= 0 and blendM > 0");

                    layer.Sites.Add(site);
                }
            }

            if (root.TryGetProperty("markers", out var markers))
            {
                foreach (var m in markers.EnumerateArray())
                {
                    Known(m, MarkerKeys, id, "a marker");
                    layer.Markers.Add(new AuthoredMarker(Need(m, "id", id).GetString()!, Need(m, "type", id).GetString()!, Need(m, "name", id).GetString()!,
                        m.TryGetProperty("site", out var site) ? site.GetString() : null));
                }
            }

            return layer;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            throw new InvalidDataException($"Authored layer {id}: malformed ({e.Message})", e);
        }
    }

    /// <summary>The canonical body file: fixed key order, LF line ends, so diffs stay readable and the hash is stable.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();

        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteString("id", Id);
            w.WriteNumber("schema", CurrentSchema);
            w.WriteNumber("seed", Seed);
            w.WriteString("kind", Kind);
            w.WriteNumber("kindSeed", KindSeed);
            w.WriteNumber("version", Version);
            w.WriteStartArray("maps");

            foreach (var m in Maps)
            {
                w.WriteStartObject();
                w.WriteString("name", m.Map.Name);
                w.WriteString("mode", "height");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("sites");

            foreach (var s in Sites)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("type", "flatten");
                w.WriteStartArray("pos");
                w.WriteNumberValue(s.X);
                w.WriteNumberValue(s.Y);
                w.WriteNumberValue(s.Z);
                w.WriteEndArray();
                w.WriteNumber("coreM", s.CoreM);
                w.WriteNumber("blendM", s.BlendM);
                w.WriteNumber("heightM", s.HeightM);
                w.WriteNumber("since", s.Since);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("markers");

            foreach (var m in Markers)
            {
                w.WriteStartObject();
                w.WriteString("id", m.Id);
                w.WriteString("type", m.Type);
                w.WriteString("name", m.Name);

                if (m.Site is not null)
                    w.WriteString("site", m.Site);

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static AuthoredLayer Load(string folder) =>
        Parse(File.ReadAllText(Path.Combine(folder, BodyFile)), name => File.ReadAllBytes(Path.Combine(folder, MapFolder, name + ".bmap")));

    /// <summary>Writes the body file and every map, and removes map files no longer listed.</summary>
    public void Save(string folder)
    {
        var maps = Path.Combine(folder, MapFolder);
        Directory.CreateDirectory(maps);
        File.WriteAllText(Path.Combine(folder, BodyFile), ToJson());

        foreach (var m in Maps)
            File.WriteAllBytes(Path.Combine(maps, m.Map.Name + ".bmap"), m.Map.Write());

        foreach (var file in Directory.GetFiles(maps, "*.bmap"))
        {
            if (Map(Path.GetFileNameWithoutExtension(file)) is null)
                File.Delete(file);
        }
    }

    /// <summary>
    /// D85 on loading a save made on <paramref name="savedVersion"/>: keeps the player's ops that are not inside the
    /// core of a site newer than the save, in order. Returns how many were kept.
    /// </summary>
    public int KeepPlayerEdits(ReadOnlySpan<EditOp> ops, int savedVersion, Span<EditOp> kept)
    {
        var count = 0;

        foreach (var op in ops)
        {
            if (!InNewerCore(op, savedVersion))
                kept[count++] = op;
        }

        return count;
    }

    private bool InNewerCore(in EditOp op, int savedVersion)
    {
        op.Centre(out var px, out var py, out var pz);
        var pl = Math.Sqrt(px * px + py * py + pz * pz);

        foreach (var s in Sites)
        {
            if (s.Since <= savedVersion)
                continue;

            var c = s.Position;
            var scale = pl / c.Length();
            double dx = px - c.X * scale, dy = py - c.Y * scale, dz = pz - c.Z * scale;
            var reach = s.CoreM + op.Radius;

            if (dx * dx + dy * dy + dz * dz <= reach * reach)
                return true;
        }

        return false;
    }

    private static void Known(JsonElement obj, string[] keys, string id, string what)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Authored layer {id}: {what} must be an object");

        foreach (var property in obj.EnumerateObject())
        {
            if (Array.IndexOf(keys, property.Name) < 0)
                throw new InvalidDataException($"Authored layer {id}: unknown key '{property.Name}' in {what}");
        }
    }

    private static JsonElement Need(JsonElement obj, string name, string id) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var e) ? e : throw new InvalidDataException($"Authored layer {id}: missing '{name}'");
}
