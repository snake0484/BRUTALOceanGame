using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using Brutal.VulkanApi;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame.PlanetDesigner.App;

/// <summary>What to open and how to start.</summary>
public sealed class PlanetDesignerOptions
{
    /// <summary>The repository's data folder: layers load from and save to <c>planets/&lt;body&gt;/</c> there.</summary>
    public required string DataRoot;
    public string Body = "home";

    /// <summary>Show the layers built into the assembly (what the game reads) instead of the ones on disk.</summary>
    public bool Embedded;
    public double3? Look;
    public double? Distance;
    public double? Pitch;
    public string? ScriptPath;
    public int CloseAfterFrames;
}

/// <summary>
/// BRUTALPlanetDesigner (D84, planet-designer-brief.md §6) on the shared DesignKit shell: one body of the fixed test
/// system with its authored layer open. The body renders as the game draws it (CelestialView's impostor from orbit,
/// TerrainWorld's octree near the ground), lit by the star; the orbit camera keeps its target on the ground with the
/// local vertical as up. A committed stroke, site change, undo or redo re-registers the layer and rebuilds the terrain
/// around it. No ticks, ocean or physics.
/// </summary>
public sealed class PlanetDesignerApp : DesignApp
{
    public const string SystemId = "test";

    private const double CoverSeconds = 0.5;
    private const double MinPitch = 0.03;

    private static readonly LogCategory Log = LogCategory.Make("PlanetDesigner");
    private static readonly Vector3 SpaceColour = new(0.004f, 0.005f, 0.008f);
    private static readonly Vector3 SkyZenith = new(0.16f, 0.30f, 0.62f);
    private static readonly Vector3 SkyHorizon = new(0.55f, 0.66f, 0.82f);

    private readonly SimWorld _sim;
    private readonly InstanceBuffer _instances;
    private readonly TerrainRenderer _terrainRenderer;
    private readonly CelestialView _celestial;
    private readonly TerrainWorld _terrain;
    private readonly OrbitCamera _camera = new() { FovY = 60f * MathF.PI / 180f };
    private readonly FlyCamera _view = new();
    private readonly PlanetDesignSession _session;
    private readonly PlanetTools _tools;
    private readonly PlanetParams _params;
    private readonly string _bodyId;
    private readonly int _body;
    private readonly int _terrainBody;
    private readonly FrameId _surface;
    private PlanetGenerator _generator;
    private bool _octreeActive;
    private bool _octreeLoaded;
    private float _octreeCover;
    private bool _terrainDrawn;
    private FocusBlend _focusBlend = FocusBlend.Alone;
    private long _lastFrame;
    private float _refreshMs;

    public PlanetDesignerApp(PlanetDesignerOptions options)
        : base(new DesignAppOptions
        {
            Title = $"BRUTAL Planet Designer - {options.Body}",
            LayoutFile = "planet-designer-layout.ini",
            ScriptPath = options.ScriptPath,
            CloseAfterFrames = options.CloseAfterFrames,
            ClearColour = new float4(0f, 0f, 0f, 1f),
        })
    {
        _bodyId = options.Body;

        if (!options.Embedded)
            RegisterDiskLayers(Path.Combine(options.DataRoot, "planets"));

        var seed = AuthoredWorld.SystemSeed(SystemId);
        _sim = new SimWorld(seed, workerThreads: 1);
        _body = -1;

        for (var i = 0; i < _sim.Celestial.Count; i++)
        {
            if (AuthoredWorld.BodyId(seed, i) == _bodyId && _sim.BodyParams[i] is not null)
                _body = i;
        }

        if (_body < 0)
            throw new ArgumentException($"No body '{_bodyId}' in the fixed system '{SystemId}'");

        for (var i = 0; i < _body; i++)
            _terrainBody += _sim.BodyParams[i] is null ? 0 : 1;

        _params = _sim.BodyParams[_body]!.Value;
        _surface = _sim.Celestial[_body].Surface;
        _session = PlanetDesignSession.Open(Path.Combine(options.DataRoot, "planets", _bodyId), _params, _bodyId);
        _tools = new PlanetTools(_session);

        _instances = new InstanceBuffer(Renderer);
        _terrainRenderer = new TerrainRenderer(Renderer, _instances);
        _celestial = new CelestialView(Renderer, _sim);
        _generator = _celestial.TerrainGenerators[_terrainBody];
        _terrain = new TerrainWorld(_terrainRenderer, _sim.Physics, _celestial.TerrainGenerators, -1, -1);

        _camera.MinDistance = 5.0;
        _camera.MaxDistance = 4.0 * _params.Radius;
        _camera.Distance = options.Distance ?? 3.0 * _params.Radius;
        _camera.Pitch = options.Pitch ?? 0.9;
        _camera.Yaw = 0.0;
        _sim.UpdateRenderPoses();
        FollowGround(double3.Normalize(new double3(0.3, 0.5, 0.8)));
        _celestial.Update(_view);
        FollowGround(options.Look is { } look ? double3.Normalize(look) : DaySideLand(_generator.Surface, _celestial.SunFrom(_body)));
        Log.Info($"{_bodyId}: layer version {_session.Layer.Version}, {_session.Layer.Sites.Count} sites, hash {_session.Layer.Hash():X16}; terrain bounds {_terrain.BoundsMs:F0} ms");
    }

