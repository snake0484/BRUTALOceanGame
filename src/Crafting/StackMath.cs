namespace BrutalGame.Crafting;

/// <summary>
/// Exact stack operations. Split, merge and blend conserve mass, true content and nominal content to the mg.
/// Decay is lazy: <c>c(t) = max(floor·m, c0 − rate·m·elapsed)</c>, a pure function of the stored origin, so reading it
/// every tick or once gives the same value, and stasis toggles shift the stamp instead of settling.
/// </summary>
public static class StackMath
{
    /// <summary>Merges <paramref name="add"/> into <paramref name="into"/> (same item, both settled to one tick).</summary>
    public static void Combine(ref ItemStack into, in ItemStack add)
    {
        into.SpreadPpm = MergedSpread(into.SpreadPpm, into.ContentMg, add.SpreadPpm, add.ContentMg);
        into.MassMg += add.MassMg;
        into.ContentMg += add.ContentMg;
        into.NominalMg += add.NominalMg;
    }

    /// <summary>
    /// Sums mass, true and nominal content of <paramref name="parts"/> into one stack of <paramref name="output"/>.
    /// Spread is the true-content-weighted mean of the parts' spreads, rounded up, so it still bounds the merged error.
    /// </summary>
    public static ItemStack Blend(ReadOnlySpan<ItemStack> parts, ItemId output)
    {
        var result = new ItemStack { Item = output };
        Int128 weighted = 0;
        var maxSpread = 0;

        foreach (ref readonly var p in parts)
        {
            result.MassMg += p.MassMg;
            result.ContentMg += p.ContentMg;
            result.NominalMg += p.NominalMg;
            weighted += (Int128)p.SpreadPpm * p.ContentMg;
            maxSpread = Math.Max(maxSpread, p.SpreadPpm);
        }

        result.SpreadPpm = result.ContentMg > 0 ? (int)Fixed.CeilDiv(weighted, result.ContentMg) : maxSpread;
        return result;
    }

    /// <summary>
    /// Moves <paramref name="massMg"/> of <paramref name="from"/> into <paramref name="part"/>. Content splits by mass;
    /// the nominal error splits with it (truncated toward zero), and the remainder keeps the rest, so sums are exact.
    /// </summary>
    public static void Split(ref ItemStack from, long massMg, out ItemStack part)
    {
        part = from;

        if (massMg >= from.MassMg)
        {
            from.MassMg = from.ContentMg = from.NominalMg = 0;
            return;
        }

        var content = Fixed.MulDiv(from.ContentMg, massMg, from.MassMg);
        var error = from.ContentMg > 0 ? (long)((Int128)(from.NominalMg - from.ContentMg) * content / from.ContentMg) : 0;
        part.MassMg = massMg;
        part.ContentMg = content;
        part.NominalMg = content + error;
        from.MassMg -= massMg;
        from.ContentMg -= content;
        from.NominalMg -= part.NominalMg;
    }

    /// <summary>The mass to take for at least <paramref name="nominalMg"/> of shown content.</summary>
    public static long MassForNominal(in ItemStack s, long nominalMg)
    {
        if (s.NominalMg <= 0)
            return long.MaxValue;

        return Math.Min(s.MassMg, Fixed.MulDivCeil(nominalMg, s.MassMg, s.NominalMg));
    }

    /// <summary>Assay: the owner now sees the true content.</summary>
    public static void Assay(ref ItemStack s)
    {
        s.NominalMg = s.ContentMg;
        s.SpreadPpm = 0;
    }

    public static int MergedSpread(int spreadA, long contentA, int spreadB, long contentB)
    {
        var total = contentA + contentB;

        if (total <= 0)
            return Math.Max(spreadA, spreadB);

        return (int)Fixed.CeilDiv((Int128)spreadA * contentA + (Int128)spreadB * contentB, total);
    }

    /// <summary>Elapsed decay ticks at <paramref name="tick"/>; frozen while in stasis.</summary>
    public static long Elapsed(in ItemStack s, long tick) => !s.Metastable ? 0 : s.InStasis ? s.Stamp : Math.Max(0, tick - s.Stamp);

