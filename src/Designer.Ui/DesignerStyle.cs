using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The designer's look, matched to the main menu (Game's MenuStyle: Segoe UI, near-black translucent panels, white text,
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
    /// Once, before the first frame: the default docked layout, unless ImGui has a saved one to load. Tools and brushes
    /// on the left; properties, mesh and hull layers as tabs top right; stats bottom right; the middle is the 3D view.
    /// </summary>
    public static void SeedLayout(float width, float height)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "imgui.ini")) || File.Exists("imgui.ini"))
            return;

        var w = (int)width;
        var h = (int)height;
        var left = Math.Min(440, w / 4);
        var right = Math.Min(420, w / 4);
        var ini = $"""
            [Window][Designer]
            DockId=0x00000007,0

            [Window][Brushes]
            DockId=0x00000008,0

            [Window][Properties]
            DockId=0x00000005,0

            [Window][Mesh]
            DockId=0x00000005,1

            [Window][Hull layers]
            DockId=0x00000005,2

            [Window][Stats]
            DockId=0x00000006,0

            [Docking][Data]
            DockSpace     ID=0x{DockSpaceId:X8} Pos=0,0 Size={w},{h} Split=X
              DockNode    ID=0x00000001 Parent=0x{DockSpaceId:X8} SizeRef={left},{h} Split=Y
                DockNode  ID=0x00000007 Parent=0x00000001 SizeRef={left},{h * 62 / 100}
                DockNode  ID=0x00000008 Parent=0x00000001 SizeRef={left},{h * 38 / 100}
              DockNode    ID=0x00000002 Parent=0x{DockSpaceId:X8} SizeRef={w - left},{h} Split=X
                DockNode  ID=0x00000003 Parent=0x00000002 SizeRef={w - left - right},{h} CentralNode=1
                DockNode  ID=0x00000004 Parent=0x00000002 SizeRef={right},{h} Split=Y
                  DockNode ID=0x00000005 Parent=0x00000004 SizeRef={right},{h * 66 / 100}
                  DockNode ID=0x00000006 Parent=0x00000004 SizeRef={right},{h * 34 / 100}

            """;
        ImGui.LoadIniSettingsFromMemory(ini);
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
