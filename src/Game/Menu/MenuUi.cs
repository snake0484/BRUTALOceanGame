using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace BrutalGame.Menu;

/// <summary>What the menu shows of the world behind it, and the last frame's cost (GPU: the whole frame's timestamp scope).</summary>
public readonly record struct MenuWorld(bool Ready, int PendingJobs, ulong Seed, float CpuMs, float GpuMs);

/// <summary>
/// The main menu, pause menu, loading screen and settings: one full-window ImGui window in <see cref="MenuStyle"/>,
/// keyboard and mouse. Draws nothing while playing (bar the fade in). It fires simple transitions on the state machine
/// itself; a new game and changed settings are handed to the owner (<see cref="TakeNewGame"/>,
/// <see cref="TakeSettingsChanged"/>), which owns the world and the window.
/// </summary>
public sealed class MenuUi
{
    private enum Screen : byte
    {
        Root,
        NewGame,
        Load,
        Multiplayer,
        Settings,
    }

    private const ImGuiWindowFlags WindowFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDocking;

    private const float Left = 128f;
    private const float Top = 140f;
    private const float ColumnWidth = 440f;
    private const float PanelWidth = 700f;
    private const float ItemHeight = 54f;
    private const float FieldWidth = 320f;
    private const float PortWidth = 130f;
    private const float LabelWidth = 290f;
    private const float ControlWidth = 340f;
    private const float BarWidth = 4f;
    private const float FooterHeight = 96f;
    private const float ShadeShare = 0.62f;
    private const float ShadeAlpha = 0.84f;
    private const float PauseDim = 0.5f;
    private const float RevealSeconds = 1.5f;
    private const float RevealTimeout = 8f;
    private const float FadeSeconds = 0.6f;
    private const float ScaleStep = 5f;
    private const double SweepSeconds = 1.6;
    private const float SweepShare = 0.3f;
    private const int NoCapture = -1;
    private const string DefaultAddress = "127.0.0.1";
    private const string DefaultPort = "27015";
    private const string BadPort = "Port must be 1 to 65535.";
    private const string SaveFailed = "Settings could not be saved.";
    private const string SettingsUnreadable = "The settings file was unreadable. Defaults restored; the old file is kept as settings.json.bad.";
    private const string SettingsNewer = "The settings file is from a newer build. Defaults in use; the old file is kept as settings.json.bad.";

