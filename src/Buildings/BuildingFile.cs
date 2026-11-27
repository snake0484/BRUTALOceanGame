using System.Buffers.Binary;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.Buildings;

public enum BuildingFileError : byte { None, TooShort, BadMagic, BadFormat, BadLengths, BadInfo, BadPlan, HashMismatch }

/// <summary>
/// <c>.bbld</c>, one building: a 32 B header (magic, format, flags, building hash, INFO and PLAN lengths), the 12 B
/// <see cref="BuildingInfo"/>, then the structure's canonical <c>.bplan</c> bytes verbatim. The building hash is xxHash64
/// of the INFO bytes followed by the plan's BODY hash, so renaming the plan (META) never changes it.
/// </summary>
public static class BuildingFile
{
    public const string Extension = "bbld";
    public const uint Magic = 'B' | ('B' << 8) | ('L' << 16) | ((uint)'D' << 24);
    public const ushort Format = 1;
    public const int HeaderBytes = 32;

    public static byte[] Encode(in BuildingInfo info, PlanDocument plan, out ulong hash)
    {
        var copy = plan.Clone();
        PlanCanonical.Canonicalise(copy);
        var planBytes = PlanFormat.Encode(copy);
        hash = Hash(info, PlanFormat.HashBody(copy));
        var bytes = new byte[HeaderBytes + BuildingInfo.SizeInBytes + planBytes.Length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Format);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], hash);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], BuildingInfo.SizeInBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)planBytes.Length);
        WriteInfo(info, span.Slice(HeaderBytes, BuildingInfo.SizeInBytes));
        planBytes.CopyTo(span[(HeaderBytes + BuildingInfo.SizeInBytes)..]);
        return bytes;
    }

    public static ulong Hash(in BuildingInfo info, PlanHash body)
    {
        Span<byte> bytes = stackalloc byte[BuildingInfo.SizeInBytes + 8];
        WriteInfo(info, bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[BuildingInfo.SizeInBytes..], body.Value);
        return XxHash64.Hash(bytes);
    }

    public static BuildingFileError TryDecode(ReadOnlySpan<byte> bytes, out BuildingInfo info, out PlanDocument plan, out ulong hash)
    {
        info = default;
        plan = new PlanDocument();
        hash = 0;

        if (bytes.Length < HeaderBytes + BuildingInfo.SizeInBytes)
            return BuildingFileError.TooShort;

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
            return BuildingFileError.BadMagic;

        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Format)
            return BuildingFileError.BadFormat;

        hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        var infoLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var planLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);

        if (infoLength != BuildingInfo.SizeInBytes || planLength > PlanLimits.MaxBundleBytes || bytes.Length != HeaderBytes + infoLength + planLength)
            return BuildingFileError.BadLengths;

        info = ReadInfo(bytes.Slice(HeaderBytes, BuildingInfo.SizeInBytes));

        if (!info.IsValid)
            return BuildingFileError.BadInfo;

        if (!PlanFormat.TryDecode(bytes[(HeaderBytes + BuildingInfo.SizeInBytes)..], out plan, out var header, out _))
            return BuildingFileError.BadPlan;

        return Hash(info, header.Hash) == hash ? BuildingFileError.None : BuildingFileError.HashMismatch;
    }

    private static void WriteInfo(in BuildingInfo info, Span<byte> span)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, info.PadHalfX);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], info.PadHalfZ);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], info.FoundationDepth);
    }

    private static BuildingInfo ReadInfo(ReadOnlySpan<byte> span) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(span),
        BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
        BinaryPrimitives.ReadInt32LittleEndian(span[8..]));
}
