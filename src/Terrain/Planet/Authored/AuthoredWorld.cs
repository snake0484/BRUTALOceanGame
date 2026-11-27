using System.Reflection;
using System.Text.Json;

namespace BrutalGame.Terrain;

/// <summary>
/// The shipped world (D83, D90): the fixed systems (<c>data/systems/*.json</c>: a system seed and the id of each
/// authored body by index) and the authored layer of each body (<c>data/planets/&lt;body&gt;/</c>), embedded in the
/// assembly like the kinds so every peer of a build agrees. The planet designer registers its working copy in place
/// of the shipped one.
/// </summary>
public static class AuthoredWorld
{
    public const string AuthoredPrefix = "Authored/";
    public const string SystemsPrefix = "Systems/";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, AuthoredLayer> Layers = new(StringComparer.Ordinal);
    private static readonly List<(ulong Seed, string Id, string[] Bodies)> Systems = [];

    static AuthoredWorld() => LoadEmbedded();

    /// <summary>The body's authored layer if one ships (or the designer registered one) and it was made on these params.</summary>
    public static AuthoredLayer? For(in PlanetParams p)
    {
        if (p.BodyId is not { } id)
            return null;

        lock (Gate)
        {
            return Layers.TryGetValue(id, out var layer) && layer.Matches(p) ? layer : null;
        }
    }

    public static AuthoredLayer? Get(string bodyId)
    {
        lock (Gate)
        {
            return Layers.GetValueOrDefault(bodyId);
        }
    }

    /// <summary>Replaces the body's layer (a frozen snapshot: jobs share it).</summary>
    public static void Register(AuthoredLayer layer)
    {
        lock (Gate)
        {
            Layers[layer.Id] = layer;
        }
    }

    /// <summary>The authored id of body <paramref name="index"/> in the fixed system with this seed, or null.</summary>
    public static string? BodyId(ulong systemSeed, int index)
    {
        lock (Gate)
        {
            foreach (var (seed, _, bodies) in Systems)
            {
                if (seed == systemSeed && index < bodies.Length && bodies[index].Length > 0)
                    return bodies[index];
            }

            return null;
        }
    }

    /// <summary>The fixed system's seed by id (the game starts there; the player never enters one).</summary>
    public static ulong SystemSeed(string id)
    {
        lock (Gate)
        {
            foreach (var (seed, systemId, _) in Systems)
            {
                if (systemId == id)
                    return seed;
            }
        }

        throw new KeyNotFoundException($"No fixed system '{id}'");
    }

    public static void ParseSystem(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = document.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var bodies = new List<string>();

        foreach (var body in root.GetProperty("bodies").EnumerateArray())
            bodies.Add(body.GetString() ?? "");

        lock (Gate)
        {
            Systems.RemoveAll(s => s.Id == id);
            Systems.Add((root.GetProperty("seed").GetUInt64(), id, [.. bodies]));
        }
    }

    private static void LoadEmbedded()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        // RecursiveDir puts the host's separators into logical names.
        foreach (var name in assembly.GetManifestResourceNames())
            names[name.Replace('\\', '/')] = name;

        foreach (var (path, name) in names)
        {
            if (path.StartsWith(SystemsPrefix, StringComparison.Ordinal))
                ParseSystem(Text(assembly, name));
        }

        foreach (var (path, name) in names)
        {
            if (!path.StartsWith(AuthoredPrefix, StringComparison.Ordinal) || !path.EndsWith("/" + AuthoredLayer.BodyFile, StringComparison.Ordinal))
                continue;

            var folder = path[..^AuthoredLayer.BodyFile.Length];
            var layer = AuthoredLayer.Parse(Text(assembly, name), map => Bytes(assembly, names.GetValueOrDefault(folder + AuthoredLayer.MapFolder + "/" + map + ".bmap") ?? throw new InvalidDataException($"Authored map {folder}{map} is missing")));
            Layers[layer.Id] = layer.Snapshot();
        }
    }

    private static string Text(Assembly assembly, string name)
    {
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    private static byte[] Bytes(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
