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
using BrutalGame.Terrain.Runtime.Water;
using BrutalGame.Terrain.Water;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;
using BrutalGame.Weather;

namespace BrutalGame;

public sealed class GameApp : IDisposable
{
    private const int TickRate = 60;
    private const int TimingSamples = 256;
    private const string Title = "BRUTALGame";
    private const int VoxelSwellIterations = 2;
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
    private readonly OceanPanel _oceanPanel;
    private readonly WeatherSystem _weather;
    private readonly WeatherPanel _weatherPanel;
    private readonly SceneLighting _clearSky;
    private readonly OceanRenderer _ocean;
    private readonly RainRenderer _rain;
    private readonly RainCover _rainCover;
    private readonly OceanFft _oceanFft;
    private readonly WindSeaBands _windSea;
    private readonly FlyCamera _camera = new() { Pitch = -0.12f };
    private readonly TerrainRenderer _terrainRenderer;
    private readonly TerrainWorld _terrain;
    private readonly BathymetryTexture _bathymetry;
    private readonly TerrainPanel _terrainPanel;
    private readonly PhysicsWorld _physics = new(workerThreads: WorkerBudget.Tick);
    private readonly TerrainBrush _brush;
    private readonly EditTest? _editTest;
    private readonly RoofTest? _roofTest;
    private readonly CaveTest? _caveTest;
    private readonly FloodTest? _floodTest;
    private readonly SeasideTest? _seasideTest;
    private readonly PondTest? _pondTest;
    private readonly LakeTest? _lakeTest;
    private readonly RiverTest? _riverTest;
    private FixedClock _clock = new(TickRate);
    private readonly int _oceanScope;
    private readonly int _waterScope;
    private readonly VoxelWaterSystem _water;
    private readonly CoastalSystem _coastal = new();
    private bool _seaSamplerWarm;
    private readonly GpuBenchmark? _bench;
    private readonly bool _benchRain;

    /// <summary>The benchmark alternates the water fog (docs/water-fog.md) instead of layer B.</summary>
    private readonly bool _benchFog;

    /// <summary>The benchmark alternates the water's screen-space reflections (docs/water-ssr.md) instead of layer B.</summary>
    private readonly bool _benchSsr;
    private readonly BreakerSet _breakers = new();
    private readonly BreakerFaces _breakerFaces = new();
    private readonly OceanSurface _surface;
    private readonly BreakerBores _breakerBores = new();
    private readonly SurfTest? _surfTest;
    private readonly FlyTest? _flyTest;
    private FrameSections _sections;
    private readonly BreakerOverlay _breakerOverlay = new();
    private readonly long _startTime = Stopwatch.GetTimestamp();
    private readonly VoxelWaterRenderer _waterRenderer;

    /// <summary>The water fog volume around the camera (docs/water-fog.md): filled from the voxel water each frame, marched by the water and terrain shaders.</summary>
    private readonly WaterFogVolume _fog;
    private readonly int _fogScope;

    /// <summary>The opaque scene copied between the passes for every water's reflections (docs/water-ssr.md).</summary>
    private readonly ScreenReflections _reflections;
    private readonly int _reflectionScope;
    private readonly int _fftScope;
    private readonly int _windSeaScope;
    private readonly int _sprayScope;
    private readonly int _uiScope;
    private readonly int _terrainScope;
    private readonly int _uploadScope;

    /// <summary>Pipeline statistics scopes (<see cref="PipelineStats"/>): sequential, never nested.</summary>
    private readonly int _terrainStats;
    private readonly int _oceanStats;
    private readonly int _waterStats;
    private readonly int _rainStats;
    private readonly int _uiStats;

