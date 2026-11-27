using System.Buffers.Binary;
using System.Text;
using BrutalGame.Ships;

namespace BrutalGame.Buildings;

/// <summary>
/// One building in a settlement: its data id and building hash, position (1/1024 m in the settlement frame, +Y up) and
/// yaw in 15 degree steps (0-23).
/// </summary>
public struct BuildingPlacement : IEquatable<BuildingPlacement>
{
    public const int YawSteps = 24;

    public string Building;
    public ulong Hash;
    public long PosX, PosY, PosZ;
    public byte Yaw;

    public readonly bool Equals(BuildingPlacement other) =>
        Building == other.Building && Hash == other.Hash && PosX == other.PosX && PosY == other.PosY && PosZ == other.PosZ && Yaw == other.Yaw;

    public override readonly bool Equals(object? obj) => obj is BuildingPlacement other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Building, Hash, PosX, PosY, PosZ, Yaw);
}

/// <summary>A settlement: buildings placed together on one pad (half extents 1/256 m), placed on a planet as one.</summary>
public sealed class SettlementDocument
{
    public string Name = "";
    public uint PadHalfX, PadHalfZ;
    public readonly List<BuildingPlacement> Placements = new();
}

public enum SettlementFileError : byte { None, TooShort, BadMagic, BadFormat, BadBody, HashMismatch }

/// <summary>
/// <c>.bset</c>: a 16 B header (magic, format, placement count, body hash) then the body: name, pad, placements. The hash
/// is xxHash64 of the body bytes.
/// </summary>
public static class SettlementFile
{
    public const string Extension = "bset";
    public const uint Magic = 'B' | ('S' << 8) | ('E' << 16) | ((uint)'T' << 24);
    public const ushort Format = 1;
    public const int HeaderBytes = 16;
    public const int MaxPlacements = 4096;
    public const int MaxStringBytes = 256;
    private const int PlacementFixedBytes = 8 + 8 * 3 + 1;

    public static byte[] Encode(SettlementDocument doc, out ulong hash)
    {
        if (doc.Placements.Count > MaxPlacements)
            throw new InvalidOperationException($"A settlement holds at most {MaxPlacements} buildings");

        var size = StringBytes(doc.Name) + 8;

        foreach (var p in doc.Placements)
        {
            if (p.Yaw >= BuildingPlacement.YawSteps)
                throw new InvalidOperationException($"Yaw step {p.Yaw} is out of range");

            size += StringBytes(p.Building) + PlacementFixedBytes;
        }

        var bytes = new byte[HeaderBytes + size];
        var body = bytes.AsSpan(HeaderBytes);
        var at = WriteString(body, doc.Name);
        BinaryPrimitives.WriteUInt32LittleEndian(body[at..], doc.PadHalfX);
        BinaryPrimitives.WriteUInt32LittleEndian(body[(at + 4)..], doc.PadHalfZ);
        at += 8;

        foreach (var p in doc.Placements)
        {
            at += WriteString(body[at..], p.Building);
            BinaryPrimitives.WriteUInt64LittleEndian(body[at..], p.Hash);
            BinaryPrimitives.WriteInt64LittleEndian(body[(at + 8)..], p.PosX);
            BinaryPrimitives.WriteInt64LittleEndian(body[(at + 16)..], p.PosY);
            BinaryPrimitives.WriteInt64LittleEndian(body[(at + 24)..], p.PosZ);
            body[at + 32] = p.Yaw;
            at += PlacementFixedBytes;
        }

        hash = XxHash64.Hash(body);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), Format);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)doc.Placements.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), hash);
        return bytes;
    }

    public static SettlementFileError TryDecode(ReadOnlySpan<byte> bytes, out SettlementDocument doc, out ulong hash)
    {
        doc = new SettlementDocument();
        hash = 0;

        if (bytes.Length < HeaderBytes)
            return SettlementFileError.TooShort;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            return SettlementFileError.BadMagic;

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Format)
            return SettlementFileError.BadFormat;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var body = bytes[HeaderBytes..];

        if (count > MaxPlacements)
            return SettlementFileError.BadBody;

        if (XxHash64.Hash(body) != hash)
            return SettlementFileError.HashMismatch;

        if (!TryReadString(body, out doc.Name, out var at) || body.Length - at < 8)
            return SettlementFileError.BadBody;

        doc.PadHalfX = BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
        doc.PadHalfZ = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
        at += 8;

        for (var i = 0; i < count; i++)
        {
            if (!TryReadString(body[at..], out var building, out var used))
                return SettlementFileError.BadBody;

            at += used;

            if (body.Length - at < PlacementFixedBytes || body[at + 32] >= BuildingPlacement.YawSteps)
                return SettlementFileError.BadBody;

            doc.Placements.Add(new BuildingPlacement
            {
                Building = building,
                Hash = BinaryPrimitives.ReadUInt64LittleEndian(body[at..]),
                PosX = BinaryPrimitives.ReadInt64LittleEndian(body[(at + 8)..]),
                PosY = BinaryPrimitives.ReadInt64LittleEndian(body[(at + 16)..]),
                PosZ = BinaryPrimitives.ReadInt64LittleEndian(body[(at + 24)..]),
                Yaw = body[at + 32],
            });
            at += PlacementFixedBytes;
        }

        return at == body.Length ? SettlementFileError.None : SettlementFileError.BadBody;
    }

    private static int StringBytes(string s)
    {
        var length = Encoding.UTF8.GetByteCount(s);

        if (length > MaxStringBytes)
            throw new InvalidOperationException($"\"{s}\" is over {MaxStringBytes} bytes");

        return 2 + length;
    }

    private static int WriteString(Span<byte> span, string s)
    {
        var length = Encoding.UTF8.GetBytes(s, span[2..]);
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)length);
        return 2 + length;
    }

    private static bool TryReadString(ReadOnlySpan<byte> span, out string s, out int used)
    {
        s = "";
        used = 0;

        if (span.Length < 2)
            return false;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(span);

        if (length > MaxStringBytes || span.Length - 2 < length)
            return false;

        s = Encoding.UTF8.GetString(span.Slice(2, length));
        used = 2 + length;
        return true;
    }
}
