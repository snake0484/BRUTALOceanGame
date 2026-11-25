using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Designer.Ui;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.App;

/// <summary>
/// BRUTALDesigner: one window (Glfw + Vulkan through Engine's renderer + ImGui), one design session, bakes on hull bake
/// workers. Per frame: input, panels, the viewport tool, polling the baker, drawing. Validation, the plan hash and a new
/// bake start only when the session's version moves (a commit), never per frame. No Sim, networking or physics.
/// </summary>
public sealed class DesignerApp : IDisposable
{
    private const string Title = "BRUTAL Designer";
    private const int TimingSamples = 256;
#if DEBUG
    private const bool Validation = true;
#else
    private const bool Validation = false;
#endif

    private readonly GlfwWindow _window;
    private readonly FrameStats _frameStats = new(TimingSamples);
    private readonly Renderer _renderer;
    private readonly ImGuiLayer _imgui;
    private readonly HullRenderer _hull;
    private readonly HullBaker _baker;
    private readonly HullBins _bins = new();
    private readonly HullMaterialCatalog _catalog;
    private readonly FilePlanHost _files = new();
    private readonly DesignSession _session = new();
    private readonly DesignerPanels _panels;
    private readonly DesignStats _stats = new();
    private readonly ViewportTool _tool = new();
    private readonly MeshEditState _meshEdit = new();
    private readonly OverlayRenderer _overlay;
    private readonly OrbitCamera _camera = new();
    private readonly SceneLighting _lighting;
    private readonly int _closeAfterFrames;
    private readonly string? _saveOnExit;
    private int _seenVersion = -1;
    private int _uploadedBake;
    private int _frames;

