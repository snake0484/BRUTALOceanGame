using Brutal.ImGuiApi;
using System.Runtime.InteropServices;
using Brutal.Numerics;

namespace BrutalGame.DesignKit.Ui;

/// <summary>
/// The design tools' look, matched to the main menu (Game's MenuStyle: Segoe UI, near-black translucent panels, white text,
/// orange accent) with square corners everywhere. Pushed round the whole UI each frame. Sizes are pixels at a 1080-high
/// window and scale with the window's height, never below the monitor's content scale. Also owns the docked layout: a
/// dockspace over the window whose central node passes the 3D view through, seeded with a default arrangement when no
/// saved layout exists.
/// </summary>
public sealed class DesignerStyle
{
    public const float ReferenceHeight = 1080f;
    public const float BodySize = 18f;
    public const float SmallSize = 15f;
    public const uint DockSpaceId = 0x0D0C5000;

    private const float MinDpiShare = 0.8f;
    private const float MinScale = 0.6f;
    private const float MaxScale = 4f;
    private const string BodyFont = "segoeui.ttf";
    private const string StrongFont = "seguisb.ttf";
    private const int Colours = 40;
    private const int Vars = 24;

    public static readonly float4 Text = new(0.93f, 0.95f, 0.97f, 1f);
    public static readonly float4 TextDim = new(0.62f, 0.67f, 0.74f, 1f);
    public static readonly float4 TextOff = new(0.36f, 0.39f, 0.44f, 1f);
    public static readonly float4 Accent = new(1f, 0.62f, 0.2f, 1f);
    public static readonly float4 AccentSoft = new(1f, 0.62f, 0.2f, 0.16f);
    public static readonly float4 AccentPress = new(1f, 0.62f, 0.2f, 0.3f);
    public static readonly float4 Frame = new(0.09f, 0.11f, 0.15f, 0.86f);
    public static readonly float4 FrameHover = new(0.15f, 0.18f, 0.24f, 0.92f);
    public static readonly float4 FrameActive = new(0.2f, 0.24f, 0.31f, 1f);
    public static readonly float4 Panel = new(0.03f, 0.04f, 0.06f, 0.94f);
    public static readonly float4 Title = new(0.02f, 0.025f, 0.035f, 0.98f);
    public static readonly float4 Border = new(1f, 1f, 1f, 0.09f);
    public static readonly float4 Clear = new(0f, 0f, 0f, 0f);

    private ImFontPtr _body;
    private ImFontPtr _strong;

    public float Scale { get; private set; } = 1f;

    public float Px(float reference) => MathF.Round(reference * Scale);

    /// <summary>Once, before the first frame: Segoe UI from the system font folder (ImGui's built-in font otherwise).</summary>
    public void LoadFonts()
    {
        var atlas = ImGui.GetIO().Fonts;

        if (atlas.Fonts.Count == 0)
            atlas.AddFontDefault();

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        _body = Load(atlas, Path.Combine(folder, BodyFont), BodySize);
        _strong = Load(atlas, Path.Combine(folder, StrongFont), BodySize);

        if (_strong.IsNull())
            _strong = _body;

        ImGui.GetStyle().WindowMenuButtonPosition = ImGuiDir.None;
    }

