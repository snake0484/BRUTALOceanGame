using Brutal.Numerics;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;
using Xunit.Abstractions;

namespace BrutalGame.Sim.Tests;

/// <summary>Per-tile swells from the weather: a pure function of seed, tile and tick, and smooth through a wind change.</summary>
public sealed class SeaSwellsTests(ITestOutputHelper output)
{
    private const double Radius = 1_000_000.0;
    private const double Dt = 1.0 / 60.0;

    private static readonly SeaTiles Tiles = new(Radius, double3.UnitY);
    private static readonly SphereWeatherField Field = new(PlanetClimate.Rocky(7, Radius, 6 * 3600.0, double3.UnitY), Dt);
    private static readonly SeaWindSchedule Winds = new(Field, Tiles);

    private static SeaSwells Swells(float groundswell = 1f)
    {
        var swells = new SeaSwells(3, Tiles, Dt, Winds);
        swells.SetBase(new SwellParams { SwellHeight = groundswell, SwellPeriod = 12f }, driven: true);
        return swells;
    }

    /// <summary>The first epoch after <paramref name="from"/> in which the tile's key changes.</summary>
    private static long Change(int tile, long from)
    {
        for (var e = from; e < from + 2000; e++)
        {
            if (Winds.At(tile, e) != Winds.At(tile, e - 1))
                return e;
        }

        throw new InvalidOperationException("no wind change");
    }

    private static double3 Point(int tile, double u, double v)
    {
        Tiles.Axes(tile, out var east, out var up, out var south);
        return double3.Normalize(up * Radius + east * u + south * v) * Radius;
    }

    [Fact]
    public void LateTile_MatchesOneLiveAllAlong()
    {
        const int tile = 1234;
        var epoch = Change(tile, 10);
        var anchor = Point(tile, 0.0, 0.0);
        var a = Swells();
        var surfaceA = new OceanSurface(a, Dt);
        var start = (epoch - 1) * SeaWind.EpochTicks + 17;
        var end = epoch * SeaWind.EpochTicks + 1200;

        for (var t = start; t <= end; t++)
            a.Tick(t, [anchor]);

        var b = Swells();
        var surfaceB = new OceanSurface(b, Dt);
        b.Tick(end, [anchor]);

        Assert.True(b.LastMade > 0);
        Assert.Equal(a.For(tile)!.GpuCarrier(end * Dt), b.For(tile)!.GpuCarrier(end * Dt));

        for (var i = 0; i < 8; i++)
        {
            var p = Point(tile, 3000.0 * i - 9000.0, 700.0 * i);
            Assert.Equal(surfaceA.SampleHeight(p, end), surfaceB.SampleHeight(p, end));
        }
    }

    [Fact]
    public void Tiles_HaveTheirOwnSeas()
    {
        var swells = Swells();
        Span<double3> anchors = [Point(100, 0.0, 0.0), Point(900, 0.0, 0.0), Point(1700, 0.0, 0.0)];
        swells.Tick(200 * SeaWind.EpochTicks, anchors);
        var heights = new HashSet<float>();

        foreach (var tile in (ReadOnlySpan<int>)[100, 900, 1700])
            heights.Add(swells.For(tile)!.SignificantWaveHeight);

        Assert.Equal(3, heights.Count);
    }

    /// <summary>Through a key change the sea moves by second differences no larger than in steady wind: no pop.</summary>
    [Fact]
    public void WindChange_HasNoPop()
    {
        const int tile = 1234;
        var epoch = Change(tile, 10);
        var swells = Swells(groundswell: 0f);
        var surface = new OceanSurface(swells, Dt);
        var anchor = Point(tile, 0.0, 0.0);
        var probe = Point(tile, 1500.0, -800.0);
        var steady = 0f;
        var blend = 0f;
        var carrierStep = 0.0;
        float h0 = 0f, h1 = 0f;
        double? phase = null;
        var start = epoch * SeaWind.EpochTicks - 600;
        var end = epoch * SeaWind.EpochTicks + 3600;

        for (var t = start; t <= end; t++)
        {
            swells.Tick(t, [anchor]);
            var h = surface.SampleHeight(probe, t);

            if (t >= start + 2)
            {
                var kick = MathF.Abs(h - 2f * h1 + h0);

                if (t < epoch * SeaWind.EpochTicks)
                    steady = MathF.Max(steady, kick);
                else
                    blend = MathF.Max(blend, kick);
            }

            var swell = swells.For(tile)!;
            var carrier = swell.GpuCarrier(t * Dt);

            if (phase is { } last)
            {
                var step = Math.IEEERemainder(last - carrier.Y, Math.Tau);
                carrierStep = Math.Max(carrierStep, Math.Abs(step / Dt - carrier.X) / Math.Max(carrier.X, 1e-3));
            }

            phase = carrier.Y;
            (h0, h1) = (h1, h);
        }

        output.WriteLine($"key {Winds.At(tile, epoch - 1)} -> {Winds.At(tile, epoch)}; second difference steady {steady * 1000f:F2} mm, blending {blend * 1000f:F2} mm; carrier rate off by {carrierStep * 100.0:F1} % at most");
        Assert.True(blend < 2f * steady + 1e-3f, $"blend kick {blend} against steady {steady}");
        Assert.True(carrierStep < 0.3, $"carrier rate off by {carrierStep:P0}");
    }

