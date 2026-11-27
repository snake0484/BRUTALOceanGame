using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Ui;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Plans;
using BrutalGame.Ships.Components;
using BrutalGame.Chemistry;
using BrutalGame.Ships;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Designer.App;

/// <summary>
/// BRUTALDesigner, on the design kit's shell (<see cref="DesignApp"/>): a tab per open ship or component (each its own
/// design session, camera and component editor state), bakes on hull bake workers. Per frame: commit and poll, panels,
/// the viewport tools, drawing the hull. Validation, the plan hash and a new bake start only when the session's version
/// moves (a commit), never per frame. No Sim, networking or physics.
/// </summary>
public sealed partial class DesignerApp : DesignApp
{
    private const string Title = "BRUTAL Designer";

    private readonly HullRenderer _hull;
    private readonly HullBaker _baker;
    private readonly HullBins _bins = new();
    private readonly HullMaterialCatalog _catalog;
    private readonly FilePlanHost _files = new();
    private DesignSession _session = null!;
    private readonly ChemistryData _chemistry;
    private readonly PrefabLibrary _library;
    private readonly PrefabPanel _prefabPanel;
    private readonly InteriorPanel _interiorPanel;
    private readonly WindowsPanel _windowsPanel;
    private readonly DecalCatalog _decalCatalog;
    private readonly DecalTextures _decalTextures;
    private readonly DecalsPanel _decalsPanel;
    private readonly FinishCatalog _finishCatalog;
    private readonly FinishTextures _finishTextures;
    private readonly PaintPanel _paintPanel;
    private readonly List<HullProjector> _projectors = new();
    private readonly int _hullScope;
    private readonly ModelCatalog _models;
    private readonly ProfilesPanel _profilesPanel;
    private readonly Dictionary<ulong, PrefabShape> _shapes = new();
    private readonly List<Document> _docs = new();
    private readonly List<DocumentTab> _tabs = new();
    private readonly DocumentBar _bar = new();
    private readonly ViewportOverlay _viewOverlay = new();
    private Document _active = null!;
    private int _nextKey;
    private int _shipCount;
    private int _componentCount;
    private readonly DesignerPanels _panels;
    private readonly DesignStats _stats = new();
    private readonly ViewportTool _tool = new();
    private readonly MeshEditState _meshEdit = new();

    /// <summary>The open tab's camera.</summary>
    protected override OrbitCamera Camera => _active.Camera;

    private OrbitCamera _camera => Camera;

    private OverlayRenderer _overlay => Overlay;

    protected override bool Busy => _baker.Busy;

    protected override bool CameraHeld => _tool.Dragging;

    /// <summary>One open plan: a ship, or a component with its own editor state (selection, test bench).</summary>
    private sealed class Document
    {
        public int Key;
        public string Name = "";
        public DesignSession Session = null!;
        public ComponentWorkspace? Component;
        public readonly OrbitCamera Camera = new();
        public bool Framed;

        public string Title => Session.Path.Length > 0 ? Path.GetFileNameWithoutExtension(Session.Path) : Name;

        /// <summary>Never touched: a new tab or file opened now replaces it.</summary>
        public bool Pristine => Session.Path.Length == 0 && !Session.Modified && Session.Undo.UndoCount == 0 && Session.Doc.Solids.Count == 0 && (Session.Doc.Components?.Parts.Count ?? 0) == 0;
    }
    private readonly SceneLighting _lighting;
    private readonly string? _saveOnExit;
    private int _seenVersion = -1;
    private int _uploadedBake;

