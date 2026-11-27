using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Plans;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlanHeader
{
    public const int SizeInBytes = 36;
    public const uint MagicValue = 'B' | ('P' << 8) | ('L' << 16) | ((uint)'N' << 24);

    public uint Magic;
    public ushort Format;
    public PlanLevel Level;
    public byte Flags;
    public PlanHash Hash;
    public PlanHash Parent;
    public uint DataVersion;
    public ushort SectionCount;
    public ushort Reserved0;
    public uint Reserved1;
}

[Flags]
public enum PlanFlags : byte { None = 0, Bundle = 1, Sealed = 2 }

public enum PlanFormatError
{
    None,
    TooLarge,
    Truncated,
    BadMagic,
    UnsupportedFormat,
    UnsupportedLevel,
    BadSection,
    SectionTooLarge,
    InflateRatio,
    Corrupt,
    MissingBody,
    DuplicateSection,
    BadBody,
    BadMeta,
    HashMismatch,
    TrailingBytes,
}

/// <summary>
/// <c>.bplan</c> formats 1 and 2 (docs/pivot/ship-design-and-construction.md "File format"): a 36 B header, then sections
/// <c>u32 tag, u32 rawLen, u32 packedLen, u8 codec, payload</c>. BODY (zlib) holds the canonical design and is the only
/// hashed section; META (raw) holds name, description, revision, tags, author and the id allocator. Same bytes for
/// files, saves and the network. Decoding checks every size against <see cref="PlanLimits"/> before inflating.
///
/// BODY format 1 (hull levels): <c>u8 class, u8 0, u16 0, u32 materialCount, u32 DefId[materialCount],
/// u32 solidCount</c>, then the solids column by column: id deltas (i32), shape, op, flags, material (u8 each),
/// position X, Y, Z deltas from the previous solid (i32), rotation (u32), half X, Y, Z, bevel, param (u16).
/// Format 2 appends the mesh table: <c>u32 meshCount</c>, then each <see cref="HullMesh"/>'s canonical bytes. Format 3
/// (layered hulls) writes the mesh table even when empty, then the <see cref="HullLayering"/> block. Each plan is written
/// in the oldest format that holds it, so older plans keep their bytes and hashes. Format 4 (brushes) writes the mesh
/// table, <c>u8 layered</c> and the layering block when set, then the <see cref="HullBrushSet"/> block. Format 5 (components)
/// writes <c>u8 flags</c> (1 layered, 2 brushes), those blocks, then the component block and the prefabs. Format 6
/// (interiors) adds flag 4 and the <see cref="HullInterior"/> block between the brushes and the components; format 7
/// (windows) adds flag 8 and the <see cref="HullWindowSet"/> block after the interior. Format 8 (route networks) writes
/// pipes and hallways as node networks with junctions and the plan's profile list; older formats are converted on load.
/// Format 9 (wall profiles) adds each profile's kind and the rooms' wall profiles to the interior block. Format 10 (brush
/// profiles) points sweep brushes at the plan's profiles with a line point and size, and sizes every stroke point; the
/// inline outlines of older sweeps become plan brush profiles on load; it also adds flag 16 and the
/// <see cref="HullDecalSet"/> block after the windows. Format 11 (paints) gives decals a shape and paint slot and adds flag
/// 32 and the <see cref="HullPaintSet"/> block (slots, the hull's paint, strokes' own paints) after the decals. Format 12
/// (model brushes) adds the game model and the repeat options to each brush. Format 13 (joints) adds flag 64 and the
/// <see cref="HullJointSet"/> block (rotors, hinges, the solids that move with them) after the paint. Format 14 (openings)
/// adds door profiles, sliders and flag 128 with the <see cref="HullOpeningSet"/> block (doorways, docking port faces)
/// after the joints. Format 15 (programmes) writes a second flags byte after the first (1: programme) and the
/// <see cref="Ships.Logic.HullProgram"/> block after the openings.
/// </summary>
public static class PlanFormat
{
    public const ushort Format = 1;
    public const ushort MeshFormat = 2;
    public const ushort LayerFormat = 3;
    public const ushort BrushFormat = 4;
    public const ushort ComponentFormat = 5;
    public const ushort InteriorFormat = 6;
    public const ushort WindowFormat = 7;
    public const ushort RouteFormat = 8;
    public const ushort WallFormat = 9;
    public const ushort BrushProfileFormat = 10;
    public const ushort PaintFormat = 11;
    public const ushort RepeatFormat = 12;
    public const ushort JointFormat = 13;
    public const ushort OpeningFormat = 14;
    public const ushort ProgramFormat = 15;

