using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalGame.Debug;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Engine.Time;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

public sealed class GameApp : IDisposable
{
    private const int TimingSamples = 256;
    private const string Title = "BRUTALGame";
    private const float OrbitSunIntensity = 1.5f;
#if DEBUG
    private const bool Validation = true;
#else
    private const bool Validation = false;
#endif

    private static readonly System.Numerics.Vector3 SpaceColour = new(0.004f, 0.005f, 0.008f);
    private static readonly System.Numerics.Vector3 MarkerColour = new(1f, 0.35f, 0.05f);

    private readonly GlfwWindow _window;
    private readonly FrameStats _stats = new(TimingSamples);
    private readonly Renderer _renderer;
    private readonly ImGuiLayer _imgui;
    private readonly ProfilerPanel _profiler;
    private readonly SimWorld _sim;
    private readonly OceanSwell _swell;
    private readonly OceanPanel _oceanPanel;
    private readonly WeatherSystem _weather;
    private readonly WeatherPanel _weatherPanel;
    private readonly SceneLighting _clearSky;
    private readonly OceanRenderer _ocean;
    private readonly OceanFft _oceanFft;
    private readonly FlyCamera _camera = new();
    private readonly IslandFrame _island;
    private readonly InstanceBuffer _instances;
    private readonly TerrainRenderer _terrainRenderer;
    private readonly TerrainWorld _terrain;
    private readonly BathymetryTexture _bathymetry;
    private readonly TerrainPanel _terrainPanel;
    private readonly TerrainBrush _brush;
    private readonly EditTest? _editTest;
    private readonly int _oceanScope;
    private readonly GpuBenchmark? _bench;
    private readonly FlyTest? _flyTest;
    private readonly OrbitTest? _orbitTest;
    private readonly PlanetRenderer? _planets;
    private FrameSections _sections;
    private readonly long _startTime = Stopwatch.GetTimestamp();
    private readonly int _fftScope;
    private readonly int _oceanComputeScope;
    private readonly int _uiScope;
    private readonly int _terrainScope;
    private readonly int _terrainCullScope;
    private readonly int _uploadScope;
    private readonly int _planetScope = -1;

    /// <summary>Pipeline statistics scopes (<see cref="PipelineStats"/>): sequential, never nested.</summary>
    private readonly int _terrainStats;
    private readonly int _oceanStats;
    private readonly int _uiStats;

    /// <param name="camera">Optional start pose: x, y, z, yaw degrees, pitch degrees.</param>
    /// <param name="orbitTest">--orbit-test [inertial]: the test planet as a smooth sphere with the island's terrain on it, flown from 2 m to 1e8 m with the camera in its surface or inertial frame; no ocean.</param>
    public GameApp(float windSpeed, bool foamDebug, bool editTest, float[]? camera = null, bool hideUi = false, double benchSeconds = 0.0, bool flyTest = false, float[]? swell = null, bool weather = true, float weatherOffset = 0f, float renderScale = 1f, OrbitTestFrame? orbitTest = null)
    {
        var weatherField = new WeatherField(new WeatherClimate());
        _sim = new SimWorld(seed: 1, WorkerBudget.Tick, new TerrainParams(), weatherField) { DriveWind = weather, WeatherTimeOffset = weatherOffset * 60.0 };

        _island = new IslandFrame(_sim.Celestial);
        _camera.Frame = _island.Frame;

        if (camera is not null)
            PlaceCamera(new System.Numerics.Vector3(camera[0], camera[1], camera[2]), camera[3] * MathF.PI / 180f, camera[4] * MathF.PI / 180f);

        if (!Glfw.Init())
            throw new InvalidOperationException("Failed to initialize GLFW");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo
        {
            Title = Title,
            Size = new int2(1600, 900),
        });
        _window.OnKey += OnKey;

        _swell = _sim.Ocean.Swell;
        _renderer = new Renderer(_window, _stats, Validation, vsync: benchSeconds <= 0.0, renderScale);
        _imgui = new ImGuiLayer(_window, _renderer);

        var wind = new SwellParams
        {
            WindSpeed = windSpeed,
            SwellHeight = swell?[0] ?? 0f,
            SwellPeriod = swell?[1] ?? 13f,
            SwellDirection = (swell?[2] ?? 0f) * MathF.PI / 180f,
        };
        _weather = new WeatherSystem(weatherField, _sim.WeatherTime, CameraXZ);
        _sim.SetSwell(wind);
        _oceanFft = new OceanFft(_renderer) { DebugView = foamDebug };
        _oceanPanel = new OceanPanel(_sim.Ocean, _oceanFft);
        _weatherPanel = new WeatherPanel(_weather.Map) { DriveOcean = weather, DriveSky = weather, TimeOffsetMinutes = weatherOffset };
        _instances = new InstanceBuffer(_renderer);
        _terrainRenderer = new TerrainRenderer(_renderer, _instances);

