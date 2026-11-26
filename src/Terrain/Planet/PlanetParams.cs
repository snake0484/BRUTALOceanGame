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
    public const uint CurrentGeneratorVersion = 2;

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

    /// <summary>The planet kind (data/planets/kinds) and the seed its parameter ranges draw from.</summary>
    public string KindId { get; init; }
    public ulong KindSeed { get; init; }

    /// <summary>Content hash of the compiled kind: joins <see cref="GeneratorVersion"/> wherever peers must agree on the terrain.</summary>
    public ulong ProgramHash { get; init; }

    /// <summary>Spin axis in the planet frame (latitude masks); unit Y by default.</summary>
    public double AxisX { get; init; }
    public double AxisY { get; init; }
    public double AxisZ { get; init; }

    public PlanetMaterials Materials { get; init; }

    public bool HasSea => !double.IsNaN(SeaRadius);

    /// <summary>Sea level about Radius (m); 0 when there is no sea.</summary>
    public float SeaHeight => HasSea ? (float)(SeaRadius - Radius) : 0f;

    /// <summary>Body i's seed in a system (planets.md "Seeding").</summary>
    public static ulong BodySeed(ulong systemSeed, int bodyIndex) => SplitMix(systemSeed + (ulong)bodyIndex);

    /// <summary>A rocky 1 g body of <paramref name="radius"/> from its seed, of a planet kind (the v1 recipe by default).</summary>
    public static PlanetParams Generate(ulong seed, double radius, double surfaceGravity = 9.80665, bool ocean = true, string? kind = null, ulong? kindSeed = null)
    {
        var recipe = PlanetKinds.Get(kind ?? PlanetKinds.Legacy);
        var state = seed;
        var sea = Math.Round(Next(ref state) * 300.0 - 150.0);
        Next(ref state);
        Next(ref state);
        var desert = Next(ref state) < 0.2;

        var p = new PlanetParams
        {
            Seed = seed,
            GeneratorVersion = CurrentGeneratorVersion,
            Radius = radius,
            SeaRadius = ocean && recipe.SeaPresent ? radius + sea : double.NaN,
            MinHeight = recipe.ReliefMin,
            MaxHeight = recipe.ReliefMax,
            DigDepth = 2000f,
            HeatOnsetDepth = 800f,
            HeatExcessK = 900f,
            ShoreProtectHeight = 6f,
            SurfaceGravity = surfaceGravity,
            KindId = recipe.Id,
            KindSeed = kindSeed ?? seed,
            AxisY = 1.0,
            Materials = recipe.MaterialSet ?? (desert ? PlanetMaterials.Desert : PlanetMaterials.Temperate),
        };

        return p with { ProgramHash = PlanetKindCompiler.Compile(recipe, p).ContentHash };
    }

    /// <summary>The same body with its spin axis (planet frame) set: latitude masks read it, so the program hash follows.</summary>
    public PlanetParams WithAxis(double x, double y, double z)
    {
        var p = this with { AxisX = x, AxisY = y, AxisZ = z };
        return p with { ProgramHash = PlanetKindCompiler.Compile(PlanetKinds.Get(KindId), p).ContentHash };
    }

    /// <summary>The v1 recipe's seeded continent offset and ridge height (compat "v1").</summary>
    internal static void LegacyDraws(ulong seed, out float landBias, out float mountainHeight)
    {
        var state = seed;
        Next(ref state);
        landBias = (float)(Next(ref state) * 0.14 - 0.08);
        mountainHeight = (float)(3500.0 + Next(ref state) * 2000.0);
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
