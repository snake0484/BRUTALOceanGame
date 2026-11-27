using System.Buffers.Binary;

namespace BrutalGame.Ships.Interior;

/// <summary>
/// A hallway: a walkable space swept along a path of nodes, with a cross-section profile and its own gravity direction.
/// Like a pipe, it is a route of nodes; unlike a pipe it is empty space: it is cut out of the hull as a room (a layered
/// hull gives it its own wall stack), and runs too steep to walk up get stairs. Positions are 1/1024 m in ship space;
/// the profile is 1/1024 m across (X) and up from the floor (Y = 0 is the floor). Treated as immutable once in a plan.
/// </summary>
public sealed class HullHallway
{
    public const int MinNodes = 2;
    public const int MaxNodes = 64;
    public const int MinProfilePoints = 3;
    public const int MaxProfilePoints = 32;

    /// <summary>Largest profile coordinate (16 m).</summary>
    public const int MaxProfileUnits = 16 * HullSolid.PositionUnitsPerMetre;

    public uint Id;

    /// <summary>The path, 1/1024 m in ship space.</summary>
    public readonly List<(int X, int Y, int Z)> Nodes = new();

    /// <summary>The cross-section: a closed outline, X across, Y up from the floor (1/1024 m).</summary>
    public readonly List<ProfilePoint> Profile = new();

    /// <summary>Gravity as a 15 degree snap rotation: down is this rotation applied to -Y (identity: ship down).</summary>
    public uint Gravity = SolidRotation.Identity;

    /// <summary>Plan material index of the stairs.</summary>
    public byte Material;

    public Vector3d Node(int i) => new(Nodes[i].X * HullSolid.MetresPerPositionUnit, Nodes[i].Y * HullSolid.MetresPerPositionUnit, Nodes[i].Z * HullSolid.MetresPerPositionUnit);

    public HullHallway Clone()
    {
        var copy = new HullHallway { Id = Id, Gravity = Gravity, Material = Material };
        copy.Nodes.AddRange(Nodes);
        copy.Profile.AddRange(Profile);
        return copy;
    }

    public double Length()
    {
        var length = 0.0;

        for (var i = 0; i + 1 < Nodes.Count; i++)
            length += HullSurface.Sub(Node(i + 1), Node(i)).Length();

        return length;
    }
}

/// <summary>The plan's interior spaces: hallways (rooms stay ordinary Room-role solids).</summary>
public sealed class HullInterior
{
    public const int MaxHallways = 255;

    public readonly List<HullHallway> Hallways = new();

    public bool IsEmpty => Hallways.Count == 0;

    public int IndexOf(uint id) => Hallways.FindIndex(h => h.Id == id);

    public HullInterior Clone()
    {
        var copy = new HullInterior();

        foreach (var h in Hallways)
            copy.Hallways.Add(h.Clone());

        return copy;
    }

    /// <summary>
    /// <c>u8 count</c>, then per hallway <c>u32 id, u32 gravity, u8 material, u8 profileCount, u8 nodeCount</c>, the profile
    /// (i32 X, Y each) and the nodes (i32 X, Y, Z each).
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 1 + Hallways.Sum(h => 11 + h.Profile.Count * 8 + h.Nodes.Count * 12);
        var b = new byte[size];
        var o = 0;
        b[o++] = (byte)Hallways.Count;

        foreach (var h in Hallways)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), h.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), h.Gravity);
            b[o + 8] = h.Material;
            b[o + 9] = (byte)h.Profile.Count;
            b[o + 10] = (byte)h.Nodes.Count;
            o += 11;

            foreach (var p in h.Profile)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), p.X);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), p.Y);
                o += 8;
            }

            foreach (var (x, y, z) in h.Nodes)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), x);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), y);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), z);
                o += 12;
            }
        }

        return b;
    }

    public static HullInterior? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1)
            return null;

        var interior = new HullInterior();
        int count = s[0], o = 1;

        for (var i = 0; i < count; i++)
        {
            if (s.Length < o + 11)
                return null;

            var h = new HullHallway
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]),
                Gravity = BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 4)..]),
                Material = s[o + 8],
            };
            int profile = s[o + 9], nodes = s[o + 10];
            o += 11;

            if (profile > HullHallway.MaxProfilePoints || nodes > HullHallway.MaxNodes || s.Length < o + profile * 8 + nodes * 12)
                return null;

            for (var j = 0; j < profile; j++, o += 8)
                h.Profile.Add(new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..])));

            for (var j = 0; j < nodes; j++, o += 12)
                h.Nodes.Add((BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..])));

            interior.Hallways.Add(h);
        }

        consumed = o;
        return interior;
    }
}