    /// <param name="view">Optional camera after framing: yaw and pitch in degrees, distance in metres.</param>
    /// <param name="cutaway">Optional cutaway at start: axis (x, y, z) and offset in metres.</param>
    public DesignerApp(string dataFolder, string? load, bool demo, bool demoMesh, string? saveOnExit, int closeAfterFrames, double[]? view = null, string? script = null, bool demoLayered = false, string[]? cutaway = null)
        : base(new DesignAppOptions { Title = Title, LayoutFile = ShipLayout.File, SeedLayout = ShipLayout.Seed, ScriptPath = script, CloseAfterFrames = closeAfterFrames })
    {
        _catalog = HullMaterialCatalog.Load(dataFolder);
        _chemistry = ChemistryData.Load(dataFolder);
        _models = ModelCatalog.Load(dataFolder);
        _panels = new DesignerPanels(_catalog, _models);
        _library = new PrefabLibrary(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BRUTALGame", "Components"));
        RefreshLibrary();
        _prefabPanel = new PrefabPanel(_library, Shape);
        _interiorPanel = new InteriorPanel(_catalog);
        _windowsPanel = new WindowsPanel(_catalog);
        _profilesPanel = new ProfilesPanel(new ProfileLibrary(ProfileLibrary.DefaultFolder));
        _saveOnExit = saveOnExit;
        _baker = new HullBaker(WorkerBudget.Background);
        _surfaceBuilder = new HullSurfaceBuilder(WorkerBudget.Background);

        // Decal images are game data only, one texture layer each in catalog order.
        _decalCatalog = DecalCatalog.Load(dataFolder);
        _decalTextures = new DecalTextures(Renderer, Enumerable.Range(0, _decalCatalog.All.Count).Select(_decalCatalog.ImagePath).ToList());
        _decalsPanel = new DecalsPanel(_decalCatalog, _decalTextures.Aspect);
        // Paint finishes are game data too: two texture arrays, a layer each in catalog order.
        _finishCatalog = FinishCatalog.Load(dataFolder);
        _finishTextures = new FinishTextures(Renderer, _finishCatalog);
        _paintPanel = new PaintPanel(_finishCatalog, new PaintLibrary(PaintLibrary.DefaultFolder));
        _hull = new HullRenderer(Renderer, _decalTextures, _finishTextures);
        _hullScope = Renderer.GpuTimer.Supported ? Renderer.GpuTimer.Register("Hull") : -1;
        _lighting = new SceneLighting
        {
            SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f)),
            SunIntensity = 1.1f,
            SkyZenith = new Vector3(0.22f, 0.26f, 0.32f),
            SkyHorizon = new Vector3(0.30f, 0.30f, 0.30f),
        };
        NewDocument(component: false);

        if (load is not null)
            Open(load);
        else if (EngineDemo)
        {
            var ship = _active;

            if (PlaceEngine)
            {
                Demo(false);
                ComponentCommands.PlacePrefab(_session, Catalog().First().Prefab, new BrutalGame.Ships.Vector3d(0, 3, -3.85), BrutalGame.Ships.SolidRotation.FromSteps(0, 6, 0));
            }

            OpenComponentCopy(Catalog().First().Prefab);

            if (PlaceEngine)
                Activate(ship);
        }
        else if (demoLayered)
            DemoLayered();
        else if (demo || demoMesh)
            Demo(demoMesh);

