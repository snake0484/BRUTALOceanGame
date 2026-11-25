using Brutal.NativeFileDialogApi;
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

    private static readonly FilterItem[] Filters = [new FilterItem { Name = "BRUTAL plan", Spec = Extension }];
    private readonly bool _dialogs;

    public FilePlanHost()
    {
        LibraryFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Plans");

        try
        {
            NativeFileDialog.Initialize();
            _dialogs = true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _dialogs = false;
        }
    }

    public string LibraryFolder { get; }

    public bool HasDialogs => _dialogs;

    public string? PickOpen()
    {
        if (!_dialogs)
            return null;

        Directory.CreateDirectory(LibraryFolder);
        var path = NativeFileDialog.OpenDialog(new FileDialogInfo { FilterList = Filters, DefaultPath = LibraryFolder }, out var result);
        return result == NativeFileDialog.Result.Okay ? path : null;
    }

    public string? PickSave(string currentPath)
    {
        if (!_dialogs)
            return null;

        Directory.CreateDirectory(LibraryFolder);
        var name = currentPath.Length > 0 ? Path.GetFileName(currentPath) : "untitled." + Extension;
        var folder = currentPath.Length > 0 ? Path.GetDirectoryName(currentPath) ?? LibraryFolder : LibraryFolder;
        var path = NativeFileDialog.OpenDialog(new SaveFileDialogInfo { FilterList = Filters, DefaultPath = folder, DefaultName = name }, out var result);

        if (result != NativeFileDialog.Result.Okay)
            return null;

        return Path.HasExtension(path) ? path : path + "." + Extension;
    }

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

    public void Dispose()
    {
        if (_dialogs)
            NativeFileDialog.Shutdown();
    }
}
