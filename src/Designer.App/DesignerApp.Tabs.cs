using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;

namespace BrutalGame.Designer.App;

/// <summary>
/// A tab that is not a plan, with its own scene, panels and keys: while it is in front the shell's plan paths hand the
/// frame to it. The shipped app has none; an authoring build (<c>-p:Authoring=true</c>) adds the planet tab (D89).
/// </summary>
public interface ITabWorkspace : IDisposable
{
    /// <summary>Background work still running (a timed run waits for it).</summary>
    bool Busy { get; }

    /// <summary>A tool owns the left mouse: the camera leaves it alone.</summary>
    bool CameraHeld { get; }

    bool Modified { get; }

    float4 ClearColour { get; }

    void Tick(long frame);

    /// <param name="dock">Where its panel docks on first use.</param>
    void DrawPanels(uint dock);

    void UpdateViewport(ImGuiIOPtr io, Vector2 viewport, in Matrix4x4 viewProj, bool mouseFree, OverlayRenderer overlay);

    void HandleKey(GlfwKey key, bool ctrl);

    void RecordBeforeScene(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj);

    void RecordScene(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, VkExtent2D output);

    /// <summary>Saves where it came from; returns the status line.</summary>
    string Save();
}

/// <summary>Tabs that are not plans (<see cref="ITabWorkspace"/>), and the hooks an authoring build fills in.</summary>
public sealed partial class DesignerApp
{
    private float4? _planClear;

    /// <summary>The arguments only an authoring build reads (--planet and its options); nothing in the shipped app.</summary>
    public static void ParseAuthoringArgs(string[] args) => ReadAuthoringArgs(args);

    static partial void ReadAuthoringArgs(string[] args);

    partial void TickAuthoring();

    partial void OpenPlanetRequest(string body);

    /// <summary>Per frame first: authoring work, the clear colour, and a non-plan tab's tick in place of the plan's.</summary>
    private bool TickTab()
    {
        _planClear ??= Renderer.ClearColor;
        TickAuthoring();
        Renderer.ClearColor = _active.Tab?.ClearColour ?? _planClear.Value;
        _active.Tab?.Tick(Frames);
        return _active.Tab is not null;
    }

    protected override void RecordBeforeScene(CommandBuffer commands, int frameIndex, in Matrix4x4 viewProj)
        => _active.Tab?.RecordBeforeScene(commands, frameIndex, viewProj);

    /// <summary>
    /// A closed tab: earlier frames may still use its GPU buffers, so the device goes idle first. Tabs close in
    /// AfterPanels, before this frame records anything.
    /// </summary>
    private void CloseTab(Document closing)
    {
        if (closing.Tab is not { } tab)
            return;

        closing.Tab = null;
        Renderer.WaitIdle();
        tab.Dispose();
    }

    private void DisposeTabs()
    {
        foreach (var d in _docs)
            d.Tab?.Dispose();
    }
}
