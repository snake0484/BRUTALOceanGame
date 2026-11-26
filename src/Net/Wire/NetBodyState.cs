using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Net.Wire;

/// <summary>A body's state as the wire carries it: every field already quantised, so both ends hold the same bits.</summary>
public struct NetBodyState : IEquatable<NetBodyState>
{
    public ushort Frame;
    public long3 Position;
    public uint Rotation;
    public ushort VelocityX, VelocityY, VelocityZ;
    public ushort AngularX, AngularY, AngularZ;

    public static NetBodyState From(in Placement placement) => new()
    {
        Frame = placement.Frame.Value,
        Position = NetQuant.Position(placement.Position),
        Rotation = NetQuant.Rotation(placement.Rotation),
        VelocityX = NetQuant.Half(placement.Velocity.X),
        VelocityY = NetQuant.Half(placement.Velocity.Y),
        VelocityZ = NetQuant.Half(placement.Velocity.Z),
        AngularX = NetQuant.Half(placement.AngularVelocity.X),
        AngularY = NetQuant.Half(placement.AngularVelocity.Y),
        AngularZ = NetQuant.Half(placement.AngularVelocity.Z),
    };

    public readonly FrameId FrameId => new(Frame);

    public readonly double3 PositionMetres => NetQuant.Position(Position);

    public readonly doubleQuat RotationQuat => NetQuant.Rotation(Rotation);

    public readonly double3 Velocity => new(NetQuant.Half(VelocityX), NetQuant.Half(VelocityY), NetQuant.Half(VelocityZ));

    public readonly float3 AngularVelocity => new((float)NetQuant.Half(AngularX), (float)NetQuant.Half(AngularY), (float)NetQuant.Half(AngularZ));

    /// <summary>Writes the state into <paramref name="placement"/>, keeping its mode.</summary>
    public readonly void To(ref Placement placement)
    {
        placement.Frame = FrameId;
        placement.Position = PositionMetres;
        placement.Rotation = RotationQuat;
        placement.Velocity = Velocity;
        placement.AngularVelocity = AngularVelocity;
    }

    public readonly bool Equals(NetBodyState other)
    {
        return Frame == other.Frame && Position == other.Position && Rotation == other.Rotation
            && VelocityX == other.VelocityX && VelocityY == other.VelocityY && VelocityZ == other.VelocityZ
            && AngularX == other.AngularX && AngularY == other.AngularY && AngularZ == other.AngularZ;
    }

    public override readonly bool Equals(object? obj) => obj is NetBodyState other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Frame, Position, Rotation, VelocityX, VelocityZ, AngularY);
}

/// <summary>
/// One body in a snapshot, delta-coded against a baseline both ends hold (the last state the client acked). Layout:
/// net id, baseline age in ticks (0 = no baseline, full state), change mask, then the changed fields. Positions are
/// zigzag varints of the int64 delta, so a body that moved under 8 m since its baseline costs 2 B an axis.
/// </summary>
public static class BodyCodec
{
    private const byte PositionBit = 1;
    private const byte RotationBit = 2;
    private const byte VelocityBit = 4;
    private const byte AngularBit = 8;

    public static void WriteFull(ref NetWriter writer, uint netId, in NetBodyState state)
    {
        writer.VarUInt(netId);
        writer.VarUInt(0);
        WriteState(ref writer, state);
    }

    /// <summary>Full state without the id and age header: spawn and frame-change events.</summary>
    public static void WriteState(ref NetWriter writer, in NetBodyState state)
    {
        writer.UShort(state.Frame);
        writer.VarInt(state.Position.X);
        writer.VarInt(state.Position.Y);
        writer.VarInt(state.Position.Z);
        writer.UInt(state.Rotation);
        WriteVelocity(ref writer, state);
        WriteAngular(ref writer, state);
    }

