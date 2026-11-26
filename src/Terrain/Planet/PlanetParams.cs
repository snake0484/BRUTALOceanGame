namespace BrutalGame.Terrain;

public readonly record struct PlanetMaterials(VoxelMaterial Surface, VoxelMaterial Subsurface, VoxelMaterial Shore, VoxelMaterial Rock)
{
    public static PlanetMaterials Temperate => new(VoxelMaterial.Grass, VoxelMaterial.Dirt, VoxelMaterial.Sand, VoxelMaterial.Rock);

    public static PlanetMaterials Desert => new(VoxelMaterial.Sand, VoxelMaterial.Sand, VoxelMaterial.Sand, VoxelMaterial.Rock);

    /// <summary>Airless moons: rock over dirt.</summary>
    public static PlanetMaterials Barren => new(VoxelMaterial.Rock, VoxelMaterial.Dirt, VoxelMaterial.Sand, VoxelMaterial.Rock);
}

/// <summary>
/// One rocky body's terrain (planets.md "Types"). Heights are metres about <see cref="Radius"/>; generated from the body
/// seed by <see cref="Generate"/>, or overridden by data. Spin and orbit live in the frames (CelestialSystem).
/// </summary>
public readonly record struct PlanetParams
{
    public const uint CurrentGeneratorVersion = 1;

    public ulong Seed { get; init; }
    public uint GeneratorVersion { get; init; }

    /// <summary>Datum sphere (m).</summary>
    public double Radius { get; init; }

    /// <summary>Sea sphere (m); NaN means no ocean, no protected band and no inland clamp.</summary>
    public double SeaRadius { get; init; }

    /// <summary>Relief bounds about Radius (m), D29: −8 km .. +9 km.</summary>
    public float MinHeight { get; init; }
    public float MaxHeight { get; init; }

    /// <summary>How far below the generated surface edits may remove rock (m), D29.</summary>
    public float DigDepth { get; init; }

    /// <summary>Deep-rock heat (planets.md "Deep heat"): onset depth (m) and excess at the dig floor (K).</summary>
    public float HeatOnsetDepth { get; init; }
    public float HeatExcessK { get; init; }

    /// <summary>Unedited ground at or below sea + this (m) is unminable, D30.</summary>
    public float ShoreProtectHeight { get; init; }

    /// <summary>m/s² at Radius.</summary>
    public double SurfaceGravity { get; init; }

    /// <summary>Continent mask offset: higher is more land.</summary>
    public float LandBias { get; init; }

    /// <summary>Ridge height on the continent interiors (m).</summary>
    public float MountainHeight { get; init; }

    public PlanetMaterials Materials { get; init; }

    public bool HasSea => !double.IsNaN(SeaRadius);

    /// <summary>Sea level about Radius (m); 0 when there is no sea.</summary>
    public float SeaHeight => HasSea ? (float)(SeaRadius - Radius) : 0f;

    /// <summary>Body i's seed in a system (planets.md "Seeding").</summary>
    public static ulong BodySeed(ulong systemSeed, int bodyIndex) => SplitMix(systemSeed + (ulong)bodyIndex);

    /// <summary>A rocky 1 g body of <paramref name="radius"/> from its seed.</summary>
    public static PlanetParams Generate(ulong seed, double radius, double surfaceGravity = 9.80665, bool ocean = true)
    {
        var state = seed;
        var sea = Math.Round(Next(ref state) * 300.0 - 150.0);
        var land = (float)(Next(ref state) * 0.14 - 0.08);
        var mountains = (float)(3500.0 + Next(ref state) * 2000.0);
        var desert = Next(ref state) < 0.2;

        return new PlanetParams
        {
            Seed = seed,
            GeneratorVersion = CurrentGeneratorVersion,
            Radius = radius,
            SeaRadius = ocean ? radius + sea : double.NaN,
            MinHeight = -8000f,
            MaxHeight = 9000f,
            DigDepth = 2000f,
            HeatOnsetDepth = 800f,
            HeatExcessK = 900f,
            ShoreProtectHeight = 6f,
            SurfaceGravity = surfaceGravity,
            LandBias = land,
            MountainHeight = mountains,
            Materials = desert ? PlanetMaterials.Desert : PlanetMaterials.Temperate,
        };
    }

    private static ulong SplitMix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static double Next(ref ulong state)
    {
        state = SplitMix(state);
        return (state >> 11) * (1.0 / (1UL << 53));
    }
}
