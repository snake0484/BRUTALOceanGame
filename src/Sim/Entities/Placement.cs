using Brutal.Numerics;
using BrutalGame.Frames;

namespace BrutalGame.Sim.Entities;

/// <summary>
/// Where an entity is, relative to its frame (docs/pivot/frames-and-zones.md). Double at runtime; int64 at 1/1024 m on
/// the wire and in saves (review C13). Bubble, body, rails and warp handles join this when M3 and M7 need them.
/// </summary>
public struct Placement
{
    public FrameId Frame;
    public double3 Position;
    public doubleQuat Rotation;
    public double3 Velocity;

    /// <summary>In frame axes, rad/s.</summary>
    public float3 AngularVelocity;

    public PlacementMode Mode;

    public static Placement At(FrameId frame, double3 position) => new()
    {
        Frame = frame,
        Position = position,
        Rotation = doubleQuat.Identity,
    };
}

/// <summary>One tick of control input for an owned entity. Axes in -127..127, body axes.</summary>
public struct EntityInput : IEquatable<EntityInput>
{
    public sbyte MoveX, MoveY, MoveZ;
    public sbyte Pitch, Yaw, Roll;
    public byte Buttons;

    public readonly bool Equals(EntityInput other)
    {
        return MoveX == other.MoveX && MoveY == other.MoveY && MoveZ == other.MoveZ
            && Pitch == other.Pitch && Yaw == other.Yaw && Roll == other.Roll && Buttons == other.Buttons;
    }

    public override readonly bool Equals(object? obj) => obj is EntityInput other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(MoveX, MoveY, MoveZ, Pitch, Yaw, Roll, Buttons);
}
