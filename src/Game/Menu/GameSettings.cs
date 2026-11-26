namespace BrutalGame.Menu;

public enum WindowMode : byte
{
    Windowed,
    Borderless,
    Fullscreen,
}

/// <summary>Player settings at runtime. <see cref="SettingsStore"/> persists them; <see cref="Sanitize"/> runs on every load.</summary>
public sealed class GameSettings
{
    public const float MinRenderScale = 0.5f;
    public const float MaxRenderScale = 1f;
    public const float MinFov = 40f;
    public const float MaxFov = 110f;
    public const float DefaultFov = 60f;
    public const float MinSensitivity = 0.1f;
    public const float MaxSensitivity = 4f;
    public const int MinWidth = 960;
    public const int MinHeight = 540;
    public const int MaxWidth = 7680;
    public const int MaxHeight = 4320;
    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 900;

    public readonly KeyBindings Keys = new();
    public float RenderScale = MaxRenderScale;
    public bool VSync = true;
    public WindowMode WindowMode = WindowMode.Windowed;
    public int Width = DefaultWidth;
    public int Height = DefaultHeight;
    public float FovDegrees = DefaultFov;
    public float MouseSensitivity = 1f;
    public bool InvertY;

    /// <summary>Placeholder until audio (Brutal.Fmod) lands: stored and shown, read by nothing.</summary>
    public float MasterVolume = 1f;

    public void Reset() => CopyFrom(new GameSettings());

    public void CopyFrom(GameSettings other)
    {
        RenderScale = other.RenderScale;
        VSync = other.VSync;
        WindowMode = other.WindowMode;
        Width = other.Width;
        Height = other.Height;
        FovDegrees = other.FovDegrees;
        MouseSensitivity = other.MouseSensitivity;
        InvertY = other.InvertY;
        MasterVolume = other.MasterVolume;
        Keys.CopyFrom(other.Keys);
    }

    public bool SameAs(GameSettings other) =>
        RenderScale == other.RenderScale
        && VSync == other.VSync
        && WindowMode == other.WindowMode
        && Width == other.Width
        && Height == other.Height
        && FovDegrees == other.FovDegrees
        && MouseSensitivity == other.MouseSensitivity
        && InvertY == other.InvertY
        && MasterVolume == other.MasterVolume
        && Keys.SameAs(other.Keys);

    public void Sanitize()
    {
        RenderScale = Clamp(RenderScale, MinRenderScale, MaxRenderScale, MaxRenderScale);
        FovDegrees = Clamp(FovDegrees, MinFov, MaxFov, DefaultFov);
        MouseSensitivity = Clamp(MouseSensitivity, MinSensitivity, MaxSensitivity, 1f);
        MasterVolume = Clamp(MasterVolume, 0f, 1f, 1f);
        Width = Math.Clamp(Width, MinWidth, MaxWidth);
        Height = Math.Clamp(Height, MinHeight, MaxHeight);

        if (!Enum.IsDefined(WindowMode))
            WindowMode = WindowMode.Windowed;
    }

    private static float Clamp(float value, float min, float max, float fallback) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
