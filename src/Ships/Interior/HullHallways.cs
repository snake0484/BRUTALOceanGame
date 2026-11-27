using System.Buffers.Binary;
using System.Text;

namespace BrutalGame.Ships.Interior;

/// <summary>A hallway's closed cross-section, one wall's section for rooms (half a hallway's), or a brush's closed outline swept along strokes.</summary>
public enum ProfileKind : byte { Hallway, Wall, Brush, Door }

/// <summary>
/// A cross-section in 1/1024 m. A hallway profile is a closed outline, X across, Y up from the floor (Y = 0), 3-32 points.
/// A wall profile is one wall's section, an open line from floor to ceiling, 2-32 points: X is how far the wall stands in
/// from the room's side (≥ 0), Y the height (from 0, never going down). A brush profile is a closed outline of 3-16 points,
/// X across the stroke, Y out of the hull; each brush using it says where in it the stroke runs and how big. The plan keeps its own copy: <see cref="Source"/>
/// names the library file it came from, but a change to that file reaches the plan only when it is reimported on purpose.
/// </summary>
public sealed class HullProfile
{
    public const int MinPoints = 3;
    public const int MinWallPoints = 2;
    public const int MaxPoints = 32;
    public const int MaxNameBytes = 64;
    public const int MaxSourceBytes = 255;

    /// <summary>Largest coordinate (16 m).</summary>
    public const int MaxUnits = 16 * HullSolid.PositionUnitsPerMetre;

    public string Name = "";

    /// <summary>The library file (relative to the profile library) it was imported from or saved to; empty if made here.</summary>
    public string Source = "";

    public ProfileKind Kind;

    public readonly List<ProfilePoint> Points = new();

    public HullProfile Clone()
    {
        var copy = new HullProfile { Name = Name, Source = Source, Kind = Kind };
        copy.Points.AddRange(Points);
        return copy;
    }

    public bool SameShape(HullProfile other) => Kind == other.Kind && Points.SequenceEqual(other.Points);
}

/// <summary>A room solid (by id) whose walls follow a wall profile (an index into the plan's profiles).</summary>
public readonly record struct RoomWall(uint Solid, byte Profile);

/// <summary>The interior block's layout: BODY formats 6-7 (one inline profile per hallway), 8 (route networks), 9 (wall profiles).</summary>
public enum InteriorLayout : byte { Legacy, Routes, Walls }

/// <summary>
/// A hallway: a walkable space swept along a network of nodes, like a pipe: nodes joined by runs, and three or more runs at
/// a node make a junction (one walkable space). It uses one of the plan's profiles and has its own gravity. It is cut out
/// of the hull as a room (a layered hull gives it its own wall stack), and runs too steep to walk up get stairs.
/// Positions are 1/1024 m in ship space. Treated as immutable once in a plan.
/// </summary>
public sealed class HullHallway
{
    public const int MaxNodes = 64;
    public const int MaxRuns = 96;

    public uint Id;

    /// <summary>The nodes, 1/1024 m in ship space.</summary>
    public readonly List<(int X, int Y, int Z)> Nodes = new();

    /// <summary>Runs between nodes (indices into <see cref="Nodes"/>).</summary>
    public readonly List<(byte A, byte B)> Runs = new();

    /// <summary>Index of the plan profile it uses.</summary>
    public byte Profile;

    /// <summary>Gravity as a 15 degree snap rotation: down is this rotation applied to -Y (identity: ship down).</summary>
    public uint Gravity = SolidRotation.Identity;

    /// <summary>Plan material index of the stairs.</summary>
    public byte Material;

    public Vector3d Node(int i) => new(Nodes[i].X * HullSolid.MetresPerPositionUnit, Nodes[i].Y * HullSolid.MetresPerPositionUnit, Nodes[i].Z * HullSolid.MetresPerPositionUnit);

    public int Degree(int node) => Runs.Count(r => r.A == node || r.B == node);

    public double Length() => Runs.Sum(r => HullSurface.Sub(Node(r.B), Node(r.A)).Length());

    /// <summary>A straight chain through <paramref name="nodes"/>.</summary>
    public void Chain(IEnumerable<(int X, int Y, int Z)> nodes)
    {
        Nodes.Clear();
        Runs.Clear();
        Nodes.AddRange(nodes);

        for (var i = 0; i + 1 < Nodes.Count; i++)
            Runs.Add(((byte)i, (byte)(i + 1)));
    }

