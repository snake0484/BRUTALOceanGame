using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;

namespace BrutalGame.Net.Wire;

/// <summary>First byte of every message; starts at RakNet's ID_USER_PACKET_ENUM (134).</summary>
public enum MessageId : byte
{
    Hello = 134,
    JoinAccept,
    JoinRefused,
    Input,
    Snapshot,
    Spawn,
    Despawn,
    FrameChange,
    Edit,
    EditRequest,
}

public enum RefuseReason : byte
{
    Protocol,
    Full,
}

/// <summary>Message layouts shared by server and client. Every write is bounded by the caller's buffer; no allocations.</summary>
public static class Messages
{
    public const ushort Protocol = 1;

    /// <summary>Inputs per input message: the newest plus redundant copies of the ones before, so one lost packet costs nothing.</summary>
    public const int InputRedundancy = 4;

    /// <summary>Snapshot payload budget: one datagram under RakNet's MTU, so a snapshot is never split.</summary>
    public const int SnapshotBudget = 1150;

    public const int SpawnFlagOwned = 1;

    public static void Hello(ref NetWriter writer)
    {
        writer.Byte((byte)MessageId.Hello);
        writer.UShort(Protocol);
    }

    public static void JoinAccept(ref NetWriter writer, int slot, uint ownedNetId, long serverTick, int tickRate, ulong seed, scoped ReadOnlySpan<byte> rails)
    {
        writer.Byte((byte)MessageId.JoinAccept);
        writer.UShort(Protocol);
        writer.Byte((byte)slot);
        writer.VarUInt(ownedNetId);
        writer.Long(serverTick);
        writer.Byte((byte)tickRate);
        writer.Long((long)seed);
        writer.VarUInt((ulong)rails.Length);
        writer.Bytes(rails);
    }

    public static void Input(ref NetWriter writer, long ackTick, long firstTick, scoped ReadOnlySpan<EntityInput> inputs)
    {
        writer.Byte((byte)MessageId.Input);
        writer.VarUInt((ulong)Math.Max(ackTick, 0));
        writer.VarUInt((ulong)firstTick);
        writer.Byte((byte)inputs.Length);

        foreach (ref readonly var input in inputs)
            WriteInput(ref writer, input);
    }

    public static void WriteInput(ref NetWriter writer, in EntityInput input)
    {
        writer.Byte((byte)input.MoveX);
        writer.Byte((byte)input.MoveY);
        writer.Byte((byte)input.MoveZ);
        writer.Byte((byte)input.Pitch);
        writer.Byte((byte)input.Yaw);
        writer.Byte((byte)input.Roll);
        writer.Byte(input.Buttons);
    }

    public static EntityInput ReadInput(ref NetReader reader) => new()
    {
        MoveX = (sbyte)reader.Byte(),
        MoveY = (sbyte)reader.Byte(),
        MoveZ = (sbyte)reader.Byte(),
        Pitch = (sbyte)reader.Byte(),
        Yaw = (sbyte)reader.Byte(),
        Roll = (sbyte)reader.Byte(),
        Buttons = reader.Byte(),
    };

    public static void Spawn(ref NetWriter writer, long tick, uint netId, EntityKind kind, bool owned, in NetBodyState state)
    {
        writer.Byte((byte)MessageId.Spawn);
        writer.VarUInt((ulong)tick);
        writer.VarUInt(netId);
        writer.Byte((byte)kind);
        writer.Byte(owned ? (byte)SpawnFlagOwned : (byte)0);
        BodyCodec.WriteState(ref writer, state);
    }

    public static void Despawn(ref NetWriter writer, long tick, uint netId)
    {
        writer.Byte((byte)MessageId.Despawn);
        writer.VarUInt((ulong)tick);
        writer.VarUInt(netId);
    }

    /// <summary>"Entity E → frame F at tick T, state S" (frames-and-zones Networking). The client applies it at T.</summary>
    public static void FrameChange(ref NetWriter writer, long tick, uint netId, in NetBodyState state)
    {
        writer.Byte((byte)MessageId.FrameChange);
        writer.VarUInt((ulong)tick);
        writer.VarUInt(netId);
        BodyCodec.WriteState(ref writer, state);
    }

    /// <summary>An accepted edit, its centre at 1/1024 m (review C13), so every peer replays the same op.</summary>
    public static void Edit(ref NetWriter writer, long tick, in BrushOp op)
    {
        writer.Byte((byte)MessageId.Edit);
        writer.VarUInt((ulong)tick);
        WriteOp(ref writer, op);
    }

    public static void EditRequest(ref NetWriter writer, in BrushOp op)
    {
        writer.Byte((byte)MessageId.EditRequest);
        WriteOp(ref writer, op);
    }

    public static BrushOp ReadOp(ref NetReader reader)
    {
        var centre = new long3(reader.VarInt(), reader.VarInt(), reader.VarInt());
        var radius = reader.Float();
        var mode = (BrushMode)reader.Byte();
        var material = (VoxelMaterial)reader.Byte();
        var metres = NetQuant.Position(centre);
        return new BrushOp(new Vector3((float)metres.X, (float)metres.Y, (float)metres.Z), radius, mode, material);
    }

    /// <summary>The op as every peer sees it after the wire's quantisation.</summary>
    public static BrushOp Quantise(in BrushOp op)
    {
        Span<byte> scratch = stackalloc byte[48];
        var writer = new NetWriter(scratch);
        WriteOp(ref writer, op);
        var reader = new NetReader(writer.Written);
        return ReadOp(ref reader);
    }

    private static void WriteOp(ref NetWriter writer, in BrushOp op)
    {
        writer.VarInt(NetQuant.Position(op.Centre.X));
        writer.VarInt(NetQuant.Position(op.Centre.Y));
        writer.VarInt(NetQuant.Position(op.Centre.Z));
        writer.Float(op.Radius);
        writer.Byte((byte)op.Mode);
        writer.Byte((byte)op.Material);
    }
}
