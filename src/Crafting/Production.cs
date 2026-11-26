namespace BrutalGame.Crafting;

public enum StartStatus : byte
{
    Ok,
    WrongKind,
    BadInput,
    BelowMinIn,
    AtOrAboveCap,
    OutsideGate,
    NotEnough,
    NoSolvent,
    NoAntimatter,
    NoSeed,
    QueueFull,
}

/// <summary>
/// Starts batches: checks every gate and input first, then debits the inventory (and AM) and schedules the batch with its
/// outputs already computed, so completion is a heap pop plus <see cref="Deliver"/>. Nothing is spent on a refusal.
/// Server-side event code: no allocation.
/// </summary>
public static class Production
{
    private enum TakeMode : byte
    {
        Mass,
        Nominal,
        Antimatter,
    }

    private struct Take
    {
        public int Slot;
        public long Amount;
        public TakeMode Mode;
    }

    private const int MaxTakes = 32;

    /// <summary>One <c>Enrich</c> batch of <paramref name="massMg"/> from <paramref name="slot"/>; T4 also takes a seed from <paramref name="seedSlot"/>.</summary>
    public static StartStatus StartEnrich(CraftingData data, RecipeDef recipe, Inventory inv, int slot, long massMg, int seedSlot,
        ref long amNg, long tick, ulong serverSeed, ProductionQueue queue, int station, out long batchId)
    {
        batchId = 0;

        if (recipe.Kind != RecipeKind.Enrich)
            return StartStatus.WrongKind;

        ref readonly var r = ref recipe.Enrich;

        if ((uint)slot >= (uint)inv.Count || data.Item(inv.ItemAt(slot)).FamilyId != r.FamilyId)
            return StartStatus.BadInput;

        var stock = inv.Read(slot, tick);

        if (massMg <= 0 || massMg > stock.MassMg)
            return StartStatus.BadInput;

        StackMath.Split(ref stock, massMg, out var portion);
        var status = EnrichMath.Balance(portion, r, serverSeed, queue.PeekNextId, out var product, out var residue, out _);

        if (status != EnrichStatus.Ok)
            return status == EnrichStatus.BelowMinIn ? StartStatus.BelowMinIn : status == EnrichStatus.AtOrAboveCap ? StartStatus.AtOrAboveCap : StartStatus.BadInput;

        var solvent = EnrichMath.SolventMg(massMg, r);

        if (solvent > 0 && inv.MassOf(r.Solvent) < solvent)
            return StartStatus.NoSolvent;

        var am = EnrichMath.AmNg(massMg, r);

        if (am > amNg)
            return StartStatus.NoAntimatter;

        long seedMass = 0;

        if (r.NeedsSeed)
        {
            if ((uint)seedSlot >= (uint)inv.Count || seedSlot == slot || data.Item(inv.ItemAt(seedSlot)).FamilyId != r.FamilyId)
                return StartStatus.NoSeed;

            var seed = inv.Read(seedSlot, tick);
            seedMass = StackMath.MassForNominal(seed, EnrichMath.SeedContentMg(portion.NominalMg, r));

            if (!Fixed.AtLeast(seed.NominalMg, seed.MassMg, r.SeedMinPpm) || seedMass == long.MaxValue || seed.NominalMg < EnrichMath.SeedContentMg(portion.NominalMg, r))
                return StartStatus.NoSeed;
        }

        if (queue.Count == queue.Capacity)
            return StartStatus.QueueFull;

        ItemStack seedTaken = default;

        if (r.NeedsSeed && seedSlot > slot)
            inv.TryTake(seedSlot, seedMass, tick, out seedTaken);

        inv.TryTake(slot, massMg, tick, out _);

        if (r.NeedsSeed && seedSlot < slot)
            inv.TryTake(seedSlot, seedMass, tick, out seedTaken);

        TakeItemMass(inv, r.Solvent, solvent, tick);
        amNg -= am;

        Span<ItemStack> outputs = stackalloc ItemStack[3];
        var count = 0;
        outputs[count++] = product;

        if (residue.MassMg > 0)
            outputs[count++] = residue;

        if (r.NeedsSeed)
        {
            StackMath.Split(ref seedTaken, EnrichMath.SeedUsedMg(seedTaken.MassMg, r), out _);

            if (seedTaken.MassMg > 0)
                outputs[count++] = seedTaken;
        }

        queue.TrySchedule(station, recipe.Id, tick, EnrichMath.Ticks(massMg, r.ThroughputMgPerHour, data.TickRateHz), outputs[..count], out batchId);
        return StartStatus.Ok;
    }

