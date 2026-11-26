namespace BrutalGame.Terrain;

public enum LayerOp : byte
{
    Fbm, Ridged, Billow, Eroded, Warp,
    Craters, Ergs,
    Curve, Terrace, Canyon, Mask, Combine, SeaClamp, Legacy,
}

/// <summary>Per-layer mode bytes: flags for noise layers, the variant for compose ops.</summary>
public static class LayerMode
{
    public const byte Gradient = 1;
    public const byte Precise = 2;
    public const byte ShapedV1 = 4;

    public const byte CurveAdd = 1;
    public const byte CurveEase = 2;
    public const byte CurveKnee = 4;

    public const byte MaskChannel = 0;
    public const byte MaskLat = 1;

    public const byte Mul = 0;
    public const byte Min = 1;
    public const byte Max = 2;
    public const byte Invert = 3;
    public const byte AddMul = 4;
    public const byte AddConst = 5;
    public const byte Copy = 6;
}

/// <summary>One crater or erg size class (planet-kinds.md §1.4): its own cube grid in one of 16 rotations, one candidate per cell.</summary>
public struct StampClass
{
    public LayerOp Kind;
    public byte Rotation;
    public int CellsPerSide;
    public uint Seed;

    /// <summary>Radii (m), the chance a cell holds a stamp, the reach in radii and the centre jitter in cells.</summary>
    public float RadiusMin, RadiusMax, Presence, Reach, Jitter;
    public float AgeMin, AgeMax, ComplexDiameter;

    /// <summary>The class's deepest bowl and highest rim (ergs: tallest dune) for bounds (m).</summary>
    public float DepthMax, RimMax;
    public float LambdaMin, LambdaMax, RatioMin, RatioMax;
}

/// <summary>
/// One body's compiled kind (planet-kinds.md §7 "Structs"): octaves, layers in evaluation order and per-LOD tables as
/// structs of arrays. Immutable once built and shared by every job; channel 0 is the height.
/// </summary>
public sealed class PlanetKindProgram
{
    public const int MaxChannels = 24;
    public const int MaxLayers = 64;
    public const int Lods = PlanetGenerator.MaxLod + 1;
    public const int TempRows = 8;

    /// <summary>Most table entries one layer holds (24 octaves of a three-field warp), and a point's cache of them with derivatives.</summary>
    public const int LayerOctaves = 72;
    public const int OctaveCache = 4 * LayerOctaves;
    public const byte None = 255;
    public const int Height = 0;

    public const float CoarseVoxels = 256f;
    public const float PreciseVoxels = 1024f;

    public string KindId = "";
    public double Radius;
    public double AxisX, AxisY, AxisZ;
    public float MinHeight, MaxHeight, SeaHeight;
    public bool HasSea;

    /// <summary>Continent value from which the sea clamp holds (D30).</summary>
    public float InlandStart;

    public double[] Frequency = [], OffsetX = [], OffsetY = [], OffsetZ = [];
    public float[] Wavelength = [], Amplitude = [];

    /// <summary>Per octave: the mean a ridged or billow octave contributes (what a LOD that skips it adds back); for eroded, its least damping factor.</summary>
    public float[] OctaveMean = [];
    public uint[] Seed = [];

    public LayerOp[] Op = [];
    public byte[] Dst = [], SrcA = [], SrcB = [], Gate = [], Scale = [], Warp = [], Mode = [];
    public ushort[] FirstOctave = [], OctaveCount = [], ParamBase = [];
    public float[] Params = [];

    /// <summary>[lod × layers + layer]: octaves the LOD keeps, and how many of those sit on the coarse grid.</summary>
    public ushort[] OctaveEnd = [], CoarseEnd = [], CoarseSlot = [];

    /// <summary>[lod × layers + layer]: riser band (terrace: m of input; canyon: share), the layer's fade weight, the mean of skipped shaped octaves.</summary>
    public float[] RiserBand = [], LodWeight = [], RidgeMean = [];

    /// <summary>[(lod × classes + class) × 2]: the stamp's weight (1 full, 0 skipped; ergs: dune detail against its mean) and the rim's.</summary>
    public float[] ClassWeight = [];
    public ulong[] LayersAtLod = new ulong[Lods];
    public int[] CoarseSlots = new int[Lods];

    public StampClass[] Classes = [];

    /// <summary>The JSON layer each op came from (debug only, not hashed).</summary>
    public string[] LayerNames = [];
    public int LayerCount, ChannelCount, MaxCoarseSlots;

    /// <summary>−1 with no sea.</summary>
    public int ContinentChannel = -1;
    public ulong ContentHash;

    public int Rows => ChannelCount + TempRows;

    public int At(int lod, int layer) => lod * LayerCount + layer;

    public bool IsNoise(int layer) => Op[layer] <= LayerOp.Warp;

    /// <summary>Octaves shorter than this many voxels are skipped: 2, or 4 where the shape has a crease.</summary>
    public float MinVoxels(int layer) => Op[layer] is LayerOp.Ridged or LayerOp.Billow && (Mode[layer] & LayerMode.ShapedV1) == 0 ? 4f : 2f;

    public float CoarseVoxelsOf(int layer) => (Mode[layer] & LayerMode.Precise) != 0 ? PreciseVoxels : CoarseVoxels;

    /// <summary>Table entries per octave: a warp samples three fields.</summary>
    public int Fields(int layer) => Op[layer] == LayerOp.Warp ? 3 : 1;

    /// <summary>Coarse-grid values per layer at a LOD: a sum per linear layer, the raw octave values per creased one, the sum and running gradient of an eroded one.</summary>
    public int SlotsOf(int layer, int coarseOctaves)
    {
        if (coarseOctaves == 0)
            return 0;

        return Op[layer] switch
        {
            LayerOp.Warp => 3,
            LayerOp.Fbm => (Mode[layer] & LayerMode.Gradient) != 0 ? 4 : 1,
            LayerOp.Eroded => 4,
            LayerOp.Ridged when (Mode[layer] & LayerMode.ShapedV1) != 0 => 1,
            _ => coarseOctaves,
        };
    }
}
