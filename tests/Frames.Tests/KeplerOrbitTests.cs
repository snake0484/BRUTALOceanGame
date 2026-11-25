using Brutal.Numerics;

namespace BrutalGame.Frames.Tests;

public class KeplerOrbitTests
{
    private const double TickSeconds = 1.0 / 60.0;
    private const double PlanetMu = 9.80665e12;

    private static KeplerOrbit Eccentric(long epoch = 1234) => KeplerOrbit.Create(PlanetMu, 2.0e7, 0.3, 0.4, 1.1, 2.3, epoch, TickSeconds);

    [Theory]
    [InlineData(0L)]
    [InlineData(17L)]
    [InlineData(-5000L)]
    public void State_AfterWholePeriods_IsBitIdentical(long offset)
    {
        var orbit = Eccentric();
        orbit.State(orbit.EpochTick + offset, TickSeconds, out var p0, out var v0);

        foreach (var periods in new[] { 1L, 1000L, 1_000_000L, -3L })
        {
            orbit.State(orbit.EpochTick + offset + periods * orbit.PeriodTicks, TickSeconds, out var p, out var v);
            Assert.Equal(p0, p);
            Assert.Equal(v0, v);
        }
    }

    [Fact]
    public void State_ConservesEnergyAndAngularMomentum()
    {
        var orbit = Eccentric();
        var energy = -PlanetMu / (2.0 * orbit.SemiMajor);
        orbit.State(orbit.EpochTick, TickSeconds, out var p0, out var v0);
        var momentum = double3.Cross(p0, v0);
        var step = Math.Max(1L, orbit.PeriodTicks / 997);

        for (var tick = orbit.EpochTick; tick < orbit.EpochTick + orbit.PeriodTicks; tick += step)
        {
            orbit.State(tick, TickSeconds, out var p, out var v);
            var e = 0.5 * v.LengthSquared() - PlanetMu / p.Length();
            Assert.True(Math.Abs(e / energy - 1.0) < 1e-12, $"energy drift {e / energy - 1.0} at {tick}");
            Assert.True((double3.Cross(p, v) - momentum).Length() < 1e-12 * momentum.Length(), $"momentum drift at {tick}");
        }
    }

    [Fact]
    public void State_VelocityMatchesFiniteDifference()
    {
        var orbit = Eccentric();

        foreach (var tick in new[] { 0L, 999_999L, orbit.EpochTick + orbit.PeriodTicks / 3 })
        {
            orbit.State(tick - 1, TickSeconds, out var before, out _);
            orbit.State(tick + 1, TickSeconds, out var after, out _);
            orbit.State(tick, TickSeconds, out _, out var velocity);
            var difference = (after - before) / (2.0 * TickSeconds);
            Assert.True((difference - velocity).Length() < 1e-6 * velocity.Length());
        }
    }

    [Fact]
    public void State_PeriapsisAtEpochAndPeriodFitsMu()
    {
        var orbit = Eccentric();
        orbit.State(orbit.EpochTick, TickSeconds, out var p, out _);
        var period = 2.0 * Math.PI * Math.Sqrt(orbit.SemiMajor * orbit.SemiMajor * orbit.SemiMajor / PlanetMu);

        Assert.Equal(orbit.SemiMajor * (1.0 - orbit.Eccentricity), p.Length(), 1e-6);
        Assert.Equal(orbit.PeriodTicks * TickSeconds, period, 1e-6);
    }

    [Fact]
    public void Prograde_EquatorialOrbit_CirculatesAboutPlusY()
    {
        var orbit = KeplerOrbit.Create(PlanetMu, 2.0e7, 0, 0, 0, 0, 0, TickSeconds);
        orbit.State(0, TickSeconds, out var p, out var v);
        var axis = double3.Normalize(double3.Cross(p, v));

        Assert.True((axis - double3.UnitY).Length() < 1e-12);
        Assert.True(Math.Abs(p.Y) < 1e-6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.95)]
    public void SolveEccentric_SatisfiesKeplersEquation(double eccentricity)
    {
        for (var i = 0; i < 64; i++)
        {
            var mean = 2.0 * Math.PI * i / 64;
            var anomaly = KeplerOrbit.SolveEccentric(mean, eccentricity);
            Assert.True(Math.Abs(anomaly - eccentricity * Math.Sin(anomaly) - mean) < 1e-13);
        }
    }
}
