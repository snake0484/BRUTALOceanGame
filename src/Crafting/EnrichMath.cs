namespace BrutalGame.Crafting;

public enum EnrichStatus : byte
{
    Ok,
    Empty,
    WrongFamily,
    BelowMinIn,
    AtOrAboveCap,
}

/// <summary>
/// The odds-ratio <c>Enrich</c> family (exotic-matter.md, Refining), in integer mg:
/// <code>
/// q_out = min(QCap, α·odds(q_in) back to q)     requires MinIn ≤ q_in &lt; QCap
/// c_p = Y·c_in,  m_p = c_p / q_out              (mass rounded up, so purity never exceeds q_out or the cap)
/// c_burnt = λ·c_in,  c_r = c_in − c_p − c_burnt
/// m_r = c_r + [(m_in − c_in) − (m_p − c_p)]
/// </code>
/// so <c>m_in = m_p + m_r + c_burnt</c> and <c>c_in = c_p + c_r + c_burnt</c> exactly.
/// </summary>
public static class EnrichMath
{
    /// <summary>q_out in ppm (floored) for an input purity, as the UI preview shows it.</summary>
    public static int Preview(int qInPpm, in EnrichParams r)
    {
        var boosted = (Int128)r.AlphaPpm * qInPpm;
        var denominator = (Int128)(Fixed.One - qInPpm) * Fixed.One + boosted;
        var q = (int)(boosted * Fixed.One / denominator);
        return Math.Min(q, r.CapPpm);
    }

    /// <summary>
    /// Splits one batch input into product, residue and burnt content. Gates check nominal purity (what the owner sees);
    /// the cap is also checked on true purity because the maths needs q_in &lt; QCap. The run uses true content; the product
    /// and residue inherit the input's nominal error, or the recipe's own seeded error when that is larger.
    /// </summary>
    public static EnrichStatus Balance(in ItemStack input, in EnrichParams r, ulong serverSeed, long batchId,
        out ItemStack product, out ItemStack residue, out long burntMg)
    {
        product = residue = default;
        burntMg = 0;
        long m = input.MassMg, c = input.ContentMg;

        if (m <= 0 || c <= 0)
            return EnrichStatus.Empty;

        if (!Fixed.AtLeast(input.NominalMg, m, r.MinInPpm))
            return EnrichStatus.BelowMinIn;

        if (Fixed.AtLeast(input.NominalMg, m, r.CapPpm) || Fixed.AtLeast(c, m, r.CapPpm))
            return EnrichStatus.AtOrAboveCap;

        var impurity = m - c;
        var boosted = (Int128)r.AlphaPpm * c;
        var denominator = (Int128)impurity * Fixed.One + boosted;
        var capped = boosted * Fixed.One >= (Int128)r.CapPpm * denominator;

        var cp = Fixed.MulDiv(c, r.YieldPpm, Fixed.One);
        var mp = capped ? Fixed.MulDivCeil(cp, Fixed.One, r.CapPpm) : Fixed.CeilDiv((Int128)cp * denominator, boosted);
        mp = Math.Clamp(mp, cp, cp + impurity);
        burntMg = Fixed.MulDiv(c, r.LossPpm, Fixed.One);
        var cr = c - cp - burntMg;
        var mr = cr + impurity - (mp - cp);

        var error = (long)((Int128)(input.NominalMg - c) * Fixed.One / c);
        var spread = input.SpreadPpm;

        if (r.SpreadOutPpm > 0)
        {
            var fresh = r.SpreadOutPpm * Fixed.SignedNoisePpm(serverSeed, batchId) / Fixed.One;

            if (Math.Abs(fresh) > Math.Abs(error))
                error = fresh;

            spread = Math.Max(spread, r.SpreadOutPpm);
        }

        product = Output(input.Item, mp, cp, error, spread);
        residue = Output(input.Item, mr, cr, error, spread);
        return EnrichStatus.Ok;
    }

    /// <summary>One batch's length: <c>m_in / Throughput</c>, rounded up to whole ticks.</summary>
    public static long Ticks(long inputMassMg, long throughputMgPerHour, int tickRateHz) =>
        Fixed.CeilDiv((Int128)inputMassMg * 3600 * tickRateHz, throughputMgPerHour);

    /// <summary>Solvent mass a batch consumes.</summary>
    public static long SolventMg(long inputMassMg, in EnrichParams r) => Fixed.MulDivCeil(inputMassMg, r.SolventPpm, Fixed.One);

    /// <summary>AM a batch consumes, ng.</summary>
    public static long AmNg(long inputMassMg, in EnrichParams r) => Fixed.MulDivCeil(inputMassMg, r.AmNgPerKg, Fixed.MgPerKg);

    /// <summary>Smallest seed content (T4): a fraction of the batch's content.</summary>
    public static long SeedContentMg(long inputContentMg, in EnrichParams r) => Fixed.MulDivCeil(inputContentMg, r.SeedFracPpm, Fixed.One);

    /// <summary>Seed mass used up; the rest of the seed comes back with the product.</summary>
    public static long SeedUsedMg(long seedMassMg, in EnrichParams r) => Fixed.MulDivCeil(seedMassMg, r.SeedUsePpm, Fixed.One);

    private static ItemStack Output(ItemId item, long mass, long content, long errorPpm, int spread)
    {
        var nominal = content + (long)((Int128)content * errorPpm / Fixed.One);
        return new ItemStack { Item = item, MassMg = mass, ContentMg = content, NominalMg = nominal, SpreadPpm = spread };
    }
}
