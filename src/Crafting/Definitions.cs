namespace BrutalGame.Crafting;

/// <summary>What a stack's <see cref="ItemStack.ContentMg"/> means for an item.</summary>
public enum ContentKind : byte
{
    None,
    Exotic,
    Active,
    Antimatter,
}

/// <summary>
/// Lazy metastable decay (exotic-matter.md, EM crystal stability). Purity or potency falls by <see cref="RatePpmPerDay"/>
/// outside stasis, never below <see cref="FloorPpm"/>. A stack starts decaying when it is at or above <see cref="BandPpm"/>.
/// </summary>
public readonly struct DecayRule(int bandPpm, int floorPpm, int ratePpmPerDay, int shockPpm, long ticksPerDay)
{
    public readonly int BandPpm = bandPpm;
    public readonly int FloorPpm = floorPpm;
    public readonly int RatePpmPerDay = ratePpmPerDay;
    public readonly int ShockPpm = shockPpm;
    public readonly long TicksPerDay = ticksPerDay;

    public bool Enabled => RatePpmPerDay > 0;
}

public sealed class ItemDef
{
    public ItemId Id { get; internal set; }
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Family { get; init; }
    public int FamilyId { get; internal set; }
    public ContentKind Content { get; init; }
    public int DensityKgM3 { get; init; }
    public long UnitMassMg { get; init; }
    public long CapacityMg { get; init; }
    public double RefCostPerKg { get; init; }
    public DecayRule Decay { get; internal set; }

    public bool IsDiscrete => UnitMassMg > 0;

    /// <summary>kg/m³ equals mg/mL, so volume is mass over density, rounded up.</summary>
    public long VolumeMl(long massMg) => massMg <= 0 ? 0 : (massMg + DensityKgM3 - 1) / DensityKgM3;
}

public enum YieldPurity : byte
{
    None,
    Deposit,
    Range,
}

/// <summary>A voxel material and what mining it yields (exotic-matter.md, Planet deposits; Remnant body).</summary>
public sealed class MaterialDef
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public int DensityKgM3 { get; init; }
    public double Hardness { get; init; }
    public bool Placeable { get; init; }
    public ItemId Yield { get; internal set; }
    public YieldPurity Purity { get; init; }
    public int PurityMinPpm { get; init; }
    public int PurityMaxPpm { get; init; }
}

public readonly record struct RecipeId(ushort Value);

public enum RecipeKind : byte
{
    Enrich,
    Blend,
    Fixed,
    AmProduction,
}

/// <summary>One tier of the odds-ratio <c>Enrich</c> family; every fraction in ppm.</summary>
public struct EnrichParams
{
    public int FamilyId;
    public long AlphaPpm;
    public int YieldPpm, LossPpm, MinInPpm, CapPpm, SpreadOutPpm;
    public long ThroughputMgPerHour;
    public ItemId Solvent;
    public int SolventPpm;
    public long AmNgPerKg;
    public int SeedMinPpm, SeedFracPpm, SeedUsePpm;

    public readonly bool NeedsSeed => SeedFracPpm > 0;
}

/// <summary>Dilution of one EM family stock with an agent into another item (slurry).</summary>
public struct BlendParams
{
    public int FamilyId;
    public int MinPpm, MaxPpm;
    public ItemId Agent;
    public int AgentMinFractionPpm;
    public int TargetPpm;
    public ItemId Output;
    public long ThroughputMgPerHour;
    public double FeePerKg;
}

public struct AmProductionParams
{
    public long JPerNg;
    public ItemId Feed;
    public int FeedPpm;
}

public enum InputKind : byte
{
    ItemMass,
    ItemCount,
    FamilyContent,
    Antimatter,
}

/// <summary>A <c>Fixed</c> recipe input per unit. Amounts are mass (mg), count, nominal content (mg) or AM (mg).</summary>
public readonly struct RecipeInput(InputKind kind, ItemId item, int familyId, int minPpm, int maxPpm, long amount)
{
    public readonly InputKind Kind = kind;
    public readonly ItemId Item = item;
    public readonly int FamilyId = familyId;
    public readonly int MinPpm = minPpm;
    public readonly int MaxPpm = maxPpm;
    public readonly long Amount = amount;
}

/// <summary>A <c>Fixed</c> recipe output per unit; <see cref="ContentPpm"/> is the potency of an additive output.</summary>
public readonly struct RecipeOutput(ItemId item, long massMg, int contentPpm)
{
    public readonly ItemId Item = item;
    public readonly long MassMg = massMg;
    public readonly int ContentPpm = contentPpm;
}

public sealed class RecipeDef
{
    public RecipeId Id { get; internal set; }
    public required string Key { get; init; }
    public RecipeKind Kind { get; init; }
    public required string Station { get; init; }
    public bool Crude { get; init; }
    public double PowerW { get; init; }
    public EnrichParams Enrich;
    public BlendParams Blend;
    public AmProductionParams Am;
    public RecipeInput[] Inputs { get; internal set; } = [];
    public RecipeOutput[] Outputs { get; internal set; } = [];
    public long TicksPerUnit { get; internal set; }
}

/// <summary>An additive's effect on a wormhole factor: multiplicative <c>1 + (v − 1)·effect</c>, or additive <c>v·effect</c>.</summary>
public readonly struct FactorEntry(int key, double value, bool additive)
{
    public readonly int Key = key;
    public readonly double Value = value;
    public readonly bool Additive = additive;
}

public sealed class AdditiveDef
{
    public required string Key { get; init; }
    public ItemId Item { get; internal set; }
    public FactorEntry[] Effects { get; internal set; } = [];
    public FactorEntry[] TradeOffs { get; internal set; } = [];
}

public sealed class AdditiveRules
{
    public int MaxKinds { get; init; }
    public int MaxMassFractionPpm { get; init; }
    public int FullActivePpm { get; init; }
    public int PotencyBucketPpm { get; init; }
    public int LabPotencyPpm { get; init; }
    public int CrudeLabPotencyPpm { get; init; }
    public double CrudeRateFactor { get; init; }
}
