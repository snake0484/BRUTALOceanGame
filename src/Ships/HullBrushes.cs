using System.Buffers.Binary;

namespace BrutalGame.Ships;

/// <summary>
/// What a brush lays along a stroke: copies of a mesh at a spacing (a stamp: a mesh of the plan's; a model: a game model),
/// a 2D profile swept along the line, or paint.
/// </summary>
public enum BrushKind : byte { Stamp, Sweep, Paint, Model }

/// <summary>How every other copy of a repeated mesh differs: not at all, mirrored across the stroke, or turned half round.</summary>
public enum RepeatAlternate : byte { None, Mirror, Turn }

/// <summary>The pattern a paint brush lays: crossing diagonal lines.</summary>
public enum PaintPattern : byte { LineCross }

/// <summary>A profile corner, in 1/1024 m (for a brush: X across the stroke, Y out of the hull).</summary>
public readonly record struct ProfilePoint(int X, int Y);

/// <summary>
/// A hull brush: a repeated design drawn onto the hull along strokes. A stamp copies a mesh from the plan's mesh table
/// (its +Y out of the hull, +Z along the stroke, origin on the surface) every <see cref="Spacing"/>; a sweep extrudes one
/// of the plan's brush profiles (<see cref="Outline"/>, a closed outline ≤ 16 corners) along the stroke, the stroke
/// running through its <see cref="Anchor"/>. A paint brush paints a band of its <see cref="Pattern"/> along the stroke,
/// <see cref="Width"/> across, onto the hull and any flair under it (drawn only: it changes no shape or mass).
/// <see cref="Scale"/> sizes any of them. Stamps and models repeat with the placement options (rows across, seeded turn and
/// size jitter, alternate copies, gaps at the ends). Treated as immutable once in a plan: edits replace the set.
/// </summary>
public sealed class HullBrush
{
    public const int MinProfilePoints = 3;
    public const int MaxProfilePoints = 16;
    public const int MinSpacing = 64;
    public const int MaxProfileUnits = 16 * HullSolid.PositionUnitsPerMetre;

    /// <summary>Sizes (brush, stroke and node scales), per mille: 0.05 to 10.</summary>
    public const ushort UnitScale = 1000;
    public const ushort MinScale = 50;
    public const ushort MaxScale = 10000;
    public const int MinPaintUnits = 8;
    public const int MaxPaintUnits = 16 * HullSolid.PositionUnitsPerMetre;
    public const int MaxRows = 16;
    public const int MaxTurnJitter = 180;
    public const ushort MaxSizeJitter = 900;

    public BrushKind Kind;

    /// <summary>Stamp: mesh table slot.</summary>
    public ushort Mesh;

    /// <summary>Stamp: distance between copies along the stroke; paint: between the pattern's lines (1/1024 m).</summary>
    public int Spacing = HullSolid.PositionUnitsPerMetre;

    /// <summary>Sweep: index of the plan profile (a brush profile) it sweeps.</summary>
    public byte Outline;

    /// <summary>Sweep: the point of the profile the stroke runs through (1/1024 m, profile coordinates).</summary>
    public ProfilePoint Anchor;

    /// <summary>Size of the design, per mille (1000 = as modelled).</summary>
    public ushort Scale = UnitScale;

    /// <summary>Paint: the pattern.</summary>
    public PaintPattern Pattern;

    /// <summary>Paint: colour as sRGB bytes, R in the low byte, alpha (opacity) in the high one.</summary>
    public uint Colour = 0xFF2A2AE6;

    /// <summary>Paint: the band's width across the stroke, 1/1024 m.</summary>
    public int Width = HullSolid.PositionUnitsPerMetre;

    /// <summary>Paint: the pattern's line width, 1/1024 m.</summary>
    public int LineWidth = 40;

    /// <summary>Model: the game model's asset id.</summary>
    public uint Model;

    /// <summary>Repeats: copies side by side across the stroke, and the distance between them (1/1024 m).</summary>
    public byte Rows = 1;

    public int RowSpacing = HullSolid.PositionUnitsPerMetre / 4;

    /// <summary>Repeats: each copy turned about the normal by up to this many degrees either way (seeded per copy).</summary>
    public byte TurnJitter;

    /// <summary>Repeats: each copy's size varied by up to this much either way, per mille (seeded per copy).</summary>
    public ushort SizeJitter;

    public RepeatAlternate Alternate;

    /// <summary>Repeats: distance kept clear at the stroke's start and end (1/1024 m).</summary>
    public int StartGap, EndGap;

