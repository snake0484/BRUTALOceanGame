using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalGame.Debug;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Engine.Time;
using BrutalGame.Physics;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

public sealed class GameApp : IDisposable
{
    private const int TickRate = 60;
    private const int TimingSamples = 256;
    private const string Title = "BRUTALGame";
#if DEBUG
    private const bool Validation = true;
#else
    private const bool Validation = false;
#endif

    private readonly GlfwWindow _window;
    private readonly FrameStats _stats = new(TimingSamples);
    private readonly Renderer _renderer;
    private readonly ImGuiLayer _imgui;
    private readonly ProfilerPanel _profiler;
    private readonly OceanSwell _swell = new(seed: 1);
    private readonly TerrainCoastalDepth _shoreDepth = new();
    private readonly OceanSurface _surface;
    private int _shoreOps = -1;
    private readonly OceanPanel _oceanPanel;
    private readonly WeatherSystem _weather;
    private readonly WeatherPanel _weatherPanel;
    private readonly SceneLighting _clearSky;
    private readonly OceanRenderer _ocean;
    private readonly OceanFft _oceanFft;
    private readonly FlyCamera _camera = new() { Pitch = -0.12f };
    private readonly TerrainRenderer _terrainRenderer;
    private readonly TerrainWorld _terrain;
    private readonly BathymetryTexture _bathymetry;
    private readonly TerrainPanel _terrainPanel;
    private readonly PhysicsWorld _physics = new(workerThreads: WorkerBudget.Tick);
    private readonly TerrainBrush _brush;
    private readonly EditTest? _editTest;
    private FixedClock _clock = new(TickRate);
    private readonly int _oceanScope;
    private readonly CoastalSystem _coastal = new();
    private readonly GpuBenchmark? _bench;
    private readonly FlyTest? _flyTest;
    private FrameSections _sections;
    private readonly long _startTime = Stopwatch.GetTimestamp();
    private readonly int _fftScope;
    private readonly int _oceanComputeScope;
    private readonly int _uiScope;
    private readonly int _terrainScope;
    private readonly int _uploadScope;

    /// <summary>Pipeline statistics scopes (<see cref="PipelineStats"/>): sequential, never nested.</summary>
    private readonly int _terrainStats;
    private readonly int _oceanStats;
    private readonly int _uiStats;

    /// <param name="camera">Optional start pose: x, y, z, yaw degrees, pitch degrees.</param>
    public GameApp(float windSpeed, bool foamDebug, bool editTest, float[]? camera = null, bool hideUi = false, bool coastal = true, double benchSeconds = 0.0, bool flyTest = false, float[]? swell = null, bool weather = true, float weatherOffset = 0f, float renderScale = 1f)
    {
        if (camera is not null)
        {
            _camera.Position = new System.Numerics.Vector3(camera[0], camera[1], camera[2]);
            _camera.Yaw = camera[3] * MathF.PI / 180f;
            _camera.Pitch = camera[4] * MathF.PI / 180f;
        }

        if (!Glfw.Init())
            throw new InvalidOperationException("Failed to initialize GLFW");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
        {
            Title = Title,
            Size = new int2(1600, 900),
        });
        _window.OnKey += OnKey;

        _renderer = new Renderer(_window, _stats, Validation, vsync: benchSeconds <= 0.0, renderScale);
        _imgui = new ImGuiLayer(_window, _renderer);

        var wind = new SwellParams
        {
            WindSpeed = windSpeed,
            SwellHeight = swell?[0] ?? 0f,
            SwellPeriod = swell?[1] ?? 13f,
            SwellDirection = (swell?[2] ?? 0f) * MathF.PI / 180f,
        };
        _weather = new WeatherSystem(new WeatherClimate(), weatherOffset * 60.0, CameraXZ);
        _swell.SetWind(weather ? _weather.WithWind(wind) : wind);
        _oceanFft = new OceanFft(_renderer) { DebugView = foamDebug };
        _surface = new OceanSurface(_swell, _shoreDepth);
        _oceanPanel = new OceanPanel(_surface, _oceanFft) { CoastalEnabled = coastal };
        _weatherPanel = new WeatherPanel(_weather.Map) { DriveOcean = weather, DriveSky = weather, TimeOffsetMinutes = weatherOffset };
        _terrainRenderer = new TerrainRenderer(_renderer);
        _terrain = new TerrainWorld(_terrainRenderer, _physics);