        if (cutaway is not null)
        {
            _panels.Cutaway.Enabled = true;
            _panels.Cutaway.Axis = cutaway[0] switch { "x" => 0, "y" => 1, _ => 2 };
            _panels.Cutaway.Offset = float.Parse(cutaway[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        if (view is not null)
        {
            _camera.Yaw = view[0] * Math.PI / 180.0;
            _camera.Pitch = view[1] * Math.PI / 180.0;
            _camera.Distance = view[2];
        }
    }

    protected override void Tick()
    {
        Commit();
        PollBake();
        _stats.HullGpuMs = _hullScope >= 0 ? Renderer.GpuTimer.Ring(_hullScope).Average : 0f;

        if (PaintBench)
            BenchTick();
    }

    protected override void OnExit()
    {
        if (_saveOnExit is not null)
            Save(_saveOnExit);
    }

    /// <summary>On a new session version: validate, hash the canonical copy, start a bake.</summary>
    private void Commit()
    {
        if (_session.Version == _seenVersion)
            return;

        _seenVersion = _session.Version;
        var doc = _session.Doc;
        PlanValidator.Validate(doc, _stats.Validation, _catalog, _bins);
        var copy = doc.Clone();
        PlanCanonical.Canonicalise(copy);
        _stats.Hash = PlanFormat.HashBody(copy);
        // Hallways become room cuts and stairs after the plan's solids; their meshes follow the plan's in the table.
        var hallways = HallwayGeometry.Expand(doc.Interior, doc.Layers is not null, doc.Meshes.Count);

        // Windows stick to the hull: their openings are cuts like the hallways', their panes flair (drawn as built, weighed).
        var windows = doc.Windows is { IsEmpty: false }
            ? WindowGeometry.Expand(doc.Windows, new HullSurface(doc.Solids.AsSpan(), doc.Meshes), doc.Layers is not null, doc.Meshes.Count + hallways.Meshes.Count)
            : new WindowGeometry.Expansion();
        // Rooms with a wall profile are cut as their walled shape in their box's place.
        var walls = WallGeometry.Expand(doc.Interior, doc.Solids.AsSpan(), doc.Meshes, doc.Meshes.Count + hallways.Meshes.Count + windows.Meshes.Count);
        var meshes = hallways.Meshes.Count + windows.Meshes.Count + walls.Meshes.Count > 0 ? [.. doc.Meshes, .. hallways.Meshes, .. windows.Meshes, .. walls.Meshes] : doc.Meshes;
        var paint = doc.Paint;
        var expanded = StrokeExpansion.Expand(doc.Solids.AsSpan(), meshes, doc.Brushes, doc.Interior?.Profiles, paint is null ? null : id => paint.SlotOf(id), _models.Mesh);

        if (windows.Panes.Count > 0)
        {
            expanded.Solids = [.. expanded.Solids, .. windows.Panes];
            expanded.Generated += windows.Panes.Count;
        }

        var extras = new List<SurfacePolygon>();
        var solids = new List<HullSolid>(doc.Solids.Count + hallways.Solids.Count + 16);
        var palette = new List<DefId>(doc.Materials);

        foreach (ref readonly var s in doc.Solids.AsSpan())
            solids.Add(s);

        foreach (var (index, room) in walls.Rooms)
            solids[index] = room;

        solids.AddRange(hallways.Solids);
        solids.AddRange(windows.Cuts);

        _stats.ComponentsKg = 0;

        if (_active.Component is { } component)
        {
            extras.AddRange(component.Surfaces(_session));

            if (doc.Components is not null)
            {
                var own = PrefabShape.Build(doc, Density);
                _stats.ComponentsKg = own.PartsMassKg;
            }
        }
        else if (doc.Components is not null)
        {
            // Placed prefabs: drawn from their own exact surfaces, cut out of the hull by their bounds.
            for (var i = 0; i < doc.Components.Placements.Count; i++)
            {
                var placement = doc.Components.Placements[i];

                if (!doc.Prefabs.TryGetValue(placement.Prefab, out var prefab))
                    continue;

                var shape = Shape(prefab);
                var remap = new byte[Math.Max(prefab.Materials.Count, 1)];

                for (var m = 0; m < prefab.Materials.Count; m++)
                {
                    var at = palette.IndexOf(prefab.Materials[m]);

                    if (at < 0)
                    {
                        at = palette.Count;
                        palette.Add(prefab.Materials[m]);
                    }

                    remap[m] = (byte)Math.Min(at, HullRenderer.PaletteSize - 1);
                }

                shape.Place(placement, m => m < remap.Length ? remap[m] : (byte)0, extras);
                solids.Add(shape.Cutter(placement, doc.Layers is not null, 0xE0000000u | (uint)i));
                _stats.ComponentsKg += shape.PartsMassKg;
            }
        }

        _panels.Strokes.Expanded = expanded;

        // Paint bands, then decals (later ones on top): drawn by the hull shader, nothing baked.
        _projectors.Clear();
        _projectors.AddRange(expanded.Projectors);

        if (doc.Decals is not null)
        {
            foreach (var decal in doc.Decals.Decals)
            {
                if (decal.Shape != DecalShape.Image)
                    _projectors.Add(decal.Projector(0));
                else if (_decalCatalog.IndexOf(new DefId(decal.Asset)) is var layer and >= 0)
                    _projectors.Add(decal.Projector(layer));
            }
        }

        _hull.SetProjectors(_projectors);
        _hull.SetPaint(paint, id => _finishCatalog.IndexOf(new DefId(id)));
        (_stats.Projectors, _stats.ProjectorRefs) = (_hull.Projectors, _hull.ProjectorRefs);
        _stats.Generated = expanded.Generated;
        _stats.Unstuck = expanded.Unstuck;
        var densities = _catalog.Densities(doc);
        _baker.InnerSurfacesOnly = !_stats.ShowGrid;
        _baker.Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids), meshes, densities, doc.Layers);

        // The drawn surface: exact polygons from the solids plus the brush flair, rebuilt on the job workers.
        _surfaceBuilder.Start(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(solids), meshes, doc.Layers is not null, expanded, extras);
        _densities = densities;
        _shownGrid = _stats.ShowGrid;
        _surfaceChanged = true;
        UpdatePalette(palette);
    }

    private int Density(DefId material) => _catalog.TryGet(material, out var m) ? m.DensityKgM3 : 0;

    /// <summary>A prefab's look, bounds and mass, built once per prefab hash.</summary>
    private PrefabShape Shape(PlanDocument prefab)
    {
        var hash = ComponentCommands.PrefabHash(prefab);

        if (!_shapes.TryGetValue(hash, out var shape))
            _shapes[hash] = shape = PrefabShape.Build(prefab, Density);

        return shape;
    }

    private List<(string Category, PlanDocument Prefab)> _catalogPrefabs = new();

