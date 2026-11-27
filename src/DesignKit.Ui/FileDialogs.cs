using Brutal.NativeFileDialogApi;

namespace BrutalGame.DesignKit.Ui;

/// <summary>
/// Native open and save dialogs for one file type, starting in a library folder (created on first use). Where the
/// native library is missing, <see cref="HasDialogs"/> is false and the picks return null (offer a typed path instead).
/// </summary>
public sealed class FileDialogs : IDisposable
{
    private readonly FilterItem[] _filters;
    private readonly bool _dialogs;

    /// <param name="typeName">Shown in the dialog's filter, e.g. "BRUTAL plan".</param>
    /// <param name="extension">Without the dot, e.g. "bplan".</param>
    /// <param name="libraryFolder">Where the dialogs open when there is no current file.</param>
    public FileDialogs(string typeName, string extension, string libraryFolder)
    {
        Extension = extension;
        LibraryFolder = libraryFolder;
        _filters = [new FilterItem { Name = typeName, Spec = extension }];

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

    public string Extension { get; }

    public string LibraryFolder { get; }

    public bool HasDialogs => _dialogs;

    /// <summary>A file to open, or null (cancelled, or no dialogs).</summary>
    public string? PickOpen(string? folder = null)
    {
        if (!_dialogs)
            return null;

        folder ??= LibraryFolder;
        Directory.CreateDirectory(folder);
        var path = NativeFileDialog.OpenDialog(new FileDialogInfo { FilterList = _filters, DefaultPath = folder }, out var result);
        return result == NativeFileDialog.Result.Okay ? path : null;
    }

    /// <summary>A path to save to (the extension added if left off), starting at <paramref name="currentPath"/> when there is one; null if cancelled.</summary>
    public string? PickSave(string currentPath, string? folder = null)
    {
        if (!_dialogs)
            return null;

        folder ??= LibraryFolder;
        Directory.CreateDirectory(folder);
        var name = currentPath.Length > 0 ? Path.GetFileName(currentPath) : "untitled." + Extension;
        var start = currentPath.Length > 0 ? Path.GetDirectoryName(currentPath) ?? folder : folder;
        var path = NativeFileDialog.OpenDialog(new SaveFileDialogInfo { FilterList = _filters, DefaultPath = start, DefaultName = name }, out var result);

        if (result != NativeFileDialog.Result.Okay)
            return null;

        return Path.HasExtension(path) ? path : path + "." + Extension;
    }

    public void Dispose()
    {
        if (_dialogs)
            NativeFileDialog.Shutdown();
    }
}