    /// <summary>Removes node <paramref name="node"/> and its runs; a node between exactly two runs is dissolved, its neighbours joined.</summary>
    public void RemoveNode(int node)
    {
        var touching = Runs.Where(r => r.A == node || r.B == node).ToList();

        if (touching.Count == 2)
        {
            var a = touching[0].A == node ? touching[0].B : touching[0].A;
            var b = touching[1].A == node ? touching[1].B : touching[1].A;
            Runs.Add((a, b));
        }

        Runs.RemoveAll(r => r.A == node || r.B == node);
        Nodes.RemoveAt(node);

        for (var i = 0; i < Runs.Count; i++)
        {
            var (a, b) = Runs[i];
            Runs[i] = ((byte)(a > node ? a - 1 : a), (byte)(b > node ? b - 1 : b));
        }
    }

    public HullHallway Clone()
    {
        var copy = new HullHallway { Id = Id, Gravity = Gravity, Material = Material, Profile = Profile };
        copy.Nodes.AddRange(Nodes);
        copy.Runs.AddRange(Runs);
        return copy;
    }
}

/// <summary>The plan's interior spaces: its profiles, hallways and the wall profiles its rooms (ordinary Room-role solids) use.</summary>
public sealed class HullInterior
{
    public const int MaxHallways = 255;
    public const int MaxProfiles = 64;
    public const int MaxRoomWalls = 255;

    public readonly List<HullProfile> Profiles = new();
    public readonly List<HullHallway> Hallways = new();
    public readonly List<RoomWall> RoomWalls = new();

    public bool IsEmpty => Hallways.Count == 0 && Profiles.Count == 0 && RoomWalls.Count == 0;

    /// <summary>Needs the kinds layout (BODY format 9 and on) to be written.</summary>
    public bool HasKinds => RoomWalls.Count > 0 || Profiles.Any(p => p.Kind != ProfileKind.Hallway);

    public int IndexOf(uint id) => Hallways.FindIndex(h => h.Id == id);

    /// <summary>The profile a hallway uses (null when its index is out of range).</summary>
    public HullProfile? ProfileOf(HullHallway hallway) => hallway.Profile < Profiles.Count ? Profiles[hallway.Profile] : null;

    /// <summary>The wall profile index room solid <paramref name="solid"/> uses, or -1.</summary>
    public int WallOf(uint solid)
    {
        foreach (var w in RoomWalls)
        {
            if (w.Solid == solid)
                return w.Profile;
        }

        return -1;
    }

    public HullInterior Clone()
    {
        var copy = new HullInterior();

        foreach (var p in Profiles)
            copy.Profiles.Add(p.Clone());

        foreach (var h in Hallways)
            copy.Hallways.Add(h.Clone());

        copy.RoomWalls.AddRange(RoomWalls);
        return copy;
    }