    /// <summary>The built-in catalog: made in code, so always present and current.</summary>
    private List<(string Category, PlanDocument Prefab)> Catalog()
    {
        if (_catalogPrefabs.Count == 0)
        {
            var rocket = SampleComponents.ChemicalRocket(DefId.FromName("mat.part.copper"), DefId.FromName("mat.part.inconel"), DefId.FromName("mat.part.steel"));
            _catalogPrefabs.Add(("Propulsion", rocket));
        }

        return _catalogPrefabs;
    }

    private void RefreshLibrary() => _library.Refresh(Catalog());

    /// <summary>Opens a new tab (an empty ship or component, or a loaded plan) and brings it to the front. An untouched empty tab gives its place up.</summary>
    private Document NewDocument(bool component, PlanDocument? doc = null, string path = "", string? name = null)
    {
        doc ??= new PlanDocument { Level = component ? PlanLevel.Component : PlanLevel.Ship };
        var session = new DesignSession();
        session.Open(doc, path);
        var document = new Document
        {
            Key = ++_nextKey,
            Session = session,
            Component = component ? new ComponentWorkspace(_catalog, _chemistry) : null,
            Name = name ?? (component ? $"Component {++_componentCount}" : $"Ship {++_shipCount}"),
        };

        var at = _docs.Count;

        if (_docs.Count > 0 && _active.Pristine)
        {
            at = _docs.IndexOf(_active);
            _docs.RemoveAt(at);
        }

        _docs.Insert(at, document);
        Activate(document);
        return document;
    }

    /// <summary>Makes a tab the one being edited: its session, camera and panels.</summary>
    private void Activate(Document document)
    {
        _active = document;
        _session = document.Session;
        var component = document.Component is not null;
        _panels.Workspace = component ? Workspace.Component : Workspace.Ship;
        _tool.GuideScale = component ? 0.08 : 1.0;
        _meshEdit.Enabled = false;
        _prefabPanel.Selected = 0;
        _interiorPanel.Reset();
        _windowsPanel.Reset();
        _decalsPanel.Reset();
        _paintPanel.Reset();
        _profilesPanel.Reset();
        _seenVersion = -1;
        _bar.Show(document.Key);

        if (!document.Framed)
        {
            document.Framed = true;
            FrameHull();
        }
    }

    private void CloseDocument(int key)
    {
        var index = _docs.FindIndex(d => d.Key == key);

        if (index < 0)
            return;

        var closing = _docs[index];
        _docs.RemoveAt(index);

        if (_docs.Count == 0)
            NewDocument(component: false);
        else if (ReferenceEquals(closing, _active))
            Activate(_docs[Math.Min(index, _docs.Count - 1)]);
    }

    /// <summary>A prefab opened as a new, unsaved component plan in its own tab: editing it never changes the original.</summary>
    private void OpenComponentCopy(PlanDocument prefab)
    {
        var copy = prefab.Clone();
        copy.Level = PlanLevel.Component;
        var name = prefab.Meta.Name.Length > 0 ? prefab.Meta.Name : "Component";
        NewDocument(component: true, copy, "", $"{name} (copy)");
    }

    private readonly HullSurfaceBuilder _surfaceBuilder;
    private int[] _densities = [];
    private HullCsgResult? _surface;
    private bool _surfaceChanged;
    private bool _shownGrid;

    /// <summary>What the hull renderer draws: the exact surface, plus the grid's inner layer surfaces (or the grid alone, for debugging).</summary>
    private void UploadSurface()
    {
        var bricks = new List<HullBrick>();

        if (_stats.ShowGrid)
            bricks.AddRange(_baker.Bricks);
        else
        {
            if (_surface is not null)
                bricks.AddRange(_surface.Bricks);

            if (_session.Doc.Layers is not null)
                bricks.AddRange(_baker.Bricks);
        }

        _hull.Upload(bricks);
        (_stats.Projectors, _stats.ProjectorRefs) = (_hull.Projectors, _hull.ProjectorRefs);
    }

    private void PollBake()
    {
        _baker.Poll();
        _stats.Baking = _baker.Busy || _surfaceBuilder.Busy;

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
            {
                _surfaceChanged = false;
                UploadSurface();
            }

            return;
        }

