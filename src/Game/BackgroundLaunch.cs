using System.Runtime.InteropServices;
using Brutal.GlfwApi;

namespace BrutalGame;

/// <summary>
/// The game opens behind the other windows without taking focus (--front opens it in front). Only the first window does:
/// a world the menu relaunches opens in front, since the player is using the game by then. The game is a Windows app,
/// so there is no console of its own; logs go to the console it was started from, or to redirected output.
/// </summary>
internal static partial class BackgroundLaunch
{
    public const string FrontFlag = "--front";

    private const int AttachParentProcess = -1;
    private const int StdOutput = -11;
    private const nint BottomOfZOrder = 1;
    private const uint NoSize = 0x0001;
    private const uint NoMove = 0x0002;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;
    private const string GlfwWindowClass = "GLFW30";

    private static bool _pending = true;

    public static void AttachConsole(string[] args)
    {
        if (Array.IndexOf(args, FrontFlag) >= 0)
            _pending = false;

        if (OperatingSystem.IsWindows() && GetStdHandle(StdOutput) == 0)
            AttachConsoleNative(AttachParentProcess);
    }

    /// <summary>Call before creating the window.</summary>
    public static void SetHints()
    {
        var front = _pending ? 0 : 1;
        Glfw.WindowHint(GlfwWindowHint.Visible, front);
        Glfw.WindowHint(GlfwWindowHint.Focused, front);
        Glfw.WindowHint(GlfwWindowHint.FocusOnShow, front);
    }

    /// <summary>Call after creating the window: shows it at the bottom of the z-order, unfocused.</summary>
    public static void Show(GlfwWindow window)
    {
        if (!_pending)
            return;

        _pending = false;
        var hwnd = OperatingSystem.IsWindows() ? FindOwnWindow() : 0;

        if (hwnd == 0 || !SetWindowPos(hwnd, BottomOfZOrder, 0, 0, 0, 0, NoSize | NoMove | NoActivate | ShowWindow))
            Glfw.PInvoke.ShowWindow(window);
    }

    private static nint FindOwnWindow()
    {
        var process = (uint)Environment.ProcessId;
        nint hwnd = 0;

        while ((hwnd = FindWindowEx(0, hwnd, GlfwWindowClass, null)) != 0)
        {
            GetWindowThreadProcessId(hwnd, out var owner);

            if (owner == process)
                return hwnd;
        }

        return 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsoleNative(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint after, string className, string? title);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