    /// <param name="view">Optional camera after framing: yaw and pitch in degrees, distance in metres.</param>
    public DesignerApp(string dataFolder, string? load, bool demo, bool demoMesh, string? saveOnExit, int closeAfterFrames, double[]? view = null, string? script = null)
    {
        _catalog = HullMaterialCatalog.Load(dataFolder);
        _panels = new DesignerPanels(_catalog);
        _saveOnExit = saveOnExit;
        _closeAfterFrames = closeAfterFrames;
        _baker = new HullBaker(WorkerBudget.Background);

        if (!Glfw.Init())
            throw new InvalidOperationException("Failed to initialize GLFW");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        _window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = Title, Size = new int2(1600, 900) });
        _renderer = new Renderer(_window, _frameStats, Validation, vsync: true) { ClearColor = new float4(0.11f, 0.12f, 0.14f, 1f) };
        _imgui = new ImGuiLayer(_window, _renderer);
        _hull = new HullRenderer(_renderer);
        _overlay = new OverlayRenderer(_renderer);
        _lighting = new SceneLighting
        {
            SunDirection = Vector3.Normalize(new Vector3(0.45f, 0.8f, 0.35f)),
            SunIntensity = 1.1f,
            SkyZenith = new Vector3(0.22f, 0.26f, 0.32f),
            SkyHorizon = new Vector3(0.30f, 0.30f, 0.30f),
        };
        _panels.PathText = Path.Combine(_files.LibraryFolder, "hull." + FilePlanHost.Extension);

        if (load is not null)
            Open(load);
        else if (demo || demoMesh)
            Demo(demoMesh);

        if (view is not null)
        {
            _camera.Yaw = view[0] * Math.PI / 180.0;
            _camera.Pitch = view[1] * Math.PI / 180.0;
            _camera.Distance = view[2];
        }

        _window.OnKey += OnKey;

        if (script is not null)
            _script = new InputScript(script, ScriptKey);
    }

    private readonly InputScript? _script;

    private void ScriptKey(string name)
    {
        var ctrl = name.StartsWith("ctrl+", StringComparison.Ordinal);
        var key = (ctrl ? name[5..] : name) switch
        {
            "tab" => GlfwKey.Tab,
            "delete" => GlfwKey.Delete,
            "1" => GlfwKey.Number1,
            "2" => GlfwKey.Number2,
            "3" => GlfwKey.Number3,
            "z" => GlfwKey.Z,
            "y" => GlfwKey.Y,
            "d" => GlfwKey.D,
            "f" => GlfwKey.F,
            _ => GlfwKey.Unknown,
        };

        HandleKey(key, ctrl);
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
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var frameMs = (float)Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            last = now;

            Commit();
            PollBake();
            BuildUi();
            Draw();

            _frameStats.Frame.Record(frameMs);
            _frames++;

            if (_closeAfterFrames > 0 && _frames >= _closeAfterFrames && !_baker.Busy)
                _window.ShouldClose = true;
        }

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
        _baker.Start(doc.Brushes.AsSpan(), doc.Meshes, _catalog.Densities(doc));
        UpdatePalette(doc);
    }

    private void PollBake()
    {
        _baker.Poll();
        _stats.Baking = _baker.Busy;

        if (_baker.Version == _uploadedBake)
            return;

        _uploadedBake = _baker.Version;
        _hull.Upload(_baker.Bricks);
        _stats.Mass = _baker.Mass;
        _stats.Bricks = _baker.Bricks.Count;
        _stats.Triangles = _baker.TriangleCount;
        _stats.BakeMs = _baker.LastBakeMs;
        _stats.BakeCpuMs = _baker.LastCpuMs;
        _stats.Rebaked = _baker.LastRebaked;
        _stats.Reused = _baker.LastReused;
        _stats.MaxBrushesPerBrick = _baker.MaxBrushesPerBrick;
        _stats.PrepareMs = _baker.LastPrepareMs;
        var com = _baker.Mass.CentreOfMass;
        Console.WriteLine($"bake {_baker.Version}: {_stats.Bricks} bricks ({_stats.Rebaked} rebaked, {_stats.Reused} reused), {_stats.Triangles} tris, {_stats.BakeMs:F2} ms wall, {_stats.BakeCpuMs:F2} ms cpu, mesh prep {_stats.PrepareMs:F2} ms; mass {_baker.Mass.MassKg:F1} kg, volume {_baker.Mass.VolumeM3:F3} m3, CoM ({com.X:F3}, {com.Y:F3}, {com.Z:F3}); hash {_stats.Hash}");
    }

    private void UpdatePalette(PlanDocument doc)
    {
        Span<Vector3> colours = stackalloc Vector3[HullRenderer.PaletteSize];
        colours.Fill(new Vector3(0.6f));

        for (var i = 0; i < Math.Min(doc.Materials.Count, colours.Length); i++)
        {
            if (!_catalog.TryGet(doc.Materials[i], out var material))
                continue;

            var rgb = material.ColourRgb;
            colours[i] = new Vector3(Linear((rgb >> 16) & 255), Linear((rgb >> 8) & 255), Linear(rgb & 255));
        }

        _hull.SetPalette(colours);
    }

    private static float Linear(uint srgb) => MathF.Pow(srgb / 255f, 2.2f);

    private void BuildUi()
    {
        _imgui.NewFrame(_script is null ? null : _script.Inject);

        if (_script is { Quit: true })
            _window.ShouldClose = true;

        var io = ImGui.GetIO();
        var output = _renderer.OutputExtent;
        var viewport = new Vector2(output.Width, output.Height);
        UpdateCamera(io, viewport.Y);
        _panels.PlaceAt = _camera.Target;
        _panels.Draw(_session, _stats, _meshEdit);
        HandleFiles();

        var viewProj = _camera.ViewProjection(viewport.X / Math.Max(1f, viewport.Y));
        _overlay.Begin(_camera.Position);
        _tool.Update(_session, _meshEdit, _camera, viewport, viewProj, !io.WantCaptureMouse, _overlay);
        _imgui.EndFrame();
    }

    private void UpdateCamera(ImGuiIOPtr io, float viewportHeight)
    {
        if (io.WantCaptureMouse || _tool.Dragging)
            return;

        var delta = io.MouseDelta;
        var panning = io.MouseDown[2] || (io.MouseDown[1] && io.KeyShift);

        if (panning)
            _camera.Pan(delta.X, delta.Y, viewportHeight);
        else if (io.MouseDown[1])
            _camera.Orbit(delta.X, delta.Y);

        if (io.MouseWheel != 0f)
            _camera.Zoom(io.MouseWheel);
    }

    private void Draw()
    {
        if (!_renderer.BeginFrame(out var commands))
            return;

        var extent = _renderer.Extent;
        var output = _renderer.OutputExtent;
        var viewProj = _camera.ViewProjection(output.Width / (float)output.Height);

        _renderer.BeginOpaquePass(commands);
        _hull.Record(commands, _renderer.FrameIndex, extent, viewProj, _camera.Position, _lighting);
        _renderer.EndOpaquePass(commands);
        _renderer.BeginWaterPass(commands);
        _overlay.Record(commands, _renderer.FrameIndex, extent, viewProj);
        _renderer.BeginUiPass(commands);
        _imgui.Draw(commands);
        _renderer.EndFrame();
    }

    private void HandleFiles()
    {
        switch (_panels.Request)
        {
            case FileRequest.New:
                _session.Open(new PlanDocument(), "");
                _stats.Message = "New plan";
                break;
            case FileRequest.Open:
                if (_files.PickOpen() is { } open)
                    Open(open);
                else if (!_files.HasDialogs)
                    _stats.Message = "No file dialog: type a path and use Load path";

                break;
            case FileRequest.OpenPath:
                Open(_panels.PathText);
                break;
            case FileRequest.Save:
                if (_session.Path.Length > 0)
                    Save(_session.Path);
                else
                    SaveAs();

                break;
            case FileRequest.SaveAs:
                SaveAs();
                break;
            case FileRequest.SavePath:
                Save(_panels.PathText);
                break;
        }
    }

    private void SaveAs()
    {
        if (_files.PickSave(_session.Path) is { } path)
            Save(path);
        else if (!_files.HasDialogs)
            _stats.Message = "No file dialog: type a path and use Save path";
    }

    private void Save(string path)
    {
        try
        {
            var hash = FilePlanHost.Save(_session.Doc, path);
            _session.MarkSaved(path);
            _panels.PathText = path;
            _stats.Message = $"Saved {Path.GetFileName(path)} ({hash})";
            Console.WriteLine($"saved {path}: {_session.Doc.Brushes.Count} brushes, hash {hash}");
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

        _session.Open(doc, path);
        _panels.PathText = path;
        _stats.Message = $"Opened {Path.GetFileName(path)}";
        Console.WriteLine($"opened {path}: {doc.Brushes.Count} brushes, hash {PlanFormat.HashBody(doc)}");
        FrameHull();
    }

    private void Demo(bool mesh)
    {
        var all = _catalog.All;

        if (mesh)
            SampleHulls.MeshHull(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);
        else
            SampleHulls.HollowBoxWithCylinder(_session, all[0].Id, all[Math.Min(2, all.Count - 1)].Id);

        _session.MarkSaved("");
        FrameHull();
    }

    private void FrameHull()
    {
        var brushes = _session.Doc.Brushes.AsSpan();

        if (brushes.Length == 0)
            return;

        double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;

        foreach (ref readonly var b in brushes)
        {
            BrushGeometry.Bounds(b, out var min, out var max);
            (x0, y0, z0) = (Math.Min(x0, min.X), Math.Min(y0, min.Y), Math.Min(z0, min.Z));
            (x1, y1, z1) = (Math.Max(x1, max.X), Math.Max(y1, max.Y), Math.Max(z1, max.Z));
        }

        var centre = new double3((x0 + x1) * 0.5, (y0 + y1) * 0.5, (z0 + z1) * 0.5);
        var radius = 0.5 * Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0) + (z1 - z0) * (z1 - z0));
        _camera.Frame(centre, radius);
    }

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action != GlfwKeyAction.Press && !(action == GlfwKeyAction.Repeat && (mods & GlfwModifier.Control) != 0))
            return;

        if (_imgui.WantCaptureKeyboard)
            return;

        HandleKey(key, (mods & GlfwModifier.Control) != 0);
    }

    private void HandleKey(GlfwKey key, bool ctrl)
    {
        if (ctrl && key == GlfwKey.Z)
            _session.UndoLast();
        else if (ctrl && key == GlfwKey.Y)
            _session.RedoNext();
        else if (ctrl && key == GlfwKey.D && _session.Selection.Primary != 0)
            HullCommands.Duplicate(_session, _session.Selection.Primary);
        else if (ctrl && key == GlfwKey.S)
            Save(_session.Path.Length > 0 ? _session.Path : _panels.PathText);
        else if (key == GlfwKey.Delete)
            HullCommands.DeleteSelected(_session);
        else if (key == GlfwKey.F)
            FrameHull();
        else if (key == GlfwKey.Tab)
            _meshEdit.Enabled = !_meshEdit.Enabled;
        else if (key is GlfwKey.Number1 or GlfwKey.Number2 or GlfwKey.Number3)
            _meshEdit.Selection.Mode = key == GlfwKey.Number1 ? Mesh.MeshSelectMode.Vertex : key == GlfwKey.Number2 ? Mesh.MeshSelectMode.Edge : Mesh.MeshSelectMode.Face;
    }

    public void Dispose()
    {
        _window.OnKey -= OnKey;
        _baker.Wait();
        _renderer.WaitIdle();
        _baker.Dispose();
        _hull.Dispose();
        _overlay.Dispose();
        _imgui.Dispose();
        _renderer.Dispose();
        _window.Dispose();
        _files.Dispose();
        Glfw.Terminate();
    }
}
