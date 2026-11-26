using Brutal.Logging;
using BrutalGame.Engine.Time;

namespace BrutalGame.Menu;

/// <summary>Frame cost over a window of frames: main-thread work, GPU frame and ImGui, as medians.</summary>
public sealed class MenuCost
{
    private const int Capacity = 1024;

    private readonly float[] _cpu = new float[Capacity];
    private readonly float[] _gpu = new float[Capacity];
    private readonly float[] _ui = new float[Capacity];
    private int _count;
    private int _allocating;
    private long _maxAllocated;

    public void Clear() => (_count, _allocating, _maxAllocated) = (0, 0, 0);

    public void Add(float cpu, float gpu, float ui, long allocated)
    {
        if (_count == Capacity)
            return;

        if (allocated > 0)
            _allocating++;

        _maxAllocated = Math.Max(_maxAllocated, allocated);

        (_cpu[_count], _gpu[_count], _ui[_count]) = (cpu, gpu, ui);
        _count++;
    }

    public string Summary() =>
        $"{_count} frames, median CPU work {Median(_cpu):F2} ms, GPU frame {Median(_gpu):F3} ms, ImGui {Median(_ui):F3} ms, {_allocating} frames allocated on the main thread (max {_maxAllocated} B)";

    private float Median(float[] values)
    {
        if (_count == 0)
            return 0f;

        var sorted = values.AsSpan(0, _count).ToArray();
        Array.Sort(sorted);
        return sorted[_count / 2];
    }
}

/// <summary>
/// --menu-test: drives the app states the way the menu's buttons and Escape do (through <see cref="MenuUi"/>, not by
/// clicking), logs each transition and the frame cost in the menu, in game and paused, then starts a second world with
/// another seed (a relaunch) and quits. Also writes a settings change so the next run can show it persisted.
/// </summary>
public sealed class MenuTest
{
    public const ulong SecondSeed = 42;
    private const float TestFov = 75f;
    private const float AltFov = 70f;
    private const double Settle = 2.0;
    private const double Measure = 3.0;
    private const double LoadTimeout = 90.0;

    private static readonly LogCategory Log = LogCategory.Make("MenuTest");

    private readonly AppStateMachine _app;
    private readonly MenuUi _ui;
    private readonly MenuLaunch _launch;
    private readonly MenuCost _cost = new();
    private int _step;
    private double _stepStart = -1.0;
    private AppState _seen = AppState.Boot;

    public MenuTest(AppStateMachine app, MenuUi ui, MenuLaunch launch, ulong worldSeed)
    {
        _app = app;
        _ui = ui;
        _launch = launch;
        var settings = launch.Settings;
        Log.Info($"settings {settings.LastLoad} from {settings.FilePath}: fov {settings.Current.FovDegrees:F0}, render scale {settings.Current.RenderScale:F2}, vsync {settings.Current.VSync}, mode {settings.Current.WindowMode}; world seed {worldSeed}");

        if (launch.StartInGame)
            _step = 100;
    }

    public bool Done { get; private set; }

    /// <summary>Once a frame after the menu has drawn. <paramref name="revealed"/>: the main menu has faded in.</summary>
    public void Update(double time, bool revealed, float cpuMs, float gpuMs, float uiMs, long allocated)
    {
        if (_app.State != _seen)
        {
            Log.Info($"state {_seen} -> {_app.State} at {time:F1} s");
            _seen = _app.State;
        }

        if (_stepStart < 0.0)
            _stepStart = time;

        var elapsed = time - _stepStart;
        _cost.Add(cpuMs, gpuMs, uiMs, allocated);

        switch (_step)
        {
            case 0 when _app.State == AppState.MainMenu && revealed:
                Next(time);
                break;
            case 1 when elapsed > Settle:
                Next(time);
                break;
            case 2 when elapsed > Measure:
                Report("main menu");
                _ui.StartNewGame(_launch.Seed);
                Next(time);
                break;
            case 3 when _app.State == AppState.Playing:
                Log.Info($"loaded in {elapsed:F1} s");
                Next(time);
                break;
            case 3 when elapsed > LoadTimeout:
                Fail("world never loaded");
                break;
            case 4 when elapsed > Settle:
                Next(time);
                break;
            case 5 when elapsed > Measure:
                Report("playing");
                Expect(_ui.Back(), AppState.Paused, "Escape pauses");
                Next(time);
                break;
            case 6 when elapsed > Settle:
                Report("paused");
                Expect(_ui.Back(), AppState.Playing, "Escape resumes");
                Next(time);
                break;
            case 7 when elapsed > 1.0:
                Expect(_ui.Back(), AppState.Paused, "Escape pauses again");
                Expect(_app.Fire(AppTrigger.ExitToMenu), AppState.MainMenu, "Exit to Menu");
                Next(time);
                break;
            case 8 when elapsed > Settle:
                WriteSettings();
                _ui.StartNewGame(SecondSeed);
                Next(time);
                break;
            case 100 when _app.State == AppState.Playing:
                Log.Info($"second world (seed {_launch.Seed}) loaded in {elapsed:F1} s");
                Next(time);
                break;
            case 100 when elapsed > LoadTimeout:
                Fail("second world never loaded");
                break;
            case 101 when elapsed > Settle:
                Report("second world playing");
                Expect(_app.Fire(AppTrigger.Quit), AppState.Quit, "Quit");
                Log.Info("PASS");
                Done = true;
                break;
        }
    }

    private void Next(double time)
    {
        _step++;
        _stepStart = time;
        _cost.Clear();
    }

    private void Report(string what) => Log.Info($"{what}: {_cost.Summary()}");

    private void Expect(bool fired, AppState state, string what)
    {
        if (!fired || _app.State != state)
            Fail($"{what}: state {_app.State}, expected {state}");
    }

    private void Fail(string why)
    {
        Log.Error($"FAIL: {why}");
        _app.Fire(AppTrigger.Quit);
        Done = true;
    }

    private void WriteSettings()
    {
        var settings = _launch.Settings;
        settings.Current.FovDegrees = settings.Current.FovDegrees == TestFov ? AltFov : TestFov;
        Log.Info($"settings: fov set to {settings.Current.FovDegrees:F0}, saved {settings.Save()}");
    }
}