    /// <summary>The newest format this build writes; bodies up to it can be read.</summary>
    public const ushort Newest = ProgramFormat;

    public static ushort FormatOf(PlanDocument doc) =>
        doc.Program is not null || doc.Prefabs.Values.Any(p => FormatOf(p) >= ProgramFormat) ? ProgramFormat
        : doc.Openings is { IsEmpty: false } || doc.Interior is { } i && i.Profiles.Exists(p => p.Kind == ProfileKind.Door)
            || doc.Joints is { } j && j.Joints.Exists(x => x.Kind == JointKind.Slider) || doc.Prefabs.Values.Any(p => FormatOf(p) >= OpeningFormat) ? OpeningFormat
        : doc.Joints is { IsEmpty: false } || doc.Prefabs.Values.Any(p => FormatOf(p) >= JointFormat) ? JointFormat
        : doc.Brushes is { NeedsRepeats: true } || doc.Prefabs.Values.Any(p => FormatOf(p) >= RepeatFormat) ? RepeatFormat
        : doc.Paint is { IsEmpty: false } || doc.Decals is { HasShapes: true } || doc.Prefabs.Values.Any(p => FormatOf(p) >= PaintFormat) ? PaintFormat
        : doc.Brushes is { NeedsProfiles: true } || doc.Decals is { IsEmpty: false } || doc.Prefabs.Values.Any(p => FormatOf(p) >= BrushProfileFormat) ? BrushProfileFormat
        : doc.Interior is { HasKinds: true } || doc.Prefabs.Values.Any(p => FormatOf(p) >= WallFormat) ? WallFormat
        : doc.Interior is { IsEmpty: false } || doc.Components is { Pipes.Count: > 0 } || doc.Prefabs.Values.Any(p => FormatOf(p) >= RouteFormat) ? RouteFormat
        : doc.Windows is { IsEmpty: false } ? WindowFormat
        : doc.Components is { IsEmpty: false } || doc.Prefabs.Count > 0 ? ComponentFormat
        : doc.Brushes is { IsEmpty: false } ? BrushFormat : doc.Layers is not null ? LayerFormat : doc.Meshes.Count > 0 ? MeshFormat : Format;
    public const uint BodyTag = 'B' | ('O' << 8) | ('D' << 16) | ((uint)'Y' << 24);
    public const uint MetaTag = 'M' | ('E' << 8) | ('T' << 16) | ((uint)'A' << 24);
    private const int SectionHeaderBytes = 13;
    private const byte CodecRaw = 0;
    private const byte CodecZlib = 1;
    private const int BodyFixedBytes = 12;