    public PlanetDesignSession Session => _session;

    public OrbitCamera Orbit => _camera;

    /// <summary>The unit direction the camera looks at on the ground.</summary>
    public double3 Target => double3.Normalize(_camera.Target);

    /// <summary>Runs once, on the first frame the octree has settled (scripted checks of the live refresh).</summary>
    public Action<PlanetDesignerApp>? OnSettled;

    protected override OrbitCamera Camera => _camera;

    protected override bool CameraHeld => _tools.Busy;

    protected override bool Busy => _octreeActive && !_terrain.Settled;

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

    /// <summary>Puts the camera's target on the ground (or the sea) at d, with the local vertical as up, and mirrors it into the game's camera.</summary>
    private void FollowGround(double3 d)
    {
        var ground = Math.Max(_generator.Surface.Height(d.X, d.Y, d.Z), _params.HasSea ? _params.SeaHeight : double.NegativeInfinity);
        _camera.Target = d * (_params.Radius + ground);
        _camera.Up = d;
        _camera.Pitch = Math.Max(_camera.Pitch, MinPitch);
        _camera.Near = (float)Math.Clamp(_camera.Distance * 0.0005, 0.05, 20.0);
        _view.Frame = _surface;
        _view.Position = _camera.Position;
        _view.Rotation = _camera.Rotation;
        _view.FovY = _camera.FovY;
        _view.Near = _camera.Near;
    }

    protected override void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastFrame == 0 ? 0.0 : Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds;
        _lastFrame = now;

        _sim.UpdateRenderPoses();
        FollowGround(Target);
        _celestial.Update(_view);
        UpdateOctree();
        Refresh();

        var terrain = _octreeActive && _view.Frame == _surface;
        var replaced = _terrain.LastReplace;
        _terrain.Update(_view.Position, 0.0);

        if (_terrain.LastReplace != replaced)
            Log.Info($"terrain: bounds rebuilt in {_terrain.LastReplace.Ms:F0} ms, {_terrain.LastReplace.Leaves} loaded leaves regenerate; ground at the target {_generator.Surface.Height(Target.X, Target.Y, Target.Z):F1} m (generated {_session.Generated.Surface.Height(Target.X, Target.Y, Target.Z):F1} m)");

        _terrainDrawn = terrain && _terrain.Focus == _terrainBody;
        UpdateCover(_terrainDrawn, seconds);

        if (OnSettled is { } settled && _terrain.Settled)
        {
            OnSettled = null;
            Log.Info($"settled after {Frames} frames with {_terrain.LoadedChunks} leaves; running the scripted edit");
            settled(this);
        }
    }

    protected override void DrawPanels(ImGuiIOPtr io) => _tools.DrawPanel(_bodyId, _refreshMs, _terrain.LastReplace, _terrain.PendingJobs);

    protected override void UpdateViewport(ImGuiIOPtr io, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree)
    {
        var mouse = new Vector2(io.MousePos.X, io.MousePos.Y);
        double3? hit = mouseFree && PlanetPick.Ray(_generator.Surface, _camera.Position, _camera.Ray(mouse, viewport), out var point) ? point : null;
        _tools.Update(_params.Radius, hit, mouseFree);
        _tools.DrawOverlay(Overlay, _camera, viewProj, viewport, _generator.Surface);
    }

    protected override void HandleKey(GlfwKey key, bool ctrl)
    {
        if (ctrl && key == GlfwKey.Z)
            _session.Undo();
        else if (ctrl && key == GlfwKey.Y)
            _session.Redo();
        else if (ctrl && key == GlfwKey.S)
            _session.Save();
        else if (!ctrl && key is >= GlfwKey.Number1 and <= GlfwKey.Number3)
            _tools.Tool = (PlanetTool)(key - GlfwKey.Number1);
    }

    /// <summary>A committed change: the new layer joins the program and the octree and the focus impostor rebuild around it.</summary>
    private void Refresh()
    {
        if (_session.Stroking || _session.TakePending() is not { } region)
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

    protected override void RecordBeforeScene(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj)
    {
        _instances.BeginFrame(frameIndex);
        _terrainRenderer.RecordUploads(commands, frameIndex);
        _celestial.RecordUploads(commands);

        if (_terrainDrawn)
            _terrainRenderer.Cull(commands, frameIndex, viewProj, _view.Position, doubleQuat.Identity);
        else
            _terrainRenderer.Skip(frameIndex);
    }

    protected override void RecordScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj)
    {
        if (_terrainDrawn)
            _terrainRenderer.Draw(commands, frameIndex, extent, viewProj, Lighting());

        var output = Renderer.OutputExtent;
        _celestial.Draw(commands, frameIndex, extent, _view, output.Width / (float)Math.Max(1u, output.Height), _focusBlend);
    }

    /// <summary>
    /// The star's light: a fixed clear sky near the ground of a body with air (no weather, fog or sea surface), turning
    /// into the impostor's look with altitude; an airless body has that look at every height.
    /// </summary>
    private TerrainLighting Lighting()
    {
        var sun = _celestial.SunFrom(_celestial.Focus);
        var centre = -_view.Position;
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

    protected override void DisposeGpu()
    {
        _terrain.Dispose();
        _terrainRenderer.Dispose();
        _celestial.Dispose();
        _instances.Dispose();
        _sim.Dispose();
    }
}