    /// <param name="camera">Optional start pose: x, y, z, yaw degrees, pitch degrees.</param>
    public GameApp(float windSpeed, bool foamDebug, bool editTest, float[]? camera = null, bool floodTest = false, int waterRate = 240, bool hideUi = false, bool seasideTest = false, bool coastal = true, double benchSeconds = 0.0, bool breakers = false, bool surfTest = false, bool faces = true, bool lips = true, bool flyTest = false, bool surfCam = false, bool surfSide = false, float[]? swell = null, bool foldLip = false, bool surfBack = false, bool chainLips = false, bool weather = true, float weatherOffset = 0f, float rain = -1f, bool benchRain = false, bool roofTest = false, string? caveTest = null, bool waterPipes = false, bool pondTest = false, string? lakeTest = null, bool benchFog = false, bool fogOn = true, bool benchSsr = false, bool ssrOn = true, bool voxelSwell = true, string? riverTest = null, float renderScale = 1f)
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
        _rainCover = new RainCover(_renderer.Context);
        _fog = new WaterFogVolume(_renderer.Context) { SeaColour = new System.Numerics.Vector4(WaterColour.R(WaterColour.Sea), WaterColour.G(WaterColour.Sea), WaterColour.B(WaterColour.Sea), WaterColour.A(WaterColour.Sea)) / 255f };
        _reflections = new ScreenReflections(_renderer) { Enabled = ssrOn };

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
        _oceanPanel = new OceanPanel(_swell, _oceanFft) { CoastalEnabled = coastal, ShowBreakers = breakers, ShowLips = lips, FoldLip = foldLip, ChainLips = chainLips };
        _weatherPanel = new WeatherPanel(_weather.Map) { DriveOcean = weather, DriveSky = weather, TimeOffsetMinutes = weatherOffset, OverrideRain = rain >= 0f, RainOverride = MathF.Max(rain, 0f) };
        _terrainRenderer = new TerrainRenderer(_renderer, _fog);
        _terrain = new TerrainWorld(_terrainRenderer, _physics);
        _fog.SeaLevel = _terrain.Params.SeaLevel;
        _fog.Enabled = fogOn;

        // Without --cam, 55 m off the west beach, 28 m up, looking at it.
        if (camera is null)
            _camera.Position = new System.Numerics.Vector3(Coastline.WestX(_terrain.Params, 0f) - 55f, 28f, 0f);

        _brush = new TerrainBrush(_terrain, _physics, _camera);
        _editTest = editTest ? new EditTest(_terrain, _physics, _brush, _camera) : null;
        _roofTest = roofTest ? new RoofTest(_terrain, _camera) : null;
        _water = new VoxelWaterSystem(_terrain, waterPipes) { StepsPerSecond = waterRate };

        // Waves physically enter sea zones through their open-sea boundary. The bare swell, not the surface: breakers
        // reach zones as bores (BreakerBores), and their drawn faces fade out inside zones, so the faces would count twice.
        _water.Zones.SeaSurface = (x, z) => _swell.SampleHeight(x, z);

        // The same swell holds the sea's voxel cells, so free voxel water against the sea rises and falls with it. Its 8 m
        // lattice takes the cheaper two-step inversion.
        if (voxelSwell)
            _water.SeaSurface = (x, z) => _swell.SampleHeight(x, z, VoxelSwellIterations);

        _water.RainAt = (x, z) => _weatherPanel.OverrideRain ? _weatherPanel.RainOverride : _weatherPanel.DriveSky ? _weather.Map.Sample(x, z).Rain : 0f;
        _water.Zones.SeaState = (x, z) =>
        {
            var s = _swell.Sample(x, z);
            return new System.Numerics.Vector3(s.Height, s.Velocity.X, s.Velocity.Z);
        };
        _surface = new OceanSurface(_swell, _breakers) { FacesEnabled = faces };
        _windSea = new WindSeaBands(_renderer);
        _waterRenderer = new VoxelWaterRenderer(_renderer, _rainCover, _fog, _reflections, _windSea.SlopeView, _windSea.Sampler);
        _floodTest = floodTest ? new FloodTest(_terrain, _water, _physics, _brush, _camera) : null;
        _seasideTest = seasideTest ? new SeasideTest(_terrain, _water, _physics, _camera) : null;
        _pondTest = pondTest ? new PondTest(_terrain, _water, _physics, _camera, camera is not null) : null;
        _lakeTest = lakeTest is not null ? new LakeTest(_terrain, _water, _camera, lakeTest) : null;
        _riverTest = riverTest is not null ? new RiverTest(_terrain, _water, _camera, riverTest, camera is not null) : null;
        _caveTest = caveTest is not null ? new CaveTest(_terrain, _water, _physics, _rainCover, caveTest) : null;
        _surfTest = surfTest || surfCam ? new SurfTest(_terrain, _camera, _swell, camera is not null || surfCam) { FollowCurl = surfCam, SideView = surfSide, BackView = surfBack } : null;
        _breakerFaces.Enabled = faces;