    /// <summary>True and nominal content at <paramref name="tick"/>, without changing the stack.</summary>
    public static void Current(in ItemStack s, long tick, in DecayRule rule, out long contentMg, out long nominalMg)
    {
        contentMg = s.ContentMg;
        nominalMg = s.NominalMg;

        if (!s.Metastable || !rule.Enabled)
            return;

        var floor = FloorContent(s.MassMg, rule);
        var loss = (long)((Int128)s.MassMg * rule.RatePpmPerDay * Elapsed(s, tick) / ((Int128)Fixed.One * rule.TicksPerDay));
        var decayed = Math.Max(Math.Min(floor, s.ContentMg), s.ContentMg - loss);
        nominalMg = ScaleNominal(s.NominalMg, s.ContentMg, decayed);
        contentMg = decayed;
    }

    /// <summary>A copy of <paramref name="s"/> as it stands at <paramref name="tick"/>.</summary>
    public static ItemStack At(in ItemStack s, long tick, in DecayRule rule)
    {
        var copy = s;
        Settle(ref copy, tick, rule);
        return copy;
    }

    /// <summary>Writes the decayed content and re-stamps the origin at <paramref name="tick"/>; clears the flag at the floor.</summary>
    public static void Settle(ref ItemStack s, long tick, in DecayRule rule)
    {
        if (!s.Metastable)
            return;

        Current(s, tick, rule, out var content, out var nominal);
        s.ContentMg = content;
        s.NominalMg = nominal;
        s.Stamp = s.InStasis ? 0 : tick;

        if (!rule.Enabled || content <= FloorContent(s.MassMg, rule))
        {
            s.Flags &= ~StackFlags.Metastable;
            s.Stamp = 0;
        }
    }

    /// <summary>Starts decay for a stack at or above its band (crystal, a mined geode, seed lattice).</summary>
    public static void MarkNew(ref ItemStack s, long tick, in DecayRule rule)
    {
        if (s.Metastable || !rule.Enabled || s.MassMg <= 0)
            return;

        if (!Fixed.AtLeast(s.ContentMg, s.MassMg, rule.BandPpm) || s.ContentMg <= FloorContent(s.MassMg, rule))
            return;

        s.Flags |= StackFlags.Metastable;
        s.Stamp = s.InStasis ? 0 : tick;
    }

    /// <summary>Moves a stack into or out of a powered stasis case. Exact: the elapsed time is kept, nothing settles.</summary>
    public static void SetStasis(ref ItemStack s, bool inStasis, long tick)
    {
        if (s.InStasis == inStasis)
            return;

        if (s.Metastable)
            s.Stamp = tick - s.Stamp;

        s.Flags = inStasis ? s.Flags | StackFlags.InStasis : s.Flags & ~StackFlags.InStasis;
    }

    /// <summary>A shock event: settle, then <c>q ← max(floor, q − loss)</c>. Stasis blocks it; stable stacks ignore it.</summary>
    public static void Shock(ref ItemStack s, long tick, in DecayRule rule)
    {
        if (!s.Metastable || s.InStasis || rule.ShockPpm <= 0)
            return;

        Settle(ref s, tick, rule);

        if (!s.Metastable)
            return;

        var floor = FloorContent(s.MassMg, rule);
        var shocked = Math.Max(floor, s.ContentMg - Fixed.MulDivCeil(s.MassMg, rule.ShockPpm, Fixed.One));
        s.NominalMg = ScaleNominal(s.NominalMg, s.ContentMg, shocked);
        s.ContentMg = shocked;
        Settle(ref s, tick, rule);
    }

    /// <summary>Smallest content whose purity is at or above the floor.</summary>
    public static long FloorContent(long massMg, in DecayRule rule) => Fixed.MulDivCeil(massMg, rule.FloorPpm, Fixed.One);

    private static long ScaleNominal(long nominal, long content, long newContent)
    {
        if (nominal == content || content <= 0)
            return newContent;

        return (long)((Int128)nominal * newContent / content);
    }
}
