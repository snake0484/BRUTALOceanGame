using Brutal.Logging;
using BrutalGame;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var windSpeed = 10f;

// Groundswell: significant height (m), peak period (s), direction it travels toward (degrees). 0 height = none.
float[] swell = [1.2f, 13f, 0f];
var foamDebug = false;
var editTest = false;
// Voxel water steps per second; by default 240 for the automaton (it spreads diffusively) and 60 for the pipe model,
// which is stable there (docs/voxel-water.md, v2).
int? waterRate = null;

// Voxel water runs the 3D pipe model with momentum (docs/voxel-water.md, v2); --water-automaton runs the old cell
// automaton instead (no momentum, no flow or foam for the renderer).
var waterPipes = true;
var hideUi = false;
var coastal = true;
var benchSeconds = 0.0;
var flyTest = false;

// Weather drives the ocean wind and the sky unless --wind pins the sea (tests, benchmarks); --weather forces it on.
bool? weather = null;

// Weather time at launch (minutes): start inside a given system's life, sea and sky already settled.
var weatherOffset = 0f;

// Rain intensity 0..1 regardless of the weather (the Weather panel's override); negative = from the weather.
var rain = -1f;
var benchRain = false;
var benchFog = false;
var benchSsr = false;
var ssrOn = true;
var voxelSwell = true;
var fogOn = true;
// Scene resolution per axis relative to the window (0.5..1); the UI stays native.
var renderScale = 1f;
float[]? camera = null;
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
    else if (args[i] == "--rain" && i + 1 < args.Length)
        rain = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--bench-rain")
    {
        benchRain = true;
        benchSeconds = benchSeconds > 0.0 ? benchSeconds : 12.0;
    }
    else if (args[i] == "--no-fog")
        fogOn = false;
    else if (args[i] == "--bench-fog")
    {
        benchFog = true;
        benchSeconds = benchSeconds > 0.0 ? benchSeconds : 12.0;
    }
    else if (args[i] == "--render-scale" && i + 1 < args.Length)
        renderScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--no-voxel-swell")
        voxelSwell = false;
    else if (args[i] == "--no-ssr")
        ssrOn = false;
    else if (args[i] == "--bench-ssr")
    {
        benchSsr = true;
        benchSeconds = benchSeconds > 0.0 ? benchSeconds : 12.0;
    }
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
    else if (args[i] == "--fly-test")
        flyTest = true;
    else if (args[i] == "--no-coastal")
        coastal = false;
    else if (args[i] == "--water-pipes")
        waterPipes = true;
    else if (args[i] == "--water-automaton")
        waterPipes = false;
    else if (args[i] == "--water-rate" && i + 1 < args.Length)
        waterRate = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--cam" && i + 5 < args.Length)
    {
        camera = new float[5];
        for (var c = 0; c < 5; c++)
            camera[c] = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

using var app = new GameApp(windSpeed, foamDebug, editTest, camera, waterRate ?? (waterPipes ? 60 : 240), hideUi, coastal, benchSeconds, flyTest, swell, weather ?? true, weatherOffset, rain, benchRain, waterPipes, benchFog, fogOn, benchSsr, ssrOn, voxelSwell, renderScale);
app.Run();
