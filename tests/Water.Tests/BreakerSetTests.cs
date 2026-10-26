using System.Diagnostics;
using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

public sealed class BreakerSetTests(ITestOutputHelper output)
{
    private const int N = CoastalField.Resolution;
    private const float Cell = CoastalField.CellSize;

    /// <summary>Plane beach rising along +x at 1:20 (or the given slope): shoreline at x = 800 m (clear of the map's edge fade).</summary>
    private static float Depth(float x, float slope = 0.05f) => slope * (800f - x);

    /// <summary>The 1:20 wind-sea beach spills (ξ ≈ 0.3); see <see cref="PlungingBeachForProbe"/> for one that curls.</summary>
    internal static OceanSwell BeachForProbe(float direction) => Beach(direction);

    /// <summary>
    /// A 1:12 beach under a 3 m, 12 s groundswell and light wind: the coherent surf wave (2–4 m with sets) breaks in 3–5 m
    /// of water, 35–60 m out, at ξ ≈ 0.6–0.8, so crests curl and throw lips. A 1:8 beach under the 16 m/s wind sea plunged
    /// too, but its surf wave broke 9 m (two texels) from the shoreline, too cramped for crest chains.
    /// </summary>
    internal static OceanSwell PlungingBeachForProbe(float direction) => Beach(direction, windSpeed: 8f, slope: 0.08f, swellHeight: 3f, swellPeriod: 12f);

    /// <summary>The island's 1:50 beach under a 20 m/s wind sea: 4 m faces on a 50 m wavelength, steep enough to stand near vertical before they break.</summary>
    internal static OceanSwell SteepBeachForProbe(float direction) => Beach(direction, windSpeed: 20f, slope: 0.02f);

    /// <summary>The game's own wind 20 scene: the 20 m/s wind sea with the default 1.2 m, 13 s groundswell under it (not the carrier: a long random residual), on the 1:50 beach.</summary>
    internal static OceanSwell StormBeachForProbe(float direction) => Beach(direction, windSpeed: 20f, slope: 0.02f, swellHeight: 1.2f, swellPeriod: 13f);

    private static OceanSwell Beach(float direction, float windSpeed = 16f, float slope = 0.05f, float swellHeight = 0f, float swellPeriod = 12f) =>
        Build((x, _) => Depth(x, slope), direction, windSpeed, swellHeight, swellPeriod);

    /// <summary>
    /// The storm scene (20 m/s with the 1.2 m, 13 s swell) on a 1:50 beach whose shoreline bows 40 m along the coast and
    /// carries, over 180 m of it, a 36 m skewed bar 1.6 m deep in about 2.8 m of water with a 5 m trough behind it, like
    /// the surf test's bar: the trough is deeper than the surf mask's bound, so crest chains split there, and a crest's
    /// length inside the mask grows as it comes in over the bowed band, so chains are resampled. For the lip seed tests.
    /// </summary>
    internal static OceanSwell BarBeachForProbe(float direction) => Build(BarDepth, direction, 20f, 1.2f, 13f);

    private static float BarDepth(float x, float z)
    {
        var shore = 820f + 40f * MathF.Cos(MathF.Tau * z / (N * Cell));
        var depth = 0.02f * (shore - x);
        var along = BreakerFaces.Smooth((90f - MathF.Abs(z - 512f)) / 20f);
        var bar = (x - 640f - 0.35f * (z - 512f)) / 12f;
        var trough = (x - 690f - 0.35f * (z - 512f)) / 15f;
        return depth - MathF.Max(depth - 1.6f, 0f) * MathF.Exp(-bar * bar) * along + 5f * MathF.Exp(-trough * trough) * along;
    }

