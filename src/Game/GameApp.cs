using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using BrutalGame.Debug;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Engine.Time;
using BrutalGame.Frames;
using BrutalGame.Physics;
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

    /// <summary>Seconds the focus body's impostor takes to hand over to its octree once that has loaded.</summary>
    private const double CoverSeconds = 0.5;

    /// <summary>The atmosphere's density scale height (m): fog and the sky's clear colour fade with altitude by it.</summary>
    private const double ScaleHeight = 8000.0;

    /// <summary>The ocean's clipmap is drawn while the camera is within this distance (m) of the sea frame's origin, its tangent plane (curved to the sphere) still within ~1 m of the sea.</summary>
    private const double OceanReach = 40_000.0;

    /// <summary>The clipmap's reach (m): past it, seabeds take the water's colour themselves.</summary>
    private const float OceanSurfaceReach = 6000f;

    /// <summary>Teleports (scripted tests) above this per-frame jump (m) reset the measured camera speed.</summary>
    private const double TeleportJump = 50_000.0;
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
    private readonly FrameId _planetFrame;
    private readonly double _planetSoi;
    private readonly BathymetryGrid _depthGrid = new();
    private readonly InstanceBuffer _instances;
    private readonly TerrainRenderer _terrainRenderer;
    private readonly TerrainWorld _terrain;
    private readonly BathymetryTexture _bathymetry;
    private readonly TerrainPanel _terrainPanel;
    private readonly TerrainBrush _brush;
    private readonly EditTest? _editTest;
    private readonly int _oceanScope;
    private readonly GpuBenchmark? _bench;
    private readonly PlanetFlyTest? _planetFly;
    private readonly OrbitTest? _orbitTest;
    private readonly MoonTest? _moonTest;
    private readonly PlanetRenderer? _planets;
    private readonly CelestialView _celestial;
    private readonly int _planetTerrainBody;
    private FrameSections _sections;
    private readonly long _startTime = Stopwatch.GetTimestamp();
    private readonly int _fftScope;
    private readonly int _oceanComputeScope;
    private readonly int _uiScope;
    private readonly int _terrainScope;
    private readonly int _terrainCullScope;
    private readonly int _uploadScope;
    private readonly int _planetScope = -1;
    private double3 _lastCamera;
    private double3 _sunCamera;
    private float _sunElevation = 1f;
    private double _cameraSpeed;
    private bool _oceanDrawn;
    private bool _octreeActive = true;
    private bool _octreeLoaded;
    private float _octreeCover;
    private FocusBlend _focusBlend = FocusBlend.Alone;

    /// <summary>Pipeline statistics scopes (<see cref="PipelineStats"/>): sequential, never nested.</summary>
    private readonly int _terrainStats;
    private readonly int _oceanStats;
    private readonly int _uiStats;

    /// <param name="camera">Optional start pose in the spawn's sea frame (y up, sea level 0, +x out to sea): x, y, z, yaw degrees, pitch degrees.</param>
    /// <param name="planetFly">--planet-fly: scripted flights over the planet at 30, 300 and 3000 m/s and a descent from 800 km, logging streaming numbers.</param>
    /// <param name="orbitTest">--orbit-test [inertial]: the test planet as a smooth sphere below the impostor switch and on the detail ladder above it, with the terrain round the spawn on it, flown from 2 m to 1e8 m with the camera in its surface or inertial frame; no ocean.</param>
    /// <param name="moonTest">--moon-test: the camera falls onto the moon from 1e9 m (a point of light) through disc, impostor and the focus switch to its octree, logging the ladder's GPU time per stage.</param>
    public GameApp(float windSpeed, bool foamDebug, bool editTest, float[]? camera = null, bool hideUi = false, double benchSeconds = 0.0, bool planetFly = false, float[]? swell = null, bool weather = true, float weatherOffset = 0f, float renderScale = 1f, OrbitTestFrame? orbitTest = null, bool moonTest = false)
    {
        var weatherField = new WeatherField(new WeatherClimate());
        _sim = new SimWorld(seed: 1, WorkerBudget.Tick, weatherField) { DriveWind = weather, WeatherTimeOffset = weatherOffset * 60.0 };

        ref readonly var planet = ref _sim.Celestial[_sim.PlanetIndex];
        _planetFrame = planet.Surface;
        _planetSoi = planet.SoiRadius;
        _camera.Frame = _planetFrame;

        // Without --cam, 55 m out from the spawn's beach, 28 m up, looking at it.
        if (camera is not null)
            PlaceCamera(new System.Numerics.Vector3(camera[0], camera[1], camera[2]), camera[3] * MathF.PI / 180f, camera[4] * MathF.PI / 180f);
        else
            PlaceCamera(new System.Numerics.Vector3(55f, 28f, 0f), MathF.PI, -0.12f);

        _lastCamera = _camera.Position;

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

        _celestial = new CelestialView(_renderer, _sim) { Pinned = orbitTest is not null || planetFly };
        _planetTerrainBody = _celestial.FocusTerrainBody;

        if (orbitTest is { } orbitFrame)
        {
            var surface = _sim.Generator.Surface;
            _orbitTest = new OrbitTest(_camera, _sim.Celestial, orbitFrame, d => PlanetImpostor.VisibleHeight(surface, d.X, d.Y, d.Z));
            _planets = new PlanetRenderer(_renderer, _instances);
        }

        _terrain = new TerrainWorld(_terrainRenderer, _sim.Physics, _celestial.TerrainGenerators, _planetTerrainBody, _planetTerrainBody);
        _brush = new TerrainBrush(_sim, _camera);
        _editTest = editTest ? new EditTest(_sim, _terrain, _brush, _camera) : null;
        _terrainPanel = new TerrainPanel(_terrain, _terrainRenderer, _sim.Physics);
        _bathymetry = new BathymetryTexture(_renderer);
        _ocean = new OceanRenderer(_renderer, _oceanFft, _bathymetry.View, _bathymetry.Sampler);
        _planetFly = planetFly ? new PlanetFlyTest(_camera, _sim, _terrain) : null;
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

        _moonTest = moonTest ? new MoonTest(_camera, _sim, _terrain, _celestial, _renderer.GpuTimer, _terrainScope) : null;

        var pipelineStats = _renderer.PipelineStats;
        _terrainStats = pipelineStats.Register("Terrain", PipelinePass.Opaque);
        _oceanStats = pipelineStats.Register("Ocean", PipelinePass.Water);
        _uiStats = pipelineStats.Register("ImGui", PipelinePass.Overlay);

        _clearSky = SceneLightingFromOcean();
        UpdateWeather();

        if (benchSeconds > 0.0)
            _bench = new GpuBenchmark(_renderer.GpuTimer, benchSeconds, ["CPU frame", "CPU record", "Terrain rec"], [_stats.Frame, _stats.Record, _terrainRenderer.DrawMs]);

        if (hideUi || _bench is not null || _planetFly is not null || _moonTest is not null)
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

            if (_orbitTest is null && _planetFly is null && _moonTest is null)
                _camera.Update(_window, frameSeconds, _imgui.WantCaptureMouse, _imgui.WantCaptureKeyboard);

            _planetFly?.Fly(Stopwatch.GetElapsedTime(_startTime).TotalSeconds);
            _orbitTest?.Fly(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, WorldReady, OutputAspect, RenderViewport, _sim.RenderTime);
            _moonTest?.Fly(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, WorldReady);
            _celestial.Update(_camera);
            UpdateCameraSpeed(frameSeconds);

            var terrainStart = Stopwatch.GetTimestamp();
            mark = terrainStart;

            if (_camera.Frame == _planetFrame)
                _brush.Update(_window, (float)frameSeconds, _imgui.WantCaptureMouse);

            _editTest?.Update(_sim.Clock.SimTime);
            _terrainPanel.BrushRadius = _brush.Radius;
            SyncEdits();
            UpdateOctree();
            var terrain = TerrainView(out var terrainCamera, out var terrainAxes);
            _terrain.Update(_orbitTest is null ? terrainCamera : _sim.Bubble.Origin, _planetFly?.Speed ?? _cameraSpeed);
            terrain &= _terrain.Focus == (_orbitTest is null ? _celestial.FocusTerrainBody : _planetTerrainBody);
            UpdateOctreeCover(terrain, frameSeconds);
            _sections.Terrain = Lap(ref mark);
            UpdateSun(terrainCamera, terrainAxes);
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
                var renderTime = _sim.RenderTime;
                var gpu = _renderer.GpuTimer;
                var pipe = _renderer.PipelineStats;
                var viewProj = _camera.ViewProjection(aspect);

                // The ocean is drawn in the sea frame (the physics bubble at the spawn) with the camera in the planet's frame, near the frame's origin; not in the orbit test.
                var seaCamera = _sim.Bubble.LocalDouble(terrainCamera);
                var ocean = terrain && _orbitTest is null && _camera.Frame == _planetFrame && _celestial.Focus == _sim.PlanetIndex && _sim.Planet.HasSea
                    && seaCamera.X * seaCamera.X + seaCamera.Z * seaCamera.Z < OceanReach * OceanReach && seaCamera.Y < OceanReach;
                _oceanDrawn = ocean;

                mark = Stopwatch.GetTimestamp();
                _instances.BeginFrame(_renderer.FrameIndex);
                gpu.Begin(commands, _uploadScope);
                _terrainRenderer.RecordUploads(commands, _renderer.FrameIndex);
                _bathymetry.Record(commands, _renderer.FrameIndex, _depthGrid);
                _celestial.RecordUploads(commands);
                gpu.End(commands, _uploadScope);
                _sections.Uploads = Lap(ref mark);

                gpu.Begin(commands, _fftScope);
                _oceanFft.Record(commands, _renderer.FrameIndex, _swell, renderTime, (float)frameSeconds);
                gpu.End(commands, _fftScope);

                if (ocean)
                {
                    _ocean.ShoreWash = _oceanPanel.ShoreWash;
                    _ocean.Prepare(_renderer.FrameIndex, _swell, renderTime, SeaViewProjection(viewProj), seaCamera, NoDepthMap, _sim.Planet.SeaRadius);
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
                else
                {
                    _terrainRenderer.Skip(_renderer.FrameIndex);
                }

                _renderer.BeginOpaquePass(commands);

                if (terrain)
                {
                    gpu.Begin(commands, _terrainScope);
                    pipe.Begin(commands, _terrainStats);
                    _terrainRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, _orbitTest is null ? PlanetTerrainLighting(terrainCamera, terrainAxes) : OrbitTerrainLighting(terrainCamera, terrainAxes));
                    pipe.End(commands, _terrainStats);
                    gpu.End(commands, _terrainScope);
                }

                if (_planets is not null)
                {
                    gpu.Begin(commands, _planetScope);
                    DrawPlanet(commands, extent, aspect);
                    gpu.End(commands, _planetScope);
                }
                else
                {
                    _celestial.Draw(commands, _renderer.FrameIndex, extent, _camera, aspect, _focusBlend);
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
            _planetFly?.Record(_sections, _camera.ViewProjection(OutputAspect));
            _moonTest?.Record(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, _stats.Frame.Last, _sections.Work, _terrainRenderer.DrawnLastFrame, _camera.ViewProjection(OutputAspect), _renderer.Extent.Height, _focusBlend.Covers);
            RecordOrbitTest();

            if (_planetFly is { Done: true } || _orbitTest is { Done: true } || _moonTest is { Done: true })
                _window.ShouldClose = true;
        }
    }

    /// <summary>
    /// Weather at the camera into the sky; the clear colour follows the fogged horizon, fading to space with altitude
    /// (space in the orbit test). The clear sky dims with the star's elevation at the camera.
    /// </summary>
    private void UpdateWeather()
    {
        var day = Smooth(-0.12f, 0.1f, _sunElevation);
        var clear = _clearSky with
        {
            SunIntensity = _clearSky.SunIntensity * Smooth(-0.02f, 0.06f, _sunElevation),
            SkyZenith = _clearSky.SkyZenith * (0.02f + 0.98f * day),
            SkyHorizon = _clearSky.SkyHorizon * (0.03f + 0.97f * day),
        };
        _weather.Update(CameraXZ, _ocean, clear, _weatherPanel.DriveSky);
        var horizon = _orbitTest is null ? System.Numerics.Vector3.Lerp(SpaceColour, _ocean.SkyHorizon, Atmosphere) : SpaceColour;
        _renderer.ClearColor = new float4(horizon.X, horizon.Y, horizon.Z, 1f);
    }

    /// <summary>Relative air density at the camera: fog and the sky colour scale with it.</summary>
    private float Atmosphere
    {
        get
        {
            var altitude = _camera.Frame == _planetFrame ? _camera.Position.Length() - _sim.Planet.Radius : double.PositiveInfinity;
            return (float)Math.Exp(-Math.Max(altitude, 0.0) / ScaleHeight);
        }
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Math.Clamp((x - from) / (to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// The star lights the focus body's terrain and the ocean (planets.md "Precision, frames, gravity, rotation": the sun
    /// direction moved into the body frame), so the octree and its impostor agree at the switch. The star's direction is
    /// in the camera frame's axes; the ocean takes it in the sea frame.
    /// </summary>
    private void UpdateSun(double3 terrainCamera, doubleQuat terrainAxes)
    {
        _sunCamera = _celestial.SunFrom(_celestial.Focus);

        if (_sunCamera == double3.Zero)
            return;

        var sunBody = doubleQuat.Conjugate(terrainAxes) * _sunCamera;
        var up = terrainCamera.Length() > 0.0 ? double3.Normalize(terrainCamera) : double3.UnitY;
        _sunElevation = (float)double3.Dot(sunBody, up);

        if (_celestial.Focus == _sim.PlanetIndex)
        {
            var sea = doubleQuat.Conjugate(_sim.Bubble.Axes) * sunBody;
            _ocean.SunDirection = new System.Numerics.Vector3((float)sea.X, (float)sea.Y, (float)sea.Z);
        }
    }

    /// <summary>
    /// The octree may show once it has loaded round the camera: until then the focus body's impostor covers it, then
    /// hands over in <see cref="CoverSeconds"/> (<see cref="DetailLadder.Blend"/>).
    /// </summary>
    private void UpdateOctreeCover(bool terrain, double frameSeconds)
    {
        if (!terrain)
        {
            (_octreeLoaded, _octreeCover) = (false, 0f);
            _focusBlend = FocusBlend.Alone;
            return;
        }

        _octreeLoaded |= _terrain.Settled;

        if (_octreeLoaded)
            _octreeCover = MathF.Min(1f, _octreeCover + (float)(frameSeconds / CoverSeconds));

        _focusBlend = DetailLadder.Blend(_celestial.FocusAltitude, _octreeCover);
    }

    /// <summary>The octree streams below <see cref="DetailLadder.OctreeTop"/> over the focus body and unloads above <see cref="DetailLadder.OctreeRelease"/>; the orbit test keeps it at the spawn.</summary>
    private void UpdateOctree()
    {
        if (_orbitTest is not null)
        {
            _terrain.SetFocus(_planetTerrainBody);
            return;
        }

        var altitude = _celestial.FocusAltitude;

        if (altitude < DetailLadder.OctreeTop)
            _octreeActive = true;
        else if (altitude > DetailLadder.OctreeRelease)
            _octreeActive = false;

        _terrain.SetFocus(_octreeActive ? _celestial.FocusTerrainBody : -1);
    }

    /// <summary>Camera speed (m/s) in its frame for the terrain's speed LOD bias, smoothed over ~0.25 s; a teleport resets it.</summary>
    private void UpdateCameraSpeed(double frameSeconds)
    {
        var jump = (_camera.Position - _lastCamera).Length();
        _lastCamera = _camera.Position;

        if (jump > TeleportJump || frameSeconds <= 0.0)
        {
            _cameraSpeed = 0.0;
            return;
        }

        var blend = Math.Min(1.0, frameSeconds / 0.25);
        _cameraSpeed += (jump / frameSeconds - _cameraSpeed) * blend;
    }

    /// <summary>The ocean's view-projection: sea frame axes into the camera's, then the camera's translation-free view-projection.</summary>
    private System.Numerics.Matrix4x4 SeaViewProjection(in System.Numerics.Matrix4x4 viewProj)
    {
        var q = _sim.Bubble.Axes;
        var rotation = System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W));
        return rotation * viewProj;
    }

    /// <summary>No depth map until M2b's planet ShoreDepthMap: every lookup falls outside it and reads open water.</summary>
    private static readonly System.Numerics.Vector4 NoDepthMap = new(1e7f, 1e7f, 1f, BathymetryGrid.UnknownDepth);

    /// <summary>
    /// --orbit-test: the smooth sphere through the marker stands in for the octree below the impostor switch (the test's
    /// octree stays at the spawn); every body on the detail ladder above it, the planet's impostor fading in across the
    /// switch band over the sphere; then the marker. Lit by the star through the frame tree. The marker probe of this
    /// slot's last frame goes to the test.
    /// </summary>
    private void DrawPlanet(Brutal.VulkanApi.CommandBuffer commands, Brutal.VulkanApi.VkExtent2D extent, float aspect)
    {
        var fade = DetailLadder.ImpostorFade(_celestial.FocusAltitude);
        _planets!.Begin(commands, _renderer.FrameIndex, extent, _camera, aspect, _orbitTest!.SunDirection, OrbitSunIntensity, _orbitTest.ProbeTag);
        _orbitTest.ResolveProbe(_planets.Probe);

        if (fade < 1f)
            _planets.DrawSphere(commands, _orbitTest.PlanetCentre, _orbitTest.PlanetRadius, _orbitTest.PlanetAxes);

        _celestial.Draw(commands, _renderer.FrameIndex, extent, _camera, aspect, fade < 1f ? new FocusBlend(fade, 0f, MathF.Min(1f, 4f * fade), DetailLadder.OverlayDepthScale) : FocusBlend.Alone);
        _planets.Bind(commands, extent);
        _planets.DrawMarker(commands, _orbitTest.Marker, _orbitTest.MarkerRadius, MarkerColour);
    }

    private void RecordOrbitTest()
    {
        if (_orbitTest is null)
            return;

        var timer = _renderer.GpuTimer;
        var bodies = new System.Numerics.Vector3(timer.Ring(_celestial.PointScope).Last, timer.Ring(_celestial.DiscScope).Last, timer.Ring(_celestial.ImpostorScope).Last);
        _orbitTest.Record(Stopwatch.GetElapsedTime(_startTime).TotalSeconds, OutputAspect, RenderViewport, _stats.Frame.Last, _sections.Work, timer.Ring(_planetScope).Last, bodies, _terrainRenderer.DrawnLastFrame);
    }

    /// <summary>The aspect the view-projection is built with.</summary>
    private float OutputAspect => _renderer.OutputExtent.Width / (float)_renderer.OutputExtent.Height;

    /// <summary>The render target's size (render scale applied): the pixels the scene is drawn in.</summary>
    private System.Numerics.Vector2 RenderViewport => new(_renderer.Extent.Width, _renderer.Extent.Height);

    private bool WorldReady => (_terrain.Settled || !_octreeActive) && _celestial.Ready;

    /// <summary>
    /// The camera in the focus body's frame and that body's axes in the camera frame's, through the frame tree at render
    /// time when the camera is in another frame. False while the octree is off (above the impostor switch); in the orbit
    /// test, outside the planet's SOI, so a heliocentric camera never casts its position into chunk coordinates.
    /// </summary>
    private bool TerrainView(out double3 camera, out doubleQuat axes)
    {
        var frame = _orbitTest is null ? _celestial.FocusSurface : _planetFrame;
        camera = _camera.Position;
        axes = doubleQuat.Identity;

        if (_camera.Frame != frame)
        {
            if (!_sim.Celestial.Frames.TryRelativeRender(frame, _camera.Frame, out var body))
                return false;

            camera = doubleQuat.Conjugate(body.Rotation) * (camera - body.Position);
            axes = body.Rotation;
        }

        return _orbitTest is null ? _octreeActive : camera.Length() < _planetSoi;
    }

    /// <summary>
    /// Terrain lit by the star (<see cref="UpdateSun"/>) under the ocean's sky, which the weather and the star's elevation
    /// set. Fog thins with altitude; seabeds past the ocean surface take the water's colour. From altitude the look turns
    /// into the impostor's (<see cref="DetailLadder.SpaceLook"/>): the star alone under the body's atmosphere. An airless
    /// focus body (the moon) has that look at every height.
    /// </summary>
    private TerrainLighting PlanetTerrainLighting(double3 camera, doubleQuat axes)
    {
        var sun = new System.Numerics.Vector3((float)_sunCamera.X, (float)_sunCamera.Y, (float)_sunCamera.Z);

        if (_celestial.Focus != _sim.PlanetIndex)
        {
            return new TerrainLighting
            {
                SunDirection = sun,
                SunIntensity = CelestialView.SunIntensity,
                SkyZenith = SpaceColour,
                SkyHorizon = SpaceColour,
                PlanetCentre = PlanetCentre(camera, axes),
                SpaceLook = 1f,
                StarIntensity = CelestialView.SunIntensity,
            };
        }

        var water = (_ocean.WaterDeep + _ocean.WaterScatter) * (0.5f * _ocean.SunIntensity);

        return new TerrainLighting
        {
            SunDirection = sun,
            SunIntensity = _ocean.SunIntensity,
            SkyZenith = _ocean.SkyZenith,
            SkyHorizon = _ocean.SkyHorizon,
            FogDensity = _ocean.FogDensity * Atmosphere,
            PlanetCentre = PlanetCentre(camera, axes),
            SeaRadius = _sim.Planet.HasSea ? (float)_sim.Planet.SeaRadius : 0f,
            WaterColour = water,
            SeaFillStart = _oceanDrawn ? OceanSurfaceReach : 0f,
            SpaceLook = DetailLadder.SpaceLook(_celestial.FocusAltitude),
            StarIntensity = CelestialView.SunIntensity,
            AtmosphereTop = _celestial.FocusAtmosphereTop,
            AtmosphereBase = _celestial.FocusAtmosphereBase,
            AtmosphereScaleHeight = _celestial.FocusScaleHeight,
        };
    }

    /// <summary>The terrain in the orbit test: lit by the star (camera frame axes, as its normals), no sky or fog.</summary>
    private TerrainLighting OrbitTerrainLighting(double3 camera, doubleQuat axes) => new()
    {
        SunDirection = _orbitTest!.SunDirection,
        SunIntensity = OrbitSunIntensity,
        SkyZenith = SpaceColour,
        SkyHorizon = SpaceColour,
        FogDensity = 0f,
        PlanetCentre = PlanetCentre(camera, axes),
    };

    /// <summary>The planet's centre relative to the camera in the camera frame's axes.</summary>
    private static System.Numerics.Vector3 PlanetCentre(double3 camera, doubleQuat axes)
    {
        var centre = axes * -camera;
        return new System.Numerics.Vector3((float)centre.X, (float)centre.Y, (float)centre.Z);
    }

    /// <summary>The camera in the sea frame's plane (weather samples there); the frame's origin outside the planet's frame.</summary>
    private System.Numerics.Vector2 CameraXZ
    {
        get
        {
            if (_camera.Frame != _planetFrame || _orbitTest is not null)
                return default;

            var local = _sim.Bubble.ToLocal(_camera.Position);
            return new System.Numerics.Vector2(local.X, local.Z);
        }
    }

    /// <summary>The camera at a point of the sea frame, looking along yaw (from +X toward +Z) and pitch.</summary>
    private void PlaceCamera(System.Numerics.Vector3 position, float yaw, float pitch)
    {
        _camera.Position = _sim.Bubble.ToPlanet(position);
        _camera.LookAlong(_sim.Bubble.ToPlanetDirection(FlyCamera.Direction(yaw, pitch)));
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

        for (var i = _terrain.EditCountOf(_planetTerrainBody); i < edits.Length; i++)
            _terrain.ApplyEdit(_planetTerrainBody, edits[i]);
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

    private void BuildUi()
    {
        var uiStart = Stopwatch.GetTimestamp();
        _imgui.NewFrame();
        _profiler.Draw();
        _renderer.PipelineStats.Enabled = _profiler.Open && _profiler.PipelineStatsOn;
        _terrainRenderer.StatsEnabled = _profiler.Open || _terrainPanel.Open || _orbitTest is not null || _moonTest is not null;
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
        _celestial.Dispose();
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
