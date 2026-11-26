using System.Text;
using BrutalGame.Ships;

namespace BrutalGame.Plans;

public enum PlanLevel : byte { Laminate, Part, Component, HullChunk, Ship }

public enum PlanClass : byte { Ship, Station }

/// <summary>xxHash64 of a plan's canonical BODY bytes: prefab id, bake cache key, network fetch key, save key.</summary>
public readonly record struct PlanHash(ulong Value)
{
    public bool IsNone => Value == 0;

    public override string ToString() => Value.ToString("x16");
}

/// <summary>A catalog data id (stable string from data/), hashed to u32.</summary>
public readonly record struct DefId(uint Value)
{
    public static DefId FromName(string name) => new((uint)XxHash64.Hash(Encoding.UTF8.GetBytes(name)));

    public override string ToString() => Value.ToString("x8");
}

public enum ElementKind : byte { Brush, Part, Connection, Route, Placement, Test, Material, Mesh }

public readonly record struct ElementRef(ElementKind Kind, uint Id);

public static class PlanLimits
{
    public const int MaxBrushes = 8192;
    public const int MaxMaterials = 256;
    public const int MaxBundleBytes = 1 << 20;
    public const int MaxSectionRawBytes = 8 << 20;
    public const int MaxInflateRatio = 32;
    public const int MaxSections = 8;
    public const int MaxStringBytes = 4096;
    public const int MaxTags = 32;
    public const int MaxMeshes = 256;
    public const int MaxMeshVertices = 131072;
    public const int MaxMeshTriangles = 1 << 20;

    /// <summary>Ship extent: every brush's bounds within this many metres of the plan origin on each axis.</summary>
    public const double MaxReachMetres = 500.0;

    /// <summary>Smallest half extent of a brush, 1/16 m (half a 0.125 m cell edge): anything thinner bakes to nothing.</summary>
    public const int MinHalfUnits = HullBrush.HalfUnitsPerMetre / 16;
}
