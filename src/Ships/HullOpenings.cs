using System.Buffers.Binary;
using BrutalGame.Ships.Interior;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships;

/// <summary>A doorway, or the face of a docking port (which is also a doorway, for its hatch).</summary>
public enum OpeningKind : byte { Door, DockingPort }

/// <summary>
/// An opening a component cuts through whatever it is set in: a door profile (<see cref="ProfileKind.Door"/>) swept
/// <see cref="DepthMm"/> to each side of its plane. In the component it cuts the frame (the ship's own part, never a
/// moving leaf); placed in a ship it cuts through the wall too. The outline's X runs across, Y up from its sill (Y = 0),
/// and its plane's normal is +Z: through the door, out of a docking port's face. Two docking ports mate face to face when
/// their profiles are the same shape. Treated as immutable once in a plan.
/// </summary>
public sealed class HullOpening
{
    public const int MaxDepthMm = 8000;

    public uint Id;
    public OpeningKind Kind;

    /// <summary>The plan's profile (index into its interior's profiles), a door profile.</summary>
    public byte Profile;

    /// <summary>The outline's origin (the middle of its sill), 1/1024 m.</summary>
    public int X, Y, Z;

    /// <summary>15 degree snap rotation of the outline's frame (X across, Y up, Z through).</summary>
    public uint Rotation = SolidRotation.Identity;

    /// <summary>How far the cut reaches to each side of the plane, mm.</summary>
    public ushort DepthMm = 500;

    public Vector3d Origin => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    public HullOpening Clone() => (HullOpening)MemberwiseClone();
}

/// <summary>A plan's openings (BODY format 14).</summary>
public sealed class HullOpeningSet
{
    public const int MaxOpenings = 16;
    private const int Bytes = 24;

    public readonly List<HullOpening> Openings = new();

    public bool IsEmpty => Openings.Count == 0;

    public int IndexOf(uint id) => Openings.FindIndex(o => o.Id == id);

    public HullOpeningSet Clone()
    {
        var copy = new HullOpeningSet();

        foreach (var o in Openings)
            copy.Openings.Add(o.Clone());

        return copy;
    }

    /// <summary><c>u8 count</c>, per opening <c>u32 id, u8 kind, u8 profile, i32 X, Y, Z, u32 rotation, u16 depth</c> (24 B).</summary>
    public byte[] ToBytes()
    {
        var b = new byte[1 + Openings.Count * Bytes];
        b[0] = (byte)Openings.Count;
        var o = 1;

        foreach (var x in Openings)
        {
            var s = b.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, x.Id);
            s[4] = (byte)x.Kind;
            s[5] = x.Profile;
            BinaryPrimitives.WriteInt32LittleEndian(s[6..], x.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[10..], x.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[14..], x.Z);
            BinaryPrimitives.WriteUInt32LittleEndian(s[18..], x.Rotation);
            BinaryPrimitives.WriteUInt16LittleEndian(s[22..], x.DepthMm);
            o += Bytes;
        }

