namespace BrutalGame.Crafting;

public enum AdditiveCheck : byte
{
    Ok,
    NoContent,
    NotAdditive,
    TooManyKinds,
    TooMuchMass,
    Purity,
}

/// <summary>
/// Wormhole charge blend rules (exotic-matter.md, Wormhole additives), in integer mg:
/// <code>
/// limits:  at most MaxKinds kinds;  Σ m_add ≤ MaxMassFraction · X
/// gate:    X ≥ MinPurity · (m_EM + Σ m_add)          additives count as impurity
/// effect_i = min(1, (c_add,i / X) / FullActive)
/// </code>
/// The plan checks nominal content; the server re-checks true content at charge start and refuses with nothing spent.
/// </summary>
public static class AdditiveMath
{
    /// <summary>
    /// Checks the reserved EM (X content and its mass) with up to <see cref="AdditiveRules.MaxKinds"/> additive kinds.
    /// Additive stacks must be settled to the charge tick. Writes each kind's effect (ppm) and item, in first-seen order.
    /// </summary>
    public static AdditiveCheck Check(CraftingData data, in ItemStack em, ReadOnlySpan<ItemStack> additives, int minPurityPpm,
        bool useTrue, Span<ItemId> kinds, Span<int> effectPpm, out int kindCount)
    {
        var rules = data.Additives;
        kindCount = 0;
        var x = useTrue ? em.ContentMg : em.NominalMg;

        if (x <= 0 || em.MassMg <= 0)
            return AdditiveCheck.NoContent;

        Span<long> active = stackalloc long[Math.Max(1, rules.MaxKinds)];
        active.Clear();
        long addedMass = 0;

        foreach (ref readonly var a in additives)
        {
            if (data.Item(a.Item).Content != ContentKind.Active)
                return AdditiveCheck.NotAdditive;

            var kind = 0;

            while (kind < kindCount && kinds[kind] != a.Item)
                kind++;

            if (kind == kindCount)
            {
                if (kindCount == rules.MaxKinds || kindCount == kinds.Length)
                    return AdditiveCheck.TooManyKinds;

                kinds[kindCount++] = a.Item;
            }

            active[kind] += useTrue ? a.ContentMg : a.NominalMg;
            addedMass += a.MassMg;
        }

        if ((Int128)addedMass * Fixed.One > (Int128)rules.MaxMassFractionPpm * x)
            return AdditiveCheck.TooMuchMass;

        if ((Int128)x * Fixed.One < (Int128)minPurityPpm * (em.MassMg + addedMass))
            return AdditiveCheck.Purity;

        for (var i = 0; i < kindCount; i++)
            effectPpm[i] = (int)Int128.Min(Fixed.One, (Int128)active[i] * Fixed.One * Fixed.One / ((Int128)x * rules.FullActivePpm));

        return AdditiveCheck.Ok;
    }

    /// <summary>A factor whose full-strength value is <paramref name="full"/>, applied at <paramref name="effectPpm"/>.</summary>
    public static double Factor(double full, int effectPpm) => 1 + (full - 1) * effectPpm / Fixed.One;

    /// <summary>
    /// Blended factors, indexed by <see cref="CraftingData.FactorKeys"/>: multiplicative keys start at 1 and multiply when
    /// two additives share them; keys ending in <c>Add</c> start at 0 and sum.
    /// </summary>
    public static void Factors(CraftingData data, ReadOnlySpan<ItemId> kinds, ReadOnlySpan<int> effectPpm, Span<double> factors)
    {
        for (var k = 0; k < factors.Length; k++)
            factors[k] = data.FactorIsAdditive(k) ? 0 : 1;

        for (var i = 0; i < kinds.Length; i++)
        {
            var def = data.Additive(kinds[i]);

            if (def is null)
                continue;

            Apply(def.Effects, effectPpm[i], factors);
            Apply(def.TradeOffs, effectPpm[i], factors);
        }
    }

    private static void Apply(FactorEntry[] entries, int effectPpm, Span<double> factors)
    {
        foreach (var e in entries)
        {
            if (e.Additive)
                factors[e.Key] += e.Value * effectPpm / Fixed.One;
            else
                factors[e.Key] *= Factor(e.Value, effectPpm);
        }
    }
}
