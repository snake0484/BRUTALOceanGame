using BrutalGame.DesignKit.Ui;
using BrutalGame.Plans;

namespace BrutalGame.Designer.App;

/// <summary>
/// The standalone host's plan files: a library folder (Documents/BRUTALGame/Plans), native open and save dialogs, and
/// load / save of <c>.bplan</c> bytes. Saving writes the canonical form of a copy, so the open document and its undo
/// log are untouched; the saved bytes load back to the same hash.
/// </summary>
public sealed class FilePlanHost : IDisposable
{
    public const string Extension = "bplan";

    private readonly FileDialogs _dialogs = new("BRUTAL plan", Extension,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Plans"));

    public string LibraryFolder => _dialogs.LibraryFolder;

    public bool HasDialogs => _dialogs.HasDialogs;

    public string? PickOpen() => _dialogs.PickOpen();

    public string? PickSave(string currentPath) => _dialogs.PickSave(currentPath);

    /// <summary>Writes the canonical bytes of a copy of <paramref name="doc"/>; returns its plan hash.</summary>
    public static PlanHash Save(PlanDocument doc, string path)
    {
        var copy = doc.Clone();
        PlanCanonical.Canonicalise(copy);
        var bytes = PlanFormat.Encode(copy);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllBytes(path, bytes);
        return PlanFormat.HashBody(copy);
    }

    public static bool TryLoad(string path, out PlanDocument doc, out string error)
    {
        doc = new PlanDocument();
        error = "";

        if (!File.Exists(path))
        {
            error = $"No file at {path}";
            return false;
        }

        var info = new FileInfo(path);

        if (info.Length > PlanLimits.MaxBundleBytes)
        {
            error = $"{path} is over the {PlanLimits.MaxBundleBytes / 1024} KB plan limit";
            return false;
        }

        if (!PlanFormat.TryDecode(File.ReadAllBytes(path), out doc, out var formatError))
        {
            error = $"{Path.GetFileName(path)}: {formatError}";
            return false;
        }

        return true;
    }

    public void Dispose() => _dialogs.Dispose();
}