    private static OceanSwell Build(Func<float, float, float> depth, float direction, float windSpeed, float swellHeight, float swellPeriod)
    {
        var depths = new float[N * N];
        for (var z = 0; z < N; z++)
            for (var x = 0; x < N; x++)
                depths[x + z * N] = depth((x + 0.5f) * Cell, (z + 0.5f) * Cell);

        using var builder = new CoastalBuilder();
        builder.TryStart(depths, Vector2.Zero, direction);

        var timer = Stopwatch.StartNew();
        while (!builder.Poll())
        {
            Assert.True(timer.Elapsed.TotalSeconds < 30, "coastal build timed out");
            Thread.Sleep(1);
        }

        var swell = new OceanSwell(5);
        swell.SetWind(new SwellParams { WindSpeed = windSpeed, WindDirection = direction, SwellHeight = swellHeight, SwellPeriod = swellPeriod, SwellDirection = direction });
        swell.Coastal = builder.Current;
        return swell;
    }

    [Fact]
    public void HeadOnSwell_GivesShoreParallelCrestsThatBreakInShallows()
    {
        var swell = Beach(0f);
        swell.SetTime(100.0);
        var set = new BreakerSet();
        set.Update(swell, 1f / 60f);

        output.WriteLine($"breakers {set.Count}, surf texels {set.SurfTexels}, {set.LastUpdateMs:F3} ms, Hs {swell.SignificantWaveHeight:F2} m, Tp {swell.PeakPeriod:F1} s");
        Assert.True(set.Count > 0);

        for (var i = 0; i < set.Count; i++)
        {
            var b = set[i];
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var minZ = float.MaxValue;
            var maxZ = float.MinValue;

            for (var v = 0; v < b.Count; v++)
            {
                var p = b.Vertices[v].Position;
                (minX, maxX, minZ, maxZ) = (MathF.Min(minX, p.X), MathF.Max(maxX, p.X), MathF.Min(minZ, p.Y), MathF.Max(maxZ, p.Y));

                if (b.Vertices[v].Stage is BreakerStage.Curling or BreakerStage.Plunging or BreakerStage.Bore)
                    Assert.True(b.Vertices[v].Cap < 1f);
            }

            output.WriteLine($"  crest {b.Crest}: {b.Count} vertices, x {minX:F0}..{maxX:F0} (depth {Depth(minX):F1}..{Depth(maxX):F1}), z span {maxZ - minZ:F0}, type {b.Type}");
            Assert.True(maxX - minX < 0.1f * (maxZ - minZ) + 2f * Cell, "head-on crests should run along the shore");
        }
    }

    [Fact]
    public void Crests_KeepTheirNumberAndMoveShoreward()
    {
        var swell = Beach(0f);
        var set = new BreakerSet();
        swell.SetTime(100.0);
        set.Update(swell, 0.5f);

        var crest = set[0].Crest;
        var before = MeanX(set[0]);

        swell.SetTime(100.5);
        set.Update(swell, 0.5f);

        var found = false;
        for (var i = 0; i < set.Count; i++)
        {
            if (set[i].Crest != crest)
                continue;

            var after = MeanX(set[i]);
            output.WriteLine($"crest {crest}: x {before:F1} -> {after:F1} in 0.5 s");
            Assert.True(after > before, "crest should travel toward the shore (+x)");
            Assert.True(after - before < 10f, "shallow-water speed is a few m/s");
            found = true;
        }

        Assert.True(found, "crest number should persist between ticks");
    }

    [Fact]
    public void ObliqueSwell_PeelsAlongTheCrest()
    {
        var swell = Beach(MathF.PI / 5f);
        var set = new BreakerSet();
        var peeling = 0;

        for (var t = 0; t < 30; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
        }

        for (var i = 0; i < set.Count; i++)
        {
            var b = set[i];
            output.WriteLine($"crest {b.Crest}: break index {b.BreakIndex}, peel dir {b.PeelDirection}, peel {b.PeelSpeed:F2} m/s, type {b.Type}");
            if (b.BreakIndex >= 0 && b.PeelSpeed > 0f)
                peeling++;
        }

        Assert.True(peeling > 0, "an oblique swell should leave break points that move along the crest");
    }