    /// <summary>
    /// <c>u8 profile count</c>, per profile <c>u8 name length, name (UTF-8), u8 source length, source, [u8 kind], u8 point
    /// count, i32 X, Y</c> per point; then <c>u8 hallway count</c>, per hallway <c>u32 id, u32 gravity, u8 material, u8
    /// profile, u8 node count, u8 run count</c>, i32 X, Y, Z per node, u8 A, B per run; then <c>[u8 room wall count, per
    /// room u32 solid, u8 profile]</c>. The bracketed parts are the wall layout (<paramref name="walls"/>, BODY format 9).
    /// (BODY formats 6 and 7 held one inline profile and a line of nodes per hallway; <see cref="Read"/> converts them.)
    /// </summary>
    public byte[] ToBytes(bool walls)
    {
        var names = Profiles.Select(p => Clip(p.Name, HullProfile.MaxNameBytes)).ToList();
        var sources = Profiles.Select(p => Clip(p.Source, HullProfile.MaxSourceBytes)).ToList();
        var extra = walls ? 1 : 0;
        var size = 1 + Profiles.Select((p, i) => 3 + extra + names[i].Length + sources[i].Length + p.Points.Count * 8).Sum()
            + 1 + Hallways.Sum(h => 12 + h.Nodes.Count * 12 + h.Runs.Count * 2) + (walls ? 1 + RoomWalls.Count * 5 : 0);
        var b = new byte[size];
        var o = 0;
        b[o++] = (byte)Profiles.Count;

        for (var i = 0; i < Profiles.Count; i++)
        {
            b[o++] = (byte)names[i].Length;
            names[i].CopyTo(b, o);
            o += names[i].Length;
            b[o++] = (byte)sources[i].Length;
            sources[i].CopyTo(b, o);
            o += sources[i].Length;

            if (walls)
                b[o++] = (byte)Profiles[i].Kind;

            b[o++] = (byte)Profiles[i].Points.Count;

            foreach (var p in Profiles[i].Points)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), p.X);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), p.Y);
                o += 8;
            }
        }

        b[o++] = (byte)Hallways.Count;

        foreach (var h in Hallways)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), h.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 4), h.Gravity);
            b[o + 8] = h.Material;
            b[o + 9] = h.Profile;
            b[o + 10] = (byte)h.Nodes.Count;
            b[o + 11] = (byte)h.Runs.Count;
            o += 12;

            foreach (var (x, y, z) in h.Nodes)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), x);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), y);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), z);
                o += 12;
            }

            foreach (var (a, c) in h.Runs)
            {
                b[o++] = a;
                b[o++] = c;
            }
        }

        if (walls)
        {
            b[o++] = (byte)RoomWalls.Count;

            foreach (var w in RoomWalls)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), w.Solid);
                b[o + 4] = w.Profile;
                o += 5;
            }
        }

        return b;
    }

    private static byte[] Clip(string text, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length <= max ? bytes : bytes[..max];
    }

    /// <summary>Reads the block at the start of <paramref name="s"/>, written in <paramref name="layout"/>.</summary>
    public static HullInterior? Read(ReadOnlySpan<byte> s, out int consumed, InteriorLayout layout)
    {
        consumed = 0;
        var interior = new HullInterior();
        var walls = layout == InteriorLayout.Walls;
        var o = 0;

        if (layout == InteriorLayout.Legacy)
            return ReadLegacy(s, out consumed);

        if (s.Length < 1)
            return null;

        int profiles = s[o++];

        if (profiles > MaxProfiles)
            return null;

        for (var i = 0; i < profiles; i++)
        {
            var p = new HullProfile();

            if (!Text(s, ref o, out p.Name) || !Text(s, ref o, out p.Source) || s.Length < o + 1 + (walls ? 1 : 0))
                return null;

            if (walls)
            {
                if (s[o] > (byte)ProfileKind.Door)
                    return null;

                p.Kind = (ProfileKind)s[o++];
            }

            int points = s[o++];

            if (points > HullProfile.MaxPoints || s.Length < o + points * 8)
                return null;

            for (var j = 0; j < points; j++, o += 8)
                p.Points.Add(new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..])));

            interior.Profiles.Add(p);
        }

        if (s.Length < o + 1)
            return null;

        int count = s[o++];

        for (var i = 0; i < count; i++)
        {
            if (s.Length < o + 12)
                return null;

            var h = new HullHallway
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]),
                Gravity = BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 4)..]),
                Material = s[o + 8],
                Profile = s[o + 9],
            };
            int nodes = s[o + 10], runs = s[o + 11];
            o += 12;

            if (nodes > HullHallway.MaxNodes || runs > HullHallway.MaxRuns || s.Length < o + nodes * 12 + runs * 2)
                return null;

            for (var j = 0; j < nodes; j++, o += 12)
                h.Nodes.Add((BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..])));

            for (var j = 0; j < runs; j++, o += 2)
                h.Runs.Add((s[o], s[o + 1]));

            interior.Hallways.Add(h);
        }

        if (walls)
        {
            if (s.Length < o + 1 || s.Length < o + 1 + s[o] * 5)
                return null;

            int rooms = s[o++];

            for (var i = 0; i < rooms; i++, o += 5)
                interior.RoomWalls.Add(new RoomWall(BinaryPrimitives.ReadUInt32LittleEndian(s[o..]), s[o + 4]));
        }

        consumed = o;
        return interior;
    }

    private static bool Text(ReadOnlySpan<byte> s, ref int o, out string text)
    {
        text = "";

        if (s.Length < o + 1 || s.Length < o + 1 + s[o])
            return false;

        int length = s[o++];
        text = Encoding.UTF8.GetString(s.Slice(o, length));
        o += length;
        return true;
    }

    /// <summary>Formats 6 and 7: per hallway an inline profile and a line of nodes. Same outlines become one plan profile.</summary>
    private static HullInterior? ReadLegacy(ReadOnlySpan<byte> s, out int consumed)
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
            int points = s[o + 9], nodes = s[o + 10];
            o += 11;

            if (points > HullProfile.MaxPoints || nodes > HullHallway.MaxNodes || s.Length < o + points * 8 + nodes * 12)
                return null;

            var profile = new HullProfile();

            for (var j = 0; j < points; j++, o += 8)
                profile.Points.Add(new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..])));

            var line = new List<(int, int, int)>();

            for (var j = 0; j < nodes; j++, o += 12)
                line.Add((BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..])));

            h.Chain(line);
            var same = interior.Profiles.FindIndex(p => p.SameShape(profile));

            if (same < 0)
            {
                profile.Name = $"Profile {interior.Profiles.Count + 1}";
                same = interior.Profiles.Count;
                interior.Profiles.Add(profile);
            }

            h.Profile = (byte)same;
            interior.Hallways.Add(h);
        }

        consumed = o;
        return interior;
    }
}