        _uploadedBake = _baker.Version;
        _surfaceChanged = false;
        UploadSurface();
        _stats.Mass = _baker.Mass;
        _stats.Bricks = _baker.Bricks.Count;
        _stats.Triangles = _baker.TriangleCount;
        _stats.BakeMs = _baker.LastBakeMs;
        _stats.BakeCpuMs = _baker.LastCpuMs;
        _stats.Rebaked = _baker.LastRebaked;
        _stats.Reused = _baker.LastReused;
        _stats.MaxSolidsPerBrick = _baker.MaxSolidsPerBrick;
        _stats.PrepareMs = _baker.LastPrepareMs;
        _stats.ExactBricks = _baker.ExactBricks;
        var com = _baker.Mass.CentreOfMass;
        Console.WriteLine($"bake {_baker.Version}: {_stats.Bricks} bricks ({_stats.ExactBricks} exact, {_stats.Rebaked} rebaked, {_stats.Reused} reused), {_stats.Triangles} tris, {_stats.BakeMs:F2} ms wall, {_stats.BakeCpuMs:F2} ms cpu, mesh prep {_stats.PrepareMs:F2} ms; mass {_baker.Mass.MassKg:F1} kg, volume {_baker.Mass.VolumeM3:F3} m3, CoM ({com.X:F3}, {com.Y:F3}, {com.Z:F3}); hash {_stats.Hash}");
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

    protected override void BeforeUi(ImGuiIOPtr io)
    {
        _panels.PlaceAt = _camera.Target;
    }

    protected override void DrawMenuBar()
    {
        _tabs.Clear();

        foreach (var d in _docs)
            _tabs.Add(new DocumentTab(d.Key, d.Title, d.Session.Modified, d.Component is not null));

        _bar.ComponentTab = _active.Component is not null;
        _bar.Draw(_tabs, _active.Key);
    }

    /// <summary>The interior view: the hull see-through and locked, the things inside it (hallways, placements, parts) still editable.</summary>
    private bool InteriorView => _bar.InteriorView;

    /// <summary>Opacity of the hull in the interior view.</summary>
    private const float GhostOpacity = 0.18f;

    protected override void DrawPanels(ImGuiIOPtr io)
    {
        if (InteriorView)
            _meshEdit.Enabled = false;

        _panels.Locked = InteriorView;
        _panels.Draw(_session, _stats, _meshEdit);
        _viewOverlay.Draw(_session, _meshEdit, _camera, _panels.Cutaway, editable: !InteriorView);
        var workspace = _active.Component;
        _prefabPanel.BeginFrame();
        var viewport = ViewportSize;

        // The selection's menu opens on a right-click in the view (a right-drag still orbits).
        ContextBar.Track(!io.WantCaptureMouse);

        if (workspace is not null)
        {
            workspace.DrawPanels(_session);
            workspace.DrawContext(_session, _camera, viewport);
        }
        else
        {
            _prefabPanel.Draw(_session);
            _interiorPanel.Draw(_session);

            if (_interiorPanel.EditProfileRequest >= 0)
                (_profilesPanel.Selected, _profilesPanel.Focus) = (_interiorPanel.EditProfileRequest, true);
            else if (_panels.EditProfileRequest >= 0)
                (_profilesPanel.Selected, _profilesPanel.Focus) = (_panels.EditProfileRequest, true);

            _profilesPanel.Draw(_session);
            _windowsPanel.Draw(_session);
            _decalsPanel.Draw(_session);
            _paintPanel.Draw(_session);
            _interiorPanel.DrawContext(_session, _camera, viewport);
            _windowsPanel.DrawContext(_session, _camera, viewport);
            _decalsPanel.DrawContext(_session, _camera, viewport);
            _prefabPanel.DrawContext(_session, _camera, viewport);
        }

        // One bar at a time: a picked node, window, placement or part outranks the selected solid.
        if (!OtherSelected)
            _panels.DrawContext(_session, _meshEdit, _camera, viewport);
    }

    /// <summary>A picked node, window, placement or part: it has the gizmo and the menu, not the selected solid.</summary>
    private bool OtherSelected => _active.Component is { } workspace
        ? workspace.SelectedPart != 0 || workspace.SelectedNode >= 0
        : _interiorPanel.SelectedNode >= 0 || _windowsPanel.Selected != 0 || _decalsPanel.Selected != 0 || _prefabPanel.Selected != 0;

    /// <summary>The Add menu: new things go at the camera target (windows wait for a click on the hull).</summary>
    private void HandleAdd()
    {
        var at = new Vector3d(_camera.Target.X, _camera.Target.Y, _camera.Target.Z);

        switch (_bar.Add)
        {
            case AddKind.Box:
                _panels.AddSolid(_session, SolidShape.Box);
                break;
            case AddKind.Wedge:
                _panels.AddSolid(_session, SolidShape.Wedge);
                break;
            case AddKind.Cylinder:
                _panels.AddSolid(_session, SolidShape.Cylinder);
                break;
            case AddKind.Hallway:
                _interiorPanel.AddHallway(_session, at);
                break;
            case AddKind.Window:
                _windowsPanel.StartPlacing();
                break;
            case AddKind.Decal:
                _decalsPanel.StartPlacing();
                break;
            case AddKind.Chamber or AddKind.Nozzle or AddKind.Injector or AddKind.Igniter when _active.Component is { } component:
                component.AddPart(_session, _bar.Add switch { AddKind.Chamber => PartKind.Chamber, AddKind.Nozzle => PartKind.Nozzle, AddKind.Injector => PartKind.Injector, _ => PartKind.Igniter });
                break;
            case AddKind.Pipe when _active.Component is { } component:
                component.AddPipe(_session, at);
                break;
        }
    }

