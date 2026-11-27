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

/// <summary>One straight run of a pipe between two of its nodes, with its own outer and inner diameter (mm).</summary>
public readonly record struct PipeRun(byte A, byte B, ushort OuterMm, ushort InnerMm)
{
    public double OuterMetres => OuterMm / 1000.0;

    public double InnerMetres => InnerMm / 1000.0;

    public double WallMetres => (OuterMm - InnerMm) / 2000.0;

    /// <summary>Burst pressure (Pa) for a material of yield <paramref name="yieldMpa"/>: Barlow's 2·σ_y·t / D_o.</summary>
    public double Rating(double yieldMpa) => OuterMm > 0 ? 2.0 * yieldMpa * 1e6 * WallMetres / OuterMetres : 0.0;

    public int Other(int node) => node == A ? B : A;
}

/// <summary>
/// A pipe: nodes (1/1024 m) joined by runs, each run straight with its own diameters; three or more runs meeting at a node
/// make a junction (a tee: the whole pipe is one gas space). A node may sit on a part's port (<see cref="Ports"/>); a
/// node at the end of one run with no port is an open inlet the ship or the test bench feeds. Its rating is its weakest
/// run's (Barlow's 2·σ_y·t / D_o, t the wall).
/// </summary>
public sealed class PipeRoute
{
    public const int MaxNodes = 64;
    public const int MaxRuns = 96;

    public uint Id;
    public byte Material;

    /// <summary>The diameters new runs get.</summary>
    public ushort OuterMm = 20;
    public ushort InnerMm = 16;

    public readonly List<(int X, int Y, int Z)> Nodes = new();
    public readonly List<PipeRun> Runs = new();

    /// <summary>Nodes that sit on a part's port.</summary>
    public readonly List<(byte Node, PipeEnd End)> Ports = new();

    public Vector3d Node(int i) => new(Nodes[i].X * HullSolid.MetresPerPositionUnit, Nodes[i].Y * HullSolid.MetresPerPositionUnit, Nodes[i].Z * HullSolid.MetresPerPositionUnit);

    /// <summary>What node <paramref name="node"/> sits on (open when none).</summary>
    public PipeEnd EndAt(int node)
    {
        foreach (var (n, end) in Ports)
        {
            if (n == node)
                return end;
        }

        return PipeEnd.Open;
    }

    /// <summary>The node on a part's port, or -1.</summary>
    public int NodeOn(uint part, int port)
    {
        foreach (var (n, end) in Ports)
        {
            if (end.Part == part && end.Port == port)
                return n;
        }

        return -1;
    }

    public int Degree(int node)
    {
        var degree = 0;

        foreach (var run in Runs)
        {
            if (run.A == node)
                degree++;

            if (run.B == node)
                degree++;
        }

        return degree;
    }

    /// <summary>Open inlets: nodes at the end of one run that sit on no port.</summary>
    public IEnumerable<int> OpenEnds()
    {
        for (var i = 0; i < Nodes.Count; i++)
        {
            if (Degree(i) == 1 && EndAt(i).IsOpen)
                yield return i;
        }
    }

    /// <summary>The run touching node <paramref name="node"/> (the first, for a junction), or -1.</summary>
    public int RunAt(int node) => Runs.FindIndex(r => r.A == node || r.B == node);

    public double RunLength(PipeRun run) => HullSurface.Sub(Node(run.B), Node(run.A)).Length();

    public double Length => Runs.Sum(RunLength);

    /// <summary>The weakest run's burst pressure (Pa).</summary>
    public double Rating(double yieldMpa) => Runs.Count == 0 ? 0.0 : Runs.Min(r => r.Rating(yieldMpa));

    /// <summary>Bore volume: π r_i² × length over every run.</summary>
    public double BoreVolume => Runs.Sum(r => Math.PI * r.InnerMetres * r.InnerMetres * 0.25 * RunLength(r));

    /// <summary>A two-node pipe from <paramref name="from"/> to <paramref name="to"/> (each a port or open).</summary>
    public static PipeRoute Between(uint id, PipeEnd from, PipeEnd to, IReadOnlyList<(int X, int Y, int Z)> nodes, ushort outerMm, ushort innerMm, byte material)
    {
        var pipe = new PipeRoute { Id = id, OuterMm = outerMm, InnerMm = innerMm, Material = material };
        pipe.Nodes.AddRange(nodes);

        for (var i = 0; i + 1 < nodes.Count; i++)
            pipe.Runs.Add(new PipeRun((byte)i, (byte)(i + 1), outerMm, innerMm));

        if (!from.IsOpen)
            pipe.Ports.Add((0, from));

        if (!to.IsOpen && nodes.Count > 0)
            pipe.Ports.Add(((byte)(nodes.Count - 1), to));

        return pipe;
    }