    [Fact]
    public void SteadyStateCost()
    {
        var swell = Beach(MathF.PI / 5f);
        var set = new BreakerSet();
        var total = 0f;
        var max = 0f;

        for (var t = 0; t < 200; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);

            if (t < 20)
                continue;

            total += set.LastUpdateMs;
            max = MathF.Max(max, set.LastUpdateMs);
        }

        output.WriteLine($"breakers {set.Count}, surf texels {set.SurfTexels}, avg {total / 180f:F3} ms, max {max:F3} ms");
    }

    /// <summary>
    /// A crest's face lift must grow gradually as it shoals into the surf zone, not pop: from 10% to 90% of its peak
    /// takes at least a second of travel.
    /// </summary>
    [Fact]
    public void FaceLift_GrowsGradually()
    {
        var swell = Beach(0f);
        var set = new BreakerSet();
        var peak = new Dictionary<long, List<(double T, float A)>>();
        var present = new HashSet<long>();

        for (var t = 0; t < 900; t++)
        {
            var time = 100.0 + t / 60.0;
            swell.SetTime(time);
            set.Update(swell, 1f / 60f);

            for (var i = 0; i < set.Count; i++)
            {
                var b = set[i];

                // Crests already in the surf zone when sampling starts show no rise; judge only crests that come in.
                if (t == 0)
                    present.Add(b.Crest);

                if (!peak.TryGetValue(b.Crest, out var series))
                    peak[b.Crest] = series = new List<(double, float)>();

                series.Add((time, CrestLift(swell, set, b)));
            }
        }

        var worst = double.MaxValue;
        foreach (var (crest, series) in peak)
        {
            var top = series.Max(p => p.A);
            if (top < 0.05f || present.Contains(crest))
                continue;

            var t10 = series.First(p => p.A >= 0.1f * top).T;
            var t90 = series.First(p => p.A >= 0.9f * top).T;
            output.WriteLine($"crest {crest}: peak lift {top:F2} m, 10-90% rise {t90 - t10:F2} s");
            worst = Math.Min(worst, t90 - t10);
        }

        Assert.True(worst >= 1.0, $"fastest rise {worst:F2} s");
    }

    /// <summary>Largest face amplitude along this crest's vertices (BreakerFace.CrestStateAt, the lift the face adds over the heightfield).</summary>
    private static float CrestLift(OceanSwell swell, BreakerSet set, Breaker crest)
    {
        var max = 0f;
        for (var v = 0; v < crest.Count; v++)
        {
            ref readonly var p = ref crest.Vertices[v];
            max = MathF.Max(max, BreakerFace.CrestStateAt(swell, set, p.Position.X, p.Position.Y).Amplitude);
        }

        return max;
    }

    /// <summary>
    /// Surf coherence moves peak-band energy into the carrier without changing the total the surface carries, where
    /// nothing breaks yet (8–10 m: the carrier has formed but breaks only nearer the shore, at its own limit). Sets
    /// modulate the carrier, so this holds averaged over one set (a group of 1 / GroupRatio waves).
    /// </summary>
    [Theory]
    [InlineData(600f)]
    [InlineData(620f)]
    [InlineData(640f)]
    public void Coherence_ConservesVariance(float x)
    {
        const int samples = 64;
        var swell = Beach(0f);
        var groupPeriod = swell.PeakPeriod / swell.GroupRatio;
        var with = 0f;
        var without = 0f;

        for (var k = 0; k < samples; k++)
        {
            swell.SetTime(100.0 + k * groupPeriod / samples);
            swell.CoherenceEnabled = true;
            with += swell.SurfVariance(x, 512f) / samples;
            swell.CoherenceEnabled = false;
            without += swell.SurfVariance(x, 512f) / samples;
        }

        output.WriteLine($"x {x} (depth {Depth(x):F1} m): sum A^2 {with:F4} with coherence, {without:F4} without");
        Assert.InRange(with / without, 0.95f, 1.05f);
    }

    /// <summary>
    /// With coherence the drawn surface has its crests where the breakers are: a local maximum across the crest. The carrier
    /// takes at most 75% of the peak band so the surf keeps some irregularity; lips shrink away where they miss a crest.
    /// </summary>
    [Fact]
    public void Coherence_PutsDrawnCrestsUnderBreakers()
    {
        var with = CrestHitRate(coherent: true);
        var without = CrestHitRate(coherent: false);

        output.WriteLine($"breaker vertices on a drawn crest: {with:P0} with coherence, {without:P0} without");
        Assert.True(with > 0.65f, $"only {with:P0} of breaker vertices sit on a drawn crest");
        Assert.True(with > without + 0.2f, "coherence should be what puts the crests there");
    }

    /// <summary>
    /// Share of breaker vertices whose drawn crest (highest surface across the crest within a quarter carrier wavelength
    /// either side) lies within an eighth of a wavelength of the vertex.
    /// </summary>
    private static float CrestHitRate(bool coherent)
    {
        const int steps = 16;
        var swell = Beach(0f);
        swell.CoherenceEnabled = coherent;
        var set = new BreakerSet();
        var hits = 0;
        var total = 0;

        for (var t = 0; t < 40; t++)
        {
            swell.SetTime(100.0 + t * 0.37);
            set.Update(swell, 0.37f);

            for (var i = 0; i < set.Count; i++)
            {
                var b = set[i];
                for (var v = 1; v + 1 < b.Count; v += 4)
                {
                    var vertex = b.Vertices[v];
                    if (vertex.Stage == BreakerStage.Shoaling || vertex.Depth < 0.5f)
                        continue;

                    var along = b.Vertices[v + 1].Position - b.Vertices[v - 1].Position;
                    var n = Vector2.Normalize(new Vector2(-along.Y, along.X));
                    var p = vertex.Position;
                    var omega = swell.PeakOmega;
                    var k = omega * omega / (9.81f * MathF.Sqrt(MathF.Tanh(omega * omega * vertex.Depth / 9.81f)));
                    var quarter = MathF.PI / (2f * k);
                    var best = 0f;
                    var highest = float.MinValue;

                    for (var s = -steps; s <= steps; s++)
                    {
                        var d = quarter * s / steps;
                        var h = swell.SampleHeight(p.X + n.X * d, p.Y + n.Y * d);
                        if (h > highest)
                            (highest, best) = (h, d);
                    }

                    total++;
                    if (MathF.Abs(best) <= 0.5f * quarter)
                        hits++;
                }
            }
        }

        return total > 0 ? hits / (float)total : 0f;
    }

    /// <summary>
    /// While a field fades in, the surf keeps its own rhythm: the surface is evaluated per field and blended, so the lag
    /// (tens of radians in the surf zone) is never swept across the fade, which made the waves race or run backwards.
    /// </summary>
    [Fact]
    public void FadingIn_DoesNotSweepThePhase()
    {
        const double fadeSeconds = 1.5;
        const double step = 1.0 / 120.0;
        var swell = Beach(0f);
        var field = swell.Coastal;
        var crossings = 0;
        var last = 0f;

        for (var t = 0.0; t <= fadeSeconds; t += step)
        {
            swell.SetTime(100.0 + t);
            swell.CoastalPrevious = null;
            swell.CoastalFade = (float)(t / fadeSeconds);
            swell.Coastal = field;

            var h = swell.SampleHeight(760f, 512f);
            if (t > 0.0 && MathF.Sign(h) != MathF.Sign(last))
                crossings++;

            last = h;
        }

        // At most two zero crossings per peak period, plus the shorter components riding on it.
        var allowed = 2.0 * fadeSeconds / swell.PeakPeriod * 2.0 + 4.0;
        output.WriteLine($"zero crossings over the fade: {crossings} (allowed {allowed:F1}, Tp {swell.PeakPeriod:F1} s)");
        Assert.True(crossings <= allowed);
    }

    /// <summary>
    /// Breaking moves smoothly: following each crest tick to tick, the cap (which sets curl, lip and whitewater) at a crest
    /// vertex differs little from the cap at the nearest vertex of the same crest a tick earlier.
    /// </summary>
    [Fact]
    public void BreakingCap_ChangesSmoothlyTickToTick()
    {
        const float dt = 1f / 60f;
        var swell = Beach(MathF.PI / 5f);
        var set = new BreakerSet();
        var before = new List<(long Crest, Vector2 Position, float Cap)>();
        var now = new List<(long Crest, Vector2 Position, float Cap)>();
        var jumps = new List<float>();

        for (var t = 0; t < 240; t++)
        {
            swell.SetTime(100.0 + t * dt);
            set.Update(swell, dt);
            now.Clear();

            for (var i = 0; i < set.Count; i++)
                for (var v = 0; v < set[i].Count; v++)
                    now.Add((set[i].Crest, set[i].Vertices[v].Position, set[i].Vertices[v].Cap));

            foreach (var (crest, position, cap) in now)
            {
                var nearest = float.MaxValue;
                var previous = 0f;
                foreach (var (oldCrest, oldPosition, oldCap) in before)
                {
                    var d = Vector2.DistanceSquared(position, oldPosition);
                    if (oldCrest != crest || d >= nearest)
                        continue;

                    (nearest, previous) = (d, oldCap);
                }

                if (nearest < 1f)
                    jumps.Add(MathF.Abs(cap - previous));
            }

            (before, now) = (now, before);
        }

        jumps.Sort();
        var p99 = jumps[(int)(0.99 * (jumps.Count - 1))];
        output.WriteLine($"{jumps.Count} vertex pairs: cap change per tick median {jumps[jumps.Count / 2]:F4}, p99 {p99:F4}, max {jumps[^1]:F4}");
        Assert.True(p99 < 0.02f, $"p99 cap jump {p99:F3} per tick");
    }

    /// <summary>
    /// The break point moves along the crest at the peel speed, smoothly: tick to tick it moves no faster than a few times
    /// the expected peel (celerity / tan of the crest's angle to the shore, about 7 m/s here).
    /// </summary>
    [Fact]
    public void BreakPoint_MovesSmoothly()
    {
        const float dt = 1f / 60f;
        var swell = Beach(MathF.PI / 5f);
        var set = new BreakerSet();
        var last = new List<(long Crest, Vector2 Point)>();
        var speeds = new List<float>();

        for (var t = 0; t < 240; t++)
        {
            swell.SetTime(100.0 + t * dt);
            set.Update(swell, dt);
            var seen = new List<(long Crest, Vector2 Point)>();

            for (var i = 0; i < set.Count; i++)
            {
                var b = set[i];
                if (b.BreakIndex < 0)
                    continue;

                // A crest split into pieces shares its number: compare with the nearest break point of that crest.
                seen.Add((b.Crest, b.BreakPoint));
                var nearest = float.MaxValue;
                foreach (var (crest, point) in last)
                    if (crest == b.Crest)
                        nearest = MathF.Min(nearest, Vector2.Distance(b.BreakPoint, point));

                if (nearest < float.MaxValue)
                    speeds.Add(nearest / dt);
            }

            last = seen;
        }

        speeds.Sort();
        var p90 = speeds[(int)(0.9 * (speeds.Count - 1))];
        output.WriteLine($"{speeds.Count} samples: break point speed median {speeds[speeds.Count / 2]:F1} m/s, p90 {p90:F1}, max {speeds[^1]:F1}");
        Assert.True(p90 < 30f, $"p90 break point speed {p90:F0} m/s");
    }

    private static float MeanX(Breaker b)
    {
        var sum = 0f;
        for (var v = 0; v < b.Count; v++)
            sum += b.Vertices[v].Position.X;

        return sum / b.Count;
    }
}
