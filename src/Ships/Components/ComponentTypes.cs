using System.Buffers.Binary;

namespace BrutalGame.Ships.Components;

/// <summary>Parametric part templates (ship-design-and-construction.md "Part"): shape and function come from numbers, not modelling.</summary>
public enum PartKind : byte { Chamber, Nozzle, Injector, Igniter }

/// <summary>What an injector feeds into its chamber.</summary>
public enum InjectorFeed : byte { Fuel, Oxidiser }

/// <summary>What a port carries: gas flow, or the spark of an igniter.</summary>
public enum PortKind : byte { Flow, Spark }

/// <summary>A connection point on a part, in the part's own axes (metres): where it is, which way it faces, what it carries.</summary>
public readonly record struct PartPort(string Name, Vector3d Position, Vector3d Direction, PortKind Kind, double Diameter);

/// <summary>
/// A placed part: template, material, position (1/1024 m) and a 15° snap rotation, and up to six integer parameters whose
/// meaning depends on the kind (lengths in mm, angles in 0.1°): see <see cref="PartGeometry"/>.
/// </summary>
public struct ComponentPart : IEquatable<ComponentPart>
{
    public const int ParamCount = 6;

    public uint Id;
    public PartKind Kind;
    public byte Material;
    public int PosX, PosY, PosZ;
    public uint Rotation;
    public int P0, P1, P2, P3, P4, P5;

    public readonly Vector3d Position => new(PosX * HullSolid.MetresPerPositionUnit, PosY * HullSolid.MetresPerPositionUnit, PosZ * HullSolid.MetresPerPositionUnit);

    public int this[int i]
    {
        readonly get => i switch { 0 => P0, 1 => P1, 2 => P2, 3 => P3, 4 => P4, _ => P5 };
        set
        {
            switch (i)
            {
                case 0: P0 = value; break;
                case 1: P1 = value; break;
                case 2: P2 = value; break;
                case 3: P3 = value; break;
                case 4: P4 = value; break;
                default: P5 = value; break;
            }
        }
    }

    public readonly bool Equals(ComponentPart o) =>
        Id == o.Id && Kind == o.Kind && Material == o.Material && PosX == o.PosX && PosY == o.PosY && PosZ == o.PosZ && Rotation == o.Rotation &&
        P0 == o.P0 && P1 == o.P1 && P2 == o.P2 && P3 == o.P3 && P4 == o.P4 && P5 == o.P5;

    public override readonly bool Equals(object? obj) => obj is ComponentPart p && Equals(p);

    public override readonly int GetHashCode() => HashCode.Combine(Id, Kind, PosX, PosY, PosZ, Rotation, P0, P1);
}

/// <summary>One end of a pipe: on a part's port, or open (a component inlet the ship or the test bench feeds).</summary>
public readonly record struct PipeEnd(uint Part, byte Port)
{
    public bool IsOpen => Part == 0;

    public static readonly PipeEnd Open = new(0, 0);
}

/// <summary>
/// A pipe: a run of straight lengths through its nodes (1/1024 m; the first and last are its ends), an outer and an inner
/// diameter (mm) and a material. Its pressure rating is Barlow's 2·σ_y·t / D_o with t the wall, (outer − inner) / 2.
/// </summary>
public sealed class PipeRoute
{
    public const int MaxNodes = 64;

    public uint Id;
    public PipeEnd From = PipeEnd.Open;
    public PipeEnd To = PipeEnd.Open;
    public ushort OuterMm = 20;
    public ushort InnerMm = 16;
    public byte Material;
    public readonly List<(int X, int Y, int Z)> Nodes = new();

    public double OuterMetres => OuterMm / 1000.0;

    public double InnerMetres => InnerMm / 1000.0;

    public double WallMetres => (OuterMm - InnerMm) / 2000.0;

    public Vector3d Node(int i) => new(Nodes[i].X * HullSolid.MetresPerPositionUnit, Nodes[i].Y * HullSolid.MetresPerPositionUnit, Nodes[i].Z * HullSolid.MetresPerPositionUnit);