    /// <summary>A <c>Blend</c> batch: dilutes <paramref name="massMg"/> of EM with agent to the target purity (or the agent minimum).</summary>
    public static StartStatus StartBlend(CraftingData data, RecipeDef recipe, Inventory inv, int slot, long massMg, long tick,
        ProductionQueue queue, int station, out long batchId)
    {
        batchId = 0;

        if (recipe.Kind != RecipeKind.Blend)
            return StartStatus.WrongKind;

        ref readonly var b = ref recipe.Blend;

        if ((uint)slot >= (uint)inv.Count || data.Item(inv.ItemAt(slot)).FamilyId != b.FamilyId)
            return StartStatus.BadInput;

        var stock = inv.Read(slot, tick);

        if (massMg <= 0 || massMg > stock.MassMg)
            return StartStatus.BadInput;

        StackMath.Split(ref stock, massMg, out var portion);

        if (!Fixed.AtLeast(portion.NominalMg, massMg, b.MinPpm) || !Fixed.AtMost(portion.NominalMg, massMg, b.MaxPpm))
            return StartStatus.OutsideGate;

        var agent = AgentMg(portion, b);

        if (inv.MassOf(b.Agent) < agent)
            return StartStatus.NotEnough;

        if (queue.Count == queue.Capacity)
            return StartStatus.QueueFull;

        inv.TryTake(slot, massMg, tick, out portion);
        TakeItemMass(inv, b.Agent, agent, tick);
        Span<ItemStack> parts = stackalloc ItemStack[2];
        parts[0] = portion;
        parts[1] = ItemStack.Plain(b.Agent, agent);
        Span<ItemStack> outputs = stackalloc ItemStack[1];
        outputs[0] = StackMath.Blend(parts, b.Output);
        queue.TrySchedule(station, recipe.Id, tick, EnrichMath.Ticks(massMg + agent, b.ThroughputMgPerHour, data.TickRateHz), outputs, out batchId);
        return StartStatus.Ok;
    }

    /// <summary>Agent for a slurry: enough to bring the nominal purity to the target, and at least the agent fraction.</summary>
    public static long AgentMg(in ItemStack em, in BlendParams b)
    {
        var toTarget = Math.Max(0, Fixed.MulDivCeil(em.NominalMg, Fixed.One, b.TargetPpm) - em.MassMg);
        var minimum = Fixed.MulDivCeil(em.MassMg, b.AgentMinFractionPpm, Fixed.One - b.AgentMinFractionPpm);
        return Math.Max(toTarget, minimum);
    }

    /// <summary><paramref name="units"/> of a <c>Fixed</c> recipe (count, or kg of output for per-kg recipes).</summary>
    public static StartStatus StartFixed(CraftingData data, RecipeDef recipe, int units, Inventory inv, long tick,
        ProductionQueue queue, int station, out long batchId)
    {
        batchId = 0;

        if (recipe.Kind != RecipeKind.Fixed || units <= 0 || recipe.Outputs.Length > ProductionQueue.MaxOutputs)
            return StartStatus.WrongKind;

        Span<Take> takes = stackalloc Take[MaxTakes];
        var count = 0;

        foreach (var input in recipe.Inputs)
        {
            var need = input.Kind == InputKind.ItemCount ? input.Amount * data.Item(input.Item).UnitMassMg * units : input.Amount * units;

            if (!Plan(data, inv, input, need, tick, takes, ref count))
                return StartStatus.NotEnough;
        }

        if (queue.Count == queue.Capacity)
            return StartStatus.QueueFull;

        SortDescending(takes[..count]);

        foreach (ref readonly var t in takes[..count])
        {
            switch (t.Mode)
            {
                case TakeMode.Mass:
                    inv.TryTake(t.Slot, t.Amount, tick, out _);
                    break;
                case TakeMode.Nominal:
                    inv.TryTakeNominal(t.Slot, t.Amount, tick, out _);
                    break;
                default:
                    inv.TryDebitContent(t.Slot, t.Amount);
                    break;
            }
        }

        Span<ItemStack> outputs = stackalloc ItemStack[ProductionQueue.MaxOutputs];

        for (var i = 0; i < recipe.Outputs.Length; i++)
        {
            var o = recipe.Outputs[i];
            var mass = o.MassMg * units;
            outputs[i] = ItemStack.Assayed(o.Item, mass, Fixed.MulDiv(mass, o.ContentPpm, Fixed.One));
        }

        queue.TrySchedule(station, recipe.Id, tick, recipe.TicksPerUnit * units, outputs[..recipe.Outputs.Length], out batchId);
        return StartStatus.Ok;
    }

