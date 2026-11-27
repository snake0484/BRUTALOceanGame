using Brutal.Numerics;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Designer.App;

/// <summary>
/// <c>--bench-paint</c>: the brushes demo from a fixed view, cycled through paint loads in ABAB order (two rounds), the hull
/// draw's GPU time averaged per state after a warm-up, then printed and closed. States: material colours with no decals;
/// the hull's base paint; base paint and N decals (images and paint shapes, 100 unless given) spread over the hull; base
/// paint and N decals stacked on one spot (every fragment there tests and lays all of them).
/// </summary>
public sealed partial class DesignerApp
{
    private const int BenchWarmFrames = 90;
    private const int BenchFrames = 360;
    private static string[] BenchStates => ["materials, no decals", "base paint", $"base paint + {BenchDecals} decals spread", $"base paint + {BenchDecals} decals stacked"];

    /// <summary>--bench-paint [decals].</summary>
    public static bool PaintBench;

    /// <summary>How many decals the decal states lay (100 unless given).</summary>
    public static int BenchDecals = 100;

    private int _benchStep = -1;
    private int _benchFrame;
    private double _benchHullSum;
    private double _benchFrameSum;
    private int _benchFrameScope = -1;
    private HullPaintSet? _benchPaint;
    private readonly double[,] _benchHull = new double[2, 4];
    private readonly double[,] _benchTotal = new double[2, 4];

    private void BenchTick()
    {
        if (_benchStep < 0)
        {
            // Wait for the demo to settle, then remember its paints (the base paint state needs a hull slot).
            if (Frames < 30 || _baker.Busy || _surfaceBuilder.Busy)
                return;

            _benchPaint = _session.Doc.Paint;

            for (var s = 0; s < Renderer.GpuTimer.ScopeCount; s++)
            {
                if (Renderer.GpuTimer.Name(s) == "Frame")
                    _benchFrameScope = s;
            }

            BenchApply(0);
            return;
        }

        if (_baker.Busy || _surfaceBuilder.Busy)
            return;

        if (++_benchFrame <= BenchWarmFrames)
            return;

        _benchHullSum += Renderer.GpuTimer.Ring(_hullScope).Last;
        _benchFrameSum += _benchFrameScope >= 0 ? Renderer.GpuTimer.Ring(_benchFrameScope).Last : 0;

        if (_benchFrame < BenchWarmFrames + BenchFrames)
            return;

        var (round, state) = (_benchStep / 4, _benchStep % 4);
        _benchHull[round, state] = _benchHullSum / BenchFrames;
        _benchTotal[round, state] = _benchFrameSum / BenchFrames;
        Console.WriteLine($"bench-paint round {round + 1} {BenchStates[state]}: hull {_benchHull[round, state]:F3} ms, frame {_benchTotal[round, state]:F3} ms gpu ({_hull.Projectors} boxes, {_hull.ProjectorRefs} list entries)");

        if (_benchStep + 1 < 8)
        {
            BenchApply(_benchStep + 1);
            return;
        }

        var extent = Renderer.OutputExtent;
        Console.WriteLine($"bench-paint summary ({extent.Width}x{extent.Height}, {_hull.BrickCount} bricks), hull draw gpu ms, mean of two rounds (A, B):");

        for (var s = 0; s < 4; s++)
            Console.WriteLine($"  {BenchStates[s],-34} {(_benchHull[0, s] + _benchHull[1, s]) * 0.5:F3}  ({_benchHull[0, s]:F3}, {_benchHull[1, s]:F3})  frame {(_benchTotal[0, s] + _benchTotal[1, s]) * 0.5:F3}");

        Window.ShouldClose = true;
    }

    private void BenchApply(int step)
    {
        (_benchStep, _benchFrame, _benchHullSum, _benchFrameSum) = (step, 0, 0, 0);
        var state = step % 4;
        var paint = _benchPaint?.Clone() ?? new HullPaintSet();

        if (state == 0)
            paint.Hull = HullPaintSet.None;

        var decals = state < 2 ? null : BenchDecalSet(stacked: state == 3);
        _session.Edit(s =>
        {
            s.Apply(PlanOp.SetPaint(s.Doc.Paint, paint.IsEmpty ? null : paint));
            s.Apply(PlanOp.SetDecals(s.Doc.Decals, decals));
        });
    }

    /// <summary>N decals cast onto the hull from around it (seeded): images and paint shapes, 1-2 m; or all on one spot.</summary>
    private HullDecalSet BenchDecalSet(bool stacked)
    {
        var doc = _session.Doc;
        var surface = new HullSurface(doc.Solids.AsSpan(), doc.Meshes);
        var random = new Random(1234);
        var set = new HullDecalSet();
        var centre = new Vector3d(0, 4, 0);
        var slots = doc.Paint?.Slots.Count ?? 0;
        var id = doc.Meta.NextId + 1000;

        for (var tries = 0; set.Decals.Count < BenchDecals && tries < BenchDecals * 20; tries++)
        {
            Vector3d dir;

            if (stacked)
            {
                dir = HullSurface.Normalise(new Vector3d((random.NextDouble() - 0.5) * 0.02, 1, (random.NextDouble() - 0.5) * 0.02), new Vector3d(0, 1, 0));
            }
            else
            {
                var z = random.NextDouble() * 2 - 1;
                var a = random.NextDouble() * Math.Tau;
                var r = Math.Sqrt(1 - z * z);
                dir = new Vector3d(r * Math.Cos(a), z, r * Math.Sin(a));
            }

            var from = HullSurface.Add(centre, HullSurface.Scale(dir, 40));

            if (!surface.Raycast(from, HullSurface.Scale(dir, -1), 80, out var t, out var normal))
                continue;

            var at = HullSurface.Add(from, HullSurface.Scale(dir, -t));
            var i = set.Decals.Count;
            var size = (ushort)(stacked ? 3000 : 1000 + random.Next(1000));
            var decal = new HullDecal
            {
                Id = id++,
                Roll = (byte)random.Next(24),
                WidthMm = size,
                HeightMm = size,
                Shape = slots == 0 ? DecalShape.Image : (DecalShape)(i % 3 == 0 ? 0 : 1 + i % 4),
                Paint = (byte)(slots == 0 ? 0 : i % slots),
                Asset = (i % 2 == 0 ? DefId.FromName("decal.emblem") : DefId.FromName("decal.roundel")).Value,
                Opacity = 220,
            };
            (decal.X, decal.Y, decal.Z) = ComponentCommands.Quantise(at);
            decal.SetNormal(normal);
            set.Decals.Add(decal);
        }

        return set;
    }
}
