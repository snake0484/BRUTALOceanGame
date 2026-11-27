using System.Numerics;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Buildings;
using BrutalGame.Designer;
using BrutalGame.Designer.Ui;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Plans;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.BuildingDesigner.App;

/// <summary>
/// BRUTALBuildingDesigner on the design kit's shell: one building (a hull plan plus <see cref="BuildingInfo"/>) on a ground
/// plane and its pad, modelled with the ship designer's panels, viewport tool and window tool. Validation, hashing and a
/// new bake start only when the session's version moves, never per frame.
/// </summary>
public sealed partial class BuildingDesignerApp : DesignApp
{
    private const string Title = "BRUTAL Building Designer";

    private readonly HullMaterialCatalog _catalog;
    private readonly ModelCatalog _models;
    private readonly FinishCatalog _finishCatalog;
    private readonly DecalTextures _decalTextures;
    private readonly FinishTextures _finishTextures;
    private readonly HullRenderer _hull;
    private readonly HullBaker _baker;
    private readonly HullSurfaceBuilder _surfaceBuilder;
    private readonly HullBins _bins = new();
    private readonly DesignerPanels _panels;
    private readonly WindowsPanel _windowsPanel;
    private readonly ViewportTool _tool = new();
    private readonly MeshEditState _meshEdit = new();
    private readonly DesignStats _stats = new();
    private readonly OrbitCamera _camera = new() { MaxDistance = 1500.0 };
    private readonly BuildingSession _session = new();
    private readonly FileDialogs _files;
    private readonly Action<DesignSession> _windowProperties;
    private readonly List<HullSolid> _solids = new();
    private readonly List<HullProjector> _projectors = new();
    private readonly List<HullBrick> _bricks = new();
    private readonly SceneLighting _lighting;
    private readonly string? _saveOnExit;
    private int _seenVersion = -1;
    private int _uploadedBake;
    private int[] _densities = [];
    private HullCsgResult? _surface;
    private bool _surfaceChanged;
    private bool _shownGrid;
    private ulong _buildingHash;
    private int _outsidePad;
    private int _belowFoundation;

    protected override OrbitCamera Camera => _camera;

    protected override bool Busy => _baker.Busy || _surfaceBuilder.Busy;

    protected override bool CameraHeld => _tool.Dragging;

    private DesignSession Plan => _session.Plan;

    /// <param name="view">Optional camera after framing: yaw and pitch in degrees, distance in metres.</param>
    public BuildingDesignerApp(string dataFolder, string? load, bool demo, string? saveOnExit, int closeAfterFrames, double[]? view, string? script)
        : base(new DesignAppOptions { Title = Title, LayoutFile = BuildingLayout.File, SeedLayout = BuildingLayout.Seed, ScriptPath = script, CloseAfterFrames = closeAfterFrames })
    {
        _catalog = HullMaterialCatalog.Load(dataFolder);
        _models = ModelCatalog.Load(dataFolder);
        _panels = new DesignerPanels(_catalog, _models) { Workspace = Workspace.Ship };
        _windowsPanel = new WindowsPanel(_catalog);
        _windowProperties = s => _windowsPanel.DrawProperties(s);
        _saveOnExit = saveOnExit;
        _files = new FileDialogs("BRUTAL building", BuildingFile.Extension,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Buildings"));
        _baker = new HullBaker(WorkerBudget.Background);
        _surfaceBuilder = new HullSurfaceBuilder(WorkerBudget.Background);

        var decals = DecalCatalog.Load(dataFolder);
        _decalTextures = new DecalTextures(Renderer, Enumerable.Range(0, decals.All.Count).Select(decals.ImagePath).ToList());
        _finishCatalog = FinishCatalog.Load(dataFolder);
        _finishTextures = new FinishTextures(Renderer, _finishCatalog);
        _hull = new HullRenderer(Renderer, _decalTextures, _finishTextures);
        _lighting = new SceneLighting
        {
            SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f)),
            SunIntensity = 1.1f,
            SkyZenith = new Vector3(0.24f, 0.29f, 0.36f),
            SkyHorizon = new Vector3(0.32f, 0.31f, 0.29f),
        };

        if (load is not null)
            Open(load);
        else if (demo)
        {
            var all = _catalog.All;
            SampleBuildings.Hut(Plan, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);
            _session.MarkSaved("");
        }

        FrameBuilding();

