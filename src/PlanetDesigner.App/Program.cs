using System.Globalization;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.PlanetDesigner.App;
using BrutalGame.Terrain;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

string? data = null;
var options = new PlanetDesignerOptions { DataRoot = "" };
var autoEdit = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--data" && i + 1 < args.Length)
        data = args[++i];
    else if (args[i] == "--body" && i + 1 < args.Length)
        options.Body = args[++i];
    else if (args[i] == "--frames" && i + 1 < args.Length)
        options.CloseAfterFrames = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--look" && i + 3 < args.Length)
        options.Look = new double3(Num(args[++i]), Num(args[++i]), Num(args[++i]));
    else if (args[i] == "--distance" && i + 1 < args.Length)
        options.Distance = Num(args[++i]);
    else if (args[i] == "--pitch" && i + 1 < args.Length)
        options.Pitch = Num(args[++i]) * Math.PI / 180.0;
    else if (args[i] == "--input-script" && i + 1 < args.Length)
        options.ScriptPath = args[++i];
    else if (args[i] == "--embedded")
        options.Embedded = true;
    else if (args[i] == "--auto-edit")
        autoEdit = true;
}

options.DataRoot = data ?? FindData(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException("No repository data folder above the app; pass --data <repo>/data");
using var app = new PlanetDesignerApp(options);

// --auto-edit: once the octree has settled, one height stroke at the camera's target and a site on its slope through
// the session, then save (a hands-off check of the live refresh and the save).
if (autoEdit)
    app.OnSettled = AutoEdit;

app.Run();

static void AutoEdit(PlanetDesignerApp app)
{
    var d = app.Target;
    var session = app.Session;
    var axis = Math.Abs(d.Y) < 0.9 ? double3.UnitY : double3.UnitX;
    var slope = double3.Normalize(d + double3.Normalize(double3.Cross(axis, d)) * (5000.0 / session.Generated.Params.Radius));
    session.BeginStroke();
    session.Dab(d, 10_000.0, 1500f, AuthoredBrush.Raise, 1f);
    session.EndStroke();
    var site = session.AddSite(slope, 600f, 400f);
    session.AddMarker("city", "Newport", site);
    session.Save();
}

static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

static string? FindData(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "data", "planets", "kinds")) && File.Exists(Path.Combine(dir.FullName, "BRUTALGame.slnx")))
            return Path.Combine(dir.FullName, "data");
    }

    return null;
}