    /// <summary>The canonical BODY bytes of <paramref name="doc"/> as it stands (call <see cref="PlanCanonical.Canonicalise"/> first for a canonical hash).</summary>
    public static byte[] EncodeBody(PlanDocument doc)
    {
        var solids = doc.Solids.AsSpan();
        var n = solids.Length;
        var meshBytes = 0;

        foreach (var mesh in doc.Meshes)
            meshBytes += mesh.ByteSize;

        var format = FormatOf(doc);
        var layers = doc.Layers?.ToBytes() ?? [];
        var brushes = format >= BrushFormat && doc.Brushes is { IsEmpty: false } ? doc.Brushes.ToBytes(format >= RepeatFormat ? BrushLayout.Repeats : format >= BrushProfileFormat ? BrushLayout.Profiles : BrushLayout.Inline) : [];
        var interior = format >= InteriorFormat && doc.Interior is { IsEmpty: false } ? doc.Interior.ToBytes(format >= WallFormat) : [];
        var windows = format >= WindowFormat && doc.Windows is { IsEmpty: false } ? doc.Windows.ToBytes() : [];
        var decals = format >= BrushProfileFormat && doc.Decals is { IsEmpty: false } ? doc.Decals.ToBytes(format >= PaintFormat) : [];
        var paint = format >= PaintFormat && doc.Paint is { IsEmpty: false } ? doc.Paint.ToBytes() : [];
        var joints = format >= JointFormat && doc.Joints is { IsEmpty: false } ? doc.Joints.ToBytes() : [];
        var openings = format >= OpeningFormat && doc.Openings is { IsEmpty: false } ? doc.Openings.ToBytes() : [];
        var program = format >= ProgramFormat && doc.Program is not null ? doc.Program.ToBytes() : [];
        var components = format >= ComponentFormat ? ComponentBlock(doc) : [];
        var bytes = new byte[BodyFixedBytes + doc.Materials.Count * 4 + n * HullSolid.SizeInBytes + (format >= MeshFormat ? 4 + meshBytes : 0)
            + (format >= BrushFormat ? 1 : 0) + layers.Length + brushes.Length + interior.Length + windows.Length + decals.Length + paint.Length + joints.Length + openings.Length + (format >= ProgramFormat ? 1 : 0) + program.Length + components.Length];
        var w = new Writer(bytes);

        w.U8((byte)doc.Class);
        w.U8(0);
        w.U16(0);
        w.U32((uint)doc.Materials.Count);

        foreach (var material in doc.Materials)
            w.U32(material.Value);

        w.U32((uint)n);

        uint prevId = 0;
        foreach (ref readonly var b in solids)
        {
            w.I32((int)(b.Id - prevId));
            prevId = b.Id;
        }

        foreach (ref readonly var b in solids)
            w.U8((byte)b.Shape);

        foreach (ref readonly var b in solids)
            w.U8((byte)b.Op);

        foreach (ref readonly var b in solids)
            w.U8((byte)b.Flags);

        foreach (ref readonly var b in solids)
            w.U8(b.Material);

        WriteDeltas(ref w, solids, 0);
        WriteDeltas(ref w, solids, 1);
        WriteDeltas(ref w, solids, 2);

        foreach (ref readonly var b in solids)
            w.U32(b.Rotation);

        foreach (ref readonly var b in solids)
            w.U16(b.HalfX);

        foreach (ref readonly var b in solids)
            w.U16(b.HalfY);

        foreach (ref readonly var b in solids)
            w.U16(b.HalfZ);

        foreach (ref readonly var b in solids)
            w.U16(b.Bevel);

        foreach (ref readonly var b in solids)
            w.U16(b.Param);

        if (format >= MeshFormat)
        {
            w.U32((uint)doc.Meshes.Count);

            foreach (var mesh in doc.Meshes)
                w.Mesh(mesh);
        }

        if (format == BrushFormat)
            w.U8(doc.Layers is not null ? (byte)1 : (byte)0);
        else if (format >= ComponentFormat)
            w.U8((byte)((doc.Layers is not null ? 1 : 0) | (brushes.Length > 0 ? 2 : 0) | (interior.Length > 0 ? 4 : 0) | (windows.Length > 0 ? 8 : 0) | (decals.Length > 0 ? 16 : 0) | (paint.Length > 0 ? 32 : 0) | (joints.Length > 0 ? 64 : 0) | (openings.Length > 0 ? 128 : 0)));

        if (format >= ProgramFormat)
            w.U8((byte)(program.Length > 0 ? 1 : 0));

        w.Bytes(layers);
        w.Bytes(brushes);
        w.Bytes(interior);
        w.Bytes(windows);
        w.Bytes(decals);
        w.Bytes(paint);
        w.Bytes(joints);
        w.Bytes(openings);
        w.Bytes(program);
        w.Bytes(components);
        return bytes;
    }

