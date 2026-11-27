using System.Text.Json;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer;

/// <summary>A folder or a profile file in the library tree.</summary>
public sealed class ProfileNode
{
    public string Name = "";
    public readonly List<ProfileNode> Children = new();

    /// <summary>The file's profile; null for a folder.</summary>
    public HullProfile? Profile;

    /// <summary>The file, relative to the library folder with '/' separators; empty for a folder.</summary>
    public string Relative = "";

    public bool IsFolder => Profile is null;
}

/// <summary>
/// The profile library on disk: <c>Documents/BRUTALGame/Profiles</c>, one <c>.bprofile</c> file per profile (JSON:
/// name, kind (hallway or wall; hallway when missing) and points in 1/1024 m), subfolders as categories. Plans hold their own copies: importing copies a file into the
/// plan, and a change to the file reaches the plan only when that profile is reimported on purpose.
/// </summary>
public sealed class ProfileLibrary
{
    public const string Extension = "bprofile";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public readonly ProfileNode Root = new() { Name = "Profiles" };

    public ProfileLibrary(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Profiles");

    /// <summary>Reads the folder tree again; files that do not parse are left out.</summary>
    public void Refresh()
    {
        Root.Children.Clear();

        if (!Directory.Exists(Folder))
            return;

        foreach (var file in Directory.EnumerateFiles(Folder, "*." + Extension, SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(Folder, file).Replace('\\', '/');

            if (Load(file, relative) is not { } profile)
                continue;

            var parent = Root;
            var parts = relative.Split('/');

            foreach (var name in parts[..^1])
            {
                var next = parent.Children.Find(c => c.IsFolder && c.Name == name);

                if (next is null)
                {
                    next = new ProfileNode { Name = name };
                    parent.Children.Add(next);
                }

                parent = next;
            }

            parent.Children.Add(new ProfileNode { Name = profile.Name, Profile = profile, Relative = relative });
        }
    }

    /// <summary>The library file a plan profile came from, read now (null when it is gone or unreadable).</summary>
    public HullProfile? Current(string relative) =>
        relative.Length == 0 ? null : Load(Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar)), relative);

    public static HullProfile? Load(string file, string relative)
    {
        try
        {
            var data = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(file));

            var kind = string.Equals(data?.Kind, nameof(ProfileKind.Wall), StringComparison.OrdinalIgnoreCase) ? ProfileKind.Wall : ProfileKind.Hallway;
            var least = kind == ProfileKind.Wall ? HullProfile.MinWallPoints : HullProfile.MinPoints;

            if (data?.Points is null || data.Points.Count < least || data.Points.Count > HullProfile.MaxPoints)
                return null;

            var profile = new HullProfile { Name = data.Name ?? Path.GetFileNameWithoutExtension(file), Source = relative, Kind = kind };

            foreach (var p in data.Points)
            {
                if (p.Length != 2)
                    return null;

                profile.Points.Add(new ProfilePoint(p[0], p[1]));
            }

            return profile;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes <paramref name="profile"/> to <paramref name="file"/>; returns its path relative to the library ('/' separated, or the full path when outside it).</summary>
    public string Save(HullProfile profile, string file)
    {
        var data = new ProfileFile
        {
            Name = profile.Name,
            Kind = profile.Kind.ToString(),
            UnitsPerMetre = HullSolid.PositionUnitsPerMetre,
            Points = profile.Points.Select(p => new[] { p.X, p.Y }).ToList(),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(data, Json));
        var relative = Path.GetRelativePath(Folder, file);
        return (relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFullPath(file) : relative).Replace('\\', '/');
    }

    private sealed class ProfileFile
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public int UnitsPerMetre { get; set; } = HullSolid.PositionUnitsPerMetre;
        public List<int[]>? Points { get; set; }
    }
}