    public double Length
    {
        get
        {
            var length = 0.0;

            for (var i = 1; i < Nodes.Count; i++)
                length += HullSurface.Sub(Node(i), Node(i - 1)).Length();

            return length;
        }
    }

    /// <summary>Burst pressure (Pa) for a material of yield <paramref name="yieldMpa"/>: 2·σ_y·t / D_o.</summary>
    public double Rating(double yieldMpa) => OuterMm > 0 ? 2.0 * yieldMpa * 1e6 * WallMetres / OuterMetres : 0.0;

    public PipeRoute Clone()
    {
        var copy = new PipeRoute { Id = Id, From = From, To = To, OuterMm = OuterMm, InnerMm = InnerMm, Material = Material };
        copy.Nodes.AddRange(Nodes);
        return copy;
    }
}

/// <summary>A component prefab placed in a ship: the prefab's plan hash, position (1/1024 m) and 15° snap rotation. Its insides are not editable here.</summary>
public struct ComponentPlacement : IEquatable<ComponentPlacement>
{
    public uint Id;
    public ulong Prefab;
    public int PosX, PosY, PosZ;
    public uint Rotation;

    public readonly Vector3d Position => new(PosX * HullSolid.MetresPerPositionUnit, PosY * HullSolid.MetresPerPositionUnit, PosZ * HullSolid.MetresPerPositionUnit);

    public readonly bool Equals(ComponentPlacement o) => Id == o.Id && Prefab == o.Prefab && PosX == o.PosX && PosY == o.PosY && PosZ == o.PosZ && Rotation == o.Rotation;

    public override readonly bool Equals(object? obj) => obj is ComponentPlacement p && Equals(p);

    public override readonly int GetHashCode() => HashCode.Combine(Id, Prefab, PosX, PosY, PosZ, Rotation);
}

/// <summary>
/// A plan's component content: parts and pipes (a component plan) and placed prefabs (a ship plan). Edits replace the
/// whole set, so undo restores it exactly. Bytes are part of BODY format 5.
/// </summary>
public sealed class ComponentData
{
    public const int MaxParts = 256;
    public const int MaxPipes = 256;
    public const int MaxPlacements = 1024;

    public readonly List<ComponentPart> Parts = new();
    public readonly List<PipeRoute> Pipes = new();
    public readonly List<ComponentPlacement> Placements = new();

    public bool IsEmpty => Parts.Count == 0 && Pipes.Count == 0 && Placements.Count == 0;

    public int IndexOfPart(uint id) => Parts.FindIndex(p => p.Id == id);

    public int IndexOfPipe(uint id) => Pipes.FindIndex(p => p.Id == id);

    public int IndexOfPlacement(uint id) => Placements.FindIndex(p => p.Id == id);

    public ComponentData Clone()
    {
        var copy = new ComponentData();
        copy.Parts.AddRange(Parts);

        foreach (var p in Pipes)
            copy.Pipes.Add(p.Clone());

        copy.Placements.AddRange(Placements);
        return copy;
    }

    /// <summary>
    /// u16 part count; per part u32 id, u8 kind, u8 material, i32 x, y, z, u32 rotation, i32 × 6 parameters (46 B).
    /// u16 pipe count; per pipe u32 id, u32 from part, u8 from port, u32 to part, u8 to port, u16 outer, u16 inner,
    /// u8 material, u8 node count, i32 × 3 per node. u16 placement count; per placement u32 id, u64 prefab, i32 x, y, z,
    /// u32 rotation (28 B).
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 2 + Parts.Count * 46 + 2 + Pipes.Sum(p => 20 + p.Nodes.Count * 12) + 2 + Placements.Count * 28;
        var b = new byte[size];
        var o = 0;
        U16(b, ref o, (ushort)Parts.Count);

        foreach (var p in Parts)
        {
            U32(b, ref o, p.Id);
            b[o++] = (byte)p.Kind;
            b[o++] = p.Material;
            I32(b, ref o, p.PosX);
            I32(b, ref o, p.PosY);
            I32(b, ref o, p.PosZ);
            U32(b, ref o, p.Rotation);

            for (var i = 0; i < ComponentPart.ParamCount; i++)
                I32(b, ref o, p[i]);
        }