    /// <summary>Format 5's tail: the <see cref="ComponentData"/> block, then u16 prefab count and per prefab u16 format, u32 length, BODY bytes.</summary>
    private static byte[] ComponentBlock(PlanDocument doc)
    {
        var data = (doc.Components ?? new ComponentData()).ToBytes();
        var prefabs = new List<(ushort Format, byte[] Body)>();

        foreach (var (_, prefab) in doc.Prefabs)
            prefabs.Add((FormatOf(prefab), EncodeBody(prefab)));

        var bytes = new byte[data.Length + 2 + prefabs.Sum(p => 6 + p.Body.Length)];
        var w = new Writer(bytes);
        w.Bytes(data);
        w.U16((ushort)prefabs.Count);

        foreach (var (format, body) in prefabs)
        {
            w.U16(format);
            w.U32((uint)body.Length);
            w.Bytes(body);
        }

        return bytes;
    }

    public static PlanHash HashBody(PlanDocument doc) => new(XxHash64.Hash(EncodeBody(doc)));

    /// <summary>The whole file: header, BODY (zlib), META (raw).</summary>
    public static byte[] Encode(PlanDocument doc)
    {
        var body = EncodeBody(doc);
        var meta = EncodeMeta(doc.Meta);
        var packed = new byte[ZLib.Bound(body.Length)];
        var packedLength = ZLib.Compress(body, packed);
        var total = PlanHeader.SizeInBytes + SectionHeaderBytes * 2 + packedLength + meta.Length;
        var bytes = new byte[total];
        var w = new Writer(bytes);

        w.U32(PlanHeader.MagicValue);
        w.U16(FormatOf(doc));
        w.U8((byte)doc.Level);
        w.U8((byte)PlanFlags.None);
        w.U64(XxHash64.Hash(body));
        w.U64(doc.Parent.Value);
        w.U32(doc.DataVersion);
        w.U16(2);
        w.U16(0);
        w.U32(0);

        w.U32(BodyTag);
        w.U32((uint)body.Length);
        w.U32((uint)packedLength);
        w.U8(CodecZlib);
        w.Bytes(packed.AsSpan(0, packedLength));

        w.U32(MetaTag);
        w.U32((uint)meta.Length);
        w.U32((uint)meta.Length);
        w.U8(CodecRaw);
        w.Bytes(meta);
        return bytes;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out PlanDocument doc, out PlanFormatError error) => TryDecode(bytes, out doc, out _, out error);

    /// <summary>Decodes and checks the header hash against the BODY; <paramref name="header"/> is the file's own.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out PlanDocument doc, out PlanHeader header, out PlanFormatError error)
    {
        doc = new PlanDocument();
        header = default;
        error = Decode(bytes, doc, ref header);
        return error == PlanFormatError.None;
    }

