using System.Diagnostics;
using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Time;

namespace BrutalGame.DesignKit.Ui;

/// <summary>What a design tool's window is: title, size, its layout file and default arrangement, optional script.</summary>
public sealed class DesignAppOptions
{
    public string Title = "BRUTAL";
    public int Width = 1600;
    public int Height = 900;

    /// <summary>The ImGui layout file under %LOCALAPPDATA%\BRUTALGame, e.g. "designer-layout.ini".</summary>
    public string LayoutFile = "layout.ini";

    /// <summary>Default docked layout (ImGui ini text) for a window of the given size, used when no layout file exists yet.</summary>
    public Func<int, int, string>? SeedLayout;

    /// <summary>--input-script: scripted mouse and keys (<see cref="InputScript"/>).</summary>
    public string? ScriptPath;

    /// <summary>Closes after this many frames once <see cref="DesignApp.Busy"/> clears (0: run until closed).</summary>
    public int CloseAfterFrames;

    public float4 ClearColour = new(0.11f, 0.12f, 0.14f, 1f);
}

/// <summary>
/// The shell of a standalone design tool: a Glfw window, Engine's Vulkan renderer, ImGui with the designer style and a
/// docked layout over a pass-through 3D view, an orbit camera, a 3D overlay, keys and scripted input. Per frame:
/// <see cref="Tick"/> (commit and poll work), then the UI (<see cref="BeforeUi"/>, <see cref="DrawMenuBar"/>, dock space,
/// <see cref="DrawPanels"/>, <see cref="AfterPanels"/>, then <see cref="UpdateViewport"/> with the overlay begun), then
/// drawing (<see cref="RecordScene"/> in the opaque pass, <see cref="RecordAfterScene"/> and the overlay in the second
/// scene pass, ImGui last). A tool subclasses it and supplies its camera and scene.
/// </summary>
public abstract class DesignApp : IDisposable
{
    private const int TimingSamples = 256;
#if DEBUG
    private const bool Validation = true;
#else
    private const bool Validation = false;
#endif

    private readonly int _closeAfterFrames;
    private bool _disposed;