        // Without --cam, 55 m off the west beach, 28 m up, looking at it.
        if (camera is null)
            _camera.Position = new System.Numerics.Vector3(Coastline.WestX(_terrain.Params, 0f) - 55f, 28f, 0f);

        _brush = new TerrainBrush(_terrain, _physics, _camera);
        _editTest = editTest ? new EditTest(_terrain, _physics, _brush, _camera) : null;
        _terrainPanel = new TerrainPanel(_terrain, _terrainRenderer, _physics);
        _bathymetry = new BathymetryTexture(_renderer);
        _ocean = new OceanRenderer(_renderer, _oceanFft, _bathymetry.View, _bathymetry.Sampler);
        _flyTest = flyTest ? new FlyTest(_camera, _terrain, _bathymetry, _coastal) : null;
        _profiler = new ProfilerPanel(_stats, _renderer, _terrainRenderer, _ocean);

        _uploadScope = _renderer.GpuTimer.Register("Uploads");
        _fftScope = _renderer.GpuTimer.Register("Ocean FFT");
        _oceanComputeScope = _renderer.GpuTimer.Register("Ocean compute");
        _terrainScope = _renderer.GpuTimer.Register("Terrain");
        _oceanScope = _renderer.GpuTimer.Register("Ocean");
        _uiScope = _renderer.GpuTimer.Register("ImGui");

        var pipelineStats = _renderer.PipelineStats;
        _terrainStats = pipelineStats.Register("Terrain", PipelinePass.Opaque);
        _oceanStats = pipelineStats.Register("Ocean", PipelinePass.Water);
        _uiStats = pipelineStats.Register("ImGui", PipelinePass.Overlay);

        _clearSky = SceneLightingFromOcean();
        UpdateWeather();

        if (benchSeconds > 0.0)
            _bench = new GpuBenchmark(_renderer.GpuTimer, benchSeconds);

