using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrutalGame.Menu;

public enum SettingsLoad : byte
{
    Missing,
    Loaded,
    Corrupt,
    NewerVersion,
}

/// <summary>
/// The settings file: versioned JSON in the user's app-data folder. A missing, unreadable or newer file leaves defaults
/// in <see cref="Current"/>; an unreadable or newer one is kept beside it as settings.json.bad before the next save
/// replaces it. Never throws.
/// </summary>
public sealed class SettingsStore
{
    public const int FileVersion = 1;
    public const string FileName = "settings.json";
    public const string BadSuffix = ".bad";

    private const string AppFolder = "BRUTALGame";
    private const string TempSuffix = ".tmp";

    public readonly string FilePath;
    public readonly GameSettings Current = new();

    public SettingsStore(string folder)
    {
        FilePath = Path.Combine(folder, FileName);
        LastLoad = Load();
    }

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);

    public SettingsLoad LastLoad { get; private set; }

    public SettingsLoad Load()
    {
        Current.Reset();
        LastLoad = Read();

        if (LastLoad is SettingsLoad.Corrupt or SettingsLoad.NewerVersion)
            KeepBadFile();

        return LastLoad;
    }

    public bool Save()
    {
        var file = new SettingsFile
        {
            Version = FileVersion,
            RenderScale = Current.RenderScale,
            VSync = Current.VSync,
            WindowMode = Current.WindowMode.ToString(),
            Width = Current.Width,
            Height = Current.Height,
            FovDegrees = Current.FovDegrees,
            MouseSensitivity = Current.MouseSensitivity,
            InvertY = Current.InvertY,
            MasterVolume = Current.MasterVolume,
            Keys = Current.Keys.ToTable(),
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + TempSuffix;
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SettingsJson.Default.SettingsFile));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private SettingsLoad Read()
    {
        SettingsFile? file;

        try
        {
            if (!File.Exists(FilePath))
                return SettingsLoad.Missing;

            file = JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.SettingsFile);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return SettingsLoad.Corrupt;
        }

        if (file is null || file.Version < 1)
            return SettingsLoad.Corrupt;

        if (file.Version > FileVersion)
            return SettingsLoad.NewerVersion;

        Current.RenderScale = file.RenderScale ?? Current.RenderScale;
        Current.VSync = file.VSync ?? Current.VSync;
        Current.Width = file.Width ?? Current.Width;
        Current.Height = file.Height ?? Current.Height;
        Current.FovDegrees = file.FovDegrees ?? Current.FovDegrees;
        Current.MouseSensitivity = file.MouseSensitivity ?? Current.MouseSensitivity;
        Current.InvertY = file.InvertY ?? Current.InvertY;
        Current.MasterVolume = file.MasterVolume ?? Current.MasterVolume;
        Current.Keys.FromTable(file.Keys);

        if (Enum.TryParse<WindowMode>(file.WindowMode, ignoreCase: true, out var mode))
            Current.WindowMode = mode;

        Current.Sanitize();
        return SettingsLoad.Loaded;
    }

    private void KeepBadFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + BadSuffix, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>On-disk shape. Every value is optional so a hand-edited file missing some keeps defaults for them.</summary>
internal sealed class SettingsFile
{
    public int Version { get; set; }

    public float? RenderScale { get; set; }

    public bool? VSync { get; set; }

    public string? WindowMode { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public float? FovDegrees { get; set; }

    public float? MouseSensitivity { get; set; }

    public bool? InvertY { get; set; }

    public float? MasterVolume { get; set; }

    public Dictionary<string, string>? Keys { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class SettingsJson : JsonSerializerContext;