    /// <summary>The cost table: a tile's wind, a tick with live tiles (epoch ticks included), a tile made, and a sample.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Costs()
    {
        var timer = new System.Diagnostics.Stopwatch();
        var at = new double[200];

        for (var i = 0; i < at.Length; i++)
        {
            timer.Restart();
            Winds.At(500 + i, 77);
            at[i] = timer.Elapsed.TotalMicroseconds;
        }

        var swells = Swells();
        Span<double3> anchors = [Point(1234, 31000.0, 31000.0), Point(1300, 0.0, 0.0)];
        var first = 100 * SeaWind.EpochTicks - 5;
        swells.Tick(first, anchors);
        var live = swells.LiveCount;
        var ticks = new double[2 * (int)SeaWind.EpochTicks];
        var bytes = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < ticks.Length; i++)
        {
            timer.Restart();
            swells.Tick(first + 1 + i, anchors);
            ticks[i] = timer.Elapsed.TotalMicroseconds;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        var made = new double[20];

        for (var i = 0; i < made.Length; i++)
        {
            var fresh = Swells();
            timer.Restart();
            fresh.Tick(first + 7 * i, [Point(1500 + 3 * i, 0.0, 0.0)]);
            made[i] = timer.Elapsed.TotalMicroseconds / fresh.LastMade;
        }

        var surface = new OceanSurface(swells, Dt);
        var pinned = new SeaSwells(3, Tiles, Dt);
        pinned.SetBase(swells.Base with { WindSpeed = 8f }, driven: false);
        pinned.Tick(swells.LastTick, anchors);
        var uniform = new OceanSurface(pinned, Dt);
        var edge = Point(1234, 31500.0, 31500.0);
        var inside = Point(1234, 0.0, 0.0);

        output.WriteLine($"tile wind (25 samples): median {Median(at):F1} us, max {Max(at):F1} us");
        var epochTicks = new double[2];
        var rest = new List<double>();

        for (var i = 0; i < ticks.Length; i++)
        {
            if ((first + 1 + i) % SeaWind.EpochTicks == 0)
                epochTicks[(int)((first + 1 + i) / SeaWind.EpochTicks - 100)] = ticks[i];
            else
                rest.Add(ticks[i]);
        }

        rest.Sort();
        output.WriteLine($"sea tick, {live} live tiles: median {Median(ticks):F1} us, p99 {rest[rest.Count * 99 / 100]:F1} us, max {rest[^1]:F1} us off epoch; epoch ticks {epochTicks[0]:F0} and {epochTicks[1]:F0} us; {allocated} B allocated over {ticks.Length} ticks");
        output.WriteLine($"tile made (2 winds, 2 sets): median {Median(made):F0} us, max {Max(made):F0} us");
        output.WriteLine($"Sample inside a tile: driven {SampleNs(surface, inside, swells.LastTick):F0} ns, uniform {SampleNs(uniform, inside, swells.LastTick):F0} ns");
        output.WriteLine($"Sample at a tile corner: driven {SampleNs(surface, edge, swells.LastTick):F0} ns, uniform {SampleNs(uniform, edge, swells.LastTick):F0} ns");
        Assert.Equal(0, allocated);
    }

