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
var benchSolids = 0;
var benchMeshes = false;
var benchSurfaceNets = false;
var demoMesh = false;
var demoLayered = false;
string[]? cutaway = null;
double[]? view = null;
string? script = null;

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
    else if (args[i] == "--demo-mesh")
        demoMesh = true;
    else if (args[i] == "--demo-layered")
        demoLayered = true;
    else if (args[i] == "--cutaway" && i + 2 < args.Length)
        cutaway = [args[++i], args[++i]];
    else if (args[i] == "--input-script" && i + 1 < args.Length)
        script = args[++i];
    else if (args[i] == "--view" && i + 3 < args.Length)
        view = [double.Parse(args[++i], CultureInfo.InvariantCulture), double.Parse(args[++i], CultureInfo.InvariantCulture), double.Parse(args[++i], CultureInfo.InvariantCulture)];
    else if (args[i] == "--bench-meshes")
        benchMeshes = true;
    else if (args[i] == "--bench-surface-nets")
        benchSurfaceNets = true;
    else if (args[i] == "--frames" && i + 1 < args.Length)
        frames = int.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--bake-bench")
        benchSolids = i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? int.Parse(args[++i], CultureInfo.InvariantCulture) : 100;
}

if (benchSolids > 0)
{
    BakeBench(data, benchSolids, benchMeshes, benchSurfaceNets);
    return;
}

using var app = new DesignerApp(data, load, demo, demoMesh, saveOnExit, frames, view, script, demoLayered, cutaway);
app.Run();

// --bake-bench [n] [--bench-meshes] [--bench-surface-nets]: headless. Cold bakes of the n-solid sample hull (a fresh
// baker each run, all bricks), then one-solid edits on a warm baker (only touched bricks re-bake), on the background
// worker budget and on one worker. --bench-meshes converts every 5th solid to a mesh solid with one Catmull-Clark level;
// --bench-surface-nets meshes with surface nets instead of dual contouring, for comparison.
static void BakeBench(string data, int count, bool meshes, bool surfaceNets)
{
    const int runs = 9;
    var catalog = HullMaterialCatalog.Load(data);
    var session = new DesignSession();
    SampleHulls.Bench(session, count, catalog.All[0].Id, catalog.All[1].Id);

    if (meshes)
        SampleHulls.MeshifyEvery(session, 5, 1);

    var doc = session.Doc;
    var densities = catalog.Densities(doc);
    var log = new ValidationLog();
    PlanValidator.Validate(doc, log, catalog);
    Console.WriteLine($"bake-bench: {count} solids ({doc.Meshes.Count} mesh solids), {(surfaceNets ? "surface nets" : "dual contouring")}, validation {log.Errors} errors {log.Warnings} warnings, workers {WorkerBudget.Background}");

    foreach (var workers in new[] { WorkerBudget.Background, 1 })
    {
        var cold = new float[runs];
        var prepare = new float[runs];
        var cpu = new float[runs];
        HullBaker? last = null;

        for (var r = 0; r < runs; r++)
        {
            last?.Dispose();
            last = new HullBaker(workers) { Mesher = surfaceNets ? HullMesher.SurfaceNets : HullMesher.DualContouring };
            last.Start(doc.Solids.AsSpan(), doc.Meshes, densities);
            last.Wait();
            prepare[r] = last.LastPrepareMs;
            cold[r] = last.LastBakeMs;
            cpu[r] = last.LastCpuMs;
        }

        var edits = new float[runs];
        var touched = new int[runs];
        var edited = doc.Clone();

        for (var r = 0; r < runs; r++)
        {
            ref var solid = ref edited.Solids[r * 7 % edited.Solids.Count];
            solid.PosY += HullSolid.PositionUnitsPerMetre / 4;
            last!.Start(edited.Solids.AsSpan(), edited.Meshes, densities);
            last.Wait();
            edits[r] = last.LastBakeMs;
            touched[r] = last.LastRebaked;
        }

        var mass = last!.Mass;
        Console.WriteLine($"  workers {workers}: cold bake median {Median(cold):F2} ms wall (min {cold.Min():F2}), cpu {Median(cpu):F2} ms, {last.Bricks.Count} bricks, {last.TriangleCount} tris, max {last.MaxSolidsPerBrick} solids/brick");
        Console.WriteLine($"  workers {workers}: one-solid edit median {Median(edits):F2} ms wall, {touched.Min()}-{touched.Max()} bricks re-baked");
        Console.WriteLine($"  mass {mass.MassKg / 1000.0:F2} t, volume {mass.VolumeM3:F1} m3, mesh prepare (main thread, first run) {prepare[0]:F2} ms");
        last.Dispose();
    }
}

static float Median(float[] values)
{
    var sorted = (float[])values.Clone();
    Array.Sort(sorted);
    return sorted[sorted.Length / 2];
}