        return b;
    }

    public static HullOpeningSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1 || s[0] > MaxOpenings || s.Length < 1 + s[0] * Bytes)
            return null;

        var set = new HullOpeningSet();
        var o = 1;

        for (var i = 0; i < s[0]; i++, o += Bytes)
        {
            var r = s.Slice(o, Bytes);
            set.Openings.Add(new HullOpening
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(r),
                Kind = (OpeningKind)r[4],
                Profile = r[5],
                X = BinaryPrimitives.ReadInt32LittleEndian(r[6..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(r[10..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(r[14..]),
                Rotation = BinaryPrimitives.ReadUInt32LittleEndian(r[18..]),
                DepthMm = BinaryPrimitives.ReadUInt16LittleEndian(r[22..]),
            });
        }

        consumed = o;
        return set;
    }
}

/// <summary>
/// The cuts openings make: each a prism of its door profile, <see cref="HullOpening.DepthMm"/> to each side of its plane,
/// as a mesh solid (subtract; on a layered hull a Room-role solid, so the passage is lined like a hallway). Generated ids
/// are <see cref="IdBase"/> | owner &lt;&lt; 4 | opening: owner 0 for the plan's own openings, placement index + 1 for a
/// placed component's.
/// </summary>
public static class OpeningGeometry
{
    public const uint IdBase = 0xB0000000;

    /// <summary>The placement index + 1 a generated cut belongs to (0: the plan's own), or -1 if <paramref name="id"/> is not a cut.</summary>
    public static int Owner(uint id) => (id & 0xF0000000) == IdBase ? (int)((id >> 4) & 0xFFFFF) : -1;

    /// <summary>
    /// Appends the cuts of <paramref name="openings"/> (profiles from <paramref name="profiles"/>), placed by rotation
    /// <paramref name="rotation"/> (row-major) and <paramref name="offset"/>, to <paramref name="solids"/> and their meshes to
    /// <paramref name="meshes"/> (whose index in the plan's mesh table starts at <paramref name="meshBase"/> + its count).
    /// </summary>
    public static void Cuts(HullOpeningSet? openings, IReadOnlyList<HullProfile>? profiles, ReadOnlySpan<double> rotation, Vector3d offset, int owner, bool layered,
        int meshBase, List<HullSolid> solids, List<HullMesh> meshes)
    {
        if (openings is null || profiles is null)
            return;

        Span<double> frame = stackalloc double[9];

        for (var i = 0; i < openings.Openings.Count && i < HullOpeningSet.MaxOpenings; i++)
        {
            var opening = openings.Openings[i];

            if (opening.Profile >= profiles.Count || profiles[opening.Profile] is not { Kind: ProfileKind.Door } profile
                || profile.Points.Count < HullProfile.MinPoints || HallwayGeometry.Area2(profile.Points) == 0)
                continue;

            SolidRotation.Matrix(opening.Rotation, frame);
            var origin = Turn(rotation, opening.Origin, offset);
            var across = Turn(rotation, new Vector3d(frame[0], frame[3], frame[6]), default);
            var up = Turn(rotation, new Vector3d(frame[1], frame[4], frame[7]), default);
            var through = Turn(rotation, new Vector3d(frame[2], frame[5], frame[8]), default);
            var depth = Math.Max(opening.DepthMm, (ushort)1) / 1000.0;
            var ring0 = new Vector3d[profile.Points.Count];
            var ring1 = new Vector3d[profile.Points.Count];

            for (var j = 0; j < ring0.Length; j++)
            {
                var p = Add(origin, Add(Scale(across, profile.Points[j].X * HullSolid.MetresPerPositionUnit), Scale(up, profile.Points[j].Y * HullSolid.MetresPerPositionUnit)));
                ring0[j] = Sub(p, Scale(through, depth));
                ring1[j] = Add(p, Scale(through, depth));
            }

            if (HallwayGeometry.Prism(ring0, ring1, through, out var anchor) is not { } mesh)
                continue;

            var solid = new HullSolid
            {
                Id = IdBase | ((uint)owner << 4) | (uint)i,
                Shape = SolidShape.Mesh,
                Op = layered ? SolidOp.Add : SolidOp.Subtract,
                Flags = layered ? SolidRoles.With(SolidFlags.None, SolidRole.Room) : SolidFlags.None,
                Rotation = SolidRotation.Identity,
                Param = (ushort)(meshBase + meshes.Count),
            };
            (solid.PosX, solid.PosY, solid.PosZ) = anchor;
            (solid.HalfX, solid.HalfY, solid.HalfZ) = mesh.HalfUnits();
            meshes.Add(mesh);
            solids.Add(solid);
        }
    }

    /// <summary>The outline of <paramref name="opening"/> in its plan's space (for markers), or empty.</summary>
    public static Vector3d[] Outline(HullOpening opening, IReadOnlyList<HullProfile>? profiles)
    {
        if (profiles is null || opening.Profile >= profiles.Count || profiles[opening.Profile] is not { Kind: ProfileKind.Door } profile)
            return [];

        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(opening.Rotation, m);
        var o = opening.Origin;
        var ring = new Vector3d[profile.Points.Count];

        for (var j = 0; j < ring.Length; j++)
        {
            double x = profile.Points[j].X * HullSolid.MetresPerPositionUnit, y = profile.Points[j].Y * HullSolid.MetresPerPositionUnit;
            ring[j] = new Vector3d(o.X + m[0] * x + m[1] * y, o.Y + m[3] * x + m[4] * y, o.Z + m[6] * x + m[7] * y);
        }

        return ring;
    }

    /// <summary>
    /// The docking type of a port profile: the same for two profiles of the same shape (whatever their names), so ports
    /// with the same type mate. Four hex digits.
    /// </summary>
    public static string PortType(HullProfile profile)
    {
        var hash = 2166136261u;

        foreach (var p in profile.Points)
        {
            hash = (hash ^ (uint)p.X) * 16777619u;
            hash = (hash ^ (uint)p.Y) * 16777619u;
        }

        return $"{(hash ^ (hash >> 16)) & 0xFFFF:X4}";
    }

    /// <summary>
    /// Whether a port profile is its own mirror image across X = 0: two ports mate face to face, each seeing the other
    /// mirrored, so a lopsided outline would not line up with its own kind.
    /// </summary>
    public static bool Symmetric(HullProfile profile)
    {
        var points = profile.Points;

        foreach (var p in points)
        {
            if (!points.Exists(q => Math.Abs(q.X + p.X) <= 2 && Math.Abs(q.Y - p.Y) <= 2))
                return false;
        }

        return true;
    }

    private static Vector3d Turn(ReadOnlySpan<double> r, Vector3d p, Vector3d offset) =>
        new(r[0] * p.X + r[1] * p.Y + r[2] * p.Z + offset.X, r[3] * p.X + r[4] * p.Y + r[5] * p.Z + offset.Y, r[6] * p.X + r[7] * p.Y + r[8] * p.Z + offset.Z);
}
