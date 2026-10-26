using System.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Weather.Tests;

public sealed class WeatherTests(ITestOutputHelper output)
{
    private const double Tick = 1.0 / 60.0;

    private static WeatherField Field(ulong seed) => new(new WeatherClimate { Seed = seed });

    private static void AssertBitIdentical(in WeatherSample a, in WeatherSample b)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Wind.X), BitConverter.SingleToInt32Bits(b.Wind.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Wind.Y), BitConverter.SingleToInt32Bits(b.Wind.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Cloud), BitConverter.SingleToInt32Bits(b.Cloud));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Rain), BitConverter.SingleToInt32Bits(b.Rain));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Storm), BitConverter.SingleToInt32Bits(b.Storm));
        Assert.Equal(BitConverter.SingleToInt32Bits(a.Pressure), BitConverter.SingleToInt32Bits(b.Pressure));
    }

    [Fact]
    public void SameSeedIsBitIdentical_DifferentSeedDiffers()
    {
        var a = Field(7);
        var b = Field(7);
        var c = Field(8);
        var differs = false;

        for (var i = 0; i < 200; i++)
        {
            var x = i % 20 * 3_700f - 37_000f;
            var z = i / 20 * 5_300f - 26_000f;
            var t = 1_234.5 + i * 17.0;
            var sa = a.Sample(x, z, t);
            AssertBitIdentical(sa, b.Sample(x, z, t));

            if (MathF.Abs(sa.Cloud - c.Sample(x, z, t).Cloud) > 1e-3f)
                differs = true;
        }

        Assert.True(differs);
    }

    /// <summary>The 3 × 3 cell bound: a point sample equals an evaluation over every system within 200 km.</summary>
    [Fact]
    public void PointSampleSeesEverySystemThatReachesIt()
    {
        var field = Field(3);
        var wide = new PressureSystem[2048];

        for (var i = 0; i < 400; i++)
        {
            var p = new Vector2(i * 1_913f % 90_000f - 45_000f, i * 2_741f % 90_000f - 45_000f);
            var t = i * 97.0;
            var count = field.Gather(p - new Vector2(200_000f), p + new Vector2(200_000f), t, wide);

            Assert.True(count < wide.Length);
            AssertBitIdentical(field.Evaluate(p, wide.AsSpan(0, count)), field.Sample(p.X, p.Y, t));
        }
    }

    /// <summary>Over a 200 km square at one instant there is both storm (thick cloud, rain, strong wind) and fair weather (little cloud, no rain, lighter than the prevailing wind).</summary>
    [Fact]
    public void StormyAndFairWeatherCoexist()
    {
        var field = Field(1);
        var stormy = 0;
        var fair = 0;
        var samples = 0;

        for (var z = -100_000f; z <= 100_000f; z += 1_000f)
        {
            for (var x = -100_000f; x <= 100_000f; x += 1_000f)
            {
                var s = field.Sample(x, z, 3.0 * 3_600.0);
                samples++;

                if (s.Cloud > 0.9f && s.Rain > 0.4f && s.WindSpeed > 10f)
                    stormy++;

                if (s.Cloud < 0.2f && s.Rain == 0f && s.WindSpeed < field.Climate.PrevailingSpeed)
                    fair++;
            }
        }

        output.WriteLine($"stormy {100.0 * stormy / samples:F1}%  fair {100.0 * fair / samples:F1}%  of {samples}");
        Assert.True(stormy > 0);
        Assert.True(fair > 0);
    }

    /// <summary>Counter-clockwise from above with +Y up means +X turns toward −Z; the wind also crosses the isobars into lows and out of highs.</summary>
    [Fact]
    public void LowsTurnCounterClockwiseFromAbove_HighsClockwise()
    {
        var field = new WeatherField(new WeatherClimate { PrevailingSpeed = 0f, BaseCloud = 0f });
        PressureSystem[] low = [new() { Centre = Vector2.Zero, Radius = 10_000f, Kind = PressureKind.Low, Strength = 1f, PeakWind = 10f, Cloud = 1f }];
        PressureSystem[] high = [new() { Centre = Vector2.Zero, Radius = 10_000f, Kind = PressureKind.High, Strength = 1f, PeakWind = 10f, Cloud = 1f }];
        var east = new Vector2(10_000f / MathF.Sqrt(5f), 0f);

        var l = field.Evaluate(east, low);
        Assert.True(l.Wind.Y < -9f);
        Assert.True(l.Wind.X < -3f);
        Assert.InRange(l.Wind.Length(), 9.99f, 10.01f);
        Assert.True(l.Cloud > 0.5f);

        var h = field.Evaluate(east, high);
        Assert.True(h.Wind.Y > 9f);
        Assert.True(h.Wind.X > 3f);
        Assert.Equal(0f, h.Cloud);
    }

    /// <summary>The baked map against the analytic field over 30 s, the camera fixed: bilinear at 1 km and the lerp in time.</summary>
    [Fact]
    public void MapMatchesFieldWithinInterpolationError()
    {
        var field = Field(5);
        var map = new WeatherMap(field);
        var centre = new Vector2(1_234f, -5_678f);
        var maxWind = 0f;
        var maxCloud = 0f;
        var maxRain = 0f;

        for (var tick = 0; tick < 60 * 30; tick++)
        {
            var t = 3_600.0 + tick * Tick;
            map.Tick(t, centre);
            Assert.True(map.Systems.Length < WeatherMap.MaxSystems);

            if (tick % 30 != 0)
                continue;

            for (var k = 0; k < 64; k++)
            {
                var p = centre + new Vector2((k % 8 - 3.5f) * 1_700f, (k / 8 - 3.5f) * 1_700f);
                var a = map.Sample(p.X, p.Y);
                var b = field.Sample(p.X, p.Y, t);
                maxWind = MathF.Max(maxWind, (a.Wind - b.Wind).Length());
                maxCloud = MathF.Max(maxCloud, MathF.Abs(a.Cloud - b.Cloud));
                maxRain = MathF.Max(maxRain, MathF.Abs(a.Rain - b.Rain));
            }
        }

        output.WriteLine($"max error: wind {maxWind:F3} m/s  cloud {maxCloud:F4}  rain {maxRain:F4}");
        Assert.True(maxWind < 1.5f);
        Assert.True(maxCloud < 0.08f);
        Assert.True(maxRain < 0.08f);
    }

    /// <summary>No pops at fixed points while snapshots rotate and re-centre under a camera flying 100 m/s. Some points must sit under a system, or the test proves nothing.</summary>
    [Fact]
    public void SampleIsSmoothTickToTick()
    {
        const int side = 8;
        var map = new WeatherMap(Field(9));
        var previous = new WeatherSample[side * side];
        var maxWind = 0f;
        var maxCloud = 0f;
        var underWeather = 0;

        for (var tick = 0; tick < 60 * 60; tick++)
        {
            var t = 7_200.0 + tick * Tick;
            map.Tick(t, new Vector2((float)(tick * Tick * 100.0), 0f));

            for (var k = 0; k < previous.Length; k++)
            {
                var s = map.Sample((k % side - 3.5f) * 2_000f + 3_000f, (k / side - 3.5f) * 2_000f);

                if (tick > 0)
                {
                    maxWind = MathF.Max(maxWind, (s.Wind - previous[k].Wind).Length());
                    maxCloud = MathF.Max(maxCloud, MathF.Abs(s.Cloud - previous[k].Cloud));
                }

                if (tick == 0 && MathF.Abs(s.Cloud - map.Field.Climate.BaseCloud) > 0.05f)
                    underWeather++;

                previous[k] = s;
            }
        }

        output.WriteLine($"max per-tick change: wind {maxWind:F5} m/s  cloud {maxCloud:F5}  points under weather {underWeather}/{previous.Length}  rebuilds {map.Rebuilds} ({map.LastRebuildMs:F3} ms)");
        Assert.True(underWeather > 0);
        Assert.Equal(1, map.Rebuilds);
        Assert.True(maxWind < 0.05f);
        Assert.True(maxCloud < 0.005f);
    }

    [Fact]
    public void TimeJumpRebuildsAndMatchesField()
    {
        var field = Field(4);
        var map = new WeatherMap(field);
        map.Tick(0.0, Vector2.Zero);
        map.Tick(5_000.0, Vector2.Zero);

        Assert.Equal(2, map.Rebuilds);

        var a = map.Sample(300f, -700f);
        var b = field.Sample(300f, -700f, 5_000.0);
        Assert.True((a.Wind - b.Wind).Length() < 1.5f);
        Assert.True(MathF.Abs(a.Cloud - b.Cloud) < 0.08f);
    }

    [Fact]
    public void TickAndSampleDoNotAllocate()
    {
        var field = Field(2);
        var map = new WeatherMap(field);

        for (var tick = 0; tick < 700; tick++)
        {
            map.Tick(tick * Tick, Vector2.Zero);
            map.Sample(100f, 200f);
            field.Sample(100f, 200f, tick * Tick);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var tick = 700; tick < 1400; tick++)
        {
            map.Tick(tick * Tick, new Vector2(tick, 0f));
            map.Sample(100f, 200f);
            field.Sample(100f, 200f, tick * Tick);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
