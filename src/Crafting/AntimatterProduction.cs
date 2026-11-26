namespace BrutalGame.Crafting;

/// <summary>An AM factory's integer bank: joules not yet turned into ng, and hydrogen drawn but not yet used (ng).</summary>
public struct AmFactoryState
{
    public long BankedJ;
    public long FeedCreditNg;
}

/// <summary>
/// The antimatter production recipe (exotic-matter.md, Production): <c>m_AM = ∫P dt / AmProductionJ</c>, banked in
/// integer joules with the remainder kept, fed 1:1 by hydrogen mass. Full traps or no feed stop production and drop the bank
/// to its remainder (the factory idles). Run by the 10 Hz ledger: one call per factory, no allocation.
/// </summary>
public static class AntimatterProduction
{
    /// <summary>Banks <paramref name="grantedJ"/> and returns ng produced; <paramref name="feedDrawMg"/> is the hydrogen to debit.</summary>
    public static long Step(ref AmFactoryState state, in AmProductionParams p, long grantedJ, long trapSpaceNg, long feedAvailableMg,
        out long feedDrawMg)
    {
        feedDrawMg = 0;
        var banked = state.BankedJ + grantedJ;
        var byEnergy = banked / p.JPerNg;
        var feedNg = state.FeedCreditNg + (Int128)feedAvailableMg * Fixed.NgPerMg;
        var byFeed = p.FeedPpm <= 0 ? long.MaxValue : (long)Int128.Min(long.MaxValue, feedNg * Fixed.One / p.FeedPpm);
        var produced = Math.Max(0, Math.Min(byEnergy, Math.Min(trapSpaceNg, byFeed)));

        if (p.FeedPpm > 0)
        {
            var needNg = Fixed.MulDivCeil(produced, p.FeedPpm, Fixed.One);
            var shortNg = Math.Max(0, needNg - state.FeedCreditNg);
            feedDrawMg = (shortNg + Fixed.NgPerMg - 1) / Fixed.NgPerMg;
            state.FeedCreditNg += feedDrawMg * Fixed.NgPerMg - needNg;
        }

        banked -= produced * p.JPerNg;
        state.BankedJ = produced < byEnergy ? banked % p.JPerNg : banked;
        return produced;
    }

    /// <summary>Ticks a factory at constant <paramref name="powerW"/> needs for <paramref name="ng"/> (event scheduling).</summary>
    public static long Ticks(long ng, long powerW, long jPerNg, int tickRateHz) =>
        Fixed.CeilDiv((Int128)ng * jPerNg * tickRateHz, powerW);

    /// <summary>Power → AM → annihilation core → power: <c>AmEnergy / AmProductionJ · η_core</c>; below 1, so no free energy.</summary>
    public static double RoundTrip(CraftingData data) => data.AmEnergyJPerKg / data.AmProductionJPerKg * data.CoreEfficiency;
}
