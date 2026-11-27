using System.Diagnostics;
using System.Numerics;
using Brutal.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Weather.Tests;

public sealed class SphereWeatherTests(ITestOutputHelper output)
{
    private const double Radius = 1_000_000.0;
    private const long TicksPerHour = 60 * 3600;
    private const double Degree = Math.PI / 180.0;

    private static readonly double3 TiltedAxis = double3.Normalize(new double3(0.2, 1.0, -0.3));

    private static SphereWeatherField Rocky(ulong seed) => new(PlanetClimate.Rocky(seed, Radius, 6 * 3600.0, double3.UnitY));

    private static SphereWeatherField Giant(ulong seed) => new(PlanetClimate.GasGiant(seed, 6_000_000.0, TiltedAxis));

    private static SphereWeatherField Calm(ulong seed) => new(new PlanetClimate { Seed = seed, Radius = Radius, EasterlySpeed = 0f, WesterlySpeed = 0f, BaseCloud = 0f });

    private static double3 Fibonacci(int i, int count)
    {
        var y = 1.0 - 2.0 * (i + 0.5) / count;
        var r = Math.Sqrt(1.0 - y * y);
        var (sin, cos) = Math.SinCos(i * 2.399963229728653);
        return new double3(r * cos, y, r * sin);
    }

    private static double3 Along(double3 from, double3 direction, double metres) =>
        double3.Normalize(from * Math.Cos(metres / Radius) + direction * Math.Sin(metres / Radius));

