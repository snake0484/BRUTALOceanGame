namespace BrutalGame.Crafting;

public enum AddResult : byte
{
    Merged,
    Added,
    Invalid,
    OverMass,
    OverVolume,
    NoSlot,
}

/// <summary>
/// A container of stacks, struct-of-arrays, preallocated. Capacity is slots, mass and volume (from item density).
/// <para>Adding auto-merges into the first slot of the same item and merge bucket (<see cref="PurityRules.BucketKey"/>);
/// buckets keep assayed and unassayed apart and never span a gate. Removing an emptied slot swaps the last slot into it.</para>
/// <para>A stasis case is an inventory with <see cref="InStasis"/> set: its metastable stacks stop decaying.</para>
/// <para>Add, take and read allocate nothing.</para>
/// </summary>
public sealed class Inventory
{
    private const int MetastableKey = int.MinValue;

    public int Capacity => _item.Length;
    public int Count { get; private set; }
    public long MaxMassMg { get; }
    public long MaxVolumeMl { get; }
    public long MassMg { get; private set; }
    public long VolumeMl { get; private set; }
    public bool InStasis { get; private set; }

    private readonly CraftingData _data;
    private readonly ItemId[] _item;
    private readonly StackFlags[] _flags;
    private readonly int[] _spread;
    private readonly long[] _mass, _content, _nominal, _stamp;
    private readonly int[] _key;

    public Inventory(CraftingData data, int slots, long maxMassMg, long maxVolumeMl, bool inStasis = false)
    {
        _data = data;
        _item = new ItemId[slots];
        _flags = new StackFlags[slots];
        _spread = new int[slots];
        _mass = new long[slots];
        _content = new long[slots];
        _nominal = new long[slots];
        _stamp = new long[slots];
        _key = new int[slots];
        MaxMassMg = maxMassMg;
        MaxVolumeMl = maxVolumeMl;
        InStasis = inStasis;
    }

    public ItemId ItemAt(int slot) => _item[slot];

    /// <summary>The stored stack (metastable content at its origin).</summary>
    public ItemStack Peek(int slot) => new()
    {
        Item = _item[slot], Flags = _flags[slot], SpreadPpm = _spread[slot], MassMg = _mass[slot],
        ContentMg = _content[slot], NominalMg = _nominal[slot], Stamp = _stamp[slot],
    };

    /// <summary>The stack as it stands at <paramref name="tick"/>, without settling it.</summary>
    public ItemStack Read(int slot, long tick) => StackMath.At(Peek(slot), tick, _data.Item(_item[slot]).Decay);

    /// <summary>Total mass of one item, mg.</summary>
    public long MassOf(ItemId item)
    {
        long total = 0;

        for (var i = 0; i < Count; i++)
        {
            if (_item[i] == item)
                total += _mass[i];
        }

        return total;
    }

    public AddResult TryAdd(in ItemStack stack, long tick) => TryAdd(stack, tick, out _);

    public AddResult TryAdd(in ItemStack stack, long tick, out int slot)
    {
        slot = -1;

        if (stack.MassMg <= 0 || stack.Item.IsNone || stack.ContentMg < 0 || stack.ContentMg > stack.MassMg)
            return AddResult.Invalid;

        var def = _data.Item(stack.Item);
        var s = stack;
        StackMath.Settle(ref s, tick, def.Decay);
        StackMath.SetStasis(ref s, InStasis, tick);
        StackMath.MarkNew(ref s, tick, def.Decay);

        if (MassMg + s.MassMg > MaxMassMg)
            return AddResult.OverMass;

        var key = KeyOf(def, s);

        if (key != PurityRules.NeverMerge)
        {
            for (var i = 0; i < Count; i++)
            {
                if (_item[i] != s.Item || KeyAt(i, def, tick) != key)
                    continue;

                var volume = VolumeMl - def.VolumeMl(_mass[i]) + def.VolumeMl(_mass[i] + s.MassMg);

                if (volume > MaxVolumeMl)
                    return AddResult.OverVolume;

                var merged = Peek(i);
                StackMath.Settle(ref merged, tick, def.Decay);
                StackMath.Settle(ref s, tick, def.Decay);
                StackMath.Combine(ref merged, s);
                StackMath.Settle(ref merged, tick, def.Decay);
                Write(i, merged, def);
                MassMg += s.MassMg;
                VolumeMl = volume;
                slot = i;
                return AddResult.Merged;
            }
        }

        if (Count == _item.Length)
            return AddResult.NoSlot;

        var added = def.VolumeMl(s.MassMg);

        if (VolumeMl + added > MaxVolumeMl)
            return AddResult.OverVolume;

        slot = Count++;
        Write(slot, s, def);
        MassMg += s.MassMg;
        VolumeMl += added;
        return AddResult.Added;
    }