        _terrainPanel = new TerrainPanel(_terrain, _terrainRenderer, _physics, _water, _waterRenderer, _fog, _reflections);
        _bathymetry = new BathymetryTexture(_renderer);
        _ocean = new OceanRenderer(_renderer, _oceanFft, _bathymetry.View, _bathymetry.Sampler, _rainCover, _fog, _reflections) { FacesEnabled = faces };
        _rain = new RainRenderer(_renderer, _ocean);
        _flyTest = flyTest ? new FlyTest(_camera, _terrain, _bathymetry, _coastal, _rainCover) : null;
        _profiler = new ProfilerPanel(_stats, _renderer, _rainCover, _fog, _terrainRenderer, _ocean, _waterRenderer);

        _uploadScope = _renderer.GpuTimer.Register("Uploads");
        _fftScope = _renderer.GpuTimer.Register("Ocean FFT");
        _windSeaScope = _renderer.GpuTimer.Register("Wind sea FFT");
        _sprayScope = _renderer.GpuTimer.Register("Spray");
        _terrainScope = _renderer.GpuTimer.Register("Terrain");
        _oceanScope = _renderer.GpuTimer.Register("Ocean");
        _waterScope = _renderer.GpuTimer.Register("Voxel water");
        _fogScope = _renderer.GpuTimer.Register("Fog volume");
        _reflectionScope = _renderer.GpuTimer.Register("Reflection copy");
        _uiScope = _renderer.GpuTimer.Register("ImGui");

        var pipelineStats = _renderer.PipelineStats;
        _terrainStats = pipelineStats.Register("Terrain", PipelinePass.Opaque);
        _oceanStats = pipelineStats.Register("Ocean", PipelinePass.Water);
        _waterStats = pipelineStats.Register("Voxel water", PipelinePass.Water);
        _rainStats = pipelineStats.Register("Rain", PipelinePass.Water);
        _uiStats = pipelineStats.Register("ImGui", PipelinePass.Overlay);

        _clearSky = SceneLightingFromOcean();
        UpdateWeather();

        if (benchSeconds > 0.0)
            _bench = new GpuBenchmark(_renderer.GpuTimer, benchSeconds);

        _benchRain = benchRain;
        _benchFog = benchFog;
        _benchSsr = benchSsr;

