using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalGame.Menu;

/// <summary>
/// The menu's look in one place: fonts, colours and spacing, pushed round the menu's windows so the debug panels keep
/// ImGui's default style. Sizes are in pixels at a 1080-high window and scale with the window's height, never below
/// the monitor's content scale.
/// </summary>
public sealed class MenuStyle
{
    public const float ReferenceHeight = 1080f;
    public const float BodySize = 22f;
    public const float SmallSize = 16f;
    public const float HeadingSize = 38f;
    public const float TitleSize = 92f;

    private const float MinDpiShare = 0.8f;
    private const float MinScale = 0.6f;
    private const float MaxScale = 4f;
    private const string BodyFont = "segoeui.ttf";
    private const string TitleFont = "segoeuil.ttf";
    private const int Colours = 26;
    private const int Vars = 12;

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
    public static readonly float4 Border = new(1f, 1f, 1f, 0.09f);
    public static readonly float4 Clear = new(0f, 0f, 0f, 0f);

    public static readonly uint AccentColour = Rgba(1f, 0.62f, 0.2f, 1f);
    public static readonly uint DimColour = Rgba(0.62f, 0.67f, 0.74f, 1f);
    public static readonly uint TrackColour = Rgba(1f, 1f, 1f, 0.1f);

    private ImFontPtr _body;
    private ImFontPtr _title;

    public float Scale { get; private set; } = 1f;

    /// <summary>ImGui's packed colour (IM_COL32: red in the low byte).</summary>
    public static uint Rgba(float r, float g, float b, float a) =>
        (uint)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f) << 24
        | (uint)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f) << 16
        | (uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f) << 8
        | (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);

    public static uint Black(float alpha) => Rgba(0.008f, 0.01f, 0.016f, alpha);

    /// <summary>
    /// Once, before the first frame. Segoe UI from the system's font folder; without it the menu uses ImGui's built-in
    /// font, scaled. The built-in font is added first so it stays the debug panels' default.
    /// </summary>
    public void LoadFonts()
    {
        var atlas = ImGui.GetIO().Fonts;

        if (atlas.Fonts.Count == 0)
            atlas.AddFontDefault();

        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        _body = Load(atlas, Path.Combine(folder, BodyFont), BodySize);
        _title = Load(atlas, Path.Combine(folder, TitleFont), TitleSize);

        if (_title.IsNull())
            _title = _body;
    }

    public float Px(float reference) => MathF.Round(reference * Scale);

    public void Begin(float windowHeight, float contentScale)
    {
        Scale = Math.Clamp(MathF.Max(windowHeight / ReferenceHeight, contentScale * MinDpiShare), MinScale, MaxScale);

        ImGui.PushFont(_body, Px(BodySize));

        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, TextOff);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Panel);
        ImGui.PushStyleColor(ImGuiCol.Border, Border);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Frame);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, FrameActive);
        ImGui.PushStyleColor(ImGuiCol.Button, Clear);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.Header, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, AccentSoft);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, Text);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.Separator, Border);
        ImGui.PushStyleColor(ImGuiCol.NavCursor, Accent);
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, AccentPress);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Clear);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, FrameHover);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, FrameActive);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, Accent);
        ImGui.PushStyleColor(ImGuiCol.InputTextCursor, Accent);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new float2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new float2(Px(18f), Px(11f)));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Px(3f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(Px(14f), Px(8f)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new float2(Px(10f), Px(6f)));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, Px(16f));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, Px(3f));
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Px(10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new float2(0f, 0.5f));
    }

    public void End()
    {
        ImGui.PopStyleVar(Vars);
        ImGui.PopStyleColor(Colours);
        ImGui.PopFont();
    }

    public void PushTitle() => ImGui.PushFont(_title, Px(TitleSize));

    public void PushHeading() => ImGui.PushFont(_title, Px(HeadingSize));

    public void PushSmall() => ImGui.PushFont(_body, Px(SmallSize));

    private static ImFontPtr Load(ImFontAtlasPtr atlas, string path, float size) => File.Exists(path) ? atlas.AddFontFromFileTTF(path, size) : default;
}
