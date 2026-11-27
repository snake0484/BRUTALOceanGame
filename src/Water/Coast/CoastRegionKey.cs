namespace BrutalGame.Water.Coast;

/// <summary>
/// The swell a coast map is baked for, quantised as it is keyed: the carrier's direction of travel (radians from the
/// tile's east toward south), its angular frequency and its deep-water height crest to trough.
/// </summary>
public readonly record struct CoastCarrier(ushort DirectionStep, ushort OmegaStep, ushort HeightStep)
{
    public const int DirectionSteps = 4096;
    public const float OmegaScale = 2048f;
    public const float HeightScale = 128f;

    public float Direction => DirectionStep * (MathF.Tau / DirectionSteps);

    public float Omega => OmegaStep / OmegaScale;

    public float Height => HeightStep / HeightScale;

    public bool Calm => OmegaStep == 0 || HeightStep == 0;

    public static CoastCarrier From(float direction, float omega, float height)
    {
        var turn = direction / MathF.Tau;
        var step = (int)MathF.Round((turn - MathF.Floor(turn)) * DirectionSteps) % DirectionSteps;

        return new CoastCarrier(
            (ushort)step,
            (ushort)Math.Clamp(MathF.Round(omega * OmegaScale), 0f, ushort.MaxValue),
            (ushort)Math.Clamp(MathF.Round(height * HeightScale), 0f, ushort.MaxValue));
    }
}

/// <summary>
/// One coast region: a window of the fixed lattice in a <see cref="Ocean.SeaTiles"/> tile's tangent plane
/// (<see cref="CoastWindow"/>), for one carrier. A map is a pure function of the world's seed and this key.
/// </summary>
public readonly record struct CoastRegionKey(int Tile, short X, short Z, CoastCarrier Carrier)
{
    public bool SameWindow(in CoastRegionKey other) => Tile == other.Tile && X == other.X && Z == other.Z;
}
