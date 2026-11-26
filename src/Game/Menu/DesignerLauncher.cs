using System.Diagnostics;

namespace BrutalGame.Menu;

/// <summary>
/// Starts the ship designer as its own process (BRUTALDesigner.exe: beside the game when bundled, or the Designer.App
/// build output in a source checkout). One at a time. An in-process designer host replaces this.
/// </summary>
public sealed class DesignerLauncher
{
    private const string Executable = "BRUTALDesigner.exe";
    private const string BundleFolder = "Designer";
    private const string SourceProject = "Designer.App";
    private const string NotFound = "Ship designer not found. Build it once with runDesigner.bat.";
    private const string AlreadyOpen = "The ship designer is already open.";
    private const string Opened = "Ship designer opened in its own window.";
    private const string Failed = "The ship designer could not be started.";

    private Process? _process;

    public string Launch()
    {
        if (_process is { HasExited: false })
            return AlreadyOpen;

        var path = Find(AppContext.BaseDirectory);

        if (path is null)
            return NotFound;

        try
        {
            _process?.Dispose();
            _process = Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false });
            return _process is null ? Failed : Opened;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Failed;
        }
    }

    /// <summary>Beside the game, in a Designer folder beside it, or src/Designer.App/bin/&lt;config&gt;/&lt;tfm&gt; when the game runs from src/Game/bin/&lt;config&gt;/&lt;tfm&gt;.</summary>
    public static string? Find(string gameFolder)
    {
        var beside = Path.Combine(gameFolder, Executable);

        if (File.Exists(beside))
            return beside;

        var bundled = Path.Combine(gameFolder, BundleFolder, Executable);

        if (File.Exists(bundled))
            return bundled;

        var tfm = new DirectoryInfo(gameFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var src = tfm.Parent?.Parent?.Parent?.Parent;

        if (src is null)
            return null;

        var built = Path.Combine(src.FullName, SourceProject, "bin", tfm.Parent!.Name, tfm.Name, Executable);
        return File.Exists(built) ? built : null;
    }
}