    public static NetBodyState ReadState(ref NetReader reader)
    {
        var state = new NetBodyState
        {
            Frame = reader.UShort(),
            Position = new long3(reader.VarInt(), reader.VarInt(), reader.VarInt()),
            Rotation = reader.UInt(),
        };

        ReadVelocity(ref reader, ref state);
        ReadAngular(ref reader, ref state);
        return state;
    }

    /// <summary>Delta against <paramref name="baseline"/> (same frame), <paramref name="age"/> ticks old (1 or more).</summary>
    public static void WriteDelta(ref NetWriter writer, uint netId, int age, in NetBodyState state, in NetBodyState baseline)
    {
        var mask = (byte)0;

        if (state.Position != baseline.Position)
            mask |= PositionBit;

        if (state.Rotation != baseline.Rotation)
            mask |= RotationBit;

        if (state.VelocityX != baseline.VelocityX || state.VelocityY != baseline.VelocityY || state.VelocityZ != baseline.VelocityZ)
            mask |= VelocityBit;

        if (state.AngularX != baseline.AngularX || state.AngularY != baseline.AngularY || state.AngularZ != baseline.AngularZ)
            mask |= AngularBit;

        writer.VarUInt(netId);
        writer.VarUInt((ulong)age);
        writer.Byte(mask);

        if ((mask & PositionBit) != 0)
        {
            writer.VarInt(state.Position.X - baseline.Position.X);
            writer.VarInt(state.Position.Y - baseline.Position.Y);
            writer.VarInt(state.Position.Z - baseline.Position.Z);
        }

        if ((mask & RotationBit) != 0)
            writer.UInt(state.Rotation);

        if ((mask & VelocityBit) != 0)
            WriteVelocity(ref writer, state);

        if ((mask & AngularBit) != 0)
            WriteAngular(ref writer, state);
    }

    /// <summary>Reads the id and baseline age; then call <see cref="ReadBody"/> with the baseline the age names.</summary>
    public static void ReadHeader(ref NetReader reader, out uint netId, out int age)
    {
        netId = (uint)reader.VarUInt();
        age = (int)Math.Min(reader.VarUInt(), int.MaxValue);
    }

    /// <summary>
    /// The body after its header. A full state (age 0) ignores <paramref name="baseline"/>. Delta fields are consumed even
    /// when the caller has no baseline (pass default and drop the result), so the rest of the snapshot still parses.
    /// </summary>
    public static NetBodyState ReadBody(ref NetReader reader, int age, in NetBodyState baseline)
    {
        if (age == 0)
            return ReadState(ref reader);

        var state = baseline;
        var mask = reader.Byte();

        if ((mask & PositionBit) != 0)
            state.Position = baseline.Position + new long3(reader.VarInt(), reader.VarInt(), reader.VarInt());

        if ((mask & RotationBit) != 0)
            state.Rotation = reader.UInt();

        if ((mask & VelocityBit) != 0)
            ReadVelocity(ref reader, ref state);

        if ((mask & AngularBit) != 0)
            ReadAngular(ref reader, ref state);

        return state;
    }

    private static void WriteVelocity(ref NetWriter writer, in NetBodyState state)
    {
        writer.UShort(state.VelocityX);
        writer.UShort(state.VelocityY);
        writer.UShort(state.VelocityZ);
    }

    private static void WriteAngular(ref NetWriter writer, in NetBodyState state)
    {
        writer.UShort(state.AngularX);
        writer.UShort(state.AngularY);
        writer.UShort(state.AngularZ);
    }

    private static void ReadVelocity(ref NetReader reader, ref NetBodyState state)
    {
        state.VelocityX = reader.UShort();
        state.VelocityY = reader.UShort();
        state.VelocityZ = reader.UShort();
    }

    private static void ReadAngular(ref NetReader reader, ref NetBodyState state)
    {
        state.AngularX = reader.UShort();
        state.AngularY = reader.UShort();
        state.AngularZ = reader.UShort();
    }
}