    /// <summary>Adds a completed batch's outputs to an inventory (new crystal starts decaying here). Returns the count delivered.</summary>
    public static int Deliver(Inventory inv, ReadOnlySpan<ItemStack> outputs, long tick)
    {
        var delivered = 0;

        foreach (ref readonly var o in outputs)
        {
            var result = inv.TryAdd(o, tick);

            if (result is AddResult.Merged or AddResult.Added)
                delivered++;
        }

        return delivered;
    }

    private static bool Plan(CraftingData data, Inventory inv, in RecipeInput input, long need, long tick, Span<Take> takes, ref int count)
    {
        if (input.Kind != InputKind.FamilyContent)
        {
            var mode = input.Kind == InputKind.Antimatter ? TakeMode.Antimatter : TakeMode.Mass;

            for (var i = 0; i < inv.Count && need > 0; i++)
            {
                if (inv.ItemAt(i) != input.Item)
                    continue;

                var s = inv.Peek(i);
                var free = (mode == TakeMode.Mass ? s.MassMg : s.ContentMg) - Reserved(takes[..count], i, mode);
                var take = Math.Min(free, need);

                if (take <= 0 || !Add(takes, ref count, i, take, mode))
                    continue;

                need -= take;
            }

            return need <= 0;
        }

        while (need > 0)
        {
            var best = -1;
            long bestFree = 0;
            var bestPurity = int.MaxValue;

            for (var i = 0; i < inv.Count; i++)
            {
                if (data.Item(inv.ItemAt(i)).FamilyId != input.FamilyId)
                    continue;

                var s = inv.Read(i, tick);

                if (!Fixed.AtLeast(s.NominalMg, s.MassMg, input.MinPpm) || !Fixed.AtMost(s.NominalMg, s.MassMg, input.MaxPpm))
                    continue;

                var free = s.NominalMg - Reserved(takes[..count], i, TakeMode.Nominal);
                var purity = s.NominalPurityPpm;

                if (free > 0 && purity < bestPurity)
                {
                    best = i;
                    bestFree = free;
                    bestPurity = purity;
                }
            }

            if (best < 0)
                return false;

            var take = Math.Min(bestFree, need);

            if (!Add(takes, ref count, best, take, TakeMode.Nominal))
                return false;

            need -= take;
        }

        return true;
    }

    private static long Reserved(ReadOnlySpan<Take> takes, int slot, TakeMode mode)
    {
        long total = 0;

        foreach (ref readonly var t in takes)
        {
            if (t.Slot == slot && t.Mode == mode)
                total += t.Amount;
        }

        return total;
    }

    private static bool Add(Span<Take> takes, ref int count, int slot, long amount, TakeMode mode)
    {
        for (var i = 0; i < count; i++)
        {
            if (takes[i].Slot != slot || takes[i].Mode != mode)
                continue;

            takes[i].Amount += amount;
            return true;
        }

        if (count == takes.Length)
            return false;

        takes[count++] = new Take { Slot = slot, Amount = amount, Mode = mode };
        return true;
    }

    private static void SortDescending(Span<Take> takes)
    {
        for (var i = 1; i < takes.Length; i++)
        {
            var t = takes[i];
            var j = i - 1;

            while (j >= 0 && takes[j].Slot < t.Slot)
            {
                takes[j + 1] = takes[j];
                j--;
            }

            takes[j + 1] = t;
        }
    }

    private static void TakeItemMass(Inventory inv, ItemId item, long mass, long tick)
    {
        for (var i = inv.Count - 1; i >= 0 && mass > 0; i--)
        {
            if (inv.ItemAt(i) != item)
                continue;

            var take = Math.Min(mass, inv.Peek(i).MassMg);
            inv.TryTake(i, take, tick, out _);
            mass -= take;
        }
    }
}