        if (hideUi || _bench is not null)
        {
            _profiler.Open = false;
            _oceanPanel.Open = false;
            _terrainPanel.Open = false;
            _weatherPanel.Open = false;
        }
    }

    /// <summary>--bench: alternates layer B (or the rain, the fog or the reflections) on/off once the world has settled, then closes.</summary>
    private void UpdateBench()
    {
        if (_bench is null)
            return;

        var ready = _terrain.LoadedChunks > 0 && _terrain.PendingJobs == 0 && _coastal.Builds > 0;
        _bench.Update(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, ready);

        if (_benchFog)
            _fog.Enabled = _bench.FeatureOn;
        else if (_benchSsr)
            _reflections.Enabled = _bench.FeatureOn;
        else if (!_benchRain)
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
            _roofTest?.Update();
            _floodTest?.Update(_clock.SimTime);
            _seasideTest?.Update(_clock.SimTime);
            _pondTest?.Update(_clock.SimTime);
            _lakeTest?.Update(_clock.SimTime);
            _riverTest?.Update(_clock.SimTime);
            _caveTest?.Update(_clock.SimTime);
            _surfTest?.Update(_clock.SimTime);
            _surfTest?.Follow(_breakers, _surface, (float)frameSeconds);
            _terrainPanel.BrushRadius = _brush.Radius;
            _terrainPanel.Probe = _brush.Probe;
            _terrain.Update(_camera.Position);
            _sections.Terrain = Lap(ref mark);
            _water.Update(_camera.Position);
            _sections.Water = Lap(ref mark);
            UpdateWeather();
            _coastal.Update(_terrain, _swell, _clock.SimTime, _oceanPanel.CoastalEnabled);

            // The full swell sample's first call compiles it (OceanSwell.SampleOf): pay that while loading, not on the
            // tick a sea zone first seeds the cells it grows over.
            if (!_seaSamplerWarm && _coastal.Builds > 0)
            {
                _water.Zones.SeaState?.Invoke(_camera.Position.X, _camera.Position.Z);
                _seaSamplerWarm = true;
            }

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
                _waterRenderer.BeginFrame(_renderer.FrameIndex);
                _water.SubmitMeshes(_waterRenderer);
                _waterRenderer.RecordUploads(commands);
                _bathymetry.Record(commands, _renderer.FrameIndex, _terrain.Bathymetry, _clock.TickIndex);
                (_terrainPanel.MapUploadMs, _terrainPanel.MapUploadCells) = (_bathymetry.LastUploadMs, _bathymetry.LastUploadCells);
                gpu.End(commands, _uploadScope);

                // The fog volume follows the camera; the stale blocks in its window are filled from the voxel water and
                // the bathymetry under a per-frame budget and copied before the pass.
                _fog.Begin(_renderer.FrameIndex, _camera.Position);
                _fog.CameraInWater = CameraInWater(out var cameraSurface);
                _fog.CameraSurface = cameraSurface;
                _water.FillFog(_fog);
                gpu.Begin(commands, _fogScope);
                _fog.Record(commands, _renderer.FrameIndex);
                gpu.End(commands, _fogScope);
                _sections.Uploads = Lap(ref mark);

                gpu.Begin(commands, _fftScope);
                _oceanFft.Record(commands, _renderer.FrameIndex, _swell, renderTime, (float)frameSeconds);
                gpu.End(commands, _fftScope);

                // Only while voxel water is drawn (and once, to lay out its images); its first frame back samples the
                // bands' last transform.
                if (_waterRenderer.MeshesLastFrame > 0 || !_windSea.Ready)
                {
                    gpu.Begin(commands, _windSeaScope);
                    _windSea.Record(commands, _renderer.FrameIndex, _rain.Wind, _swell.GpuSpectrum.Params.Spread, _swell.GpuSpectrum.SpreadNorm, renderTime);
                    gpu.End(commands, _windSeaScope);
                }

                _ocean.DrawLips = _oceanPanel.ShowLips;
                _ocean.FoldLip = _oceanPanel.FoldLip;
                _breakerFaces.UseChains = _oceanPanel.ChainLips;
                PrepareRain(_renderer.FrameIndex, renderTime, (float)frameSeconds, extent.Height);
                _ocean.Prepare(_renderer.FrameIndex, _swell, _breakerFaces, renderTime, (float)_clock.Alpha, viewProj, _camera.Position, _bathymetry.Rect,
                    _water.Splashes, _water.Zones.FlowRect, _water.Zones.PackedFlow);
                _sections.Prepare = Lap(ref mark);
                gpu.Begin(commands, _sprayScope);
                _ocean.RecordSpray(commands, _renderer.FrameIndex, _breakerFaces.SegmentCount, _breakerFaces.SlotCount, (float)frameSeconds, _clock.TickIndex);
                gpu.End(commands, _sprayScope);
                _rain.RecordSplashes(commands, _renderer.FrameIndex);

                _renderer.BeginOpaquePass(commands);

                gpu.Begin(commands, _terrainScope);
                pipe.Begin(commands, _terrainStats);
                _terrainRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, _camera.Position, TerrainLightingFromOcean());
                pipe.End(commands, _terrainStats);
                gpu.End(commands, _terrainScope);

                // The opaque scene, before any water, is what every water reflects.
                _renderer.EndOpaquePass(commands);
                gpu.Begin(commands, _reflectionScope);
                _reflections.Record(commands, _renderer.FrameIndex, _renderer.ColourImage, _renderer.DepthImage, extent, viewProj, _camera.Near, _fog.Enabled);
                gpu.End(commands, _reflectionScope);
                _renderer.BeginWaterPass(commands);

                gpu.Begin(commands, _oceanScope);
                pipe.Begin(commands, _oceanStats);
                _ocean.Record(commands, _renderer.FrameIndex, extent, _camera.Position, _breakerFaces.SegmentCount);
                pipe.End(commands, _oceanStats);
                gpu.End(commands, _oceanScope);

                gpu.Begin(commands, _waterScope);
                pipe.Begin(commands, _waterStats);
                _waterRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, _camera.Position, SceneLightingFromOcean(), (float)(renderTime % 1000.0), _rain.Surface, _rain.Wind);
                pipe.End(commands, _waterStats);
                gpu.End(commands, _waterScope);

                pipe.Begin(commands, _rainStats);
                _rain.Record(commands, _renderer.FrameIndex, extent);
                pipe.End(commands, _rainStats);

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

    private System.Numerics.Vector2 CameraXZ => new(_camera.Position.X, _camera.Position.Z);

    /// <summary>The camera is in voxel water (a lake, a pool) or under the sea's surface; surface = the water's surface over it.</summary>
    private bool CameraInWater(out float surface)
    {
        if (_water.WaterAt(_camera.Position, out surface))
            return true;

        surface = _surface.SampleHeight(_camera.Position.X, _camera.Position.Z);
        return _terrain.Bathymetry.IsSea(_camera.Position.X, _camera.Position.Z) && _camera.Position.Y < surface;
    }

    /// <summary>
    /// Rain where the camera is (or the panel's override), carried by the local wind, hidden under terrain tops. The cover
    /// is written rain or not: the ocean's wind sea reads its fetch and shelter too, measured along the spectrum's wind.
    /// </summary>
    private void PrepareRain(int frameIndex, double renderTime, float frameSeconds, int screenHeight)
    {
        _rain.Intensity = _benchRain && _bench is { FeatureOn: false } ? 0f : _weatherPanel.OverrideRain ? _weatherPanel.RainOverride : _weatherPanel.DriveSky ? _weather.Local.Rain : 0f;
        _rain.Wind = _weather.Local.Wind;
        _weatherPanel.RainDrops = _rain.Drops;

        var grid = _terrain.Bathymetry;
        _rainCover.Write(frameIndex, _camera.Position, grid.Tops, grid.Origin, BathymetryGrid.Resolution, BathymetryGrid.CellSize, BathymetryGrid.SeaLevel, grid.Version, _swell.GpuSpectrum.Params.WindDirection);

        var pixelAngle = 2f * MathF.Tan(_camera.FovY * 0.5f) / Math.Max(1, screenHeight);
        _rain.Prepare(frameIndex, renderTime, frameSeconds, pixelAngle);
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
        _water.Zones.WavePeriod = _swell.PeakPeriod;
        _water.Tick(dt);

        var start = Stopwatch.GetTimestamp();
        _weather.Tick(tickIndex * dt + _weatherPanel.TimeOffset, CameraXZ);
        _weatherPanel.TickMs.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);

        start = Stopwatch.GetTimestamp();
        _swell.SetTime(tickIndex * dt);
        _breakers.Update(_swell, (float)dt);
        _breakerFaces.Build(_breakers, _swell, CameraXZ, BreakerFaces.LipDistance);
        _breakerBores.Apply(_breakers, _swell, _water.Zones, (float)dt);
        _oceanPanel.BreakerMs.Record(_breakers.LastUpdateMs);
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
        _oceanPanel.BreakerCount = _breakers.Count;
        _oceanPanel.SurfTexels = _breakers.SurfTexels;
        _oceanPanel.Draw();
        _breakerOverlay.Enabled = _oceanPanel.ShowBreakers;
        var extent = _renderer.OutputExtent;
        _breakerOverlay.Draw(_breakers, _camera.ViewProjection(extent.Width / (float)Math.Max(1u, extent.Height)));
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
        Console.WriteLine($"fog fill: {_fog.Fills} blocks over the run; warm frames that filled: avg {_fog.FillMs.Average:F3} ms, peak {_fog.FillPeakMs:F3} ms for {_fog.FillPeakUploads} blocks; longest block {_fog.BlockPeakMs:F3} ms, longest keying {_fog.KeysPeakMs:F3} ms");
        _coastal.Dispose();
        _water.Dispose();
        _waterRenderer.Dispose();
        _terrain.Dispose();
        _terrainRenderer.Dispose();
        _physics.Dispose();
        _rain.Dispose();
        _ocean.Dispose();
        _reflections.Dispose();
        _fog.Dispose();
        _rainCover.Dispose();
        _bathymetry.Dispose();
        _oceanFft.Dispose();
        _windSea.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        Glfw.Terminate();
    }
}
