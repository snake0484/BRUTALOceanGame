namespace BrutalGame.Frames;

/// <summary>Index into <see cref="FrameTree"/>.</summary>
public readonly record struct FrameId(ushort Value)
{
    public static readonly FrameId None = new(ushort.MaxValue);

    public bool IsValid => Value != ushort.MaxValue;
}

/// <summary>A star system: the <see cref="FrameId"/> of its Solar root.</summary>
public readonly record struct SystemId(ushort Value)
{
    public FrameId Root => new(Value);
}

/// <summary>Shared enum (docs/pivot/frames-and-zones.md owns it). Mouth, Tunnel: wormholes.md; Lane, Field: rings-and-asteroids.md.</summary>
public enum FrameKind : byte
{
    Solar,
    BodyInertial,
    BodySurface,
    Lagrange,
    Grid,
    Mouth,
    Tunnel,
    Lane,
    Field,
}

public enum CelestialKind : byte
{
    Star,
    Planet,
    Moon,
    GasGiant,
    BlackHole,
    Remnant,
}