        if (hideUi || _bench is not null)
        {
            _profiler.Open = false;
            _oceanPanel.Open = false;
            _terrainPanel.Open = false;
            _weatherPanel.Open = false;
        }
    }

    /// <summary>--bench: alternates layer B on/off once the world has settled, then closes.</summary>
    private void UpdateBench()
    {
        if (_bench is null)
            return;

        var ready = _terrain.LoadedChunks > 0 && _terrain.PendingJobs == 0 && _coastal.Builds > 0;
        _bench.Update(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, ready);

        _oceanPanel.CoastalEnabled = _bench.FeatureOn;

        if (_bench.Done)
            _window.ShouldClose = true;
    }

    public void Run()
    {
        var last = Stopwatch.GetTimestamp();

        while (!_window.ShouldClose)
        {
            Glfw.PollEvents();

            var size = _window.FramebufferSize;
            if (size.X == 0 || size.Y == 0)
            {
                Glfw.WaitEvents();
                last = Stopwatch.GetTimestamp();
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var frameSeconds = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;

            var mark = Stopwatch.GetTimestamp();
            RunTicks(frameSeconds);
            _sections.Ticks = Lap(ref mark);
            _camera.Update(_window, (float)frameSeconds, _imgui.WantCaptureMouse, _imgui.WantCaptureKeyboard);
            _flyTest?.Fly(_clock.SimTime, _terrain.LoadedChunks > 0 && _terrain.PendingJobs == 0 && _coastal.Builds > 0);

            var terrainStart = Stopwatch.GetTimestamp();
            mark = terrainStart;
            _brush.Update(_window, (float)frameSeconds, _imgui.WantCaptureMouse);
            _editTest?.Update(_clock.SimTime);
            _terrainPanel.BrushRadius = _brush.Radius;
            _terrain.Update(_camera.Position);
            SnapshotShoreDepth();
            _sections.Terrain = Lap(ref mark);
            UpdateWeather();
            _coastal.Update(_terrain, _swell, _clock.SimTime, _oceanPanel.CoastalEnabled);
            _oceanPanel.CoastalBuildMs = _coastal.LastBuildMs;
            _oceanPanel.CoastalBuilds = _coastal.Builds;
            UpdateBench();
            _sections.Coastal = Lap(ref mark);
            _terrainPanel.UpdateMs.Record((float)Stopwatch.GetElapsedTime(terrainStart).TotalMilliseconds);
            BuildUi();
            _sections.Ui = Lap(ref mark);

            if (_renderer.BeginFrame(out var commands))
            {
                var extent = _renderer.Extent;
                var output = _renderer.OutputExtent;
                var aspect = output.Width / (float)output.Height;
                var renderTime = _swell.Time + _clock.Alpha * _clock.TickSeconds;
                var gpu = _renderer.GpuTimer;
                var pipe = _renderer.PipelineStats;
                var viewProj = _camera.ViewProjection(aspect);

                mark = Stopwatch.GetTimestamp();
                gpu.Begin(commands, _uploadScope);
                _terrainRenderer.RecordUploads(commands, _renderer.FrameIndex);
                _bathymetry.Record(commands, _renderer.FrameIndex, _terrain.Bathymetry);
                (_terrainPanel.MapUploadMs, _terrainPanel.MapUploadCells) = (_bathymetry.LastUploadMs, _bathymetry.LastUploadCells);
                gpu.End(commands, _uploadScope);
                _sections.Uploads = Lap(ref mark);

                gpu.Begin(commands, _fftScope);
                _oceanFft.Record(commands, _renderer.FrameIndex, _swell, renderTime, (float)frameSeconds);
                gpu.End(commands, _fftScope);

                _ocean.ShoreWash = _oceanPanel.ShoreWash;
                _ocean.Prepare(_renderer.FrameIndex, _swell, renderTime, viewProj, _camera.Position, _bathymetry.Rect);
                _sections.Prepare = Lap(ref mark);
                gpu.Begin(commands, _oceanComputeScope);
                _ocean.RecordCompute(commands, _renderer.FrameIndex, (float)frameSeconds, _clock.TickIndex);
                gpu.End(commands, _oceanComputeScope);

                _renderer.BeginOpaquePass(commands);

                gpu.Begin(commands, _terrainScope);
                pipe.Begin(commands, _terrainStats);
                _terrainRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, _camera.Position, TerrainLightingFromOcean());
                pipe.End(commands, _terrainStats);
                gpu.End(commands, _terrainScope);

                _renderer.EndOpaquePass(commands);
                _renderer.BeginWaterPass(commands);

                gpu.Begin(commands, _oceanScope);
                pipe.Begin(commands, _oceanStats);
                _ocean.Record(commands, _renderer.FrameIndex, extent, _camera.Position);
                pipe.End(commands, _oceanStats);
                gpu.End(commands, _oceanScope);

                // The scene to the swapchain at native size (Upscale), then the UI over it, sharp at any render scale.
                _renderer.BeginUiPass(commands);
                gpu.Begin(commands, _uiScope);
                pipe.Begin(commands, _uiStats);
                _imgui.Draw(commands);
                pipe.End(commands, _uiStats);
                gpu.End(commands, _uiScope);
                _renderer.EndFrame();
            }

            _stats.Frame.Record((float)(frameSeconds * 1000.0));
            _stats.RecordAllocations();
            _sections.Record = _stats.Record.Last;
            _sections.Present = _stats.Present.Last;
            _sections.GpuWait = _stats.GpuWait.Last;
            _flyTest?.Record(_clock.SimTime, _sections);

            if (_flyTest is { Done: true })
                _window.ShouldClose = true;
        }
    }

    /// <summary>Weather at the camera into the swell wind and the sky; the clear colour follows the fogged horizon.</summary>
    private void UpdateWeather()
    {
        _weather.Update(CameraXZ, _swell, _ocean, _clearSky, _weatherPanel.DriveOcean, _weatherPanel.DriveSky);
        var horizon = _ocean.SkyHorizon;
        _renderer.ClearColor = new float4(horizon.X, horizon.Y, horizon.Z, 1f);
    }

    /// <summary>The surface's depth cap reads the analytic terrain plus edits; re-snapshot when an edit lands (between ticks).</summary>
    private void SnapshotShoreDepth()
    {
        if (_terrain.Ops.Count == _shoreOps)
            return;

        _shoreDepth.Snapshot(_terrain.Params, _terrain.Ops);
        _shoreOps = _terrain.Ops.Count;
    }

    private System.Numerics.Vector2 CameraXZ => new(_camera.Position.X, _camera.Position.Z);

    /// <summary>Milliseconds since <paramref name="mark"/>, which moves to now.</summary>
    private static float Lap(ref long mark)
    {
        var now = Stopwatch.GetTimestamp();
        var ms = (float)Stopwatch.GetElapsedTime(mark, now).TotalMilliseconds;
        mark = now;
        return ms;
    }

    private void RunTicks(double frameSeconds)
    {
        var ticks = _clock.Advance(frameSeconds);
        for (var i = 0; i < ticks; i++)
        {
            var tickStart = Stopwatch.GetTimestamp();
            Tick(_clock.TickIndex, _clock.TickSeconds);
            _clock.TickIndex++;
            _stats.Tick.Record((float)Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);
        }

        _stats.TicksThisFrame = ticks;
        _stats.TickIndex = _clock.TickIndex;
        _stats.SimTime = _clock.SimTime;
    }

    private void Tick(long tickIndex, double dt)
    {
        _physics.Step((float)dt);

        var start = Stopwatch.GetTimestamp();
        _weather.Tick(tickIndex * dt + _weatherPanel.TimeOffset, CameraXZ);
        _weatherPanel.TickMs.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);

        start = Stopwatch.GetTimestamp();
        _swell.SetTime(tickIndex * dt);
        _oceanPanel.PrepareMs.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    private SceneLighting SceneLightingFromOcean() => new()
    {
        SunDirection = _ocean.SunDirection,
        SunIntensity = _ocean.SunIntensity,
        SkyZenith = _ocean.SkyZenith,
        SkyHorizon = _ocean.SkyHorizon,
        FogDensity = _ocean.FogDensity,
        WaterDeep = _ocean.WaterDeep,
        WaterScatter = _ocean.WaterScatter,
    };

    private TerrainLighting TerrainLightingFromOcean() => new()
    {
        SunDirection = _ocean.SunDirection,
        SunIntensity = _ocean.SunIntensity,
        SkyZenith = _ocean.SkyZenith,
        SkyHorizon = _ocean.SkyHorizon,
        FogDensity = _ocean.FogDensity,
    };

    private void BuildUi()
    {
        var uiStart = Stopwatch.GetTimestamp();
        _imgui.NewFrame();
        _profiler.Draw();
        _renderer.PipelineStats.Enabled = _profiler.Open && _profiler.PipelineStatsOn;
        _oceanPanel.Draw();
        _terrainPanel.Draw();
        _weatherPanel.Draw(CameraXZ);
        _imgui.EndFrame();
        _stats.Ui.Record((float)Stopwatch.GetElapsedTime(uiStart).TotalMilliseconds);
    }

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action != GlfwKeyAction.Press)
            return;

        if (key == GlfwKey.F3)
            _profiler.Open = !_profiler.Open;
        else if (key == GlfwKey.F4)
            _oceanPanel.Open = !_oceanPanel.Open;
        else if (key == GlfwKey.F5)
            _terrainPanel.Open = !_terrainPanel.Open;
        else if (key == GlfwKey.F6)
            _weatherPanel.Open = !_weatherPanel.Open;
        else if (key == GlfwKey.Escape && !_imgui.WantCaptureKeyboard)
            window.ShouldClose = true;
    }

    public void Dispose()
    {
        _window.OnKey -= OnKey;
        _renderer.WaitIdle();
        _coastal.Dispose();
        _terrain.Dispose();
        _terrainRenderer.Dispose();
        _physics.Dispose();
        _ocean.Dispose();
        _bathymetry.Dispose();
        _oceanFft.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Glfw.Terminate();
    }
}
