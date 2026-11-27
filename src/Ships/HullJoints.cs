using System.Buffers.Binary;

namespace BrutalGame.Ships;

/// <summary>A rotor turns without end; a hinge swings between limits.</summary>
public enum JointKind : byte { Rotor, Hinge }

/// <summary>
/// A movable joint: a pivot and an axis on the hull (or on another joint's moving part), with a motor. The solids that are
/// its members, and what sits on them (windows, decals, brush strokes, components), are its moving part: a child grid of
/// the ship with its own mass, turning about the axis. Joints nest at most <see cref="HullJointSet.MaxDepth"/> deep.
/// Angles are in tenths of a degree, positive right-handed about the axis. Treated as immutable once in a plan.
/// </summary>
public sealed class HullJoint
{
    public const int MaxTorqueNm = 100_000_000;

    public uint Id;
    public JointKind Kind;

    /// <summary>The joint whose moving part this one is mounted on; 0 for the ship itself.</summary>
    public uint Parent;

    /// <summary>The pivot, 1/1024 m in ship space (at rest).</summary>
    public int X, Y, Z;

    /// <summary>15 degree snap rotation: the axis is it applied to +Y, the zero direction it applied to +Z.</summary>
    public uint Rotation = SolidRotation.Identity;

    /// <summary>Rotor: turning speed, tenths of an rpm (signed). Hinge: swing speed, tenths of a degree per second.</summary>
    public short Speed = 100;

    /// <summary>The motor's torque, N·m.</summary>
    public uint TorqueNm = 10_000;

    /// <summary>Hinge: the limits and the angle it is driven to (tenths of a degree, -1800 to 1800).</summary>
    public short Min = -900, Max = 900, Target;

    public Vector3d Pivot => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    /// <summary>The axis, ship space (unit).</summary>
    public Vector3d Axis
    {
        get
        {
            Span<double> m = stackalloc double[9];
            SolidRotation.Matrix(Rotation, m);
            return new Vector3d(m[1], m[4], m[7]);
        }
    }

    public HullJoint Clone() => (HullJoint)MemberwiseClone();
}

/// <summary>The plan's joints and which solids move with each (BODY format 13).</summary>
public sealed class HullJointSet
{
    public const int MaxJoints = 16;
    public const int MaxDepth = 3;
    private const int Bytes = 37;

    public readonly List<HullJoint> Joints = new();

    /// <summary>Solids that move with a joint (by id); the rest are the ship's own.</summary>
    public readonly List<(uint Solid, uint Joint)> Members = new();

    public bool IsEmpty => Joints.Count == 0 && Members.Count == 0;

    public int IndexOf(uint id) => Joints.FindIndex(j => j.Id == id);

    /// <summary>The joint solid <paramref name="solid"/> moves with, or 0.</summary>
    public uint JointOf(uint solid)
    {
        foreach (var (s, j) in Members)
        {
            if (s == solid)
                return j;
        }

        return 0;
    }

    /// <summary>How many joints deep <paramref name="joint"/> sits (1 for one on the ship); 0 for none or a loop.</summary>
    public int Depth(uint joint)
    {
        var depth = 0;

        for (var at = joint; at != 0 && depth <= MaxJoints; depth++)
        {
            var index = IndexOf(at);

            if (index < 0)
                return 0;

            at = Joints[index].Parent;
        }

        return depth > MaxJoints ? 0 : depth;
    }

    public HullJointSet Clone()
    {
        var copy = new HullJointSet();

        foreach (var j in Joints)
            copy.Joints.Add(j.Clone());

        copy.Members.AddRange(Members);
        return copy;
    }

    /// <summary>
    /// <c>u8 joint count</c>, per joint <c>u32 id, u8 kind, u32 parent, i32 X, Y, Z, u32 rotation, i16 speed, u32 torque,
    /// i16 min, max, target</c> (37 B); <c>u16 member count</c>, per member <c>u32 solid, u32 joint</c>.
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new byte[1 + Joints.Count * Bytes + 2 + Members.Count * 8];
        b[0] = (byte)Joints.Count;
        var o = 1;

        foreach (var j in Joints)
        {
            var s = b.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, j.Id);
            s[4] = (byte)j.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(s[5..], j.Parent);
            BinaryPrimitives.WriteInt32LittleEndian(s[9..], j.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[13..], j.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[17..], j.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(s[21..], j.Rotation);
            BinaryPrimitives.WriteInt16LittleEndian(s[25..], j.Speed);
            BinaryPrimitives.WriteUInt32LittleEndian(s[27..], j.TorqueNm);
            BinaryPrimitives.WriteInt16LittleEndian(s[31..], j.Min);
            BinaryPrimitives.WriteInt16LittleEndian(s[33..], j.Max);
            BinaryPrimitives.WriteInt16LittleEndian(s[35..], j.Target);
            o += Bytes;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Members.Count);
        o += 2;

        foreach (var (solid, joint) in Members)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), solid);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), joint);
            o += 8;
        }

        return b;
    }

    public static HullJointSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1 || s[0] > MaxJoints || s.Length < 1 + s[0] * Bytes + 2)
            return null;

        var set = new HullJointSet();
        int count = s[0], o = 1;

        for (var i = 0; i < count; i++, o += Bytes)
        {
            var r = s.Slice(o, Bytes);
            set.Joints.Add(new HullJoint
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(r),
                Kind = (JointKind)r[4],
                Parent = BinaryPrimitives.ReadUInt32LittleEndian(r[5..]),
                X = BinaryPrimitives.ReadInt32LittleEndian(r[9..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(r[13..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(r[17..]),
                Rotation = BinaryPrimitives.ReadUInt32LittleEndian(r[21..]),
                Speed = BinaryPrimitives.ReadInt16LittleEndian(r[25..]),
                TorqueNm = BinaryPrimitives.ReadUInt32LittleEndian(r[27..]),
                Min = BinaryPrimitives.ReadInt16LittleEndian(r[31..]),
                Max = BinaryPrimitives.ReadInt16LittleEndian(r[33..]),
                Target = BinaryPrimitives.ReadInt16LittleEndian(r[35..]),
            });
        }

        int members = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;

        if (s.Length < o + members * 8)
            return null;

        for (var i = 0; i < members; i++, o += 8)
            set.Members.Add((BinaryPrimitives.ReadUInt32LittleEndian(s[o..]), BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 4)..])));

        consumed = o;
        return set;
    }
}
