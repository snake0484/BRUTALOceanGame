using System.Buffers.Binary;

namespace BrutalGame.Ships;

/// <summary>What a brush lays along a stroke: copies of a mesh at a spacing, or a 2D profile swept along the line.</summary>
public enum BrushKind : byte { Stamp, Sweep }

/// <summary>A sweep profile corner: X across the stroke, Y out of the hull, in 1/1024 m.</summary>
public readonly record struct ProfilePoint(int X, int Y);

/// <summary>
/// A hull brush: a repeated design drawn onto the hull along strokes. A stamp copies a mesh from the plan's mesh table
/// (its +Y out of the hull, +Z along the stroke, origin on the surface) every <see cref="Spacing"/>; a sweep extrudes
/// a closed profile (≤ 16 corners) along the stroke. Treated as immutable once in a plan: edits replace the set.
/// </summary>
public sealed class HullBrush
{
    public const int MinProfilePoints = 3;
    public const int MaxProfilePoints = 16;
    public const int MinSpacing = 64;
    public const int MaxProfileUnits = 16 * HullSolid.PositionUnitsPerMetre;

    public BrushKind Kind;

    /// <summary>Stamp: mesh table slot.</summary>
    public ushort Mesh;

    /// <summary>Stamp: distance between copies along the stroke, 1/1024 m.</summary>
    public int Spacing = HullSolid.PositionUnitsPerMetre;

    /// <summary>Sweep: closed polygon, either winding (the expansion orients it).</summary>
    public readonly List<ProfilePoint> Profile = new();

    public HullBrush Clone()
    {
        var copy = new HullBrush { Kind = Kind, Mesh = Mesh, Spacing = Spacing };
        copy.Profile.AddRange(Profile);
        return copy;
    }
}

/// <summary>A stroke point on the hull: position (1/1024 m, ship frame) and the surface normal (×32767).</summary>
public readonly record struct StrokePoint(int X, int Y, int Z, short NX, short NY, short NZ)
{
    public const double NormalScale = 32767.0;

    public Vector3d Position => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    public Vector3d Normal => new(NX / NormalScale, NY / NormalScale, NZ / NormalScale);

    public static StrokePoint From(Vector3d position, Vector3d normal)
    {
        var length = normal.Length();
        var n = length > 1e-9 ? new Vector3d(normal.X / length, normal.Y / length, normal.Z / length) : new Vector3d(0, 1, 0);
        return new StrokePoint(
            (int)Math.Round(position.X * HullSolid.PositionUnitsPerMetre),
            (int)Math.Round(position.Y * HullSolid.PositionUnitsPerMetre),
            (int)Math.Round(position.Z * HullSolid.PositionUnitsPerMetre),
            (short)Math.Round(n.X * NormalScale),
            (short)Math.Round(n.Y * NormalScale),
            (short)Math.Round(n.Z * NormalScale));
    }
}

/// <summary>
/// A line drawn on the hull that lays a brush along it: small visual flair, drawn from its own geometry (not the hull
/// grid), counted for mass only. The points stick to the hull: each rebuild re-projects them onto the current surface
/// along their normal, so the stroke follows the hull as it is reshaped.
/// </summary>
public sealed class HullStroke
{
    public const int MinPoints = 2;
    public const int MaxPoints = 64;
    public const ushort UnitScale = 1000;

    public uint Id;
    public ushort Brush;
    public byte Material;

    /// <summary>Along the surface normal, 1/1024 m: lifts the design off the hull (+) or sinks it in (−).</summary>
    public int Offset;

    /// <summary>Size of the design, per mille (1000 = as modelled).</summary>
    public ushort Scale = UnitScale;

    public readonly List<StrokePoint> Points = new();

    public HullStroke Clone()
    {
        var copy = new HullStroke { Id = Id, Brush = Brush, Material = Material, Offset = Offset, Scale = Scale };
        copy.Points.AddRange(Points);
        return copy;
    }
}

/// <summary>The plan's brushes and strokes (BODY format 4). Edits replace the whole set, so undo restores it exactly.</summary>
public sealed class HullBrushSet
{
    public const int MaxBrushes = 64;
    public const int MaxStrokes = 256;