    protected DesignApp(DesignAppOptions options)
    {
        _closeAfterFrames = options.CloseAfterFrames;

        if (!Glfw.Init())
            throw new InvalidOperationException("Failed to initialize GLFW");

        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        Window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = options.Title, Size = new int2(options.Width, options.Height) });
        Renderer = new Renderer(Window, FrameStats, Validation, vsync: true) { ClearColor = options.ClearColour };
        Ui = new ImGuiLayer(Window, Renderer);
        Style.LoadFonts();
        DesignerStyle.UseLayout(options.LayoutFile, options.Width, options.Height, options.SeedLayout);
        Overlay = new OverlayRenderer(Renderer);
        Window.OnKey += OnKey;

        if (options.ScriptPath is not null)
            Script = new InputScript(options.ScriptPath, ScriptKey);
    }

    protected GlfwWindow Window { get; }

    protected FrameStats FrameStats { get; } = new(TimingSamples);

    protected Renderer Renderer { get; }

    protected ImGuiLayer Ui { get; }

    protected DesignerStyle Style { get; } = new();

    protected OverlayRenderer Overlay { get; }

    protected InputScript? Script { get; }

    /// <summary>Frames run so far.</summary>
    protected int Frames { get; private set; }

    /// <summary>The camera the view is drawn from this frame.</summary>
    protected abstract OrbitCamera Camera { get; }

    /// <summary>Background work still running: a timed run (<see cref="DesignAppOptions.CloseAfterFrames"/>) waits for it.</summary>
    protected virtual bool Busy => false;

    /// <summary>True while a tool owns the mouse (a drag): the camera ignores the mouse.</summary>
    protected virtual bool CameraHeld => false;

    /// <summary>The 3D view's size in pixels (the whole window; panels float over it).</summary>
    protected Vector2 ViewportSize
    {
        get
        {
            var output = Renderer.OutputExtent;
            return new Vector2(output.Width, output.Height);
        }
    }

    public void Run()
    {
        var last = Stopwatch.GetTimestamp();

        while (!Window.ShouldClose)
        {
            Glfw.PollEvents();

            var size = Window.FramebufferSize;

            if (size.X == 0 || size.Y == 0)
            {
                Glfw.WaitEvents();
                continue;
            }

            var now = Stopwatch.GetTimestamp();
            var frameMs = (float)Stopwatch.GetElapsedTime(last, now).TotalMilliseconds;
            last = now;

            Tick();
            BuildFrame();
            Draw();

            FrameStats.Frame.Record(frameMs);
            Frames++;

            if (_closeAfterFrames > 0 && Frames >= _closeAfterFrames && !Busy)
                Window.ShouldClose = true;
        }

        OnExit();
    }

    /// <summary>Start of each frame, before the UI: commit changes, poll background work.</summary>
    protected virtual void Tick()
    {
    }

    /// <summary>After the camera moved, before the style is pushed.</summary>
    protected virtual void BeforeUi(ImGuiIOPtr io)
    {
    }

    /// <summary>Inside the style, before the dock space: a main menu bar if the tool has one.</summary>
    protected virtual void DrawMenuBar()
    {
    }

    /// <summary>Inside the style, after the dock space: the tool's windows and view overlays.</summary>
    protected abstract void DrawPanels(ImGuiIOPtr io);

    /// <summary>After the style is popped: act on what the panels asked for (files, tabs). The camera may change here.</summary>
    protected virtual void AfterPanels(ImGuiIOPtr io)
    {
    }

    /// <summary>The 3D view's mouse tools, with <see cref="Overlay"/> begun at the camera. <paramref name="mouseFree"/>: not over UI.</summary>
    protected virtual void UpdateViewport(ImGuiIOPtr io, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree)
    {
    }

    /// <summary>Before the scene passes, outside any render pass: uploads and compute (culling) the scene needs.</summary>
    protected virtual void RecordBeforeScene(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj)
    {
    }

    /// <summary>The scene, in the opaque pass.</summary>
    protected abstract void RecordScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj);

    /// <summary>In the second scene pass (depth readable), before the overlay: water, transparent things.</summary>
    protected virtual void RecordAfterScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj)
    {
    }

    /// <summary>A key pressed (or Ctrl held and repeating) while the UI does not want the keyboard, or a script's key.</summary>
    protected virtual void HandleKey(GlfwKey key, bool ctrl)
    {
    }

    /// <summary>After the loop ends, before disposal (save on exit and the like).</summary>
    protected virtual void OnExit()
    {
    }

    /// <summary>Disposal, before the GPU is idled: stop and join background work.</summary>
    protected virtual void StopWork()
    {
    }

    /// <summary>Disposal, with the GPU idle and before the renderer goes: free the tool's GPU resources.</summary>
    protected virtual void DisposeGpu()
    {
    }

    private void BuildFrame()
    {
        Ui.NewFrame(Script is null ? null : Script.Inject);

        if (Script is { Quit: true })
            Window.ShouldClose = true;

        var io = ImGui.GetIO();
        var viewport = ViewportSize;
        Camera.Step(Math.Min(io.DeltaTime, 0.1));

        if (!io.WantCaptureMouse && !CameraHeld)
            Camera.Mouse(io, viewport.Y);

        BeforeUi(io);
        Style.Begin(io.DisplaySize.Y, Window.ContentScale.X);
        DrawMenuBar();
        DesignerStyle.DockSpace();
        DrawPanels(io);
        Style.End();
        AfterPanels(io);

        var viewProj = Camera.ViewProjection(viewport.X / Math.Max(1f, viewport.Y));
        Overlay.Begin(Camera.Position);
        UpdateViewport(io, viewport, viewProj, !io.WantCaptureMouse);
        Ui.EndFrame();
    }

    private void Draw()
    {
        if (!Renderer.BeginFrame(out var commands))
            return;

        var extent = Renderer.Extent;
        var output = Renderer.OutputExtent;
        var viewProj = Camera.ViewProjection(output.Width / (float)Math.Max(1u, output.Height));

        RecordBeforeScene(commands, Renderer.FrameIndex, viewProj);
        Renderer.BeginOpaquePass(commands);
        RecordScene(commands, Renderer.FrameIndex, extent, viewProj);
        Renderer.EndOpaquePass(commands);
        Renderer.BeginWaterPass(commands);
        RecordAfterScene(commands, Renderer.FrameIndex, extent, viewProj);
        Overlay.Record(commands, Renderer.FrameIndex, extent, viewProj);
        Renderer.BeginUiPass(commands);
        Ui.Draw(commands);
        Renderer.EndFrame();
    }

    private void OnKey(GlfwWindow window, GlfwKey key, int scancode, GlfwKeyAction action, GlfwModifier mods)
    {
        if (action != GlfwKeyAction.Press && !(action == GlfwKeyAction.Repeat && (mods & GlfwModifier.Control) != 0))
            return;

        if (Ui.WantCaptureKeyboard)
            return;

        HandleKey(key, (mods & GlfwModifier.Control) != 0);
    }

    /// <summary>A script's key by name: "ctrl+" prefix, then a letter, a digit, or a Glfw key name ("tab", "delete", "escape").</summary>
    private void ScriptKey(string name)
    {
        var ctrl = name.StartsWith("ctrl+", StringComparison.OrdinalIgnoreCase);
        var bare = ctrl ? name[5..] : name;
        var key = bare.Length == 1 && char.IsDigit(bare[0]) ? GlfwKey.Number0 + (bare[0] - '0')
            : Enum.TryParse<GlfwKey>(bare, ignoreCase: true, out var parsed) ? parsed : GlfwKey.Unknown;

        if (key == GlfwKey.Unknown)
            Console.WriteLine($"script: unknown key {name}");
        else
            HandleKey(key, ctrl);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Window.OnKey -= OnKey;
        StopWork();
        Renderer.WaitIdle();
        DisposeGpu();
        Overlay.Dispose();
        Ui.Dispose();
        Renderer.Dispose();
        Window.Dispose();
        Glfw.Terminate();
        GC.SuppressFinalize(this);
    }
}
