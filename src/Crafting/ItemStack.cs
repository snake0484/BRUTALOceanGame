using System.Runtime.InteropServices;

namespace BrutalGame.Crafting;

/// <summary>Dense item index assigned at load (ordinal order of keys); 0 is no item.</summary>
public readonly record struct ItemId(ushort Value)
{
    public static ItemId None => default;

    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString();
}

[Flags]
public enum StackFlags : byte
{
    None = 0,
    Metastable = 1,
    InStasis = 2,
}

/// <summary>
/// One stack, 40 B. Mass and content are integer mg, so every split, merge and blend conserves both exactly.
/// <list type="bullet">
/// <item><see cref="ContentMg"/> is the TRUE EM-bearing part (EM), the active mass (additives), or AM (cells); 0 otherwise.
/// Purity or potency is content ÷ mass.</item>
/// <item><see cref="NominalMg"/> is the content the owner is shown. It equals content once assayed; crude refineries
/// make it differ by up to <see cref="SpreadPpm"/>. It is stored, not derived, so nominal sums conserve exactly too.</item>
/// <item>Metastable stacks (crystal, seed lattice) store content and nominal at their decay origin; read them through
/// <see cref="StackMath.Current"/>. <see cref="Stamp"/> is the origin tick, or the elapsed decay ticks while in stasis.</item>
/// </list>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ItemStack
{
    public ItemId Item;
    public StackFlags Flags;
    private byte _reserved;
    public int SpreadPpm;
    public long MassMg;
    public long ContentMg;
    public long NominalMg;
    public long Stamp;

    public readonly bool IsEmpty => MassMg <= 0;

    public readonly bool IsAssayed => SpreadPpm == 0;

    public readonly bool Metastable => (Flags & StackFlags.Metastable) != 0;

    public readonly bool InStasis => (Flags & StackFlags.InStasis) != 0;

    /// <summary>True purity at the stored origin, ppm.</summary>
    public readonly int PurityPpm => Fixed.Fraction(ContentMg, MassMg);

    /// <summary>Shown purity at the stored origin, ppm.</summary>
    public readonly int NominalPurityPpm => Fixed.Fraction(NominalMg, MassMg);

    public static ItemStack Plain(ItemId item, long massMg) => new() { Item = item, MassMg = massMg };

    public static ItemStack Assayed(ItemId item, long massMg, long contentMg) =>
        new() { Item = item, MassMg = massMg, ContentMg = contentMg, NominalMg = contentMg };

    public static ItemStack Unassayed(ItemId item, long massMg, long contentMg, long nominalMg, int spreadPpm) =>
        new() { Item = item, MassMg = massMg, ContentMg = contentMg, NominalMg = nominalMg, SpreadPpm = spreadPpm };
}
