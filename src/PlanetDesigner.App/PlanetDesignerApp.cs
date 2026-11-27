using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Engine.Time;
using BrutalGame.Frames;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.PlanetDesigner.App;

/// <summary>
/// BRUTALPlanetDesigner (D84, planet-designer-brief.md §6): one body of the fixed test system with its authored layer
/// open. The body renders as the game draws it (CelestialView's impostor from orbit, TerrainWorld's octree near the
/// ground), lit by the star; the layer's edits refresh both when a stroke or drag ends. No ticks, ocean or physics.
/// </summary>
public sealed class PlanetDesignerApp : IDisposable
{
    public const string SystemId = "test";

    private const int TimingSamples = 256;
    private const double CoverSeconds = 0.5;

    private static readonly LogCategory Log = LogCategory.Make("PlanetDesigner");
    private static readonly Vector3 SpaceColour = new(0.004f, 0.005f, 0.008f);
    private static readonly Vector3 SkyZenith = new(0.16f, 0.30f, 0.62f);
    private static readonly Vector3 SkyHorizon = new(0.55f, 0.66f, 0.82f);

    private readonly FrameStats _stats = new(TimingSamples);
    private readonly SimWorld _sim;
    private readonly GlfwWindow _window;
    private readonly Renderer _renderer;
    private readonly ImGuiLayer _imgui;
    private readonly InstanceBuffer _instances;
    private readonly TerrainRenderer _terrainRenderer;
    private readonly CelestialView _celestial;
    private readonly TerrainWorld _terrain;
    private readonly FlyCamera _camera = new();
    private readonly PlanetCameraRig _rig;
    private readonly PlanetDesignSession _session;
    private readonly PlanetTools _tools;
    private readonly PlanetParams _params;
    private readonly string _bodyId;
    private readonly int _body;
    private readonly int _terrainBody;
    private readonly FrameId _surface;
    private readonly int _frames;
    private PlanetGenerator _generator;
    private bool _octreeActive;
    private bool _octreeLoaded;
    private float _octreeCover;
    private FocusBlend _focusBlend = FocusBlend.Alone;
    private float _refreshMs;