        U16(b, ref o, (ushort)Pipes.Count);

        foreach (var p in Pipes)
        {
            U32(b, ref o, p.Id);
            U32(b, ref o, p.From.Part);
            b[o++] = p.From.Port;
            U32(b, ref o, p.To.Part);
            b[o++] = p.To.Port;
            U16(b, ref o, p.OuterMm);
            U16(b, ref o, p.InnerMm);
            b[o++] = p.Material;
            b[o++] = (byte)p.Nodes.Count;

            foreach (var (x, y, z) in p.Nodes)
            {
                I32(b, ref o, x);
                I32(b, ref o, y);
                I32(b, ref o, z);
            }
        }

        U16(b, ref o, (ushort)Placements.Count);

        foreach (var p in Placements)
        {
            U32(b, ref o, p.Id);
            BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), p.Prefab);
            o += 8;
            I32(b, ref o, p.PosX);
            I32(b, ref o, p.PosY);
            I32(b, ref o, p.PosZ);
            U32(b, ref o, p.Rotation);
        }

        return b;
    }

    /// <summary>Reads the block at the start of <paramref name="s"/>, structurally checked; null on failure.</summary>
    public static ComponentData? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;
        var data = new ComponentData();
        var o = 0;

        if (!R16(s, ref o, out var parts) || parts > MaxParts || s.Length < o + parts * 46)
            return null;

        for (var i = 0; i < parts; i++)
        {
            var p = new ComponentPart { Id = R32(s, ref o), Kind = (PartKind)s[o++], Material = s[o++] };
            p.PosX = RI(s, ref o);
            p.PosY = RI(s, ref o);
            p.PosZ = RI(s, ref o);
            p.Rotation = R32(s, ref o);

            for (var k = 0; k < ComponentPart.ParamCount; k++)
                p[k] = RI(s, ref o);

            data.Parts.Add(p);
        }

        if (!R16(s, ref o, out var pipes) || pipes > MaxPipes)
            return null;

        for (var i = 0; i < pipes; i++)
        {
            if (s.Length < o + 20)
                return null;

            var p = new PipeRoute { Id = R32(s, ref o) };
            var fromPart = R32(s, ref o);
            p.From = new PipeEnd(fromPart, s[o++]);
            var toPart = R32(s, ref o);
            p.To = new PipeEnd(toPart, s[o++]);
            p.OuterMm = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
            p.InnerMm = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]);
            o += 4;
            p.Material = s[o++];
            int nodes = s[o++];

            if (nodes > PipeRoute.MaxNodes || s.Length < o + nodes * 12)
                return null;

            for (var k = 0; k < nodes; k++)
                p.Nodes.Add((RI(s, ref o), RI(s, ref o), RI(s, ref o)));

            data.Pipes.Add(p);
        }

        if (!R16(s, ref o, out var placements) || placements > MaxPlacements || s.Length < o + placements * 28)
            return null;

        for (var i = 0; i < placements; i++)
        {
            var p = new ComponentPlacement { Id = R32(s, ref o) };
            p.Prefab = BinaryPrimitives.ReadUInt64LittleEndian(s[o..]);
            o += 8;
            p.PosX = RI(s, ref o);
            p.PosY = RI(s, ref o);
            p.PosZ = RI(s, ref o);
            p.Rotation = R32(s, ref o);
            data.Placements.Add(p);
        }

        consumed = o;
        return data;
    }

    private static void U16(byte[] b, ref int o, ushort v)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
        o += 2;
    }

    private static void U32(byte[] b, ref int o, uint v)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        o += 4;
    }

    private static void I32(byte[] b, ref int o, int v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        o += 4;
    }

    private static bool R16(ReadOnlySpan<byte> s, ref int o, out int v)
    {
        v = 0;

        if (s.Length < o + 2)
            return false;

        v = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;
        return true;
    }

    private static uint R32(ReadOnlySpan<byte> s, ref int o)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
        o += 4;
        return v;
    }

    private static int RI(ReadOnlySpan<byte> s, ref int o)
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(s[o..]);
        o += 4;
        return v;
    }
}