    private static void AssertBitIdentical(in WeatherSample a, in WeatherSample b)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Wind.X), BitConverter.SingleToInt32Bits(b.Wind.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Wind.Y), BitConverter.SingleToInt32Bits(b.Wind.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Cloud), BitConverter.SingleToInt32Bits(b.Cloud));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Rain), BitConverter.SingleToInt32Bits(b.Rain));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Storm), BitConverter.SingleToInt32Bits(b.Storm));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Pressure), BitConverter.SingleToInt32Bits(b.Pressure));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Gust), BitConverter.SingleToInt32Bits(b.Gust));
    }

    /// <summary>Points that stress the lattice: everywhere, hard against both poles, and either side of the antimeridian.</summary>
    private static IEnumerable<double3> LatticePoints(SphereWeatherField field)
    {
        for (var i = 0; i < 400; i++)
            yield return Fibonacci(i, 400);

        foreach (var latitude in new[] { 90.0, 89.999, 89.99, 89.9, 89.8, 89.5, 89.0, 88.0, 85.0, 80.0, 76.0, 74.0, 60.0, 0.0 })
        {
            for (var k = 0; k < 12; k++)
            {
                yield return field.Direction(latitude * Degree, k * 30.0 * Degree + 0.1);
                yield return field.Direction(-latitude * Degree, k * 30.0 * Degree + 0.2);
            }

            yield return field.Direction(latitude * Degree, Math.PI - 1e-9);
            yield return field.Direction(latitude * Degree, -Math.PI + 1e-9);
        }
    }

    [Fact]
    public void SameSeedIsBitIdentical_DifferentSeedDiffers()
    {
        var a = Rocky(7);
        var b = Rocky(7);
        var c = Rocky(8);
        var differs = false;

        for (var i = 0; i < 300; i++)
        {
            var p = Fibonacci(i, 300) * (Radius + i * 10.0);
            var tick = 1_234L + i * 7_919L;
            var sa = a.Sample(p, tick);
            AssertBitIdentical(sa, b.Sample(p, tick));

            if (MathF.Abs(sa.Cloud - c.Sample(p, tick).Cloud) > 1e-3f)
                differs = true;
        }

        Assert.True(differs);
    }

    /// <summary>The lattice window: a point sample equals an evaluation over every system within a wide cap, at the poles and across the antimeridian too.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointSampleSeesEverySystemThatReachesIt(bool giant)
    {
        var field = giant ? Giant(3) : Rocky(3);
        var extra = giant ? 0.4 : 200_000.0 / Radius;
        var wide = new SphereSystem[8192];
        var near = new SphereSystem[SphereWeatherField.MaxPointSystems];
        var points = 0;
        var underWeather = 0;
        var mostNear = 0;

        foreach (var point in LatticePoints(field))
        {
            var p = double3.Normalize(point);
            var time = points++ * 977.0 * field.Climate.TimeScale;
            var count = field.Gather(p, extra, time, wide);
            var nearCount = field.Gather(p, 0.0, time, near);
            var sample = field.SampleTime(point, time);

            Assert.True(count < wide.Length);
            Assert.True(nearCount < near.Length);
            AssertBitIdentical(field.Evaluate(p, wide.AsSpan(0, count)), sample);
            mostNear = Math.Max(mostNear, nearCount);

            if (MathF.Abs(sample.Pressure) > 0.5f)
                underWeather++;
        }

        output.WriteLine($"{points} points, {underWeather} under a system, at most {mostNear} systems near one point, {field.Rows} rows");
        Assert.True(underWeather > points / 20);
    }

    /// <summary>D31's cap: ≤ 3 candidate cells per row equatorward of ~75°, ≤ 4 per row and ≤ 12 cells (24 systems) anywhere.</summary>
    [Fact]
    public void CandidateCellsStayCappedToThePoles()
    {
        var field = Rocky(1);
        var mostLow = 0;
        var mostHigh = 0;
        var mostTotal = 0;

        for (var latitude = 0.0; latitude <= 90.0; latitude += 0.01)
        {
            for (var k = 0; k < 7; k++)
            {
                var total = field.CandidateCells(field.Direction(latitude * Degree, k * 0.917), out var perRow);
                mostTotal = Math.Max(mostTotal, total);

                if (latitude < 75.0)
                    mostLow = Math.Max(mostLow, perRow);
                else
                    mostHigh = Math.Max(mostHigh, perRow);
            }
        }

        output.WriteLine($"per row: {mostLow} below 75°, {mostHigh} above; total {mostTotal} cells");
        Assert.True(mostLow <= 3);
        Assert.True(mostHigh <= 4);
        Assert.True(mostTotal * SphereWeatherField.Slots <= 24);
    }

    /// <summary>1 mm either side of the antimeridian the weather is the same; there is weather on it to compare.</summary>
    [Fact]
    public void ContinuousAcrossTheAntimeridian()
    {
        var field = Rocky(11);
        var maxWind = 0.0;
        var maxCloud = 0f;
        var underWeather = 0;

        for (var latitude = -89.5; latitude <= 89.5; latitude += 0.05)
        {
            var tick = (long)(latitude * 1_000.0) + 50 * TicksPerHour;
            var west = field.Direction(latitude * Degree, Math.PI - 5e-10);
            var east = field.Direction(latitude * Degree, -Math.PI + 5e-10);
            var a = field.Sample(west, tick);
            var b = field.Sample(east, tick);
            maxWind = Math.Max(maxWind, double3.Distance(field.ToPlanet(west, a.Wind), field.ToPlanet(east, b.Wind)));
            maxCloud = MathF.Max(maxCloud, MathF.Abs(a.Cloud - b.Cloud));

            if (MathF.Abs(a.Pressure) > 0.5f)
                underWeather++;
        }

        output.WriteLine($"antimeridian: max wind step {maxWind:E2} m/s, cloud {maxCloud:E2}, {underWeather} latitudes under weather");
        Assert.True(underWeather > 20);
        Assert.True(maxWind < 1e-3);
        Assert.True(maxCloud < 1e-4f);
    }

    /// <summary>Walking 10 m steps straight over each pole, the planet-frame wind and the scalars move smoothly, at a time a system sits on the pole.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(-1.0)]
    public void ContinuousOverThePoles(double hemisphere)
    {
        var field = Rocky(5);
        var pole = field.Pole * hemisphere;
        var tick = 0L;

        while (MathF.Abs(field.Sample(pole, tick).Pressure) < 2f)
        {
            tick += TicksPerHour / 6;
            Assert.True(tick < 200 * TicksPerHour, "no system crossed the pole");
        }

        var start = Along(pole, field.Prime, -2_000.0);
        var direction = double3.Normalize(double3.Cross(double3.Cross(start, pole), start));
        var previous = default(WeatherSample);
        var previousWind = double3.Zero;
        var maxWind = 0.0;
        var maxCloud = 0f;

        for (var step = 0; step <= 400; step++)
        {
            var p = Along(start, direction, step * 10.0);
            var s = field.Sample(p, tick);
            var wind = field.ToPlanet(p, s.Wind);
            Assert.True(double.IsFinite(wind.X) && double.IsFinite(wind.Y) && double.IsFinite(wind.Z));

            if (step > 0)
            {
                maxWind = Math.Max(maxWind, double3.Distance(wind, previousWind));
                maxCloud = MathF.Max(maxCloud, MathF.Abs(s.Cloud - previous.Cloud));
            }

            previous = s;
            previousWind = wind;
        }

        output.WriteLine($"over the pole at tick {tick}: pressure {field.Sample(pole, tick).Pressure:F1} hPa, max 10 m step wind {maxWind:F4} m/s, cloud {maxCloud:F5}");
        Assert.True(maxWind < 0.2);
        Assert.True(maxCloud < 0.01f);
    }

    /// <summary>Counter-clockwise about local up round northern lows, clockwise round southern ones, highs the other way, all with inflow to lows.</summary>
    [Theory]
    [InlineData(45.0, PressureKind.Low, 1f)]
    [InlineData(-45.0, PressureKind.Low, -1f)]
    [InlineData(45.0, PressureKind.High, -1f)]
    [InlineData(-45.0, PressureKind.High, 1f)]
    public void CirculationSenseFollowsTheHemisphere(double latitude, PressureKind kind, float spin)
    {
        var field = Calm(1);
        var centre = field.Direction(latitude * Degree, 1.0);
        field.Basis(centre, out var east, out _);
        var system = new SphereSystem
        {
            Centre = centre,
            Radius = 10_000f,
            InvAngularRadius = Radius / 10_000.0,
            Kind = kind,
            Strength = 1f,
            Spin = spin,
            PeakWind = 10f,
        };
        var p = Along(centre, east, 10_000.0 / Math.Sqrt(5.0));
        var wind = field.Evaluate(p, [system]).Wind;
        var northward = latitude > 0 == (kind == PressureKind.Low);
        var inward = kind == PressureKind.Low;

        Assert.InRange(wind.Length(), 9.99f, 10.01f);
        Assert.True(northward ? wind.Y > 9f : wind.Y < -9f);
        Assert.True(inward ? wind.X < -3f : wind.X > 3f);
    }

    /// <summary>The lattice's systems carry that sense: cyclonic lows north and south, none spinning on the equator.</summary>
    [Fact]
    public void SystemsSpinByHemisphereAndNotOnTheEquator()
    {
        var field = Rocky(2);
        var systems = new SphereSystem[SphereWeatherField.MaxPointSystems];
        var seen = 0;

        for (var i = 0; i < 2_000; i++)
        {
            var count = field.Gather(Fibonacci(i, 2_000), 0.0, 9_000.0 + i * 13.0, systems);

            foreach (var s in systems.AsSpan(0, count))
            {
                var sinLatitude = double3.Dot(s.Centre, field.Pole);
                var cyclonic = s.Kind == PressureKind.High ? -s.Spin : s.Spin;
                seen++;

                Assert.True(cyclonic * sinLatitude >= 0.0);
                Assert.InRange(cyclonic, -1f, 1f);

                if (Math.Abs(sinLatitude) < Math.Sin(0.5 * Degree))
                    Assert.True(MathF.Abs(s.Spin) < 0.1f);

                if (Math.Abs(sinLatitude) > Math.Sin(field.Climate.EquatorFade))
                    Assert.Equal(1f, MathF.Abs(s.Spin));
            }
        }

        Assert.True(seen > 1_000);
    }

    /// <summary>The zonal steering alternates trades and westerlies and is still at the equator and the poles.</summary>
    [Fact]
    public void ZonalBandsAlternateAndVanishAtEquatorAndPoles()
    {
        var field = new SphereWeatherField(new PlanetClimate { ZonalCells = 3 });

        Assert.True(Math.Abs(field.Zonal(0.0)) < 1e-9);
        Assert.True(Math.Abs(field.Zonal(0.5 * Math.PI)) < 1e-9);
        Assert.True(Math.Abs(field.Zonal(-0.5 * Math.PI)) < 1e-9);
        Assert.True(field.Zonal(15.0 * Degree) < -3.0);
        Assert.True(field.Zonal(45.0 * Degree) > 3.0);
        Assert.True(field.Zonal(75.0 * Degree) < -3.0);
        Assert.True(field.Zonal(-45.0 * Degree) > 3.0);

        var giant = Giant(9);
        output.WriteLine($"giant: {giant.Climate.ZonalCells} bands per hemisphere, {giant.Rows} rows, jet at 10° {giant.Zonal(10.0 * Degree):F0} m/s");
        Assert.InRange(giant.Climate.ZonalCells, 4, 10);
    }

    /// <summary>The cache against the field over 30 s, anchor fixed: bilinear at ~1 km and the lerp in time.</summary>
    [Fact]
    public void CacheMatchesFieldWithinInterpolationError()
    {
        var field = Rocky(5);
        var cache = new SphereWeatherCache(field);
        var anchor = field.Direction(31.0 * Degree, 2.0);
        field.Basis(anchor, out var east, out var north);
        Span<double3> anchors = [anchor * Radius];
        var maxWind = 0f;
        var maxCloud = 0f;
        var maxRain = 0f;
        var underWeather = 0;
        var tick0 = FindWeather(field, anchor, 3_500.0);

        for (var tick = tick0; tick < tick0 + 60 * 30; tick++)
        {
            cache.Tick(tick, anchors);

            if (tick % 30 != 0)
                continue;

            for (var k = 0; k < 64; k++)
            {
                var p = double3.Normalize(anchor + (k % 8 - 3.5) * 900.0 / Radius * east + (k / 8 - 3.5) * 900.0 / Radius * north);
                Assert.True(cache.TrySample(p, tick, out var a));
                var b = field.Sample(p, tick);
                maxWind = MathF.Max(maxWind, (a.Wind - b.Wind).Length());
                maxCloud = MathF.Max(maxCloud, MathF.Abs(a.Cloud - b.Cloud));
                maxRain = MathF.Max(maxRain, MathF.Abs(a.Rain - b.Rain));

                if (MathF.Abs(b.Pressure) > 0.5f)
                    underWeather++;
            }
        }

        output.WriteLine($"max error: wind {maxWind:F3} m/s  cloud {maxCloud:F4}  rain {maxRain:F4}  under weather {underWeather}  sync bakes {cache.SyncBakes} ({cache.LastSyncMs:F3} ms)");
        Assert.True(underWeather > 0);
        Assert.True(maxWind < 1.5f);
        Assert.True(maxCloud < 0.08f);
        Assert.True(maxRain < 0.08f);
    }

    /// <summary>Who anchors the cache never changes its values: two caches with different anchors agree bit for bit where both are kept.</summary>
    [Fact]
    public void CacheValuesDoNotDependOnAnchors()
    {
        var field = Rocky(6);
        var a = new SphereWeatherCache(field);
        var b = new SphereWeatherCache(field);
        var centre = field.Direction(-12.0 * Degree, 4.0);
        field.Basis(centre, out var east, out var north);
        Span<double3> anchorsA = [centre * Radius];
        Span<double3> anchorsB = [Along(centre, east, 2_500.0) * Radius, field.Direction(60.0 * Degree, 1.0) * Radius];
        var tick0 = FindWeather(field, centre, 2_000.0);
        var compared = 0;

        for (var tick = tick0; tick < tick0 + 900; tick++)
        {
            a.Tick(tick, anchorsA);
            b.Tick(tick, anchorsB);

            if (tick % 50 != 0)
                continue;

            for (var k = 0; k < 25; k++)
            {
                var p = double3.Normalize(centre + (k % 5 - 2) * 300.0 / Radius * east + (k / 5 - 2) * 300.0 / Radius * north);
                Assert.True(a.TrySample(p, tick, out var sa));
                Assert.True(b.TrySample(p, tick, out var sb));
                AssertBitIdentical(sa, sb);
                compared++;
            }
        }

        Assert.True(compared > 400);
    }

    /// <summary>No pops while snapshots rotate and tiles come and go under an anchor flying 100 m/s; no synchronous bake after the first tick.</summary>
    [Fact]
    public void CacheSampleIsSmoothTickToTick()
    {
        var field = Rocky(9);
        var cache = new SphereWeatherCache(field);
        var mid = field.Direction(52.0 * Degree, -2.5);
        var tick0 = FindWeather(field, mid, 2_000.0);
        field.Basis(mid, out var east, out var north);
        var start = Along(mid, -east, 3_000.0);
        Span<double3> anchors = [default];
        var previous = new WeatherSample[25];
        var maxWind = 0f;
        var maxCloud = 0f;
        var firstSyncBakes = 0;

        for (var step = 0; step < 60 * 60; step++)
        {
            var tick = tick0 + step;
            anchors[0] = Along(start, east, step / 60.0 * 100.0) * Radius;
            cache.Tick(tick, anchors);

            if (step == 0)
                firstSyncBakes = cache.SyncBakes;

            for (var k = 0; k < previous.Length; k++)
            {
                var p = double3.Normalize(mid + (k % 5 - 2) * 200.0 / Radius * east + (k / 5 - 2) * 200.0 / Radius * north);
                Assert.True(cache.TrySample(p, tick, out var s));

                if (step > 0)
                {
                    maxWind = MathF.Max(maxWind, (s.Wind - previous[k].Wind).Length());
                    maxCloud = MathF.Max(maxCloud, MathF.Abs(s.Cloud - previous[k].Cloud));
                }

                previous[k] = s;
            }
        }

        output.WriteLine($"max per-tick change: wind {maxWind:F5} m/s  cloud {maxCloud:F5}  sync bakes {cache.SyncBakes} (first tick {firstSyncBakes})  rows baked {cache.BakedRows}  dropped {cache.DroppedTiles}");
        Assert.Equal(firstSyncBakes, cache.SyncBakes);
        Assert.Equal(0, cache.DroppedTiles);
        Assert.True(maxWind < 0.05f);
        Assert.True(maxCloud < 0.005f);
    }

    /// <summary>Across a cube face edge and corner the cache is continuous: both faces put their shared nodes at the same points.</summary>
    [Fact]
    public void CacheIsContinuousAcrossCubeFaces()
    {
        var field = Rocky(4);
        var cache = new SphereWeatherCache(field);
        var corner = double3.Normalize(new double3(1.0, 1.0, 1.0));
        var direction = double3.Normalize(double3.Cross(corner, new double3(0.0, 0.3, -1.0)));
        var start = Along(corner, direction, -2_000.0);
        Span<double3> anchors = [corner * Radius];
        var tick = FindWeather(field, corner, 3_000.0);
        cache.Tick(tick, anchors);
        var previous = default(WeatherSample);
        var previousWind = double3.Zero;
        var maxWind = 0.0;
        var maxCloud = 0f;
        var faces = 0;
        var lastFace = -1;

        for (var step = 0; step <= 400; step++)
        {
            var p = Along(start, direction, step * 10.0);
            cache.Locate(p, out var face, out _, out _);
            Assert.True(cache.TrySample(p, tick, out var s));
            var wind = field.ToPlanet(p, s.Wind);

            if (face != lastFace)
            {
                faces++;
                lastFace = face;
            }

            if (step > 0)
            {
                maxWind = Math.Max(maxWind, double3.Distance(wind, previousWind));
                maxCloud = MathF.Max(maxCloud, MathF.Abs(s.Cloud - previous.Cloud));
            }

            previous = s;
            previousWind = wind;
        }

        output.WriteLine($"faces crossed {faces}, max 10 m step wind {maxWind:F4} m/s, cloud {maxCloud:F5}");
        Assert.True(faces >= 2);
        Assert.True(maxWind < 0.2);
        Assert.True(maxCloud < 0.01f);
    }

    [Fact]
    public void CacheTimeJumpRebakesAndMatchesField()
    {
        var field = Rocky(4);
        var cache = new SphereWeatherCache(field);
        var p = field.Direction(10.0 * Degree, 0.5);
        Span<double3> anchors = [p * Radius];
        cache.Tick(0, anchors);
        var first = cache.SyncBakes;
        cache.Tick(10 * TicksPerHour, anchors);

        Assert.True(cache.SyncBakes > first);
        Assert.True(cache.TrySample(p, 10 * TicksPerHour, out var a));
        var b = field.Sample(p, 10 * TicksPerHour);
        Assert.True((a.Wind - b.Wind).Length() < 1.5f);
        Assert.True(MathF.Abs(a.Cloud - b.Cloud) < 0.08f);
    }

    [Fact]
    public void SampleTickAndCacheDoNotAllocate()
    {
        var field = Rocky(2);
        var cache = new SphereWeatherCache(field);
        var p = field.Direction(20.0 * Degree, 1.0) * Radius;
        var anchors = new double3[1];

        for (var tick = 0L; tick < 700; tick++)
            Exercise(tick);

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 700L; tick < 1_400; tick++)
            Exercise(tick);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void Exercise(long tick)
        {
            anchors[0] = p + new double3(tick, 0.0, 0.0);
            cache.Tick(tick, anchors);
            cache.Sample(p, tick);
            field.Sample(p, tick);
        }
    }

    /// <summary>Best of three passes after warm-up.</summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void SampleCost()
    {
        const int points = 4_096;
        const int ticks = 9_000;
        var field = Rocky(3);
        var cache = new SphereWeatherCache(field);
        var anchor = field.Direction(40.0 * Degree, 1.0);
        field.Basis(anchor, out var east, out _);
        var directions = new double3[points];
        var near = new double3[points];
        var tickMs = new double[ticks];
        Span<double3> anchors = [anchor * Radius];
        var sink = 0f;
        var fieldNs = double.MaxValue;
        var cacheNs = double.MaxValue;

        for (var i = 0; i < points; i++)
        {
            directions[i] = Fibonacci(i, points);
            near[i] = Along(anchor, east, i % 2_000 - 1_000.0);
        }

        for (var tick = 0; tick < ticks; tick++)
        {
            anchors[0] = Along(anchor, east, tick / 60.0 * 30.0) * Radius;
            var t0 = Stopwatch.GetTimestamp();
            cache.Tick(tick, anchors);
            tickMs[tick] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }

        var firstSync = cache.LastSyncMs;
        var syncBakes = cache.SyncBakes;
        var last = ticks - 1L;
        var at = double3.Normalize(anchors[0]);

        for (var i = 0; i < points; i++)
            near[i] = Along(at, east, i % 2_000 - 1_000.0);

        for (var pass = 0; pass < 6; pass++)
        {
            var start = Stopwatch.GetTimestamp();

            for (var i = 0; i < points; i++)
                sink += field.Sample(directions[i], pass * 977L + i * 31L).Cloud;

            fieldNs = Math.Min(fieldNs, Stopwatch.GetElapsedTime(start).TotalNanoseconds / points);
            start = Stopwatch.GetTimestamp();

            for (var i = 0; i < points; i++)
                sink += cache.Sample(near[i], last).Cloud;

            cacheNs = Math.Min(cacheNs, Stopwatch.GetElapsedTime(start).TotalNanoseconds / points);
        }

        var steady = tickMs.AsSpan(1);
        steady.Sort();
        output.WriteLine($"sample: field {fieldNs:F0} ns, cache {cacheNs:F0} ns");
        output.WriteLine($"cache tick (anchor 30 m/s): median {steady[steady.Length / 2] * 1000.0:F2} µs, p99 {steady[steady.Length * 99 / 100] * 1000.0:F1} µs, max {steady[^1] * 1000.0:F1} µs; first tick {tickMs[0]:F3} ms ({syncBakes} sync bakes, {firstSync:F3} ms) ({sink:F0})");
        Assert.True(fieldNs < 10_000.0);
        Assert.True(cacheNs < 2_000.0);
    }

    /// <summary>A tick within the next 20 h when a system covers <paramref name="p"/> out to <paramref name="metres"/>, so a test has weather to look at.</summary>
    private static long FindWeather(SphereWeatherField field, double3 p, double metres)
    {
        field.Basis(p, out var east, out _);

        for (var tick = 0L; tick < 20 * TicksPerHour; tick += TicksPerHour / 12)
        {
            if (MathF.Abs(field.Sample(p, tick).Pressure) > 2f && MathF.Abs(field.Sample(Along(p, east, metres), tick).Pressure) > 0.5f)
                return tick;
        }

        throw new InvalidOperationException("no weather found");
    }
}