    /// <param name="dataRoot">The repository's data folder: layers load from and save to <c>planets/&lt;body&gt;/</c> there.</param>
    /// <param name="embedded">Show the layers built into the assembly (what the game reads) instead of the ones on disk.</param>
    public PlanetDesignerApp(string dataRoot, string bodyId, int frames, double3? look, double? distance, bool embedded = false)
    {
        _bodyId = bodyId;
        _frames = frames;

        if (!embedded)
            RegisterDiskLayers(Path.Combine(dataRoot, "planets"));

        var seed = AuthoredWorld.SystemSeed(SystemId);
        _sim = new SimWorld(seed, workerThreads: 1);
        _body = -1;

        for (var i = 0; i < _sim.Celestial.Count; i++)
        {
            if (AuthoredWorld.BodyId(seed, i) == bodyId && _sim.BodyParams[i] is not null)
                _body = i;
        }

        if (_body < 0)
            throw new ArgumentException($"No body '{bodyId}' in the fixed system '{SystemId}'");

        for (var i = 0; i < _body; i++)
            _terrainBody += _sim.BodyParams[i] is null ? 0 : 1;

        _params = _sim.BodyParams[_body]!.Value;
        _surface = _sim.Celestial[_body].Surface;
        _session = PlanetDesignSession.Open(Path.Combine(dataRoot, "planets", bodyId), _params, bodyId);
        _tools = new PlanetTools(_session);

        if (!Glfw.Init())
            throw new InvalidOperationException("Failed to initialize GLFW");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = $"BRUTAL Planet Designer - {bodyId}", Size = new int2(1600, 900) });
        _renderer = new Renderer(_window, _stats, enableValidation: false, vsync: true) { ClearColor = new float4(0f, 0f, 0f, 1f) };
        _imgui = new ImGuiLayer(_window, _renderer);
        _instances = new InstanceBuffer(_renderer);
        _terrainRenderer = new TerrainRenderer(_renderer, _instances);
        _celestial = new CelestialView(_renderer, _sim);
        _generator = _celestial.TerrainGenerators[_terrainBody];
        _terrain = new TerrainWorld(_terrainRenderer, _sim.Physics, _celestial.TerrainGenerators, -1, -1);
        _rig = new PlanetCameraRig(_camera, _params.Radius);

        _sim.UpdateRenderPoses();
        _rig.Place(_surface);
        _celestial.Update(_camera);
        _rig.Target = look is { } l ? double3.Normalize(l) : DaySideLand(_generator.Surface, _celestial.SunFrom(_body));

        if (distance is { } d)
            _rig.Distance = d;

        _rig.Place(_surface);
        Log.Info($"{bodyId}: layer version {_session.Layer.Version}, {_session.Layer.Sites.Count} sites, hash {_session.Layer.Hash():X16}; terrain bounds {_terrain.BoundsMs:F0} ms");
    }

    /// <summary>Land above the sea with the sun about 25 degrees up, as a seeded search finds it: where the view opens, relief in raking light.</summary>
    private static double3 DaySideLand(PlanetSurface surface, double3 sun)
    {
        var random = new Random(11);
        sun = sun.Length() > 1e-9 ? double3.Normalize(sun) : double3.UnitX;
        var best = sun;
        var score = double.NegativeInfinity;

        for (var i = 0; i < 4000; i++)
        {
            var d = double3.Normalize(new double3(random.NextDouble() * 2.0 - 1.0, random.NextDouble() * 2.0 - 1.0, random.NextDouble() * 2.0 - 1.0));
            var lit = -Math.Abs(double3.Dot(d, sun) - 0.42);

            if (lit > score && surface.Height(d.X, d.Y, d.Z) > surface.SeaHeight + 150f)
                (best, score) = (d, lit);
        }

        return best;
    }

    /// <summary>Layers on disk win over the ones built into the assembly: the designer edits what is in the repository now.</summary>
    private static void RegisterDiskLayers(string planets)
    {
        if (!Directory.Exists(planets))
            return;

        foreach (var folder in Directory.GetDirectories(planets))
        {
            if (File.Exists(Path.Combine(folder, AuthoredLayer.BodyFile)))
                AuthoredWorld.Register(AuthoredLayer.Load(folder).Snapshot());
        }
    }

    public PlanetDesignSession Session => _session;

    public PlanetCameraRig Rig => _rig;

    /// <summary>Runs once, on the first frame the octree has settled (scripted checks of the live refresh).</summary>
    public Action<PlanetDesignerApp>? OnSettled;

    public void Run()
    {
        var last = Stopwatch.GetTimestamp();
        var frame = 0;

        while (!_window.ShouldClose && (_frames <= 0 || frame < _frames))
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
            var seconds = Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;
            frame++;

            _sim.UpdateRenderPoses();
            _imgui.NewFrame();
            var io = ImGui.GetIO();
            var mouseFree = !io.WantCaptureMouse;
            var viewport = new Vector2(_renderer.OutputExtent.Width, _renderer.OutputExtent.Height);

            _rig.Ground = Math.Max(_generator.Surface.Height(_rig.Target.X, _rig.Target.Y, _rig.Target.Z), _params.HasSea ? _params.SeaHeight : double.NegativeInfinity);
            _rig.Update(_surface, mouseFree && !ImGui.IsMouseDown(ImGuiMouseButton.Left));
            _celestial.Update(_camera);
            UpdateOctree();

            var (origin, direction) = _rig.Ray(new Vector2(io.MousePos.X, io.MousePos.Y), viewport);
            double3? hit = mouseFree && PlanetPick.Ray(_generator.Surface, origin, direction, out var point) ? point : null;
            _tools.Update(_generator.Surface, hit, mouseFree);

            if (OnSettled is { } settled && _terrain.Settled)
            {
                OnSettled = null;
                Log.Info($"settled after {frame} frames with {_terrain.LoadedChunks} leaves; running the scripted edit");
                settled(this);
            }

            Refresh();

            var terrain = _octreeActive && _camera.Frame == _surface;
            var replaced = _terrain.LastReplace;
            _terrain.Update(_camera.Position, 0.0);

            if (_terrain.LastReplace != replaced)
                Log.Info($"terrain: bounds rebuilt in {_terrain.LastReplace.Ms:F0} ms, {_terrain.LastReplace.Leaves} loaded leaves regenerate; ground at the target {_generator.Surface.Height(_rig.Target.X, _rig.Target.Y, _rig.Target.Z):F1} m (generated {_session.Generated.Surface.Height(_rig.Target.X, _rig.Target.Y, _rig.Target.Z):F1} m)");
            terrain &= _terrain.Focus == _terrainBody;
            UpdateCover(terrain, seconds);

            _tools.DrawPanel(_bodyId, _refreshMs, _terrain.PendingJobs);
            _tools.DrawOverlay(_rig, _generator.Surface, viewport);
            _imgui.EndFrame();
            Draw(terrain);
            _stats.Frame.Record((float)(seconds * 1000.0));
        }

        _renderer.WaitIdle();
    }

    /// <summary>A committed change: the new layer joins the program and the octree and the focus impostor rebuild around it.</summary>
    private void Refresh()
    {
        if (_session.TakePending() is not { } region)
            return;

        var start = Stopwatch.GetTimestamp();
        AuthoredWorld.Register(_session.Snapshot());
        _generator = new PlanetGenerator(_params.WithBody(_bodyId));
        _terrain.ReplaceGenerator(_terrainBody, _generator, region.Direction, region.Angle);
        _celestial.ReplaceGenerator(_body, _generator);
        _refreshMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Log.Info($"refresh: program {_generator.Params.ProgramHash:X16} in {_refreshMs:F0} ms, region {region.Angle * _params.Radius / 1000.0:F1} km");
    }

    private void UpdateOctree()
    {
        var altitude = _celestial.FocusAltitude;

        if (altitude < DetailLadder.OctreeTop)
            _octreeActive = true;
        else if (altitude > DetailLadder.OctreeRelease)
            _octreeActive = false;

        _terrain.SetFocus(_octreeActive && _celestial.Focus == _body ? _terrainBody : -1);
    }

    private void UpdateCover(bool terrain, double seconds)
    {
        if (!terrain)
        {
            (_octreeLoaded, _octreeCover) = (false, 0f);
            _focusBlend = FocusBlend.Alone;
            return;
        }

        _octreeLoaded |= _terrain.Settled;

        if (_octreeLoaded)
            _octreeCover = MathF.Min(1f, _octreeCover + (float)(seconds / CoverSeconds));

        _focusBlend = DetailLadder.Blend(_celestial.FocusAltitude, _octreeCover);
    }

    private void Draw(bool terrain)
    {
        if (!_renderer.BeginFrame(out var commands))
            return;

        var extent = _renderer.Extent;
        var output = _renderer.OutputExtent;
        var aspect = output.Width / (float)output.Height;
        var viewProj = _camera.ViewProjection(aspect);

        _instances.BeginFrame(_renderer.FrameIndex);
        _terrainRenderer.RecordUploads(commands, _renderer.FrameIndex);
        _celestial.RecordUploads(commands);

        if (terrain)
            _terrainRenderer.Cull(commands, _renderer.FrameIndex, viewProj, _camera.Position, doubleQuat.Identity);
        else
            _terrainRenderer.Skip(_renderer.FrameIndex);

        _renderer.BeginOpaquePass(commands);

        if (terrain)
            _terrainRenderer.Draw(commands, _renderer.FrameIndex, extent, viewProj, Lighting());

        _celestial.Draw(commands, _renderer.FrameIndex, extent, _camera, aspect, _focusBlend);
        _renderer.EndOpaquePass(commands);
        _renderer.BeginWaterPass(commands);
        _renderer.BeginUiPass(commands);
        _imgui.Draw(commands);
        _renderer.EndFrame();
    }

    /// <summary>
    /// The star's light: a fixed clear sky near the ground of a body with air (no weather, fog or sea surface), turning
    /// into the impostor's look with altitude; an airless body has that look at every height.
    /// </summary>
    private TerrainLighting Lighting()
    {
        var sun = _celestial.SunFrom(_celestial.Focus);
        var centre = -_camera.Position;
        var air = _celestial.FocusAtmosphereTop > 0f;

        return new TerrainLighting
        {
            SunDirection = new Vector3((float)sun.X, (float)sun.Y, (float)sun.Z),
            SunIntensity = CelestialView.SunIntensity,
            SkyZenith = air ? SkyZenith : SpaceColour,
            SkyHorizon = air ? SkyHorizon : SpaceColour,
            PlanetCentre = new Vector3((float)centre.X, (float)centre.Y, (float)centre.Z),
            SpaceLook = air ? DetailLadder.SpaceLook(_celestial.FocusAltitude) : 1f,
            StarIntensity = CelestialView.SunIntensity,
            AtmosphereTop = _celestial.FocusAtmosphereTop,
            AtmosphereBase = _celestial.FocusAtmosphereBase,
            AtmosphereScaleHeight = _celestial.FocusScaleHeight,
        };
    }

    public void Dispose()
    {
        _renderer.WaitIdle();
        _terrain.Dispose();
        _terrainRenderer.Dispose();
        _celestial.Dispose();
        _instances.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        _sim.Dispose();
    }
}