        if (view is not null)
        {
            _camera.Yaw = view[0] * Math.PI / 180.0;
            _camera.Pitch = view[1] * Math.PI / 180.0;
            _camera.Distance = view[2];
        }
    }

    protected override void Tick()
    {
        _session.Sync();
        Commit();
        PollBake();
    }

    protected override void OnExit()
    {
        if (_saveOnExit is not null)
            Save(_saveOnExit);
    }

    // ---- commit, bake, upload ----

    /// <summary>On a new session version: validate, hash, ground checks, expand windows and strokes, start the bakes.</summary>
    private void Commit()
    {
        if (_session.Version == _seenVersion)
            return;

        _seenVersion = _session.Version;
        var doc = Plan.Doc;
        var info = _session.Info;
        PlanValidator.Validate(doc, _stats.Validation, _catalog, _bins);
        var copy = doc.Clone();
        PlanCanonical.Canonicalise(copy);
        _stats.Hash = PlanFormat.HashBody(copy);
        _buildingHash = BuildingFile.Hash(info, _stats.Hash);
        _outsidePad = BuildingCheck.OutsidePad(doc, info);
        _belowFoundation = BuildingCheck.BelowFoundation(doc, info);

        var windows = doc.Windows is { IsEmpty: false }
            ? WindowGeometry.Expand(doc.Windows, new HullSurface(doc.Solids.AsSpan(), doc.Meshes), doc.Layers is not null, doc.Meshes.Count)
            : new WindowGeometry.Expansion();
        var meshes = windows.Meshes.Count > 0 ? [.. doc.Meshes, .. windows.Meshes] : doc.Meshes;
        var paint = doc.Paint;
        var expanded = StrokeExpansion.Expand(doc.Solids.AsSpan(), meshes, doc.Brushes, doc.Interior?.Profiles, paint is null ? null : id => paint.SlotOf(id), _models.Mesh);

        if (windows.Panes.Count > 0)
        {
            expanded.Solids = [.. expanded.Solids, .. windows.Panes];
            expanded.Generated += windows.Panes.Count;
        }

        _panels.Strokes.Expanded = expanded;
        _solids.Clear();

        foreach (ref readonly var s in doc.Solids.AsSpan())
            _solids.Add(s);

        _solids.AddRange(windows.Cuts);
        _projectors.Clear();
        _projectors.AddRange(expanded.Projectors);
        _hull.SetProjectors(_projectors);
        _hull.SetPaint(paint, id => _finishCatalog.IndexOf(new DefId(id)));
        _stats.Generated = expanded.Generated;
        _stats.Unstuck = expanded.Unstuck + windows.Unstuck;
        _densities = _catalog.Densities(doc);
        _baker.InnerSurfacesOnly = !_stats.ShowGrid;
        _baker.Start(CollectionsMarshal.AsSpan(_solids), meshes, _densities, doc.Layers);
        _surfaceBuilder.Start(CollectionsMarshal.AsSpan(_solids), meshes, doc.Layers is not null, expanded);
        _shownGrid = _stats.ShowGrid;
        _surfaceChanged = true;
        UpdatePalette(doc.Materials);
    }

    private void PollBake()
    {
        _baker.Poll();
        _stats.Baking = _baker.Busy || _surfaceBuilder.Busy;
        _stats.HullGpuMs = 0f;

        if (_surfaceBuilder.Poll())
        {
            _surface = _surfaceBuilder.Result;
            _stats.SurfaceMs = _surfaceBuilder.LastMs;
            _stats.SurfacePieces = _surface!.Fragments;
            _stats.SurfaceTriangles = _surface.Triangles;
            _stats.FlairKg = 0;

            foreach (var (material, volume) in _surface.FlairVolume)
                _stats.FlairKg += volume * (material < _densities.Length ? _densities[material] : 0);

            _surfaceChanged = true;
        }

        if (_stats.ShowGrid != _shownGrid)
            _seenVersion = -1;

        if (_baker.Version == _uploadedBake)
        {
            if (_surfaceChanged)
                UploadSurface();

            return;
        }

        _uploadedBake = _baker.Version;
        UploadSurface();
        _stats.Bricks = _baker.Bricks.Count;
        _stats.Triangles = _baker.TriangleCount;
        _stats.BakeMs = _baker.LastBakeMs;
        _stats.BakeCpuMs = _baker.LastCpuMs;
        _stats.Rebaked = _baker.LastRebaked;
        _stats.Reused = _baker.LastReused;
        _stats.MaxSolidsPerBrick = _baker.MaxSolidsPerBrick;
        _stats.PrepareMs = _baker.LastPrepareMs;
        _stats.ExactBricks = _baker.ExactBricks;
        Console.WriteLine($"bake {_baker.Version}: {_stats.Bricks} bricks, {_stats.Triangles} tris, {_stats.BakeMs:F2} ms wall; mass {_baker.Mass.MassKg:F1} kg; plan {_stats.Hash}, building {_buildingHash:x16}; outside pad {_outsidePad}, below foundation {_belowFoundation}");
    }

    /// <summary>The exact surface, plus the grid's inner layer surfaces (or the grid alone, for debugging).</summary>
    private void UploadSurface()
    {
        _surfaceChanged = false;
        _bricks.Clear();

        if (_stats.ShowGrid)
            _bricks.AddRange(_baker.Bricks);
        else
        {
            if (_surface is not null)
                _bricks.AddRange(_surface.Bricks);

            if (Plan.Doc.Layers is not null)
                _bricks.AddRange(_baker.Bricks);
        }

        _hull.Upload(_bricks);
        _stats.Mass = _baker.Mass;
        (_stats.Projectors, _stats.ProjectorRefs) = (_hull.Projectors, _hull.ProjectorRefs);
    }

    private void UpdatePalette(IReadOnlyList<DefId> materials)
    {
        Span<Vector4> colours = stackalloc Vector4[HullRenderer.PaletteSize];
        colours.Fill(new Vector4(0.6f, 0.6f, 0.6f, 1f));

        for (var i = 0; i < Math.Min(materials.Count, colours.Length); i++)
        {
            if (!_catalog.TryGet(materials[i], out var material))
                continue;

            var rgb = material.ColourRgb;
            colours[i] = new Vector4(Linear((rgb >> 16) & 255), Linear((rgb >> 8) & 255), Linear(rgb & 255), material.Opacity);
        }

        _hull.SetPalette(colours);
    }

    private static float Linear(uint srgb) => MathF.Pow(srgb / 255f, 2.2f);

    // ---- frame ----

    protected override void BeforeUi(ImGuiIOPtr io) => _panels.PlaceAt = _camera.Target;

    protected override void DrawPanels(ImGuiIOPtr io)
    {
        KeepOneSelected();
        _panels.OtherProperties = _windowsPanel.Selected != 0 ? _windowProperties : null;
        _panels.Draw(Plan, _stats, _meshEdit);
        DrawBuildingPanel();
        DrawMenus(io);
    }

    protected override void UpdateViewport(ImGuiIOPtr io, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree)
    {
        DrawGround();
        var cut = _panels.Cutaway;
        _hull.SetCutaway(cut.Enabled ? cut.Axis : -1, cut.Offset, cut.Flip);
        var used = !_tool.Dragging && _windowsPanel.Update(Plan, _camera, viewport, viewProj, mouseFree, Overlay);
        var stroke = !used && !_tool.Dragging && _panels.Strokes.Update(Plan, _camera, viewport, viewProj, mouseFree, Overlay);
        _tool.Handles = _windowsPanel.Selected == 0;
        _tool.Update(Plan, _meshEdit, _camera, viewport, viewProj, mouseFree && !used && !stroke, Overlay);
    }

    protected override void RecordScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj) =>
        _hull.Record(commands, frameIndex, extent, viewProj, _camera.Position, _lighting);

    protected override void RecordAfterScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj) =>
        _hull.RecordGlazing(commands, frameIndex, extent, viewProj, _camera.Position, _lighting);

    protected override void HandleKey(GlfwKey key, bool ctrl)
    {
        if (ctrl && key == GlfwKey.Z)
            _session.Undo();
        else if (ctrl && key == GlfwKey.Y)
            _session.Redo();
        else if (ctrl && key == GlfwKey.S)
            SaveCurrent();
        else if (ctrl && key == GlfwKey.O)
            PickAndOpen();
        else if (ctrl && key == GlfwKey.N)
            NewBuilding();
        else if (ctrl && key == GlfwKey.D && Plan.Selection.Primary != 0)
            HullCommands.Duplicate(Plan, Plan.Selection.Primary);
        else if (key == GlfwKey.Delete)
            HullCommands.DeleteSelected(Plan);
        else if (key == GlfwKey.F)
            FrameBuilding();
        else if (key == GlfwKey.Tab)
            _meshEdit.Toggle(Plan);
        else if (key is GlfwKey.Number1 or GlfwKey.Number2 or GlfwKey.Number3)
            _meshEdit.Selection.Mode = key == GlfwKey.Number1 ? Designer.Mesh.MeshSelectMode.Vertex : key == GlfwKey.Number2 ? Designer.Mesh.MeshSelectMode.Edge : Designer.Mesh.MeshSelectMode.Face;
    }

    /// <summary>The building and its pad in view.</summary>
    private void FrameBuilding()
    {
        var info = _session.Info;
        double x0 = -info.PadWidthMetres * 0.5, z0 = -info.PadDepthMetres * 0.5, y0 = -info.FoundationMetres;
        double x1 = -x0, z1 = -z0, y1 = 3.0;

        foreach (ref readonly var s in Plan.Doc.Solids.AsSpan())
        {
            SolidGeometry.Bounds(s, out var min, out var max);
            (x0, y0, z0) = (Math.Min(x0, min.X), Math.Min(y0, min.Y), Math.Min(z0, min.Z));
            (x1, y1, z1) = (Math.Max(x1, max.X), Math.Max(y1, max.Y), Math.Max(z1, max.Z));
        }

        var centre = new double3((x0 + x1) * 0.5, (y0 + y1) * 0.5, (z0 + z1) * 0.5);
        _camera.Frame(centre, 0.5 * Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0)));
    }

    protected override void StopWork()
    {
        _baker.Wait();
        _surfaceBuilder.Wait();
        _surfaceBuilder.Dispose();
        _files.Dispose();
    }

    protected override void DisposeGpu()
    {
        _baker.Dispose();
        _hull.Dispose();
        _decalTextures.Dispose();
        _finishTextures.Dispose();
    }
}