    /// <summary>Takes <paramref name="massMg"/> from a slot (settled to <paramref name="tick"/> first). Discrete items take whole units.</summary>
    public bool TryTake(int slot, long massMg, long tick, out ItemStack taken)
    {
        taken = default;

        if ((uint)slot >= (uint)Count || massMg <= 0 || massMg > _mass[slot])
            return false;

        var def = _data.Item(_item[slot]);

        if (def.IsDiscrete && massMg % def.UnitMassMg != 0)
            return false;

        var s = Peek(slot);
        StackMath.Settle(ref s, tick, def.Decay);
        var oldVolume = def.VolumeMl(s.MassMg);
        StackMath.Split(ref s, massMg, out taken);
        MassMg -= massMg;
        VolumeMl += def.VolumeMl(s.MassMg) - oldVolume;

        if (s.MassMg == 0)
            RemoveAt(slot);
        else
            Write(slot, s, def);

        return true;
    }

    /// <summary>Takes enough of a slot for at least <paramref name="nominalMg"/> of shown content.</summary>
    public bool TryTakeNominal(int slot, long nominalMg, long tick, out ItemStack taken)
    {
        taken = default;

        if ((uint)slot >= (uint)Count)
            return false;

        var mass = StackMath.MassForNominal(Read(slot, tick), nominalMg);
        return mass != long.MaxValue && TryTake(slot, mass, tick, out taken);
    }

    /// <summary>Debits content without mass (AM drawn from cells: the cell stays, emptier).</summary>
    public bool TryDebitContent(int slot, long contentMg)
    {
        if ((uint)slot >= (uint)Count || contentMg < 0 || contentMg > _content[slot])
            return false;

        var nominal = _nominal[slot] == _content[slot] ? contentMg : Fixed.MulDiv(_nominal[slot], contentMg, Math.Max(1, _content[slot]));
        _content[slot] -= contentMg;
        _nominal[slot] -= nominal;
        RefreshKey(slot);
        return true;
    }

    /// <summary>Powers a stasis case on or off. Exact and cheap: stamps shift, nothing settles.</summary>
    public void SetStasis(bool inStasis, long tick)
    {
        if (InStasis == inStasis)
            return;

        InStasis = inStasis;

        for (var i = 0; i < Count; i++)
        {
            var s = Peek(i);
            StackMath.SetStasis(ref s, inStasis, tick);
            _flags[i] = s.Flags;
            _stamp[i] = s.Stamp;
        }
    }

    /// <summary>A shock event reaching this room: every metastable stack loses its shock fraction (not in stasis).</summary>
    public void Shock(long tick)
    {
        for (var i = 0; i < Count; i++)
        {
            if ((_flags[i] & StackFlags.Metastable) == 0)
                continue;

            var def = _data.Item(_item[i]);
            var s = Peek(i);
            StackMath.Shock(ref s, tick, def.Decay);
            Write(i, s, def);
        }
    }

    public void Assay(int slot)
    {
        _nominal[slot] = _content[slot];
        _spread[slot] = 0;
        RefreshKey(slot);
    }

    private int KeyOf(ItemDef def, in ItemStack s) =>
        _data.Purity.BucketKey(def.Content, s.NominalMg, s.MassMg, s.IsAssayed, s.Metastable);

    private int KeyAt(int slot, ItemDef def, long tick)
    {
        if (_key[slot] != MetastableKey)
            return _key[slot];

        return KeyOf(def, Read(slot, tick));
    }

    private void RefreshKey(int slot) => _key[slot] = (_flags[slot] & StackFlags.Metastable) != 0
        ? MetastableKey
        : KeyOf(_data.Item(_item[slot]), Peek(slot));

    private void Write(int slot, in ItemStack s, ItemDef def)
    {
        _item[slot] = s.Item;
        _flags[slot] = s.Flags;
        _spread[slot] = s.SpreadPpm;
        _mass[slot] = s.MassMg;
        _content[slot] = s.ContentMg;
        _nominal[slot] = s.NominalMg;
        _stamp[slot] = s.Stamp;
        _key[slot] = s.Metastable ? MetastableKey : KeyOf(def, s);
    }

    private void RemoveAt(int slot)
    {
        var last = --Count;

        if (slot != last)
        {
            _item[slot] = _item[last];
            _flags[slot] = _flags[last];
            _spread[slot] = _spread[last];
            _mass[slot] = _mass[last];
            _content[slot] = _content[last];
            _nominal[slot] = _nominal[last];
            _stamp[slot] = _stamp[last];
            _key[slot] = _key[last];
        }

        _item[last] = default;
        _mass[last] = _content[last] = _nominal[last] = _stamp[last] = 0;
    }
}