    protected override void AfterPanels(ImGuiIOPtr io)
    {
        if (_prefabPanel.OpenRequest is { } open)
            OpenComponentCopy(open);

        if (_prefabPanel.RefreshRequest)
            RefreshLibrary();

        _active.Component?.Step(Math.Clamp(io.DeltaTime, 0.0, 1.0 / 30.0));
        HandleAdd();
        HandleFiles();

        if (_bar.SelectedKey >= 0 && _docs.Find(d => d.Key == _bar.SelectedKey) is { } picked)
            Activate(picked);

        if (_bar.CloseKey >= 0)
            CloseDocument(_bar.CloseKey);
    }

    protected override void UpdateViewport(ImGuiIOPtr io, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree)
    {
        var workspace = _active.Component;
        var cut = _panels.Cutaway;

        if (_viewOverlay.PickingCut && PickCut(io, viewport, mouseFree, cut))
            return;

        _hull.SetCutaway(cut.Enabled ? cut.Axis : -1, cut.Offset, cut.Flip);
        var used = !_tool.Dragging && (workspace is not null
            ? workspace.Update(_session, _camera, viewport, viewProj, mouseFree, _overlay)
            : _windowsPanel.Update(_session, _camera, viewport, viewProj, mouseFree, _overlay)
              || _decalsPanel.Update(_session, _camera, viewport, viewProj, mouseFree, _overlay)
              || _interiorPanel.Update(_session, _camera, viewport, viewProj, mouseFree, _session.Snap.StepMetres, _overlay)
              || _prefabPanel.Update(_session, _camera, viewport, viewProj, mouseFree, _session.Snap.StepMetres, _overlay));
        // The interior view leaves the hull alone: no solid picking, gizmos or strokes.
        if (InteriorView)
            return;

        var strokeUsedMouse = !used && !_tool.Dragging && workspace is null && _panels.Strokes.Update(_session, _camera, viewport, viewProj, mouseFree, _overlay);
        _tool.Handles = !OtherSelected;
        _tool.Update(_session, _meshEdit, _camera, viewport, viewProj, mouseFree && !strokeUsedMouse && !used, _overlay);
    }

    /// <summary>
    /// The cutaway's Pick: a click on the hull puts the plane just inside the point clicked (on the cut axis). The ray passes
    /// through hull already cut away, so it lands on what is shown. Esc or a click off the hull cancels.
    /// </summary>
    private bool PickCut(ImGuiIOPtr io, Vector2 viewport, bool mouseFree, Cutaway cut)
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
        {
            _viewOverlay.PickingCut = false;
            return true;
        }

        if (!mouseFree || !ImGui.IsMouseClicked(ImGuiMouseButton.Left, false))
            return false;

        _viewOverlay.PickingCut = false;
        var surface = new HullSurface(_session.Doc.Solids.AsSpan(), _session.Doc.Meshes);
        Vector3d origin = _camera.Position;
        Vector3d ray = _camera.Ray(new Vector2(io.MousePos.X, io.MousePos.Y), viewport);
        var sign = cut.Flip ? -1.0 : 1.0;

        for (var hop = 0; hop < 32 && surface.Raycast(origin, ray, 4000, out var t, out _); hop++)
        {
            var hit = HullSurface.Add(origin, HullSurface.Scale(ray, t));
            var along = cut.Axis == 0 ? hit.X : cut.Axis == 1 ? hit.Y : hit.Z;

            if (!cut.Enabled || sign * along <= sign * cut.Offset + 1e-6)
            {
                // 1 mm into the hull from the point clicked: a face square to the axis is cut away (showing what is
                // behind it) rather than sitting exactly on the plane and flickering.
                along -= sign * 1e-3;
                cut.Offset = (float)along;
                cut.Enabled = true;
                _stats.Message = $"Cut at {along:F2} m";
                return true;
            }

            origin = HullSurface.Add(hit, HullSurface.Scale(ray, 1e-3));
        }

