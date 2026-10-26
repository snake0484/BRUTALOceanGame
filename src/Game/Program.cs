using Brutal.Logging;
using BrutalGame;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var windSpeed = 10f;

// Groundswell: significant height (m), peak period (s), direction it travels toward (degrees). 0 height = none.
float[] swell = [1.2f, 13f, 0f];
var foamDebug = false;
var editTest = false;
var floodTest = false;
var waterRate = 240;
var hideUi = false;
var seasideTest = false;
var coastal = true;
var benchSeconds = 0.0;
var breakers = false;
var surfTest = false;
var faces = true;
var lips = true;
var foldLip = true;
var flyTest = false;
var surfCam = false;
var surfSide = false;
var surfBack = false;
var chainLips = false;

// Weather drives the ocean wind and the sky unless --wind pins the sea (tests, benchmarks); --weather forces it on.
bool? weather = null;

// Weather time at launch (minutes): start inside a given system's life, sea and sky already settled.
var weatherOffset = 0f;

// Rain intensity 0..1 regardless of the weather (the Weather panel's override); negative = from the weather.
var rain = -1f;
var benchRain = false;
var roofTest = false;
// --cave-test site: all, cave, pool or cove; null = off.
string? caveTest = null;
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
    else if (args[i] == "--roof-test")
        roofTest = true;
    else if (args[i] == "--cave-test")
    {
        caveTest = "all";

        if (i + 1 < args.Length && args[i + 1] is "cave" or "pool" or "cove")
            caveTest = args[++i];
    }
    else if (args[i] == "--bench-rain")
    {
        benchRain = true;
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
    else if (args[i] == "--flood-test")
        floodTest = true;
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
    else if (args[i] == "--no-lips")
        lips = false;
    else if (args[i] == "--fold-lip")
        foldLip = true;
    else if (args[i] == "--ribbon-lip")
        foldLip = false;
    else if (args[i] == "--chain-lips")
        chainLips = true;
    else if (args[i] == "--fly-test")
        flyTest = true;
    else if (args[i] == "--surf-cam")
    {
        surfCam = true;

        if (i + 1 < args.Length && args[i + 1] == "side")
        {
            surfSide = true;
            i++;
        }
        else if (i + 1 < args.Length && args[i + 1] == "back")
        {
            surfBack = true;
            i++;
        }
    }
    else if (args[i] == "--no-faces")
        faces = false;
    else if (args[i] == "--surf-test")
        surfTest = true;
    else if (args[i] == "--breakers")
        breakers = true;
    else if (args[i] == "--no-coastal")
        coastal = false;
    else if (args[i] == "--seaside-test")
        seasideTest = true;
    else if (args[i] == "--water-rate" && i + 1 < args.Length)
        waterRate = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else if (args[i] == "--cam" && i + 5 < args.Length)
    {
        camera = new float[5];
        for (var c = 0; c < 5; c++)
            camera[c] = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    }
}

using var app = new GameApp(windSpeed, foamDebug, editTest, camera, floodTest, waterRate, hideUi, seasideTest, coastal, benchSeconds, breakers, surfTest, faces, lips, flyTest, surfCam, surfSide, swell, foldLip, surfBack, chainLips, weather ?? true, weatherOffset, rain, benchRain, roofTest, caveTest);
app.Run();