        if (orbitTest is { } orbitFrame)
        {
            _orbitTest = new OrbitTest(_camera, _sim.Celestial, orbitFrame);
            _planets = new PlanetRenderer(_renderer, _instances);
        }

        _terrain = new TerrainWorld(_terrainRenderer, _sim.Physics, _sim.Terrain);

        // Without --cam, 55 m off the west beach, 28 m up, looking at it.
        if (camera is null)
            PlaceCamera(new System.Numerics.Vector3(Coastline.WestX(_terrain.Params, 0f) - 55f, 28f, 0f), 0f, -0.12f);

        _brush = new TerrainBrush(_sim, _camera, _island);
        _editTest = editTest ? new EditTest(_sim, _terrain, _brush, _camera, _island) : null;
        _terrainPanel = new TerrainPanel(_terrain, _terrainRenderer, _sim.Physics);
        _bathymetry = new BathymetryTexture(_renderer);
        _ocean = new OceanRenderer(_renderer, _oceanFft, _bathymetry.View, _bathymetry.Sampler);
        _flyTest = flyTest ? new FlyTest(_camera, _island, _terrain, _bathymetry) : null;
        _profiler = new ProfilerPanel(_stats, _renderer, _terrainRenderer, _ocean);

        _uploadScope = _renderer.GpuTimer.Register("Uploads");
        _fftScope = _renderer.GpuTimer.Register("Ocean FFT");
        _oceanComputeScope = _renderer.GpuTimer.Register("Ocean compute");
        _terrainCullScope = _renderer.GpuTimer.Register("Terrain cull");
        _terrainScope = _renderer.GpuTimer.Register("Terrain");
        _oceanScope = _renderer.GpuTimer.Register("Ocean");
        _uiScope = _renderer.GpuTimer.Register("ImGui");

        if (_planets is not null)
            _planetScope = _renderer.GpuTimer.Register("Planet");

        var pipelineStats = _renderer.PipelineStats;
        _terrainStats = pipelineStats.Register("Terrain", PipelinePass.Opaque);
        _oceanStats = pipelineStats.Register("Ocean", PipelinePass.Water);
        _uiStats = pipelineStats.Register("ImGui", PipelinePass.Overlay);

        _clearSky = SceneLightingFromOcean();
        UpdateWeather();

        if (benchSeconds > 0.0)
            _bench = new GpuBenchmark(_renderer.GpuTimer, benchSeconds, ["CPU frame", "CPU record", "Terrain rec"], [_stats.Frame, _stats.Record, _terrainRenderer.DrawMs]);

