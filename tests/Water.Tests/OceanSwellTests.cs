using BrutalGame.Water.Ocean;
using Brutal.Numerics;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

public sealed class OceanSwellTests(ITestOutputHelper output)
{
    private const double Tick = 1.0 / 60.0;

    private static readonly float2[] Points =
    [
        new(0f, 0f), new(13.7f, -4.2f), new(-250.5f, 88.1f), new(1024f, 2048f), new(-3.3f, 517.9f), new(77.7f, -901.2f),
    ];

    private static OceanSwell Create(ulong seed, in SwellParams p, double time = 0.0)
    {
        var swell = new OceanSwell(seed);
        swell.SetWind(p);
        swell.SetTime(time);
        return swell;
    }

    private static float Dot(float3 a, float3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    [Fact]
    public void SameSeedIsBitIdentical_DifferentSeedDiffers()
    {
        var p = new SwellParams();
        var a = Create(42, p, 123.4);
        var b = Create(42, p, 123.4);
        var c = Create(43, p, 123.4);
        var anyDiff = false;

        foreach (var pt in Points)
        {
            var sa = a.Sample(pt.X, pt.Y);
            var sb = b.Sample(pt.X, pt.Y);

            Assert.Equal(BitConverter.SingleToInt32Bits(sa.Height), BitConverter.SingleToInt32Bits(sb.Height));
            Assert.Equal(sa.Normal, sb.Normal);
            Assert.Equal(sa.Velocity, sb.Velocity);
            Assert.Equal(BitConverter.SingleToInt32Bits(sa.Jacobian), BitConverter.SingleToInt32Bits(sb.Jacobian));

            if (MathF.Abs(sa.Height - c.SampleHeight(pt.X, pt.Y)) > 1e-3f)
                anyDiff = true;
        }

        Assert.True(anyDiff);
    }

    [Fact]
    public void CalmWindIsFlat()
    {
        var swell = Create(42, new SwellParams { WindSpeed = 0f }, 10.0);

        foreach (var pt in Points)
        {
            var s = swell.Sample(pt.X, pt.Y);

            Assert.Equal(0f, s.Height);
            Assert.Equal(new float3(0f, 1f, 0f), s.Normal);
            Assert.Equal(new float3(0f, 0f, 0f), s.Velocity);
        }

        Assert.Equal(0f, swell.SignificantWaveHeight);
    }

    [Fact]
    public void SignificantWaveHeightMatchesSpectrum()
    {
        var p = new SwellParams();
        var swell = Create(42, p);

        double windSpeed = p.WindSpeed;
        double fetch = Math.Max(p.Fetch, Jonswap.MinFetch);
        var alpha = Jonswap.Alpha(windSpeed, fetch);
        var peak = Jonswap.PeakOmega(windSpeed, fetch);
        var lo = 0.5 * peak;
        var hi = Jonswap.Omega(2.0 * Math.PI / p.MinWavelength, p.Depth);

        const int steps = 20000;
        var dw = (hi - lo) / steps;
        var m0 = 0.0;
        for (var i = 0; i < steps; i++)
            m0 += Jonswap.Spectrum(lo + (i + 0.5) * dw, alpha, peak, p.Gamma) * dw;

        var expected = 4.0 * Math.Sqrt(m0);
        var measured = swell.SignificantWaveHeight;
        output.WriteLine($"Hs measured {measured:F4} m, expected {expected:F4} m, ratio {measured / expected:F4}");

        Assert.InRange(measured / expected, 0.85, 1.15);
        Assert.InRange(measured, 1.0f, 2.5f);
    }

    [Fact]
    public void HeightInvertsSurfacePoint()
    {
        var swell = Create(42, new SwellParams { WindSpeed = 15f }, 37.5);
        var maxErr = 0f;

        for (var ix = 0; ix < 20; ix++)
        for (var iz = 0; iz < 20; iz++)
        {
            var p = swell.SurfacePoint(ix * 7.31f - 70f, iz * 9.17f - 90f);
            maxErr = MathF.Max(maxErr, MathF.Abs(swell.SampleHeight(p.X, p.Z) - p.Y));
        }

        output.WriteLine($"Choppiness {swell.Choppiness:F3}, Hs {swell.SignificantWaveHeight:F3}, max inversion error {maxErr:F5} m");
        Assert.True(maxErr < 0.02f, $"max inversion error {maxErr} m");
    }

    [Fact]
    public void ChoppinessHoldsInHighSeas()
    {
        var swell = Create(42, new SwellParams { WindSpeed = 25f }, 5.0);
        output.WriteLine($"Choppiness {swell.Choppiness:F3}, Hs {swell.SignificantWaveHeight:F3}");
        Assert.InRange(swell.Choppiness, 0.79f, 0.81f);
    }

    [Fact]
    public void NormalMatchesFiniteDifferences()
    {
        var swell = Create(42, new SwellParams { WindSpeed = 15f, Choppiness = 0f }, 12.0);
        const float eps = 0.05f;
        var minDot = 1f;

        for (var i = 0; i < 100; i++)
        {
            var x = i * 3.71f - 180f;
            var z = i * -2.93f + 40f;
            var dhdx = (swell.SampleHeight(x + eps, z) - swell.SampleHeight(x - eps, z)) / (2f * eps);
            var dhdz = (swell.SampleHeight(x, z + eps) - swell.SampleHeight(x, z - eps)) / (2f * eps);
            var fd = float3.Normalize(new float3(-dhdx, 1f, -dhdz));
            minDot = MathF.Min(minDot, Dot(fd, swell.Sample(x, z).Normal));
        }

        output.WriteLine($"min normal dot {minDot:F6}");
        Assert.True(minDot > 0.999f, $"min dot {minDot}");
    }

    [Fact]
    public void WindChangeBlendsWithoutSnap()
    {
        var swell = Create(42, new SwellParams { WindSpeed = 6f, WindDirection = 0f });
        const float x = 12.5f;
        const float z = -7.5f;
        var prev = swell.SampleHeight(x, z);
        var maxStep = 0f;
        var tick = 0;

        for (; tick <= 120; tick++)
        {
            swell.SetTime(tick * Tick);
            var h = swell.SampleHeight(x, z);
            maxStep = MathF.Max(maxStep, MathF.Abs(h - prev));
            prev = h;
        }

        swell.SetWind(new SwellParams { WindSpeed = 20f, WindDirection = 2.5f });
        Assert.True(swell.BlendProgress < 1f);

        var h0 = swell.SampleHeight(x, z);
        maxStep = MathF.Max(maxStep, MathF.Abs(h0 - prev));
        prev = h0;

        for (var end = tick + 3600; tick <= end; tick++)
        {
            swell.SetTime(tick * Tick);
            var h = swell.SampleHeight(x, z);
            maxStep = MathF.Max(maxStep, MathF.Abs(h - prev));
            prev = h;
        }

        output.WriteLine($"max height step {maxStep:F4} m, final Hs {swell.SignificantWaveHeight:F3}, progress {swell.BlendProgress}");
        Assert.True(maxStep < 0.5f, $"max step {maxStep} m");
        Assert.Equal(1f, swell.BlendProgress);
        Assert.Equal(20f, swell.Wind.WindSpeed);
    }

    [Fact]
    public void VerticalVelocityMatchesHeightRate()
    {
        var swell = new OceanSwell(42);
        swell.SetWind(new SwellParams { WindSpeed = 12f, Choppiness = 0f });
        const double t = 55.0;
        const double dt = 0.01;
        var maxErr = 0f;

        swell.SetTime(t);
        var hs = swell.SignificantWaveHeight;

        foreach (var pt in Points)
        {
            swell.SetTime(t + dt);
            var hp = swell.SampleHeight(pt.X, pt.Y);
            swell.SetTime(t - dt);
            var hm = swell.SampleHeight(pt.X, pt.Y);
            swell.SetTime(t);
            var rate = (float)((hp - hm) / (2.0 * dt));
            maxErr = MathF.Max(maxErr, MathF.Abs(rate - swell.Sample(pt.X, pt.Y).Velocity.Y));
        }

        output.WriteLine($"Hs {hs:F3}, max |vy - dh/dt| {maxErr:F5} m/s");
        Assert.True(maxErr < 0.01f * hs + 0.005f, $"max err {maxErr} m/s");
    }

    /// <summary>The explicit wavenumber stays within 2% of the exact dispersion solve from the beach to deep water.</summary>
    [Fact]
    public void WaveNumberApprox_MatchesDispersion()
    {
        var worst = 0.0;
        foreach (var omega in (ReadOnlySpan<float>)[0.3f, 0.6f, 0.9f, 1.5f, 3f])
        {
            for (var depth = 0.05f; depth < 200f; depth *= 1.3f)
            {
                var exact = Jonswap.WaveNumber(omega, depth);
                worst = Math.Max(worst, Math.Abs(Jonswap.WaveNumberApprox(omega, depth) / exact - 1.0));
            }
        }

        output.WriteLine($"worst relative error {worst:P2}");
        Assert.True(worst < 0.02);
    }

    /// <summary>A groundswell has the height, period and direction asked for, with no wind at all.</summary>
    [Fact]
    public void Groundswell_HasItsHeightPeriodAndDirection()
    {
        var swell = Create(7, new SwellParams { WindSpeed = 0f, SwellHeight = 1.5f, SwellPeriod = 14f, SwellDirection = 0.6f });

        output.WriteLine($"Hs {swell.SignificantWaveHeight:F3} m, Tp {swell.PeakPeriod:F2} s, direction {swell.DominantDirection:F2}, components {swell.ComponentCount}");
        Assert.InRange(swell.SignificantWaveHeight, 1.49f, 1.51f);
        Assert.InRange(swell.PeakPeriod, 13.99f, 14.01f);
        Assert.Equal(0.6f, swell.DominantDirection);
        Assert.Equal(new SwellParams().SwellBands * new SwellParams().SwellDirections, swell.ComponentCount);
    }

    /// <summary>Swell and wind sea have independent random phases, so their variances add.</summary>
    [Fact]
    public void Groundswell_AddsToTheWindSea()
    {
        var wind = Create(7, new SwellParams { WindSpeed = 12f }).SignificantWaveHeight;
        var both = Create(7, new SwellParams { WindSpeed = 12f, SwellHeight = 1.5f, SwellPeriod = 14f }).SignificantWaveHeight;
        var expected = MathF.Sqrt(wind * wind + 1.5f * 1.5f);

        output.WriteLine($"wind sea Hs {wind:F3} m, with a 1.5 m groundswell {both:F3} m (expected {expected:F3})");
        Assert.InRange(both, 0.99f * expected, 1.01f * expected);
    }

    /// <summary>
    /// The surf carrier and the coastal maps follow the dominant band: the groundswell once its height is half the wind
    /// sea's or more, else the wind sea, whatever the swell's direction.
    /// </summary>
    [Fact]
    public void Carrier_FollowsTheDominantBand()
    {
        var swellDominant = Create(7, new SwellParams { WindSpeed = 8f, WindDirection = 1f, SwellHeight = 1.5f, SwellPeriod = 14f, SwellDirection = -0.4f });
        var windDominant = Create(7, new SwellParams { WindSpeed = 20f, WindDirection = 1f, SwellHeight = 0.3f, SwellPeriod = 14f, SwellDirection = -0.4f });

        output.WriteLine($"swell dominant: Tp {swellDominant.PeakPeriod:F1} s toward {swellDominant.DominantDirection:F2}; wind dominant: Tp {windDominant.PeakPeriod:F1} s toward {windDominant.DominantDirection:F2}");
        Assert.InRange(swellDominant.PeakPeriod, 13.99f, 14.01f);
        Assert.Equal(-0.4f, swellDominant.DominantDirection);
        Assert.True(windDominant.PeakPeriod < 13f, $"wind sea peak {windDominant.PeakPeriod} s");
        Assert.Equal(1f, windDominant.DominantDirection);
    }

    /// <summary>Sets come every 1 / (2 × bandwidth) waves of the dominant band: 8 for a wind sea, longer for narrower swell.</summary>
    [Fact]
    public void Sets_FollowTheDominantBandwidth()
    {
        var wind = Create(7, new SwellParams { WindSpeed = 12f });
        var swell = Create(7, new SwellParams { WindSpeed = 0f, SwellHeight = 1.5f, SwellBandwidth = 0.05f });
        var narrow = Create(7, new SwellParams { WindSpeed = 0f, SwellHeight = 1.5f, SwellBandwidth = 0.03f });

        output.WriteLine($"sets every {1f / wind.GroupRatio:F1} (wind sea), {1f / swell.GroupRatio:F1} (5% swell), {1f / narrow.GroupRatio:F1} (3% swell) waves");
        Assert.Equal(8f, 1f / wind.GroupRatio, 0.01f);
        Assert.Equal(10f, 1f / swell.GroupRatio, 0.01f);
        Assert.Equal(1f / 0.06f, 1f / narrow.GroupRatio, 0.01f);
    }
}
