using System.Globalization;
using Brutal.ImGuiApi;

namespace BrutalGame.Designer.App;

/// <summary>
/// --input-script: drives the app with scripted mouse input for hands-off UI tests, injected into ImGui after the
/// platform backend each frame (so neither OS focus nor the real cursor interferes). Coordinates are client pixels.
/// One command a line: <c>wait frames</c>, <c>move x y</c>, <c>click x y</c>, <c>drag x0 y0 x1 y1 frames</c>,
/// <c>key name</c> (the app's own key handler: tab, delete, 1, 2, 3, ctrl+z, ctrl+y), <c>shot name</c> (writes
/// <c>name.ready</c> next to the script and holds until <c>name.done</c> appears, so an outside tool can capture the
/// window), <c>log text</c>, <c>quit</c>. Blank lines and lines starting with # are skipped.
/// </summary>
public sealed class InputScript
{
    private const int ShotTimeoutFrames = 3600;

    private readonly string[] _lines;
    private readonly string _folder;
    private readonly Action<string> _key;
    private int _line;
    private int _wait;
    private readonly Queue<Action<ImGuiIOPtr>> _pending = new();
    private string? _shot;
    private int _shotFrames;
    private float _x = -1f;
    private float _y = -1f;

    public InputScript(string path, Action<string> key)
    {
        _lines = File.ReadAllLines(path);
        _folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _key = key;
    }

    public bool Done { get; private set; }

    public bool Quit { get; private set; }

    /// <summary>Between the platform backend and ImGui's frame start: queue this frame's events.</summary>
    public void Inject()
    {
        var io = ImGui.GetIO();

        // The platform backend re-sends the real cursor every frame while focused: hold the scripted one over it.
        if (_x >= 0f)
            io.AddMousePosEvent(_x, _y);

        if (_pending.Count > 0)
        {
            _pending.Dequeue()(io);
            return;
        }

        if (_shot is not null)
        {
            if (File.Exists(Path.Combine(_folder, _shot + ".done")) || ++_shotFrames > ShotTimeoutFrames)
                _shot = null;

            return;
        }

        if (_wait > 0)
        {
            _wait--;
            return;
        }

        while (_line < _lines.Length)
        {
            var text = _lines[_line++].Trim();

            if (text.Length == 0 || text.StartsWith('#'))
                continue;

            Run(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return;
        }

        Done = true;
    }

    private void Move(ImGuiIOPtr io, float x, float y)
    {
        (_x, _y) = (x, y);
        io.AddMousePosEvent(x, y);
    }

    private void Run(string[] f)
    {
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        switch (f[0])
        {
            case "wait":
                _wait = int.Parse(f[1], CultureInfo.InvariantCulture);
                break;
            case "move":
                _pending.Enqueue(io => Move(io, F(f[1]), F(f[2])));
                break;
            case "click":
                var (cx, cy) = (F(f[1]), F(f[2]));
                _pending.Enqueue(io => Move(io, cx, cy));
                _pending.Enqueue(io => io.AddMouseButtonEvent(0, true));
                _pending.Enqueue(_ => { });
                _pending.Enqueue(io => io.AddMouseButtonEvent(0, false));
                _pending.Enqueue(_ => { });
                _pending.Enqueue(_ => { });
                break;
            case "drag":
                var (x0, y0, x1, y1) = (F(f[1]), F(f[2]), F(f[3]), F(f[4]));
                var frames = f.Length > 5 ? int.Parse(f[5], CultureInfo.InvariantCulture) : 20;
                _pending.Enqueue(io => Move(io, x0, y0));
                _pending.Enqueue(io => io.AddMouseButtonEvent(0, true));

                for (var i = 1; i <= frames; i++)
                {
                    var t = i / (float)frames;
                    _pending.Enqueue(io => Move(io, x0 + (x1 - x0) * t, y0 + (y1 - y0) * t));
                }

                _pending.Enqueue(io => io.AddMouseButtonEvent(0, false));
                _pending.Enqueue(_ => { });
                break;
            case "key":
                _key(f[1]);
                break;
            case "shot":
                _shot = f[1];
                _shotFrames = 0;
                File.Delete(Path.Combine(_folder, _shot + ".done"));
                File.WriteAllText(Path.Combine(_folder, _shot + ".ready"), "");
                break;
            case "log":
                var pos = ImGui.GetIO().MousePos;
                Console.WriteLine($"script: {string.Join(' ', f.Skip(1))} (mouse {pos.X:F0}, {pos.Y:F0}, ui wants mouse {ImGui.GetIO().WantCaptureMouse})");
                break;
            case "quit":
                Quit = true;
                Done = true;
                break;
            default:
                Console.WriteLine($"script: unknown command {f[0]}");
                break;
        }
    }
}