    private static PlanFormatError Decode(ReadOnlySpan<byte> bytes, PlanDocument doc, ref PlanHeader header)
    {
        if (bytes.Length > PlanLimits.MaxBundleBytes)
            return PlanFormatError.TooLarge;

        if (bytes.Length < PlanHeader.SizeInBytes)
            return PlanFormatError.Truncated;

        header = MemoryMarshal.Read<PlanHeader>(bytes);

        if (header.Magic != PlanHeader.MagicValue)
            return PlanFormatError.BadMagic;

        if (header.Format is not (Format or MeshFormat or LayerFormat or BrushFormat or ComponentFormat or InteriorFormat or WindowFormat or RouteFormat or WallFormat or BrushProfileFormat or PaintFormat or RepeatFormat or JointFormat or OpeningFormat or ProgramFormat))
            return PlanFormatError.UnsupportedFormat;

        if (header.Level is not (PlanLevel.Ship or PlanLevel.HullChunk or PlanLevel.Component))
            return PlanFormatError.UnsupportedLevel;

        if (header.SectionCount > PlanLimits.MaxSections)
            return PlanFormatError.BadSection;

        doc.Level = header.Level;
        doc.Parent = header.Parent;
        doc.DataVersion = header.DataVersion;

        var offset = PlanHeader.SizeInBytes;
        byte[]? body = null;
        var hasMeta = false;

        for (var s = 0; s < header.SectionCount; s++)
        {
            if (bytes.Length - offset < SectionHeaderBytes)
                return PlanFormatError.Truncated;

            var tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            var raw = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 4)..]);
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset + 8)..]);
            var codec = bytes[offset + 12];
            offset += SectionHeaderBytes;

            if (packed > (uint)(bytes.Length - offset))
                return PlanFormatError.Truncated;

            if (raw > PlanLimits.MaxSectionRawBytes)
                return PlanFormatError.SectionTooLarge;

            if (codec == CodecZlib ? raw > (ulong)packed * PlanLimits.MaxInflateRatio : codec != CodecRaw || raw != packed)
                return codec == CodecZlib ? PlanFormatError.InflateRatio : PlanFormatError.BadSection;

            var payload = bytes.Slice(offset, (int)packed);
            offset += (int)packed;
            var data = new byte[raw];

            if (codec == CodecZlib ? !ZLib.TryDecompress(payload, data) : !payload.TryCopyTo(data))
                return PlanFormatError.Corrupt;

            if (tag == BodyTag)
            {
                if (body is not null)
                    return PlanFormatError.DuplicateSection;

                body = data;

                if (!DecodeBody(data, doc, header.Format))
                    return PlanFormatError.BadBody;
            }
            else if (tag == MetaTag)
            {
                if (hasMeta)
                    return PlanFormatError.DuplicateSection;

                hasMeta = true;

                if (!DecodeMeta(data, doc.Meta))
                    return PlanFormatError.BadMeta;
            }
        }

        if (offset != bytes.Length)
            return PlanFormatError.TrailingBytes;

        if (body is null)
            return PlanFormatError.MissingBody;

        if (XxHash64.Hash(body) != header.Hash.Value)
            return PlanFormatError.HashMismatch;

        doc.FixNextId();
        return PlanFormatError.None;
    }

    private static bool DecodeBody(byte[] bytes, PlanDocument doc, ushort format)
    {
        var r = new Reader(bytes);

        if (!r.U8(out var planClass) || !r.U8(out var pad0) || !r.U16(out var pad1) || !r.U32(out var materials))
            return false;

        if (planClass > (byte)PlanClass.Station || pad0 != 0 || pad1 != 0 || materials > PlanLimits.MaxMaterials)
            return false;

        doc.Class = (PlanClass)planClass;

        for (var i = 0; i < materials; i++)
        {
            if (!r.U32(out var id))
                return false;

            doc.Materials.Add(new DefId(id));
        }

        var meshes = format >= MeshFormat;

        if (!r.U32(out var count) || count > PlanLimits.MaxSolids || r.Remaining < count * HullSolid.SizeInBytes || (!meshes && r.Remaining != count * HullSolid.SizeInBytes))
            return false;

        var n = (int)count;
        var solids = new HullSolid[n];
        uint id0 = 0;

        for (var i = 0; i < n; i++)
        {
            r.I32(out var delta);
            id0 = (uint)(id0 + delta);
            solids[i].Id = id0;
        }

        for (var i = 0; i < n; i++)
        {
            r.U8(out var v);
            solids[i].Shape = (SolidShape)v;
        }

        for (var i = 0; i < n; i++)
        {
            r.U8(out var v);
            solids[i].Op = (SolidOp)v;
        }

        for (var i = 0; i < n; i++)
        {
            r.U8(out var v);
            solids[i].Flags = (SolidFlags)v;
        }

        for (var i = 0; i < n; i++)
            r.U8(out solids[i].Material);

        ReadDeltas(ref r, solids, 0);
        ReadDeltas(ref r, solids, 1);
        ReadDeltas(ref r, solids, 2);

        for (var i = 0; i < n; i++)
            r.U32(out solids[i].Rotation);

        for (var i = 0; i < n; i++)
            r.U16(out solids[i].HalfX);

        for (var i = 0; i < n; i++)
            r.U16(out solids[i].HalfY);

        for (var i = 0; i < n; i++)
            r.U16(out solids[i].HalfZ);

        for (var i = 0; i < n; i++)
            r.U16(out solids[i].Bevel);

        for (var i = 0; i < n; i++)
            r.U16(out solids[i].Param);

        foreach (ref readonly var b in solids.AsSpan())
            doc.Solids.Add(b);

        if (meshes)
        {
            if (!r.U32(out var meshCount) || (meshCount == 0 && format == MeshFormat) || meshCount > PlanLimits.MaxMeshes)
                return false;

            for (var i = 0; i < meshCount; i++)
            {
                var mesh = r.Mesh();

                if (mesh is null)
                    return false;

                doc.Meshes.Add(mesh);
            }
        }

        var layered = format == LayerFormat;
        var hasBrushes = format == BrushFormat;
        var hasInterior = false;
        var hasWindows = false;
        var hasDecals = false;
        var hasPaint = false;
        var hasJoints = false;
        var hasOpenings = false;
        var hasProgram = false;

        if (format == BrushFormat)
        {
            if (!r.U8(out var flag) || flag > 1)
                return false;

            layered = flag == 1;
        }
        else if (format >= ComponentFormat)
        {
            if (!r.U8(out var flags) || flags > (format >= OpeningFormat ? 255 : format >= JointFormat ? 127 : format >= PaintFormat ? 63 : format >= BrushProfileFormat ? 31 : format >= WindowFormat ? 15 : format == InteriorFormat ? 7 : 3))
                return false;

            layered = (flags & 1) != 0;
            hasBrushes = (flags & 2) != 0;
            hasInterior = (flags & 4) != 0;
            hasWindows = (flags & 8) != 0;
            hasDecals = (flags & 16) != 0;
            hasPaint = (flags & 32) != 0;
            hasJoints = (flags & 64) != 0;
            hasOpenings = (flags & 128) != 0;

            if (format >= ProgramFormat)
            {
                if (!r.U8(out var more) || more > 1)
                    return false;

                hasProgram = (more & 1) != 0;
            }
        }

        if (layered)
        {
            doc.Layers = r.Layers();

            if (doc.Layers is null)
                return false;
        }

        var inline = new List<(int Brush, List<ProfilePoint> Points)>();

        if (hasBrushes)
        {
            doc.Brushes = r.Brushes(format >= RepeatFormat ? BrushLayout.Repeats : format >= BrushProfileFormat ? BrushLayout.Profiles : BrushLayout.Inline, inline);

            if (doc.Brushes is null)
                return false;
        }

        if (hasInterior)
        {
            doc.Interior = r.Interior(format < RouteFormat ? InteriorLayout.Legacy : format < WallFormat ? InteriorLayout.Routes : InteriorLayout.Walls);

            if (doc.Interior is null)
                return false;
        }

        if (inline.Count > 0 && !AdoptInlineOutlines(doc, inline))
            return false;

        if (hasWindows)
        {
            doc.Windows = r.Windows();

            if (doc.Windows is null)
                return false;
        }

        if (hasDecals)
        {
            doc.Decals = r.Decals(format >= PaintFormat);

            if (doc.Decals is null)
                return false;
        }

        if (hasPaint)
        {
            doc.Paint = r.Paint();

            if (doc.Paint is null)
                return false;
        }

        if (hasJoints)
        {
            doc.Joints = r.Joints();

            if (doc.Joints is null)
                return false;
        }

        if (hasOpenings)
        {
            doc.Openings = r.Openings();

            if (doc.Openings is null)
                return false;
        }

        if (hasProgram)
        {
            doc.Program = r.Program();

            if (doc.Program is null)
                return false;
        }

        if (format >= ComponentFormat)
        {
            doc.Components = r.Components(legacy: format < RouteFormat);

            if (doc.Components is null || !r.U16(out var prefabs) || prefabs > PlanLimits.MaxPrefabs)
                return false;

            for (var i = 0; i < prefabs; i++)
            {
                if (!r.U16(out var prefabFormat) || !r.U32(out var length) || length > r.Remaining || prefabFormat > Newest)
                    return false;

                var body = r.Take((int)length);
                var prefab = new PlanDocument { Level = PlanLevel.Component };

                if (!DecodeBody(body, prefab, prefabFormat))
                    return false;

                doc.Prefabs[XxHash64.Hash(body)] = prefab;
            }

            if (doc.Components.IsEmpty)
                doc.Components = null;
        }

        return r.Remaining == 0;
    }

    /// <summary>Formats 4-9: each sweep's inline outline becomes a plan brush profile (the same outline shared), the line at its origin.</summary>
    private static bool AdoptInlineOutlines(PlanDocument doc, List<(int Brush, List<ProfilePoint> Points)> inline)
    {
        doc.Interior ??= new HullInterior();
        var profiles = doc.Interior.Profiles;

        foreach (var (brush, points) in inline)
        {
            var index = profiles.FindIndex(p => p.Kind == ProfileKind.Brush && p.Points.SequenceEqual(points));

            if (index < 0)
            {
                if (profiles.Count >= HullInterior.MaxProfiles)
                    return false;

                index = profiles.Count;
                var profile = new HullProfile { Name = $"Brush {brush + 1}", Kind = ProfileKind.Brush };
                profile.Points.AddRange(points);
                profiles.Add(profile);
            }

            doc.Brushes!.Brushes[brush].Outline = (byte)index;
        }

        return true;
    }

    private static void WriteDeltas(ref Writer w, ReadOnlySpan<HullSolid> solids, int axis)
    {
        var prev = 0;

        foreach (ref readonly var b in solids)
        {
            var v = axis == 0 ? b.PosX : axis == 1 ? b.PosY : b.PosZ;
            w.I32(unchecked(v - prev));
            prev = v;
        }
    }

    private static void ReadDeltas(ref Reader r, HullSolid[] solids, int axis)
    {
        var prev = 0;

        for (var i = 0; i < solids.Length; i++)
        {
            r.I32(out var delta);
            prev = unchecked(prev + delta);

            if (axis == 0)
                solids[i].PosX = prev;
            else if (axis == 1)
                solids[i].PosY = prev;
            else
                solids[i].PosZ = prev;
        }
    }

    private static byte[] EncodeMeta(PlanMeta meta)
    {
        var name = Clip(meta.Name);
        var description = Clip(meta.Description);
        var author = Clip(meta.Author);
        var tagCount = Math.Min(meta.Tags.Count, PlanLimits.MaxTags);
        var size = 8 + StringBytes(name) + StringBytes(description) + StringBytes(author) + 2;

        for (var i = 0; i < tagCount; i++)
            size += StringBytes(Clip(meta.Tags[i]));

        var bytes = new byte[size];
        var w = new Writer(bytes);
        w.U32(meta.Revision);
        w.U32(meta.NextId);
        w.String(name);
        w.String(description);
        w.String(author);
        w.U16((ushort)tagCount);

        for (var i = 0; i < tagCount; i++)
            w.String(Clip(meta.Tags[i]));

        return bytes;
    }

    /// <summary>Cuts a string to <see cref="PlanLimits.MaxStringBytes"/> of UTF-8 on a character boundary.</summary>
    private static string Clip(string s)
    {
        while (Encoding.UTF8.GetByteCount(s) > PlanLimits.MaxStringBytes)
            s = s[..^1];

        return s;
    }

    private static bool DecodeMeta(byte[] bytes, PlanMeta meta)
    {
        var r = new Reader(bytes);

        if (!r.U32(out meta.Revision) || !r.U32(out meta.NextId))
            return false;

        if (!r.String(out meta.Name) || !r.String(out meta.Description) || !r.String(out meta.Author) || !r.U16(out var tags) || tags > PlanLimits.MaxTags)
            return false;

        meta.Tags.Clear();

        for (var i = 0; i < tags; i++)
        {
            if (!r.String(out var tag))
                return false;

            meta.Tags.Add(tag);
        }

        return r.Remaining == 0;
    }

    private static int StringBytes(string s) => 2 + Encoding.UTF8.GetByteCount(s);

    private ref struct Writer(Span<byte> bytes)
    {
        private readonly Span<byte> _bytes = bytes;
        private int _offset;

        public void U8(byte v) => _bytes[_offset++] = v;

        public void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes[_offset..], v);
            _offset += 2;
        }

        public void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes[_offset..], v);
            _offset += 4;
        }

        public void I32(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_bytes[_offset..], v);
            _offset += 4;
        }

        public void U64(ulong v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_bytes[_offset..], v);
            _offset += 8;
        }

        public void Bytes(ReadOnlySpan<byte> v)
        {
            v.CopyTo(_bytes[_offset..]);
            _offset += v.Length;
        }

        public void Mesh(HullMesh mesh)
        {
            mesh.Write(_bytes[_offset..]);
            _offset += mesh.ByteSize;
        }

        /// <summary>u16 length + UTF-8; callers clip to <see cref="PlanLimits.MaxStringBytes"/> first.</summary>
        public void String(string s)
        {
            U16((ushort)Encoding.UTF8.GetByteCount(s));
            _offset += Encoding.UTF8.GetBytes(s, _bytes[_offset..]);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;

        public readonly int Remaining => _bytes.Length - _offset;

        public HullLayering? Layers()
        {
            var layers = HullLayering.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return layers;
        }

        public ComponentData? Components(bool legacy)
        {
            var data = ComponentData.Read(_bytes[_offset..], out var consumed, legacy);
            _offset += consumed;
            return data;
        }

        public byte[] Take(int length)
        {
            var bytes = _bytes.Slice(_offset, length).ToArray();
            _offset += length;
            return bytes;
        }

        public HullDecalSet? Decals(bool shapes)
        {
            var decals = HullDecalSet.Read(_bytes[_offset..], out var consumed, shapes);
            _offset += consumed;
            return decals;
        }

        public HullJointSet? Joints()
        {
            var joints = HullJointSet.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return joints;
        }

        public Ships.Logic.HullProgram? Program()
        {
            var program = Ships.Logic.HullProgram.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return program;
        }

        public HullOpeningSet? Openings()
        {
            var openings = HullOpeningSet.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return openings;
        }

        public HullPaintSet? Paint()
        {
            var paint = HullPaintSet.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return paint;
        }

        public HullWindowSet? Windows()
        {
            var windows = HullWindowSet.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return windows;
        }

        public HullInterior? Interior(InteriorLayout layout)
        {
            var interior = HullInterior.Read(_bytes[_offset..], out var consumed, layout);
            _offset += consumed;
            return interior;
        }

        public HullBrushSet? Brushes(BrushLayout layout, List<(int Brush, List<ProfilePoint> Points)> inline)
        {
            var set = HullBrushSet.Read(_bytes[_offset..], out var consumed, layout, inline);
            _offset += consumed;
            return set;
        }

        public HullMesh? Mesh()
        {
            var mesh = HullMesh.Read(_bytes[_offset..], out var consumed);
            _offset += consumed;
            return mesh;
        }

        public bool U8(out byte v)
        {
            v = 0;

            if (Remaining < 1)
                return false;

            v = _bytes[_offset++];
            return true;
        }

        public bool U16(out ushort v)
        {
            v = 0;

            if (Remaining < 2)
                return false;

            v = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_offset..]);
            _offset += 2;
            return true;
        }

        public bool U32(out uint v)
        {
            v = 0;

            if (Remaining < 4)
                return false;

            v = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_offset..]);
            _offset += 4;
            return true;
        }

        public bool I32(out int v)
        {
            v = 0;

            if (Remaining < 4)
                return false;

            v = BinaryPrimitives.ReadInt32LittleEndian(_bytes[_offset..]);
            _offset += 4;
            return true;
        }

        public bool String(out string v)
        {
            v = "";

            if (!U16(out var length) || length > PlanLimits.MaxStringBytes || Remaining < length)
                return false;

            v = Encoding.UTF8.GetString(_bytes.Slice(_offset, length));
            _offset += length;
            return true;
        }
    }
}
