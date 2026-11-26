namespace BrutalGame.Crafting.Tests;

public class AntimatterTests
{
    private static readonly AmProductionParams Am = TestData.Data.Recipe("am.production").Am;

    [Fact]
    public void Produced_IsIntegralPowerOverProductionEnergy_WithRemainderKept()
    {
        var rng = new Random(41);
        var state = new AmFactoryState();
        long totalJ = 0, totalNg = 0, totalFeedMg = 0;

        for (var i = 0; i < 100_000; i++)
        {
            var j = rng.NextInt64(0, 2_000_000_000);
            totalJ += j;
            totalNg += AntimatterProduction.Step(ref state, Am, j, long.MaxValue, long.MaxValue / 4, out var feed);
            totalFeedMg += feed;
        }

        Assert.Equal(totalJ / 2000, totalNg);
        Assert.Equal(totalJ % 2000, state.BankedJ);
        Assert.Equal(totalFeedMg * Fixed.NgPerMg - totalNg, state.FeedCreditNg);
        Assert.InRange(state.FeedCreditNg, 0, Fixed.NgPerMg - 1);
    }

    [Fact]
    public void FullTraps_OrNoHydrogen_StopProduction()
    {
        var state = new AmFactoryState();

        Assert.Equal(500, AntimatterProduction.Step(ref state, Am, 10_000_000, 500, 10, out _));
        Assert.True(state.BankedJ < Am.JPerNg);
        Assert.Equal(0, AntimatterProduction.Step(ref state, Am, 10_000_000, 0, 10, out _));

        state = new AmFactoryState();
        Assert.Equal(0, AntimatterProduction.Step(ref state, Am, 10_000_000, long.MaxValue, 0, out var draw));
        Assert.Equal(0, draw);
    }

    [Fact]
    public void FactoryRates_MatchTheDoc_AndTheRoundTripIsTwentyPercent()
    {
        var hourJ = 20_000_000L * 3600;
        var state = new AmFactoryState();
        Assert.Equal(36_000_000, AntimatterProduction.Step(ref state, Am, hourJ, long.MaxValue, long.MaxValue / 4, out _));
        Assert.Equal(3600 * 60, AntimatterProduction.Ticks(36_000_000, 20_000_000, Am.JPerNg, 60));
        Assert.Equal(0.2, AntimatterProduction.RoundTrip(TestData.Data), 12);
    }
}