    private static readonly string[] ModeLabels = ["Windowed", "Borderless", "Fullscreen"];
    private static readonly int2[] StandardSizes = [new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160)];

    private readonly AppStateMachine _app;
    private readonly MenuStyle _style;
    private readonly SettingsStore _settings;
    private readonly ISaveCatalog _saves;
    private readonly ISessionLauncher _sessions;
    private readonly DesignerLauncher _designer;
    private readonly ImInputString _seed = new(24, "1"u8);
    private readonly ImInputString _address = new(64, DefaultAddress);
    private readonly ImInputString _joinPort = new(8, DefaultPort);
    private readonly ImInputString _hostPort = new(8, DefaultPort);
    private int2[] _sizes = StandardSizes;
    private string[] _sizeLabels = Labels(StandardSizes);
    private AppState _state = AppState.Boot;
    private Screen _screen;
    private string _message;
    private int _capture = NoCapture;
    private bool _focusFirst;
    private bool _focusWindow;
    private bool _settingsDirty;
    private bool _settingsChanged;
    private bool _newGame;
    private ulong _newGameSeed;
    private bool _revealing;
    private float _revealWait;
    private float _reveal;
    private float _fade;

    public MenuUi(AppStateMachine app, MenuStyle style, SettingsStore settings, ISaveCatalog saves, ISessionLauncher sessions, DesignerLauncher designer)
    {
        _app = app;
        _style = style;
        _settings = settings;
        _saves = saves;
        _sessions = sessions;
        _designer = designer;
        _message = settings.LastLoad switch
        {
            SettingsLoad.Corrupt => SettingsUnreadable,
            SettingsLoad.NewerVersion => SettingsNewer,
            _ => "",
        };
    }

    /// <summary>The resolutions offered: the standard ones the monitor can show, and its native one.</summary>
    public void SetDisplay(int2 native)
    {
        var sizes = new List<int2>();

        foreach (var size in StandardSizes)
        {
            if (size.X <= native.X && size.Y <= native.Y)
                sizes.Add(size);
        }

        if (!sizes.Contains(native))
            sizes.Add(native);

        _sizes = [.. sizes];
        _sizeLabels = Labels(_sizes);
    }

    public void StartNewGame(ulong seed)
    {
        _newGame = true;
        _newGameSeed = seed;
    }

    public bool TakeNewGame(out ulong seed)
    {
        seed = _newGameSeed;
        var taken = _newGame;
        _newGame = false;
        return taken;
    }

    /// <summary>The main menu has faded in over the streamed planet.</summary>
    public bool Revealed => _reveal >= 1f;

    public bool TakeSettingsChanged()
    {
        var changed = _settingsChanged;
        _settingsChanged = false;
        return changed;
    }

    /// <summary>Escape: cancel a key capture, leave a sub-screen, pause or resume. True when the key was used.</summary>
    public bool Back()
    {
        if (_capture != NoCapture)
        {
            _capture = NoCapture;
            return true;
        }

        if (_app.State == AppState.Playing)
            return _app.Fire(AppTrigger.Pause);

        if (!_app.MenuVisible)
            return false;

        if (_screen != Screen.Root)
            Go(Screen.Root);
        else if (_app.State == AppState.Paused)
            _app.Fire(AppTrigger.Resume);
        else if (_app.State == AppState.Loading)
            _app.Fire(AppTrigger.ExitToMenu);

        return true;
    }

    /// <summary>While a binding waits for its key, the next key press is that key.</summary>
    public bool CaptureKey(GlfwKey key)
    {
        if (_capture == NoCapture || key == GlfwKey.Escape)
            return false;

        _settings.Current.Keys[(GameAction)_capture] = key;
        _capture = NoCapture;
        Changed();
        return true;
    }

    public void Draw(float dt, float contentScale, in MenuWorld world)
    {
        var state = _app.State;

        if (state != _state)
            Enter(state);

        if (state == AppState.Playing)
        {
            DrawFade(dt);
            return;
        }

        if (!_app.MenuVisible)
            return;

        var viewport = ImGui.GetMainViewport();
        var origin = viewport.Pos;
        var size = viewport.Size;
        _style.Begin(size.Y, contentScale);
        ImGui.SetNextWindowPos(origin);
        ImGui.SetNextWindowSize(size);

        if (_focusWindow)
        {
            ImGui.SetNextWindowFocus();
            _focusWindow = false;
        }

        if (ImGui.Begin("##menu"u8, WindowFlags))
        {
            Backdrop(state, origin, size, dt, world.Ready);
            ImGui.Indent(_style.Px(Left));
            ImGui.SetCursorPosY(_style.Px(Top));

            if (state == AppState.Loading)
                DrawLoading(world);
            else if (_screen == Screen.Root)
                DrawRoot(state);
            else if (_screen == Screen.NewGame)
                DrawNewGame();
            else if (_screen == Screen.Load)
                DrawLoad();
            else if (_screen == Screen.Multiplayer)
                DrawMultiplayer();
            else
                DrawSettings(size.Y);

            Footer(size.Y);
            Cost(origin, size, world);
            ImGui.Unindent(_style.Px(Left));
        }

        ImGui.End();
        _style.End();
        SaveSettings();
    }

    private static string[] Labels(int2[] sizes)
    {
        var labels = new string[sizes.Length];

        for (var i = 0; i < sizes.Length; i++)
            labels[i] = string.Create(CultureInfo.InvariantCulture, $"{sizes[i].X} x {sizes[i].Y}");

        return labels;
    }

    private void Enter(AppState state)
    {
        if (state == AppState.Playing && _state == AppState.Loading)
            _fade = 1f;

        if (state == AppState.MainMenu)
        {
            _saves.Refresh();
            _revealing = false;
            _revealWait = 0f;
            _reveal = 0f;
        }

        _state = state;
        _screen = Screen.Root;
        _capture = NoCapture;
        _focusFirst = true;
        _focusWindow = true;
    }

    private void Go(Screen screen)
    {
        _screen = screen;
        _capture = NoCapture;
        _focusFirst = true;
        _message = "";
    }

    private void Changed()
    {
        _settings.Current.Sanitize();
        _settingsDirty = true;
        _settingsChanged = true;
    }

    /// <summary>Written once the edit is over (slider released), not on every step of a drag.</summary>
    private void SaveSettings()
    {
        if (!_settingsDirty || ImGui.IsAnyItemActive())
            return;

        _settingsDirty = false;

        if (!_settings.Save())
            _message = SaveFailed;
    }

    /// <summary>
    /// Shade under the menu column so text reads over the planet; the pause menu dims the world, loading hides it, and
    /// the main menu stays black until the planet has streamed in, then fades it up.
    /// </summary>
    private void Backdrop(AppState state, float2 origin, float2 size, float dt, bool worldReady)
    {
        var draw = ImGui.GetWindowDrawList();
        var corner = new float2(origin.X + size.X, origin.Y + size.Y);

        if (state == AppState.Loading)
        {
            draw.AddRectFilled(origin, corner, MenuStyle.Black(1f));
            return;
        }

        if (state == AppState.Paused)
            draw.AddRectFilled(origin, corner, MenuStyle.Black(PauseDim));

        var shade = MenuStyle.Black(ShadeAlpha);
        var none = MenuStyle.Black(0f);
        draw.AddRectFilledMultiColor(origin, new float2(origin.X + size.X * ShadeShare, corner.Y), shade, none, none, shade);

        if (state != AppState.MainMenu || _reveal >= 1f)
            return;

        _revealWait += dt;
        _revealing |= worldReady || _revealWait > RevealTimeout;

        if (_revealing)
            _reveal = MathF.Min(1f, _reveal + dt / RevealSeconds);

        draw.AddRectFilled(origin, corner, MenuStyle.Black(1f - _reveal));
    }

    private void DrawFade(float dt)
    {
        if (_fade <= 0f)
            return;

        var viewport = ImGui.GetMainViewport();
        var origin = viewport.Pos;
        var size = viewport.Size;
        ImGui.GetForegroundDrawList().AddRectFilled(origin, new float2(origin.X + size.X, origin.Y + size.Y), MenuStyle.Black(_fade));
        _fade -= dt / FadeSeconds;
    }

    private void DrawRoot(AppState state)
    {
        if (state == AppState.Paused)
        {
            Title("Paused"u8, "The world is stopped."u8);
            FocusFirst();

            if (Item("Resume"u8))
                _app.Fire(AppTrigger.Resume);

            if (Item("Settings"u8))
                Go(Screen.Settings);

            if (Item("Exit to Menu"u8))
                _app.Fire(AppTrigger.ExitToMenu);

            if (Item("Quit"u8))
                _app.Fire(AppTrigger.Quit);

            return;
        }

        var hasSaves = _saves.Slots.Length > 0;
        Title("BRUTALGame"u8, "Development build"u8);
        FocusFirst();

        if (Item("New Game"u8))
            Go(Screen.NewGame);

        if (Item("Continue"u8, hasSaves))
            LoadSlot(_saves.Slots[0]);

        if (Item("Load Game"u8, hasSaves))
            Go(Screen.Load);

        if (Item("Multiplayer"u8))
            Go(Screen.Multiplayer);

        if (Item("Ship Designer"u8))
            _message = _designer.Launch();

        if (Item("Settings"u8))
            Go(Screen.Settings);

        if (Item("Quit"u8))
            _app.Fire(AppTrigger.Quit);
    }

    private void DrawNewGame()
    {
        Heading("New Game"u8);
        Caption("World seed"u8);
        ImGui.SetNextItemWidth(_style.Px(FieldWidth));
        var entered = ImGui.InputText("##seed"u8, _seed, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        Hint("Digits are the seed itself. Other text is hashed. Blank picks one at random."u8);
        Gap(18f);
        FocusFirst();

        if (Item("Start"u8) || entered)
            StartNewGame(MenuLaunch.ParseSeed(_seed.ToString()));

        if (Item("Back"u8))
            Go(Screen.Root);
    }

    private void DrawLoad()
    {
        Heading("Load Game"u8);
        FocusFirst();
        var slots = _saves.Slots;

        for (var i = 0; i < slots.Length; i++)
        {
            if (Item($"{slots[i].Name}   {slots[i].SavedUtc.ToLocalTime():yyyy-MM-dd HH:mm}##slot{i}"))
                LoadSlot(slots[i]);
        }

        if (Item("Back"u8))
            Go(Screen.Root);
    }

    private void LoadSlot(in SaveSlot slot)
    {
        var result = _sessions.Load(slot);
        _message = result.Message;

        if (result.Started)
            _app.Fire(AppTrigger.StartGame);
    }

    private void DrawMultiplayer()
    {
        var available = _sessions.Available;
        Heading("Multiplayer"u8);

        if (!available)
        {
            ImGui.TextColored(MenuStyle.Accent, _sessions.Status);
            Gap(10f);
        }

        Caption("Host on port"u8);
        ImGui.SetNextItemWidth(_style.Px(PortWidth));
        ImGui.InputText("##hostport"u8, _hostPort, ImGuiInputTextFlags.CharsDecimal);
        FocusFirst();

        if (Item("Host Game"u8, available))
            Connect(host: true);

        Gap(18f);
        Caption("Join address and port"u8);
        ImGui.SetNextItemWidth(_style.Px(FieldWidth));
        ImGui.InputText("##address"u8, _address);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(_style.Px(PortWidth));
        ImGui.InputText("##joinport"u8, _joinPort, ImGuiInputTextFlags.CharsDecimal);

        if (Item("Join Game"u8, available))
            Connect(host: false);

        if (Item("Back"u8))
            Go(Screen.Root);
    }

    private void Connect(bool host)
    {
        if (!ushort.TryParse((host ? _hostPort : _joinPort).ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port == 0)
        {
            _message = BadPort;
            return;
        }

        var result = host ? _sessions.Host(MenuLaunch.ParseSeed(_seed.ToString()), port) : _sessions.Join(_address.ToString(), port);
        _message = result.Message;

        if (result.Started)
            _app.Fire(AppTrigger.StartGame);
    }

    private void DrawSettings(float windowHeight)
    {
        var settings = _settings.Current;
        Heading("Settings"u8);
        var listHeight = windowHeight - ImGui.GetCursorPosY() - _style.Px(FooterHeight + 2f * ItemHeight + 40f);

        if (ImGui.BeginChild("##settings"u8, new float2(_style.Px(PanelWidth), listHeight), ImGuiChildFlags.NavFlattened))
        {
            DrawDisplay(settings);
            DrawControls(settings);
            Section("Audio"u8);
            Row("Master volume"u8);
            var volume = settings.MasterVolume * 100f;

            if (ImGui.SliderFloat("##volume"u8, ref volume, 0f, 100f, "%.0f%%"u8))
            {
                settings.MasterVolume = volume / 100f;
                Changed();
            }

            Hint("No audio in this build yet: the value is only stored."u8);
        }

        ImGui.EndChild();
        Gap(12f);

        if (Item("Reset to Defaults"u8))
        {
            settings.Reset();
            Changed();
        }

        if (Item("Back"u8))
            Go(Screen.Root);
    }

    private void DrawDisplay(GameSettings settings)
    {
        Section("Display"u8);
        Row("Window mode"u8);
        FocusFirst();

        if (ImGui.BeginCombo("##mode"u8, ModeLabels[(int)settings.WindowMode]))
        {
            for (var i = 0; i < ModeLabels.Length; i++)
            {
                if (ImGui.Selectable(ModeLabels[i], i == (int)settings.WindowMode) && i != (int)settings.WindowMode)
                {
                    settings.WindowMode = (WindowMode)i;
                    Changed();
                }
            }

            ImGui.EndCombo();
        }

        Row("Resolution"u8);
        ImGui.BeginDisabled(settings.WindowMode == WindowMode.Borderless);

        if (ImGui.BeginCombo("##size"u8, $"{settings.Width} x {settings.Height}"))
        {
            for (var i = 0; i < _sizes.Length; i++)
            {
                var current = _sizes[i].X == settings.Width && _sizes[i].Y == settings.Height;

                if (ImGui.Selectable(_sizeLabels[i], current) && !current)
                {
                    settings.Width = _sizes[i].X;
                    settings.Height = _sizes[i].Y;
                    Changed();
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
        Row("VSync"u8);

        if (ImGui.Checkbox("##vsync"u8, ref settings.VSync))
            Changed();

        Row("Render scale"u8);
        var scale = settings.RenderScale * 100f;

        if (ImGui.SliderFloat("##scale"u8, ref scale, GameSettings.MinRenderScale * 100f, GameSettings.MaxRenderScale * 100f, "%.0f%%"u8))
        {
            var stepped = MathF.Round(scale / ScaleStep) * ScaleStep / 100f;

            if (stepped != settings.RenderScale)
            {
                settings.RenderScale = stepped;
                Changed();
            }
        }

        Row("Field of view"u8);

        if (ImGui.SliderFloat("##fov"u8, ref settings.FovDegrees, GameSettings.MinFov, GameSettings.MaxFov, "%.0f deg"u8))
            Changed();
    }

    private void DrawControls(GameSettings settings)
    {
        Section("Controls"u8);
        Row("Mouse sensitivity"u8);

        if (ImGui.SliderFloat("##sensitivity"u8, ref settings.MouseSensitivity, GameSettings.MinSensitivity, GameSettings.MaxSensitivity, "%.2f"u8))
            Changed();

        Row("Invert Y"u8);

        if (ImGui.Checkbox("##inverty"u8, ref settings.InvertY))
            Changed();

        var keySize = new float2(_style.Px(ControlWidth), 0f);
        ImGui.PushStyleColor(ImGuiCol.Button, MenuStyle.Frame);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, MenuStyle.FrameHover);

        for (var i = 0; i < KeyBindings.Count; i++)
        {
            var action = (GameAction)i;
            Row(KeyBindings.Label(action));
            var pressed = _capture == i ? ImGui.Button($"Press a key...##key{i}", keySize) : ImGui.Button($"{settings.Keys.KeyName(action)}##key{i}", keySize);

            if (pressed)
                _capture = i;
        }

        ImGui.PopStyleColor(2);
    }

    private void DrawLoading(in MenuWorld world)
    {
        Title("Loading"u8, $"World seed {world.Seed}");
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = _style.Px(ColumnWidth);
        var height = _style.Px(3f);
        var sweep = (float)(ImGui.GetTime() % SweepSeconds / SweepSeconds) * (1f + SweepShare) - SweepShare;
        var from = start.X + width * MathF.Max(sweep, 0f);
        var to = start.X + width * MathF.Min(sweep + SweepShare, 1f);
        draw.AddRectFilled(start, new float2(start.X + width, start.Y + height), MenuStyle.TrackColour);
        draw.AddRectFilled(new float2(from, start.Y), new float2(to, start.Y + height), MenuStyle.AccentColour);
        ImGui.Dummy(new float2(width, height));
        Gap(8f);
        Hint($"{world.PendingJobs} terrain jobs");
    }

    private void Footer(float windowHeight)
    {
        ImGui.SetCursorPosY(windowHeight - _style.Px(FooterHeight));

        if (_message.Length > 0)
            ImGui.TextColored(MenuStyle.Accent, _message);

        Hint("Up / Down  select      Enter  confirm      Esc  back"u8);
    }

    /// <summary>The menu's own frame cost, bottom right: it must stay near nothing.</summary>
    private void Cost(float2 origin, float2 size, in MenuWorld world)
    {
        _style.PushSmall();
        ImString text = $"GPU frame {world.GpuMs:F2} ms   CPU work {world.CpuMs:F2} ms";
        var width = ImGui.CalcTextSize(text).X;
        var margin = _style.Px(24f);
        ImGui.GetWindowDrawList().AddText(new float2(origin.X + size.X - width - margin, origin.Y + size.Y - margin - ImGui.GetFontSize()), MenuStyle.DimColour, text);
        ImGui.PopFont();
    }

    private void Title(ImString text, ImString caption)
    {
        _style.PushTitle();
        ImGui.Text(text);
        ImGui.PopFont();
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, new float2(start.X + _style.Px(72f), start.Y + _style.Px(4f)), MenuStyle.AccentColour);
        Gap(18f);
        ImGui.TextColored(MenuStyle.TextDim, caption);
        Gap(30f);
    }

    private void Heading(ImString text)
    {
        _style.PushHeading();
        ImGui.Text(text);
        ImGui.PopFont();
        Gap(16f);
    }

    private void Section(ImString text)
    {
        Gap(12f);
        _style.PushSmall();
        ImGui.TextColored(MenuStyle.Accent, text);
        ImGui.PopFont();
        ImGui.Separator();
        Gap(2f);
    }

    private void Caption(ImString text) => ImGui.TextColored(MenuStyle.TextDim, text);

    private void Hint(ImString text)
    {
        _style.PushSmall();
        ImGui.TextColored(MenuStyle.TextDim, text);
        ImGui.PopFont();
    }

    private void Gap(float height) => ImGui.Dummy(new float2(0f, _style.Px(height)));

    private void Row(ImString label)
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MenuStyle.TextDim, label);
        ImGui.SameLine(_style.Px(LabelWidth));
        ImGui.SetNextItemWidth(_style.Px(ControlWidth));
    }

    /// <summary>
    /// Keyboard focus for the next item, once per screen, with the nav cursor shown: hidden, ImGui spends the first key
    /// press on showing it, so Enter would do nothing.
    /// </summary>
    private void FocusFirst()
    {
        if (!_focusFirst)
            return;

        ImGui.SetKeyboardFocusHere();
        ImGui.SetNavCursorVisible(true);
        _focusFirst = false;
    }

    private bool Item(ImString label, bool enabled = true)
    {
        ImGui.BeginDisabled(!enabled);
        var pressed = ImGui.Button(label, new float2(_style.Px(ColumnWidth), _style.Px(ItemHeight)));
        ImGui.EndDisabled();

        if (enabled && (ImGui.IsItemHovered() || (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible)))
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddRectFilled(min, new float2(min.X + _style.Px(BarWidth), max.Y), MenuStyle.AccentColour);
        }

        return pressed;
    }
}
