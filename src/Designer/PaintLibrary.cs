using System.Globalization;
using System.Text.Json;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer;

/// <summary>A folder or a paint file in the library tree.</summary>
public sealed class PaintNode
{
    public string Name = "";
    public readonly List<PaintNode> Children = new();

    /// <summary>The file's paint; null for a folder.</summary>
    public HullPaint? Paint;

    /// <summary>The file, relative to the library folder with '/' separators; empty for a folder.</summary>
    public string Relative = "";

    public bool IsFolder => Paint is null;
}

/// <summary>
/// The paint library on disk: <c>Documents/BRUTALGame/Paints</c>, one <c>.bpaint</c> file per paint (JSON: name, the
/// finish's data key, colour as RRGGBBAA hex), subfolders as categories. Plans hold their own copies in slots: importing
/// copies a file into a slot, and a change to the file reaches the plan only when that slot is reimported on purpose.
/// </summary>
public sealed class PaintLibrary
{
    public const string Extension = "bpaint";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public readonly PaintNode Root = new() { Name = "Paints" };

    public PaintLibrary(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Paints");

    /// <summary>Reads the folder tree again; files that do not parse are left out.</summary>
    public void Refresh()
    {
        Root.Children.Clear();

        if (!Directory.Exists(Folder))
            return;

        foreach (var file in Directory.EnumerateFiles(Folder, "*." + Extension, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(Folder, file).Replace('\\', '/');

            if (Load(file, relative) is not { } paint)
                continue;

            var parent = Root;

            foreach (var name in relative.Split('/')[..^1])
            {
                var next = parent.Children.Find(c => c.IsFolder && c.Name == name);

                if (next is null)
                {
                    next = new PaintNode { Name = name };
                    parent.Children.Add(next);
                }

                parent = next;
            }

            parent.Children.Add(new PaintNode { Name = paint.Name, Paint = paint, Relative = relative });
        }
    }

    /// <summary>The library file a slot came from, read now (null when it is gone or unreadable).</summary>
    public HullPaint? Current(string relative) =>
        relative.Length == 0 ? null : Load(Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar)), relative);

    public static HullPaint? Load(string file, string relative)
    {
        try
        {
            var data = JsonSerializer.Deserialize<PaintFile>(File.ReadAllText(file));

            if (data?.Finish is null || data.Colour is null || !uint.TryParse(data.Colour.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
                return null;

            // RRGGBBAA as written; the plan keeps R in the low byte.
            var colour = (rgba >> 24) | ((rgba >> 8) & 0xFF00) | ((rgba << 8) & 0xFF0000) | (rgba << 24);
            return new HullPaint { Name = data.Name ?? Path.GetFileNameWithoutExtension(file), Source = relative, Finish = DefId.FromName(data.Finish).Value, Colour = colour };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="paint"/> (its finish named by <paramref name="finishKey"/>) to <paramref name="file"/>; returns its path relative to the library ('/' separated, or the full path when outside it).</summary>
    public string Save(HullPaint paint, string finishKey, string file)
    {
        var c = paint.Colour;
        var data = new PaintFile
        {
            Name = paint.Name,
            Finish = finishKey,
            Colour = $"{c & 0xFF:X2}{(c >> 8) & 0xFF:X2}{(c >> 16) & 0xFF:X2}{c >> 24:X2}",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(data, Json));
        var relative = Path.GetRelativePath(Folder, file);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFullPath(file) : relative).Replace('\\', '/');
    }

    private sealed class PaintFile
    {
        public string? Name { get; set; }
        public string? Finish { get; set; }
        public string? Colour { get; set; }
    }
}
