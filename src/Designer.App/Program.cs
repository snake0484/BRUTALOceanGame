using System.Globalization;
using Brutal.Logging;
using BrutalGame.Designer;
using BrutalGame.Designer.App;
using BrutalGame.Engine.Jobs;
using BrutalGame.Plans;
using BrutalGame.Ships;
using Microsoft.Extensions.Logging;

LogSystem.Initialize(builder => builder.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

var data = Path.Combine(AppContext.BaseDirectory, "data");
string? load = null;
string? saveOnExit = null;
var demo = false;
var frames = 0;
var benchBrushes = 0;

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
    else if (args[i] == "--bake-bench")
        benchBrushes = i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? int.Parse(args[++i], CultureInfo.InvariantCulture) : 100;
}

if (benchBrushes > 0)
{
    BakeBench(data, benchBrushes);
    return;
}

using var app = new DesignerApp(data, load, demo, saveOnExit, frames);
app.Run();

// --bake-bench [n]: headless. Cold bakes of the n-brush sample hull (a fresh baker each run, all bricks), then one-brush
// edits on a warm baker (only touched bricks re-bake), on the background worker budget and on one worker.
static void BakeBench(string data, int count)
{
    const int runs = 9;
    var catalog = HullMaterialCatalog.Load(data);
    var session = new DesignSession();
    SampleHulls.Bench(session, count, catalog.All[0].Id, catalog.All[1].Id);
    var doc = session.Doc;
    var densities = catalog.Densities(doc);
    var log = new ValidationLog();
    PlanValidator.Validate(doc, log, catalog);
    Console.WriteLine($"bake-bench: {count} brushes, validation {log.Errors} errors {log.Warnings} warnings, workers {WorkerBudget.Background}");

    foreach (var workers in new[] { WorkerBudget.Background, 1 })
    {
        var cold = new float[runs];
        var cpu = new float[runs];
        HullBaker? last = null;

        for (var r = 0; r < runs; r++)
        {
            last?.Dispose();
            last = new HullBaker(workers);
            last.Start(doc.Brushes.AsSpan(), densities);
            last.Wait();
            cold[r] = last.LastBakeMs;
            cpu[r] = last.LastCpuMs;
        }

        var edits = new float[runs];
        var touched = new int[runs];
        var edited = doc.Clone();

        for (var r = 0; r < runs; r++)
        {
            ref var brush = ref edited.Brushes[r * 7 % edited.Brushes.Count];
            brush.PosY += HullBrush.PositionUnitsPerMetre / 4;
            last!.Start(edited.Brushes.AsSpan(), densities);
            last.Wait();
            edits[r] = last.LastBakeMs;
            touched[r] = last.LastRebaked;
        }

        var mass = last!.Mass;
        Console.WriteLine($"  workers {workers}: cold bake median {Median(cold):F2} ms wall (min {cold.Min():F2}), cpu {Median(cpu):F2} ms, {last.Bricks.Count} bricks, {last.TriangleCount} tris, max {last.MaxBrushesPerBrick} brushes/brick");
        Console.WriteLine($"  workers {workers}: one-brush edit median {Median(edits):F2} ms wall, {touched.Min()}-{touched.Max()} bricks re-baked");
        Console.WriteLine($"  mass {mass.MassKg / 1000.0:F2} t, volume {mass.VolumeM3:F1} m3");
        last.Dispose();
    }
}

static float Median(float[] values)
{
    var sorted = (float[])values.Clone();
    Array.Sort(sorted);
    return sorted[sorted.Length / 2];
}