    /// <summary>
    /// A tool's layout file: per user, outside the build output, so the arrangement survives rebuilds and every checkout
    /// shares it. ImGui loads it on the first frame and saves it a few seconds after each change and on exit.
    /// </summary>
    public static string LayoutPath(string fileName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BRUTALGame", fileName);

    /// <summary>
    /// Once, before the first frame: points ImGui at <see cref="LayoutPath"/> of <paramref name="fileName"/> and, when that
    /// file does not exist yet, loads <paramref name="seed"/>'s default layout (ImGui ini text for the window size; dock
    /// it under <see cref="DockSpaceId"/>, with a CentralNode=1 node for the 3D view).
    /// </summary>
    public static unsafe void UseLayout(string fileName, float width, float height, Func<int, int, string>? seed)
    {
        var path = LayoutPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // ImGui keeps the pointer for the context's lifetime: allocated once per process, never freed.
        ImGui.GetIO().IniFilename = (byte*)Marshal.StringToCoTaskMemUTF8(path);

        if (File.Exists(path) || seed is null)
            return;

        ImGui.LoadIniSettingsFromMemory(seed((int)width, (int)height));
    }

    /// <summary>The 3D view's free area: the dock space's central node (after <see cref="DockSpace"/> this frame), else the whole window.</summary>
    public static void CentralRect(out float2 min, out float2 max)
    {
        var node = ImGui.Internal.DockBuilderGetCentralNode(new ImGuiID(DockSpaceId));

        if (!node.IsNull())
        {
            min = node.Pos;
            max = new float2(node.Pos.X + node.Size.X, node.Pos.Y + node.Size.Y);
            return;
        }

        var viewport = ImGui.GetMainViewport();
        min = viewport.WorkPos;
        max = new float2(viewport.WorkPos.X + viewport.WorkSize.X, viewport.WorkPos.Y + viewport.WorkSize.Y);
    }

    /// <summary>The dockspace over the whole window; its empty middle passes the mouse through to the 3D view.</summary>
    public static void DockSpace() =>
        ImGui.DockSpaceOverViewport(new ImGuiID(DockSpaceId), ImGui.GetMainViewport(), ImGuiDockNodeFlags.PassthruCentralNode, default);

    public void Begin(float windowHeight, float contentScale)
    {
        Scale = Math.Clamp(MathF.Max(windowHeight / ReferenceHeight, contentScale * MinDpiShare), MinScale, MaxScale);

        if (!_body.IsNull())
            ImGui.PushFont(_body, Px(BodySize));
        else
            ImGui.PushFont(ImGui.GetIO().Fonts.Fonts[0], Px(BodySize));

        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextOff);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Panel);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Frame);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameActive);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Title);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Title);
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, Title);
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, Title);
        ImGui.PushStyleColor(ImGuiCol.Button, Frame);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.Header, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Text);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.Separator, Border);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGrip, Clear);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.Tab, Title);
        ImGui.PushStyleColor(ImGuiCol.TabHovered, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.TabSelected, Panel);
        ImGui.PushStyleColor(ImGuiCol.TabSelectedOverline, Accent);
        ImGui.PushStyleColor(ImGuiCol.TabDimmed, Title);
        ImGui.PushStyleColor(ImGuiCol.TabDimmedSelected, Panel);
        ImGui.PushStyleColor(ImGuiCol.DockingPreview, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.DockingEmptyBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.NavCursor, Accent);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, FrameHover);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabBarBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabBarOverlineSize, Px(2f));
        ImGui.PushStyleVar(ImGuiStyleVar.DockingSeparatorSize, Px(2f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(Px(14f), Px(12f)));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new float2(Px(10f), Px(5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(Px(10f), Px(7f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new float2(Px(8f), Px(5f)));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, Px(12f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Px(10f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowTitleAlign, new float2(0f, 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.SeparatorTextBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.SeparatorTextPadding, new float2(0f, Px(6f)));
    }

    public void End()
    {
        ImGui.PopStyleVar(Vars);
        ImGui.PopStyleColor(Colours);
        ImGui.PopFont();
    }

    /// <summary>An item width given in pixels at the body size, scaled with the current font.</summary>
    public static void ItemWidth(float reference) => ImGui.SetNextItemWidth(MathF.Round(reference * ImGui.GetFontSize() / BodySize));

    /// <summary>Semibold body text for section headings.</summary>
    public void PushStrong() => ImGui.PushFont(_strong.IsNull() ? ImGui.GetIO().Fonts.Fonts[0] : _strong, Px(BodySize));

    public void PushSmall() => ImGui.PushFont(_body.IsNull() ? ImGui.GetIO().Fonts.Fonts[0] : _body, Px(SmallSize));

    private static ImFontPtr Load(ImFontAtlasPtr atlas, string path, float size) => File.Exists(path) ? atlas.AddFontFromFileTTF(path, size) : default;
}