    /// <summary>Has placement options beyond plain copies at the spacing.</summary>
    public bool HasRepeats => Kind == BrushKind.Model || Rows != 1 || TurnJitter != 0 || SizeJitter != 0 || Alternate != RepeatAlternate.None || StartGap != 0 || EndGap != 0;

    public HullBrush Clone() => new()
    {
        Kind = Kind, Mesh = Mesh, Spacing = Spacing, Outline = Outline, Anchor = Anchor, Scale = Scale,
        Pattern = Pattern, Colour = Colour, Width = Width, LineWidth = LineWidth,
        Model = Model, Rows = Rows, RowSpacing = RowSpacing, TurnJitter = TurnJitter, SizeJitter = SizeJitter, Alternate = Alternate,
        StartGap = StartGap, EndGap = EndGap,
    };
}

/// <summary>A stroke point on the hull: position (1/1024 m, ship frame), the surface normal (×32767) and the design's size there (per mille).</summary>
public readonly record struct StrokePoint(int X, int Y, int Z, short NX, short NY, short NZ)
{
    public const double NormalScale = 32767.0;

    /// <summary>Size of the design at this point, per mille; blended along the stroke to the next point.</summary>
    public ushort Scale { get; init; } = HullBrush.UnitScale;

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
    public const ushort UnitScale = HullBrush.UnitScale;

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

/// <summary>
/// The brush block's layout: BODY formats 4-9 held each sweep's outline inline and no point sizes (<see cref="Inline"/>);
/// format 10 points sweeps at plan profiles with a line point and size, and gives every stroke point a size; format 12
/// adds models and the repeat options.
/// </summary>
public enum BrushLayout : byte { Inline, Profiles, Repeats }

/// <summary>The plan's brushes and strokes (BODY format 4). Edits replace the whole set, so undo restores it exactly.</summary>
public sealed class HullBrushSet
{
    public const int MaxBrushes = 64;
    public const int MaxStrokes = 256;
    private const int BrushBytes = 31;
    private const int RepeatBytes = 52;

    public readonly List<HullBrush> Brushes = new();
    public readonly List<HullStroke> Strokes = new();

    public bool IsEmpty => Brushes.Count == 0 && Strokes.Count == 0;

    /// <summary>Needs the profiles layout (BODY format 10): a sweep or paint, or a size anywhere but the unit.</summary>
    public bool NeedsProfiles =>
        Brushes.Exists(b => b.Kind != BrushKind.Stamp || b.Scale != HullBrush.UnitScale)
        || Strokes.Exists(s => s.Points.Exists(p => p.Scale != HullBrush.UnitScale));

    /// <summary>Needs the repeats layout (BODY format 12): a model brush, or placement options.</summary>
    public bool NeedsRepeats => Brushes.Exists(b => b.HasRepeats);

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
    /// Profiles layout: u8 brush count; per brush u8 kind, u16 mesh, i32 spacing, u8 outline, i32 anchor x, y, u16 scale,
    /// u8 pattern, u32 colour, i32 width, i32 line width; the repeats layout adds u32 model, u8 rows, i32 row spacing,
    /// u8 turn jitter, u16 size jitter, u8 alternate, i32 start gap, i32 end gap.
    /// u16 stroke count; per stroke u32 id, u16 brush, u8 material, i32 offset, u16 scale, u8 point count, (i32 x, y, z,
    /// i16 nx, ny, nz, u16 scale) each. The inline layout (<paramref name="profiles"/> false, formats 4-9) has per brush u8
    /// kind, u16 mesh, i32 spacing, u8 outline point count (here 0: only stamps of unit size are written so), and no point sizes.
    /// </summary>
    public byte[] ToBytes(BrushLayout layout)
    {
        var profiles = layout != BrushLayout.Inline;
        var repeats = layout == BrushLayout.Repeats;
        var brushBytes = repeats ? RepeatBytes : profiles ? BrushBytes : 8;
        var pointBytes = profiles ? 20 : 18;
        var size = 1 + Brushes.Count * brushBytes + 2 + Strokes.Sum(s => 14 + s.Points.Count * pointBytes);
        var b = new byte[size];
        var o = 0;
        b[o++] = (byte)Brushes.Count;

        foreach (var brush in Brushes)
        {
            b[o] = (byte)brush.Kind;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 1), brush.Mesh);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 3), brush.Spacing);

            if (profiles)
            {
                b[o + 7] = brush.Outline;
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), brush.Anchor.X);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 12), brush.Anchor.Y);
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 16), brush.Scale);
                b[o + 18] = (byte)brush.Pattern;
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o + 19), brush.Colour);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 23), brush.Width);
                BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 27), brush.LineWidth);
            }

            if (repeats)
            {
                var r = b.AsSpan(o + BrushBytes);
                BinaryPrimitives.WriteUInt32LittleEndian(r, brush.Model);
                r[4] = brush.Rows;
                BinaryPrimitives.WriteInt32LittleEndian(r[5..], brush.RowSpacing);
                r[9] = brush.TurnJitter;
                BinaryPrimitives.WriteUInt16LittleEndian(r[10..], brush.SizeJitter);
                r[12] = (byte)brush.Alternate;
                BinaryPrimitives.WriteInt32LittleEndian(r[13..], brush.StartGap);
                BinaryPrimitives.WriteInt32LittleEndian(r[17..], brush.EndGap);
            }

            o += brushBytes;
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

                if (profiles)
                    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 18), p.Scale);

                o += pointBytes;
            }
        }

        return b;
    }

    /// <summary>
    /// Reads the block at the start of <paramref name="s"/>, structurally checked (counts within limits); null on failure.
    /// In the inline layout each sweep's outline lands in <paramref name="inline"/> (brush index, corners) for the caller to
    /// turn into plan profiles.
    /// </summary>
    public static HullBrushSet? Read(ReadOnlySpan<byte> s, out int consumed, BrushLayout layout, List<(int Brush, List<ProfilePoint> Points)>? inline = null)
    {
        consumed = 0;

        if (s.Length < 1)
            return null;

        var set = new HullBrushSet();
        var profiles = layout != BrushLayout.Inline;
        var repeats = layout == BrushLayout.Repeats;
        var pointBytes = profiles ? 20 : 18;
        int brushes = s[0], o = 1;

        if (brushes > MaxBrushes)
            return null;

        for (var i = 0; i < brushes; i++)
        {
            if (s.Length < o + (repeats ? RepeatBytes : profiles ? BrushBytes : 8))
                return null;

            var brush = new HullBrush
            {
                Kind = (BrushKind)s[o],
                Mesh = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 1)..]),
                Spacing = BinaryPrimitives.ReadInt32LittleEndian(s[(o + 3)..]),
            };

            if (profiles)
            {
                brush.Outline = s[o + 7];
                brush.Anchor = new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 12)..]));
                brush.Scale = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 16)..]);
                brush.Pattern = (PaintPattern)s[o + 18];
                brush.Colour = BinaryPrimitives.ReadUInt32LittleEndian(s[(o + 19)..]);
                brush.Width = BinaryPrimitives.ReadInt32LittleEndian(s[(o + 23)..]);
                brush.LineWidth = BinaryPrimitives.ReadInt32LittleEndian(s[(o + 27)..]);

                if (repeats)
                {
                    var r = s[(o + BrushBytes)..];
                    brush.Model = BinaryPrimitives.ReadUInt32LittleEndian(r);
                    brush.Rows = r[4];
                    brush.RowSpacing = BinaryPrimitives.ReadInt32LittleEndian(r[5..]);
                    brush.TurnJitter = r[9];
                    brush.SizeJitter = BinaryPrimitives.ReadUInt16LittleEndian(r[10..]);
                    brush.Alternate = (RepeatAlternate)r[12];
                    brush.StartGap = BinaryPrimitives.ReadInt32LittleEndian(r[13..]);
                    brush.EndGap = BinaryPrimitives.ReadInt32LittleEndian(r[17..]);
                }

                o += repeats ? RepeatBytes : BrushBytes;
            }
            else
            {
                int points = s[o + 7];
                o += 8;

                if (points > HullBrush.MaxProfilePoints || s.Length < o + points * 8)
                    return null;

                var outline = new List<ProfilePoint>(points);

                for (var j = 0; j < points; j++, o += 8)
                    outline.Add(new ProfilePoint(BinaryPrimitives.ReadInt32LittleEndian(s[o..]), BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..])));

                if (brush.Kind == BrushKind.Sweep)
                    inline?.Add((i, outline));
            }

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
            int count = s[o + 13];
            o += 14;

            if (count > HullStroke.MaxPoints || s.Length < o + count * pointBytes)
                return null;

            for (var j = 0; j < count; j++, o += pointBytes)
            {
                var point = new StrokePoint(
                    BinaryPrimitives.ReadInt32LittleEndian(s[o..]),
                    BinaryPrimitives.ReadInt32LittleEndian(s[(o + 4)..]),
                    BinaryPrimitives.ReadInt32LittleEndian(s[(o + 8)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 12)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 14)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(s[(o + 16)..]));

                stroke.Points.Add(profiles ? point with { Scale = BinaryPrimitives.ReadUInt16LittleEndian(s[(o + 18)..]) } : point);
            }

            set.Strokes.Add(stroke);
        }

        consumed = o;
        return set;
    }
}
