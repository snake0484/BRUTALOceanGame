using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;
using BrutalGame.Menu;

namespace BrutalGame;

/// <summary>
/// The menu's side of <see cref="GameApp"/>: app states, the main and pause menus, settings and the orbit view behind
/// the main menu. GameApp.cs calls in at a few lines marked "menu:"; with no <see cref="MenuLaunch"/> (a test or bench
/// flag) every hook returns at once and the app runs as before.
/// </summary>
public sealed partial class GameApp
{
    private const double BaseLookSensitivity = 0.0025;

    private MenuLaunch? _menu;
    private AppStateMachine? _app;
    private MenuUi? _menuUi;
    private MenuStyle? _menuStyle;
    private MenuTest? _menuTest;
    private CameraPose _playPose;
    private CameraPose _heldPose;
    private int _gpuFrameScope = -1;
    private ulong _worldSeed;
    private bool _worldUsed;
    private bool _spawnStreaming;
    private double _menuSeconds;
    private WindowMode _appliedMode;
    private int2 _appliedSize;
    private int2 _windowedPos;

    private readonly record struct CameraPose(FrameId Frame, double3 Position, doubleQuat Rotation)
    {
        public static CameraPose Of(FlyCamera camera) => new(camera.Frame, camera.Position, camera.Rotation);

        public void ApplyTo(FlyCamera camera)
        {
            camera.Frame = Frame;
            camera.Position = Position;
            camera.Rotation = Rotation;
        }
    }

    private void InitMenu(MenuLaunch? menu)
    {
        if (menu is null)
            return;

        _menu = menu;
        _worldSeed = menu.Seed;
        _app = new AppStateMachine();
        _menuStyle = new MenuStyle();
        _menuStyle.LoadFonts();
        _menuUi = new MenuUi(_app, _menuStyle, menu.Settings, new EmptySaveCatalog(), new OfflineSessionLauncher(), new DesignerLauncher());
        _gpuFrameScope = FindScope("Frame");
        _playPose = CameraPose.Of(_camera);
        _windowedPos = _window.Position;
        _appliedMode = WindowMode.Windowed;
        _appliedSize = _window.Size;

        var monitor = Glfw.PrimaryMonitor;

        if (!monitor.IsNull())
        {
            var mode = monitor.VideoMode;
            _menuUi.SetDisplay(new int2(mode.Width, mode.Height));
        }

        _profiler.Open = false;
        _oceanPanel.Open = false;
        _terrainPanel.Open = false;
        _weatherPanel.Open = false;
        ApplySettings();

        if (menu.Scripted)
            _menuTest = new MenuTest(_app, _menuUi, menu, _worldSeed);

        _app.Fire(menu.StartInGame ? AppTrigger.StartGame : AppTrigger.ShowMenu);
        menu.StartInGame = false;
    }

    /// <summary>The sim advances only while playing.</summary>
    private double MenuSimSeconds(double frameSeconds) => _app is null || _app.SimRuns ? frameSeconds : 0.0;

    /// <summary>Before the fly camera's input: paused, the pose is kept so the camera cannot move under the menu.</summary>
    private void MenuCameraBefore()
    {
        if (_app?.State == AppState.Paused)
            _heldPose = CameraPose.Of(_camera);
    }

    /// <summary>
    /// After it: the main menu flies the orbit drift, loading holds the spawn, the pause menu undoes any input. Loading
    /// completes only once the terrain has updated at the spawn (<see cref="_spawnStreaming"/>): its Settled flag still
    /// describes the orbit view in the frame the game starts.
    /// </summary>
    private void MenuCameraAfter(double frameSeconds)
    {
        if (_app is null)
            return;

        _spawnStreaming = _app.State == AppState.Loading;

        switch (_app.State)
        {
            case AppState.MainMenu:
                _menuSeconds += frameSeconds;
                _camera.Frame = _planetFrame;
                MenuCamera.Apply(_camera, _sim.Planet.Radius, MenuSun(), _menuSeconds);
                break;
            case AppState.Loading:
                _playPose.ApplyTo(_camera);
                _camera.FovY = PlayFov;
                break;
            case AppState.Paused:
                _heldPose.ApplyTo(_camera);
                break;
            case AppState.Playing:
                _camera.FovY = PlayFov;
                break;
        }
    }