        _stats.Message = "Pick missed the hull";
        return true;
    }

    protected override void RecordScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj)
    {
        if (InteriorView)
            return;

        if (_hullScope >= 0)
            Renderer.GpuTimer.Begin(commands, _hullScope);

        _hull.Record(commands, frameIndex, extent, viewProj, _camera.Position, _lighting);

        if (_hullScope >= 0)
            Renderer.GpuTimer.End(commands, _hullScope);
    }

    protected override void RecordAfterScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj)
    {
        if (InteriorView)
            _hull.RecordGhost(commands, frameIndex, extent, viewProj, _camera.Position, _lighting, GhostOpacity);
        else
            _hull.RecordGlazing(commands, frameIndex, extent, viewProj, _camera.Position, _lighting);
    }

    private void HandleFiles()
    {
        switch (_bar.Request)
        {
            case FileRequest.New:
                NewDocument(component: false);
                _stats.Message = "New ship";
                break;
            case FileRequest.NewComponent:
                NewDocument(component: true);
                _stats.Message = "New component: add parts in the Parts panel";
                break;
            case FileRequest.Open:
                PickAndOpen();
                break;
            case FileRequest.Save:
                SaveCurrent();
                break;
            case FileRequest.SaveAs:
                SaveAs();
                break;
        }
    }

    /// <summary>Where the dialogs start for the open tab: components in the prefab library, ships in the plans folder.</summary>
    private string DialogFolder => _active.Component is not null ? _library.UserFolder : _files.LibraryFolder;

    private void PickAndOpen()
    {
        if (_files.PickOpen(DialogFolder) is { } open)
            Open(open);
        else if (!_files.HasDialogs)
            _stats.Message = "File dialogs are unavailable on this system (nfd.dll missing)";
    }

    /// <summary>Saves to the tab's file; a plan never saved asks where first.</summary>
    private void SaveCurrent()
    {
        if (_session.Path.Length > 0)
            Save(_session.Path);
        else
            SaveAs();
    }

    private void SaveAs()
    {
        if (_files.PickSave(_session.Path, DialogFolder) is { } path)
            Save(path);
        else if (!_files.HasDialogs)
            _stats.Message = "File dialogs are unavailable on this system (nfd.dll missing)";
    }

    private void Save(string path)
    {
        try
        {
            var hash = FilePlanHost.Save(_session.Doc, path);
            _session.MarkSaved(path);

            if (_session.Doc.Level == PlanLevel.Component)
                RefreshLibrary();

            _stats.Message = $"Saved {Path.GetFileName(path)} ({hash})";
            Console.WriteLine($"saved {path}: {_session.Doc.Solids.Count} solids, hash {hash}");
        }
        catch (IOException e)
        {
            _stats.Message = $"Save failed: {e.Message}";
        }
    }

    private void Open(string path)
    {
        if (!FilePlanHost.TryLoad(path, out var doc, out var error))
        {
            _stats.Message = error;
            Console.WriteLine($"open failed: {error}");
            return;
        }

        var full = Path.GetFullPath(path);

        if (_docs.Find(d => d.Session.Path.Length > 0 && string.Equals(Path.GetFullPath(d.Session.Path), full, StringComparison.OrdinalIgnoreCase)) is { } already)
        {
            Activate(already);
            _stats.Message = $"{Path.GetFileName(path)} is already open";
            return;
        }

        NewDocument(doc.Level == PlanLevel.Component, doc, path);
        _stats.Message = $"Opened {Path.GetFileName(path)}";
        Console.WriteLine($"opened {path}: {doc.Solids.Count} solids, hash {PlanFormat.HashBody(doc)}");
    }

    /// <summary>--demo-hallway: the layered demo hull with a hallway and a stair run.</summary>
    public static bool HallwayDemo;

    /// <summary>--demo-hallway-plain: the hallway in a solid block without layers.</summary>
    public static bool HallwayPlain;

    /// <summary>--demo-walls: a room with a coved wall profile in a solid block.</summary>
    public static bool WallDemo;

    private void DemoLayered()
    {
        var all = _catalog.All;

        if (WallDemo)
            SampleHulls.WallRoom(_session, all[0].Id);
        else if (HallwayDemo && HallwayPlain)
            SampleHulls.HallwayBlock(_session, all[0].Id);
        else if (HallwayDemo)
            SampleHulls.HallwayHull(_session, all[Math.Min(2, all.Count - 1)].Id, all[0].Id, all[Math.Min(1, all.Count - 1)].Id);
        else
            SampleHulls.LayeredHull(_session, all[Math.Min(2, all.Count - 1)].Id, all[0].Id, all[Math.Min(1, all.Count - 1)].Id);
        _session.MarkSaved("");
        FrameHull();
    }

    /// <summary>--demo-windows: the demo hull with three windows.</summary>
    public static bool WindowDemo;

    /// <summary>--demo-brushes: the demo hull dressed with brush strokes.</summary>
    public static bool BrushDemo;

    /// <summary>--demo-engine: the catalog rocket open in the component editor; with --place, placed on the demo ship.</summary>
    public static bool EngineDemo;

    public static bool PlaceEngine;

    private void Demo(bool mesh)
    {
        var all = _catalog.All;

        if (WindowDemo)
            SampleHulls.WindowedHull(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id, DefId.FromName("mat.glazing.soda"), DefId.FromName("mat.glazing.alon"));
        else if (BrushDemo)
            SampleHulls.BrushedHull(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);
        else if (mesh)
            SampleHulls.MeshHull(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);
        else
            SampleHulls.HollowBoxWithCylinder(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);

        _session.MarkSaved("");
        FrameHull();
    }

    private void FrameHull()
    {
        var solids = _session.Doc.Solids.AsSpan();
        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;

        if (_active.Component is { } component)
        {
            foreach (var poly in component.Surfaces(_session))
            {
                foreach (var p in poly.Points)
                {
                    (x0, y0, z0) = (Math.Min(x0, p.X), Math.Min(y0, p.Y), Math.Min(z0, p.Z));
                    (x1, y1, z1) = (Math.Max(x1, p.X), Math.Max(y1, p.Y), Math.Max(z1, p.Z));
                }
            }
        }

        if (solids.Length == 0 && x0 > x1)
            return;

        foreach (ref readonly var b in solids)
        {
            SolidGeometry.Bounds(b, out var min, out var max);
            (x0, y0, z0) = (Math.Min(x0, min.X), Math.Min(y0, min.Y), Math.Min(z0, min.Z));
            (x1, y1, z1) = (Math.Max(x1, max.X), Math.Max(y1, max.Y), Math.Max(z1, max.Z));
        }

        var centre = new double3((x0 + x1) * 0.5, (y0 + y1) * 0.5, (z0 + z1) * 0.5);
        var radius = 0.5 * Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
        _camera.Frame(centre, radius);
    }

    protected override void HandleKey(GlfwKey key, bool ctrl)
    {
        if (ctrl && key == GlfwKey.W)
            _bar.AskClose(_active.Key);
        else if (ctrl && key == GlfwKey.N)
            NewDocument(component: false);
        else if (ctrl && key == GlfwKey.O)
            PickAndOpen();
        else if (!ctrl && key == GlfwKey.I)
            _bar.InteriorView = !_bar.InteriorView;
        else if (key == GlfwKey.Delete && _active.Component is { } component)
        {
            if (!component.DeleteSelected(_session) && !InteriorView)
                HullCommands.DeleteSelected(_session);
        }
        else if (InteriorView && (key is GlfwKey.Delete or GlfwKey.Tab or GlfwKey.Number1 or GlfwKey.Number2 or GlfwKey.Number3 || (ctrl && key == GlfwKey.D)))
        {
            // The hull is locked in the interior view.
        }
        else if (ctrl && key == GlfwKey.Z)
            _session.UndoLast();
        else if (ctrl && key == GlfwKey.Y)
            _session.RedoNext();
        else if (ctrl && key == GlfwKey.D && _session.Selection.Primary != 0)
            HullCommands.Duplicate(_session, _session.Selection.Primary);
        else if (ctrl && key == GlfwKey.S)
            SaveCurrent();
        else if (key == GlfwKey.Delete)
            HullCommands.DeleteSelected(_session);
        else if (key == GlfwKey.F)
            FrameHull();
        else if (key == GlfwKey.Tab)
            _meshEdit.Toggle(_session);
        else if (key is GlfwKey.Number1 or GlfwKey.Number2 or GlfwKey.Number3)
            _meshEdit.Selection.Mode = key == GlfwKey.Number1 ? Mesh.MeshSelectMode.Vertex : key == GlfwKey.Number2 ? Mesh.MeshSelectMode.Edge : Mesh.MeshSelectMode.Face;
    }

    protected override void StopWork()
    {
        _baker.Wait();
        _surfaceBuilder.Wait();
        _surfaceBuilder.Dispose();
        _files.Dispose();
        _profilesPanel.Dispose();
        _paintPanel.Dispose();
    }

    protected override void DisposeGpu()
    {
        _baker.Dispose();
        _hull.Dispose();
        _decalTextures.Dispose();
        _finishTextures.Dispose();
    }
}
