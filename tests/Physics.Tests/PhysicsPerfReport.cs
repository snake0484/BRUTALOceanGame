using System.Diagnostics;
using System.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Physics.Tests;

/// <summary>Perf report only, no timing assertions. Read the numbers in the detailed test output.</summary>
public class PhysicsPerfReport(ITestOutputHelper output)
{
    const int ChunksX = 40;
    const int ChunksZ = 25;
    const int Quads = 32;
    const float Spacing = 1f;
    const int Spheres = 200;
    const int TotalSteps = 600;
    const int PhaseSteps = 120;
    const int Rays = 1000;
    const float Dt = 1f / 60f;

    [Fact]
    [Trait("Category", "Perf")]
    public void StepTime1000StaticMeshes200Spheres()
    {
        long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        var shapes = new StaticMeshShape?[ChunksX * ChunksZ];
        var build = Stopwatch.StartNew();

        Parallel.For(0, shapes.Length, i =>
        {
            var (vertices, indices) = TerrainMesh.Grid(Quads, Spacing, i * 0.37f);
            shapes[i] = StaticMeshShape.Create(vertices, indices);
        });

        build.Stop();
        long bvhBytes = 0;

        using var world = new PhysicsWorld();
        var add = Stopwatch.StartNew();

        for (int i = 0; i < shapes.Length; i++)
        {
            var shape = shapes[i]!;
            bvhBytes += shape.Bvh?.MemoryBytes ?? 0;
            world.AddStatic(shape, new Vector3(i % ChunksX * Quads * Spacing, 0f, i / ChunksX * Quads * Spacing));
            shape.Dispose();
        }

        add.Stop();
        var rng = new Random(1234);
        float extentX = ChunksX * Quads * Spacing;
        float extentZ = ChunksZ * Quads * Spacing;

        for (int i = 0; i < Spheres; i++)
            world.AddDynamicSphere(new Vector3(rng.NextSingle() * extentX, 5f + rng.NextSingle() * 10f, rng.NextSingle() * extentZ), 0.5f);

        GC.Collect();
        long privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;
        var samples = new float[TotalSteps];

        for (int i = 0; i < TotalSteps; i++)
        {
            world.Step(Dt);
            samples[i] = world.LastStepMs;
        }

        for (int pass = 0; pass < 3; pass++)
        {
            for (int i = 0; i < 5000; i++)
                world.CastRay(new Vector3(rng.NextSingle() * extentX, 50f, rng.NextSingle() * extentZ), -Vector3.UnitY, 100f);

            Thread.Sleep(200);
        }

        var rayTimer = Stopwatch.StartNew();
        int rayHits = 0;

        for (int i = 0; i < Rays; i++)
        {
            if (world.CastRay(new Vector3(rng.NextSingle() * extentX, 50f, rng.NextSingle() * extentZ), -Vector3.UnitY, 100f).Hit)
                rayHits++;
        }

        rayTimer.Stop();

        output.WriteLine($"native mesh={JoltRuntime.NativeMeshShape} native ray={JoltRuntime.NativeRayCast} workers={world.WorkerThreads} cores={Environment.ProcessorCount} bodies={world.BodyCount} tris/mesh={shapes[0]!.TriangleCount}");
        output.WriteLine($"build {shapes.Length} meshes (parallel): {build.Elapsed.TotalMilliseconds:F0} ms; add statics: {add.Elapsed.TotalMilliseconds:F0} ms; private bytes +{(privateAfter - privateBefore) / (1024 * 1024)} MB (managed ray BVH {bvhBytes / (1024 * 1024)} MB)");
        output.WriteLine($"step 0 (incl. OptimizeBroadPhase): {samples[0]:F2} ms");
        Report("steps 1-120 (falling + impacts)", samples.AsSpan(1, PhaseSteps));
        Report("steps 120-240", samples.AsSpan(PhaseSteps, PhaseSteps));
        Report("steps 480-600 (settled)", samples.AsSpan(TotalSteps - PhaseSteps, PhaseSteps));
        output.WriteLine($"{Rays} rays: {rayTimer.Elapsed.TotalMilliseconds:F2} ms ({rayTimer.Elapsed.TotalMilliseconds * 1000.0 / Rays:F1} us/ray, {rayHits} hits)");
    }

    void Report(string label, Span<float> phase)
    {
        var sorted = phase.ToArray();
        Array.Sort(sorted);
        output.WriteLine($"{label}: avg {sorted.Average():F3} p50 {sorted[sorted.Length / 2]:F3} p95 {sorted[sorted.Length * 95 / 100]:F3} max {sorted[^1]:F3} ms");
    }
}