    private (int Changes, int Rebakes, int MaxSpeed) Timeline(SimWorld sim, long from, long to, bool print)
    {
        var swells = new SeaSwells(1, sim.Ocean.Tiles, Dt, sim.SeaWinds);
        swells.SetBase(new SwellParams { SwellHeight = 1.2f, SwellPeriod = 13f }, driven: true);
        var anchor = sim.Bubble.Origin;
        var baked = default(BrutalGame.Water.Coast.CoastCarrier);
        var (changes, rebakes, maxSpeed) = (0, 0, 0);
        var last = default(SeaWindKey);

        for (var epoch = from; epoch <= to; epoch++)
        {
            swells.Tick(epoch * SeaWind.EpochTicks, [anchor]);
            var swell = swells.For(sim.SpawnTile)!;
            var key = sim.SeaWinds.At(sim.SpawnTile, epoch);
            var carrier = BrutalGame.Water.Coast.CoastCarrier.From(swell.TargetCarrierDirection, swell.TargetCarrierOmega, swell.TargetCarrierHeight);
            var rebake = epoch == from || !BrutalGame.Water.Coast.CoastRegionSet.Keeps(baked, carrier);
            changes += key != last && epoch > from ? 1 : 0;
            maxSpeed = Math.Max(maxSpeed, key.Speed);

            if (rebake)
            {
                rebakes += epoch > from ? 1 : 0;
                baked = carrier;
            }

            if (print && (key != last || rebake))
                output.WriteLine($"{epoch,5} min: wind {key.Speed,2} m/s {key.Direction * 10,3} deg  Hs {swell.TargetSignificantHeight:F2} m  carrier T {(carrier.Calm ? 0f : MathF.Tau / carrier.Omega):F2} s H {carrier.Height:F2} m dir {carrier.Direction * 180f / MathF.PI:F0}{(rebake ? "  REBAKE" : "")}");

            last = key;
        }

        return (changes, rebakes, maxSpeed);
    }

    private static double SampleNs(OceanSurface surface, double3 p, long tick)
    {
        var sink = 0f;

        for (var i = 0; i < 20000; i++)
            sink += surface.Sample(p, tick).Height;

        var timer = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 2000; i++)
            sink += surface.Sample(p, tick).Height;

        return float.IsNaN(sink) ? 0.0 : timer.Elapsed.TotalNanoseconds / 2000;
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    private static double Max(double[] values)
    {
        var max = 0.0;

        foreach (var v in values)
            max = Math.Max(max, v);

        return max;
    }

    /// <summary>
    /// The game's spawn tile (seed 1, the default groundswell) through the weather: its key, the carrier the coast
    /// regions are baked for, and how often that carrier passes the rebake tolerance (CoastRegionSet.Keeps): over the
    /// first twelve hours, and minute by minute through the week's strongest wind there.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void SpawnTile_StormTimeline()
    {
        using var sim = new SimWorld(seed: 1, workerThreads: 1, weather: true);
        var (changes, rebakes, maxSpeed) = Timeline(sim, 1, 720, print: false);
        output.WriteLine($"first 12 h: {changes} key changes, {rebakes} rebakes, top wind {maxSpeed} m/s");
        var top = (Speed: 0, Epoch: 0L);

        for (var epoch = 0L; epoch < 7 * 1440; epoch++)
        {
            var speed = sim.SeaWinds.At(sim.SpawnTile, epoch).Speed;

            if (speed > top.Speed)
                top = (speed, epoch);
        }

        output.WriteLine($"spawn tile over a week: top {top.Speed} m/s at {top.Epoch} min");
        (changes, rebakes, maxSpeed) = Timeline(sim, top.Epoch - 90, top.Epoch + 60, print: true);
        output.WriteLine($"through it: {changes} key changes, {rebakes} rebakes");

        var histogram = new int[40];
        var peak = new System.Numerics.Vector2[40];

        for (var tile = 0; tile < sim.Ocean.Tiles.Count; tile += 7)
        {
            for (var epoch = 0L; epoch < 720; epoch += 10)
            {
                var mean = sim.SeaWinds.Mean(tile, epoch * SeaWind.EpochTicks).Length();
                histogram[Math.Min((int)mean, 39)]++;
            }
        }

        output.WriteLine($"tile-mean wind over 12 h, every 7th tile every 10 min: {string.Join(" ", histogram.Select((n, i) => n > 0 ? $"{i}:{n}" : "").Where(t => t.Length > 0))}");
    }
}