    /// <summary>Removes node <paramref name="node"/> and its runs; a node between exactly two runs is dissolved, its neighbours joined.</summary>
    public void RemoveNode(int node)
    {
        var touching = Runs.Where(r => r.A == node || r.B == node).ToList();

        if (touching.Count == 2)
        {
            var (a, b) = (touching[0].Other(node), touching[1].Other(node));
            Runs.Add(new PipeRun((byte)a, (byte)b, touching[0].OuterMm, touching[0].InnerMm));
        }

        Runs.RemoveAll(r => r.A == node || r.B == node);
        Ports.RemoveAll(p => p.Node == node);
        Nodes.RemoveAt(node);

        for (var i = 0; i < Runs.Count; i++)
        {
            var run = Runs[i];
            Runs[i] = run with { A = (byte)(run.A > node ? run.A - 1 : run.A), B = (byte)(run.B > node ? run.B - 1 : run.B) };
        }

        for (var i = 0; i < Ports.Count; i++)
        {
            var (n, end) = Ports[i];
            Ports[i] = ((byte)(n > node ? n - 1 : n), end);
        }
    }

    public PipeRoute Clone()
    {
        var copy = new PipeRoute { Id = Id, OuterMm = OuterMm, InnerMm = InnerMm, Material = Material };
        copy.Nodes.AddRange(Nodes);
        copy.Runs.AddRange(Runs);
        copy.Ports.AddRange(Ports);
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
    /// u16 pipe count; per pipe u32 id, u8 material, u16 outer, u16 inner (for new runs), u8 node, run and port counts,
    /// i32 × 3 per node, per run u8 a, u8 b, u16 outer, u16 inner, per port u8 node, u32 part, u8 port. u16 placement
    /// count; per placement u32 id, u64 prefab, i32 x, y, z, u32 rotation (28 B). Plan BODY formats before 8 held two-ended
    /// pipes (<see cref="Read"/> with legacy pipes converts them).
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 2 + Parts.Count * 46 + 2 + Pipes.Sum(p => 12 + p.Nodes.Count * 12 + p.Runs.Count * 6 + p.Ports.Count * 6) + 2 + Placements.Count * 28;
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
            b[o++] = p.Material;
            U16(b, ref o, p.OuterMm);
            U16(b, ref o, p.InnerMm);
            b[o++] = (byte)p.Nodes.Count;
            b[o++] = (byte)p.Runs.Count;
            b[o++] = (byte)p.Ports.Count;

            foreach (var (x, y, z) in p.Nodes)
            {
                I32(b, ref o, x);
                I32(b, ref o, y);
                I32(b, ref o, z);
            }

            foreach (var run in p.Runs)
            {
                b[o++] = run.A;
                b[o++] = run.B;
                U16(b, ref o, run.OuterMm);
                U16(b, ref o, run.InnerMm);
            }

            foreach (var (node, end) in p.Ports)
            {
                b[o++] = node;
                U32(b, ref o, end.Part);
                b[o++] = end.Port;
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

    /// <summary>Reads the block at the start of <paramref name="s"/>, structurally checked; null on failure. <paramref name="legacyPipes"/>: the two-ended layout of BODY formats 5 to 7.</summary>
    public static ComponentData? Read(ReadOnlySpan<byte> s, out int consumed, bool legacyPipes = false)
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
            var p = legacyPipes ? LegacyPipe(s, ref o) : Pipe(s, ref o);

            if (p is null)
                return null;

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

    private static PipeRoute? Pipe(ReadOnlySpan<byte> s, ref int o)
    {
        if (s.Length < o + 12)
            return null;

        var p = new PipeRoute { Id = R32(s, ref o), Material = s[o++] };
        p.OuterMm = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        p.InnerMm = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]);
        o += 4;
        int nodes = s[o++], runs = s[o++], ports = s[o++];

        if (nodes > PipeRoute.MaxNodes || runs > PipeRoute.MaxRuns || ports > nodes || s.Length < o + nodes * 12 + runs * 6 + ports * 6)
            return null;

        for (var k = 0; k < nodes; k++)
            p.Nodes.Add((RI(s, ref o), RI(s, ref o), RI(s, ref o)));

        for (var k = 0; k < runs; k++)
        {
            p.Runs.Add(new PipeRun(s[o], s[o + 1], BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]), BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 4)..])));
            o += 6;
        }

        for (var k = 0; k < ports; k++)
        {
            var node = s[o++];
            var part = R32(s, ref o);
            p.Ports.Add((node, new PipeEnd(part, s[o++])));
        }

        return p;
    }

    /// <summary>Formats 5 to 7: u32 id, from part and port, to part and port, u16 outer, inner, u8 material, nodes in a line.</summary>
    private static PipeRoute? LegacyPipe(ReadOnlySpan<byte> s, ref int o)
    {
        if (s.Length < o + 20)
            return null;

        var id = R32(s, ref o);
        var from = new PipeEnd(R32(s, ref o), s[o++]);
        var to = new PipeEnd(R32(s, ref o), s[o++]);
        var outer = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        var inner = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 2)..]);
        o += 4;
        var material = s[o++];
        int count = s[o++];

        if (count > PipeRoute.MaxNodes || s.Length < o + count * 12)
            return null;

        var nodes = new List<(int, int, int)>(count);

        for (var k = 0; k < count; k++)
            nodes.Add((RI(s, ref o), RI(s, ref o), RI(s, ref o)));

        return PipeRoute.Between(id, from, to, nodes, outer, inner, material);
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