    private float PlayFov => _menu!.Settings.Current.FovDegrees * MathF.PI / 180f;

    /// <summary>Toward the star from the planet, in the planet's surface frame; the planet's +Y before the first poses.</summary>
    private double3 MenuSun()
    {
        var sun = _celestial.SunFrom(_sim.PlanetIndex);
        return sun == double3.Zero ? double3.UnitY : sun;
    }

    /// <summary>In <see cref="BuildUi"/>: the menu, then whatever it asked for.</summary>
    private void DrawMenu()
    {
        if (_app is null)
            return;

        var cpuMs = _sections.Work;
        var gpuMs = _gpuFrameScope >= 0 ? _renderer.GpuTimer.Ring(_gpuFrameScope).Last : 0f;
        _menuUi!.Draw(ImGui.GetIO().DeltaTime, _window.ContentScale.X, new MenuWorld(_app.State == AppState.MainMenu ? _celestial.Ready : WorldReady, _terrain.PendingJobs, _worldSeed, cpuMs, gpuMs));
        _menuTest?.Update(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, _menuUi.Revealed, cpuMs, gpuMs, _renderer.GpuTimer.Ring(_uiScope).Last, _stats.AllocatedMain);

        if (_menuUi.TakeSettingsChanged())
            ApplySettings();

        if (_menuUi.TakeNewGame(out var seed))
            NewGame(seed);

        if (_app.State == AppState.Loading && _spawnStreaming && WorldReady)
            EnterWorld();

        if (_app.State == AppState.Quit)
            _window.ShouldClose = true;
    }

    /// <summary>The world built at launch serves a new game with its seed until it has been played; anything else relaunches.</summary>
    private void NewGame(ulong seed)
    {
        if (seed == _worldSeed && !_worldUsed)
        {
            _app!.Fire(AppTrigger.StartGame);
            return;
        }

        _menu!.RequestNewGame(seed);
        _window.ShouldClose = true;
    }

    private void EnterWorld()
    {
        _app!.Fire(AppTrigger.WorldReady);
        _worldUsed = true;
    }

    /// <summary>Escape and key capture; true when the menu used the key.</summary>
    private bool MenuKey(GlfwKey key)
    {
        if (_menuUi is null)
            return false;

        if (_menuUi.CaptureKey(key))
            return true;

        return key == _menu!.Settings.Current.Keys[GameAction.Pause] && _menuUi.Back();
    }

    private void ApplySettings()
    {
        var settings = _menu!.Settings.Current;
        _renderer.VSync = settings.VSync;
        _renderer.RenderScale = settings.RenderScale;
        _camera.LookSensitivity = BaseLookSensitivity * settings.MouseSensitivity;
        _camera.InvertY = settings.InvertY;

        if (_app!.State is AppState.Playing or AppState.Loading)
            _camera.FovY = PlayFov;

        ApplyWindow(settings);
    }

    /// <summary>Only when the mode or size changed, so a slider elsewhere never resets the window.</summary>
    private void ApplyWindow(GameSettings settings)
    {
        var size = new int2(settings.Width, settings.Height);

        if (settings.WindowMode == _appliedMode && (size == _appliedSize || settings.WindowMode == WindowMode.Borderless))
            return;

        var monitor = Glfw.PrimaryMonitor;

        if (monitor.IsNull())
            return;

        var video = monitor.VideoMode;

        if (_appliedMode == WindowMode.Windowed)
            _windowedPos = _window.Position;

        switch (settings.WindowMode)
        {
            case WindowMode.Fullscreen:
                _window.SetFullscreen(monitor, size, video.RefreshRate);
                break;
            case WindowMode.Borderless:
                _window.SetAttribute(GlfwWindowAttribute.Decorated, 0);
                _window.SetWindowed(monitor.Position, new int2(video.Width, video.Height));
                break;
            default:
                _window.SetAttribute(GlfwWindowAttribute.Decorated, 1);
                _window.SetWindowed(_windowedPos, size);
                break;
        }

        _appliedMode = settings.WindowMode;
        _appliedSize = size;
    }

    private int FindScope(string name)
    {
        var gpu = _renderer.GpuTimer;

        for (var i = 0; i < gpu.ScopeCount; i++)
        {
            if (gpu.Name(i) == name)
                return i;
        }

        return -1;
    }
}