        if (hideUi || _bench is not null)
        {
            _profiler.Open = false;
            _oceanPanel.Open = false;
            _terrainPanel.Open = false;
            _weatherPanel.Open = false;
        }
    }

    /// <summary>--bench: alternates the shore wash on/off once the world has settled, then closes.</summary>
    private void UpdateBench()
    {
        if (_bench is null)
            return;

        _bench.Update(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, WorldReady);
        _oceanPanel.ShoreWash = _bench.FeatureOn;

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
            _sim.UpdateRenderPoses();
            _sections.Ticks = Lap(ref mark);

            if (_orbitTest is null)
                _camera.Update(_window, frameSeconds, _imgui.WantCaptureMouse, _imgui.WantCaptureKeyboard);

            _flyTest?.Fly(_sim.Clock.SimTime, WorldReady);
            _orbitTest?.Fly(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, WorldReady, OutputAspect, RenderViewport, _sim.RenderTime);

            var terrainStart = Stopwatch.GetTimestamp();
            mark = terrainStart;
            _brush.Update(_window, (float)frameSeconds, _imgui.WantCaptureMouse);
            _editTest?.Update(_sim.Clock.SimTime);
            _terrainPanel.BrushRadius = _brush.Radius;
            SyncEdits();
            _terrain.Update(CameraOnIsland);
            _sections.Terrain = Lap(ref mark);
            UpdateWeather();
            UpdateBench();
            _sections.Weather = Lap(ref mark);
            _terrainPanel.UpdateMs.Record((float)Stopwatch.GetElapsedTime(terrainStart).TotalMilliseconds);
            BuildUi();
            _sections.Ui = Lap(ref mark);

            if (_renderer.BeginFrame(out var commands))
            {
                var extent = _renderer.Extent;
                var output = _renderer.OutputExtent;
                var aspect = output.Width / (float)output.Height;
                var renderTime = _swell.Time + _sim.Clock.Alpha * _sim.Clock.TickSeconds;
                var gpu = _renderer.GpuTimer;
                var pipe = _renderer.PipelineStats;
                var viewProj = _camera.ViewProjection(aspect);
                var terrain = TerrainView(out var terrainCamera, out var terrainAxes);

                // The ocean takes no axes, so it is drawn only with the camera in the island's frame; not in the orbit test.
                var ocean = terrain && _orbitTest is null && _camera.Frame == _island.Frame;

                mark = Stopwatch.GetTimestamp();
                _instances.BeginFrame(_renderer.FrameIndex);
                gpu.Begin(commands, _uploadScope);
                _terrainRenderer.RecordUploads(commands, _renderer.FrameIndex);
                _bathymetry.Record(commands, _renderer.FrameIndex, _terrain.Bathymetry);
                (_terrainPanel.MapUploadMs, _terrainPanel.MapUploadCells) = (_bathymetry.LastUploadMs, _bathymetry.LastUploadCells);
                gpu.End(commands, _uploadScope);
                _sections.Uploads = Lap(ref mark);

                gpu.Begin(commands, _fftScope);
                _oceanFft.Record(commands, _renderer.FrameIndex, _swell, renderTime, (float)frameSeconds);
                gpu.End(commands, _fftScope);

                if (ocean)
                {
                    _ocean.ShoreWash = _oceanPanel.ShoreWash;
                    _ocean.Prepare(_renderer.FrameIndex, _swell, renderTime, viewProj, terrainCamera, _bathymetry.Rect);
                    _sections.Prepare = Lap(ref mark);
                    gpu.Begin(commands, _oceanComputeScope);
                    _ocean.RecordCompute(commands, _renderer.FrameIndex, (float)frameSeconds, _sim.Clock.TickIndex);
                    gpu.End(commands, _oceanComputeScope);
                }

                if (terrain)
                {
                    gpu.Begin(commands, _terrainCullScope);
                    _terrainRenderer.Cull(commands, _renderer.FrameIndex, viewProj, terrainCamera, terrainAxes);
                    gpu.End(commands, _terrainCullScope);
                }

                _renderer.BeginOpaquePass(commands);

                if (terrain)
                {
                    gpu.Begin(commands, _terrainScope);
                    pipe.Begin(commands, _terrainStats);
                    _terrainRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, terrainCamera, _orbitTest is null ? TerrainLightingFromOcean() : OrbitTerrainLighting());
                    pipe.End(commands, _terrainStats);
                    gpu.End(commands, _terrainScope);
                }

                if (_planets is not null)
                {
                    gpu.Begin(commands, _planetScope);
                    DrawPlanet(commands, extent, aspect);
                    gpu.End(commands, _planetScope);
                }

                _renderer.EndOpaquePass(commands);

                if (_planets is not null)
                    PlanetRenderer.EndPass(commands);

                _renderer.BeginWaterPass(commands);

                if (ocean)
                {
                    gpu.Begin(commands, _oceanScope);
                    pipe.Begin(commands, _oceanStats);
                    _ocean.Record(commands, _renderer.FrameIndex, extent);
                    pipe.End(commands, _oceanStats);
                    gpu.End(commands, _oceanScope);
                }

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
            _flyTest?.Record(_sim.Clock.SimTime, _sections);
            RecordOrbitTest();

            if (_flyTest is { Done: true } || _orbitTest is { Done: true })
                _window.ShouldClose = true;
        }
    }

    /// <summary>Weather at the camera into the sky; the clear colour follows the fogged horizon (space in the orbit test).</summary>
    private void UpdateWeather()
    {
        _weather.Update(CameraXZ, _ocean, _clearSky, _weatherPanel.DriveSky);
        var horizon = _orbitTest is null ? _ocean.SkyHorizon : SpaceColour;
        _renderer.ClearColor = new float4(horizon.X, horizon.Y, horizon.Z, 1f);
    }

    /// <summary>--orbit-test: the planet sphere and the marker, lit by the star through the frame tree. The marker probe of this slot's last frame goes to the test.</summary>
    private void DrawPlanet(Brutal.VulkanApi.CommandBuffer commands, Brutal.VulkanApi.VkExtent2D extent, float aspect)
    {
        _planets!.Begin(commands, _renderer.FrameIndex, extent, _camera, aspect, _orbitTest!.SunDirection, OrbitSunIntensity, _orbitTest.ProbeTag);
        _orbitTest.ResolveProbe(_planets.Probe);
        _planets.DrawSphere(commands, _orbitTest.PlanetCentre, _orbitTest.PlanetRadius, _orbitTest.PlanetAxes);
        _planets.DrawMarker(commands, _orbitTest.Marker, _orbitTest.MarkerRadius, MarkerColour);
    }

    private void RecordOrbitTest()
    {
        if (_orbitTest is null)
            return;

        _orbitTest.Record(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, OutputAspect, RenderViewport, _stats.Frame.Last, _sections.Work, _renderer.GpuTimer.Ring(_planetScope).Last, _terrainRenderer.DrawnLastFrame);
    }

    /// <summary>The aspect the view-projection is built with.</summary>
    private float OutputAspect => _renderer.OutputExtent.Width / (float)_renderer.OutputExtent.Height;

    /// <summary>The render target's size (render scale applied): the pixels the scene is drawn in.</summary>
    private System.Numerics.Vector2 RenderViewport => new(_renderer.Extent.Width, _renderer.Extent.Height);

    private bool WorldReady => _terrain.LoadedChunks > 0 && _terrain.PendingJobs == 0;

    /// <summary>
    /// The camera in island coordinates and the island's axes in the camera frame's, through the frame tree at render
    /// time when the camera is in another frame. False outside the planet's SOI, so a heliocentric camera never casts
    /// its position into chunk coordinates.
    /// </summary>
    private bool TerrainView(out double3 camera, out doubleQuat axes)
    {
        camera = _camera.Position;
        axes = IslandFrame.Axes;

        if (_camera.Frame != _island.Frame)
        {
            if (!_sim.Celestial.Frames.TryRelativeRender(_island.Frame, _camera.Frame, out var island))
                return false;

            camera = doubleQuat.Conjugate(island.Rotation) * (camera - island.Position);
            axes = island.Rotation;
        }

        var inSoi = camera.Length() < _island.SoiRadius;
        camera -= _island.Origin;
        return inSoi;
    }

    /// <summary>The island's terrain in the orbit test: lit by the star (camera frame axes, as its normals), no sky or fog.</summary>
    private TerrainLighting OrbitTerrainLighting() => new()
    {
        SunDirection = _orbitTest!.SunDirection,
        SunIntensity = OrbitSunIntensity,
        SkyZenith = SpaceColour,
        SkyHorizon = SpaceColour,
        FogDensity = 0f,
    };

    /// <summary>The camera in island coordinates; the island's origin during the orbit test, so the island stays loaded and still.</summary>
    private System.Numerics.Vector3 CameraOnIsland => _orbitTest is null ? _island.ToIsland(_camera.Position) : default;

    private System.Numerics.Vector2 CameraXZ
    {
        get
        {
            var camera = CameraOnIsland;
            return new System.Numerics.Vector2(camera.X, camera.Z);
        }
    }

    /// <summary>The camera at an island point, looking along yaw (from +X toward +Z) and pitch.</summary>
    private void PlaceCamera(System.Numerics.Vector3 position, float yaw, float pitch)
    {
        _camera.Position = _island.ToFrame(position);
        _camera.LookAlong(FlyCamera.Direction(yaw, pitch));
    }

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
        _sim.DriveWind = _weatherPanel.DriveOcean;
        _sim.WeatherTimeOffset = _weatherPanel.TimeOffset;

        var ticks = _sim.Advance(frameSeconds);
        for (var i = 0; i < ticks; i++)
        {
            var tickStart = Stopwatch.GetTimestamp();
            _weather.Tick(_sim.WeatherTime, CameraXZ);
            _weatherPanel.TickMs.Record((float)Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);

            _sim.Tick();
            _oceanPanel.PrepareMs.Record(_sim.LastSwellMs);
            _stats.Tick.Record((float)Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);
        }

        ref readonly var clock = ref _sim.Clock;
        _stats.TicksThisFrame = ticks;
        _stats.TickIndex = clock.TickIndex;
        _stats.SimTime = clock.SimTime;
    }

    /// <summary>The client's terrain replays the edits the sim accepted (they only ever append).</summary>
    private void SyncEdits()
    {
        var edits = _sim.Edits;

        for (var i = _terrain.EditCount; i < edits.Length; i++)
            _terrain.ApplyBrush(edits[i]);
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
        _terrainRenderer.StatsEnabled = _profiler.Open || _terrainPanel.Open || _orbitTest is not null;
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
        _terrain.Dispose();
        _terrainRenderer.Dispose();
        _planets?.Dispose();
        _instances.Dispose();
        _sim.Dispose();
        _ocean.Dispose();
        _bathymetry.Dispose();
        _oceanFft.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Glfw.Terminate();
    }
}