    public readonly List<HullBrush> Brushes = new();
    public readonly List<HullStroke> Strokes = new();

    public bool IsEmpty => Brushes.Count == 0 && Strokes.Count == 0;

    public HullBrushSet Clone()
    {
        var copy = new HullBrushSet();

        foreach (var b in Brushes)
            copy.Brushes.Add(b.Clone());

        foreach (var s in Strokes)
            copy.Strokes.Add(s.Clone());

        return copy;
    }

    public int IndexOfStroke(uint id) => Strokes.FindIndex(s => s.Id == id);

    // ---- bytes ----

    /// <summary>
    /// u8 brush count; per brush u8 kind, u16 mesh, i32 spacing, u8 profile count, (i32 x, i32 y) each. u16 stroke count;
    /// per stroke u32 id, u16 brush, u8 material, i32 offset, u16 scale, u8 point count, (i32 x, y, z, i16 nx, ny, nz) each.
    /// </summary>
    public byte[] ToBytes()
    {
        var size = 1 + Brushes.Sum(b => 8 + b.Profile.Count * 8) + 2 + Strokes.Sum(s => 14 + s.Points.Count * 18);
        var b = new byte[size];
        var o = 0;
        b[o++] = (byte)Brushes.Count;

        foreach (var brush in Brushes)
        {
            b[o++] = (byte)brush.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), brush.Mesh);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 2), brush.Spacing);
            b[o + 6] = (byte)brush.Profile.Count;
            o += 7;

            foreach (var p in brush.Profile)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), p.X);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), p.Y);
                o += 8;
            }
        }

        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)Strokes.Count);
        o += 2;

        foreach (var s in Strokes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), s.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4), s.Brush);
            b[o + 6] = s.Material;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 7), s.Offset);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 11), s.Scale);
            b[o + 13] = (byte)s.Points.Count;
            o += 14;

            foreach (var p in s.Points)
            {
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), p.X);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 4), p.Y);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), p.Z);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 12), p.NX);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 14), p.NY);
                BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 16), p.NZ);
                o += 18;
            }
        }

        return b;
    }

    /// <summary>Reads the block at the start of <paramref name="s"/>, structurally checked (counts within limits); null on failure.</summary>
    public static HullBrushSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1)
            return null;

        var set = new HullBrushSet();
        int brushes = s[0], o = 1;

        if (brushes > MaxBrushes)
            return null;

        for (var i = 0; i < brushes; i++)
        {
            if (s.Length < o + 8)
                return null;

            var brush = new HullBrush
            {
                Kind = (BrushKind)s[o],
                Mesh = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 1)..]),
                Spacing = BinaryPrimitives.ReadInt32LittleEndian(s[(o + 3)..]),
            };
            int points = s[o + 7];
            o += 8;

            if (points > HullBrush.MaxProfilePoints || s.Length < o + points * 8)
                return null;

            for (var j = 0; j < points; j++, o += 8)
                brush.Profile.Add(new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..])));

            set.Brushes.Add(brush);
        }

        if (s.Length < o + 2)
            return null;

        int strokes = BinaryPrimitives.ReadUInt16LittleEndian(s[o..]);
        o += 2;

        if (strokes > MaxStrokes)
            return null;

        for (var i = 0; i < strokes; i++)
        {
            if (s.Length < o + 14)
                return null;

            var stroke = new HullStroke
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(s[o..]),
                Brush = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 4)..]),
                Material = s[o + 6],
                Offset = BinaryPrimitives.ReadInt32LittleEndian(s[(o + 7)..]),
                Scale = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 11)..]),
            };
            int points = s[o + 13];
            o += 14;

            if (points > HullStroke.MaxPoints || s.Length < o + points * 18)
                return null;

            for (var j = 0; j < points; j++, o += 18)
            {
                stroke.Points.Add(new StrokePoint(
                    BinaryPrimitives.ReadInt32LittleEndian(s[o..]),
                    BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 12)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 14)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 16)..])));
            }

            set.Strokes.Add(stroke);
        }

        consumed = o;
        return set;
    }
}
