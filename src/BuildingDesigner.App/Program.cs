using System.Globalization;
using Brutal.Logging;
using BrutalGame.BuildingDesigner.App;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var data = Path.Combine(AppContext.BaseDirectory, "data");
string? load = null;
string? saveOnExit = null;
string? script = null;
double[]? view = null;
var demo = false;
var frames = 0;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length)
        data = args[++i];
    else if (args[i] == "--load" && i + 1 < args.Length)
        load = args[++i];
    else if (args[i] == "--save-on-exit" && i + 1 < args.Length)
        saveOnExit = args[++i];
    else if (args[i] == "--demo")
        demo = true;
    else if (args[i] == "--frames" && i + 1 < args.Length)
        frames = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--input-script" && i + 1 < args.Length)
        script = args[++i];
    else if (args[i] == "--view" && i + 3 < args.Length)
        view = [double.Parse(args[++i], CultureInfo.InvariantCulture), double.Parse(args[++i], CultureInfo.InvariantCulture), double.Parse(args[++i], CultureInfo.InvariantCulture)];
}

using var app = new BuildingDesignerApp(data, load, demo, saveOnExit, frames, view, script);
app.Run();
