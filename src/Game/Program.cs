using Brutal.Logging;
using BrutalGame;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var windSpeed = 10f;

// Groundswell: significant height (m), peak period (s), direction it travels toward (degrees from east toward south). 0 height = none.
float[] swell = [1.2f, 13f, 0f];
var foamDebug = false;
var editTest = false;
var hideUi = false;
var benchSeconds = 0.0;
var planetFly = false;
var moonTest = false;
var shoreFly = false;
var seamTest = false;
OrbitTestFrame? orbitTest = null;

// Weather drives the ocean wind and the sky unless --wind pins the sea (tests, benchmarks); --weather forces it on.
bool? weather = null;

// Weather time at launch (minutes): start inside a given system's life, sea and sky already settled.
var weatherOffset = 0f;

// Scene resolution per axis relative to the window (0.5..1); the UI stays native.
var renderScale = 1f;
float[]? camera = null;

// Planet kind of the terrain planet and the moons (data/planets/kinds), and the seed its ranges draw from.
string? planetKind = null;
ulong? kindSeed = null;
var steep = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--wind" && i + 1 < args.Length)
    {
        windSpeed = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
        weather ??= false;
    }
    else if (args[i] == "--weather")
        weather = true;
    else if (args[i] == "--no-weather")
        weather = false;
    else if (args[i] == "--weather-offset" && i + 1 < args.Length)
        weatherOffset = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--render-scale" && i + 1 < args.Length)
        renderScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--swell" && i + 3 < args.Length)
    {
        for (var c = 0; c < 3; c++)
            swell[c] = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--foam-debug")
        foamDebug = true;
    else if (args[i] == "--edit-test")
        editTest = true;
    else if (args[i] == "--hide-ui")
        hideUi = true;
    else if (args[i] == "--bench")
    {
        benchSeconds = 12.0;

        if (i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            benchSeconds = seconds;
            i++;
        }
    }
    else if (args[i] == "--planet-fly")
        planetFly = true;
    else if (args[i] == "--moon-test")
        moonTest = true;
    else if (args[i] == "--shore-fly")
        shoreFly = true;
    else if (args[i] == "--seam-test")
        seamTest = true;
    else if (args[i] == "--planet-kind" && i + 1 < args.Length)
        planetKind = args[++i];
    else if (args[i] == "--kind-seed" && i + 1 < args.Length)
        kindSeed = ulong.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--steep")
        steep = true;
    else if (args[i] == "--orbit-test")
    {
        orbitTest = OrbitTestFrame.Surface;

        if (i + 1 < args.Length && args[i + 1] == "inertial")
        {
            orbitTest = OrbitTestFrame.Inertial;
            i++;
        }
    }
    else if (args[i] == "--cam" && i + 5 < args.Length)
    {
        camera = new float[5];
        for (var c = 0; c < 5; c++)
            camera[c] = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

// menu: without a test or bench flag the menu runs the game; a new world with another seed relaunches it (Menu/MenuLaunch.cs).
if (BrutalGame.Menu.MenuLaunch.FromArgs(args) is { } menu)
{
    do
    {
        using var menuApp = new GameApp(windSpeed, foamDebug, editTest, camera, hideUi, benchSeconds, planetFly, swell, weather ?? true, weatherOffset, renderScale, orbitTest, moonTest, shoreFly, seamTest, menu, planetKind, kindSeed, steep);
        menuApp.Run();
    }
    while (menu.TakeRelaunch());

    return;
}

using var app = new GameApp(windSpeed, foamDebug, editTest, camera, hideUi, benchSeconds, planetFly, swell, weather ?? true, weatherOffset, renderScale, orbitTest, moonTest, shoreFly, seamTest, planetKind: planetKind, kindSeed: kindSeed, steep: steep);
app.Run();
