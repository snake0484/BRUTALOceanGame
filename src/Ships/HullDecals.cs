using System.Buffers.Binary;

namespace BrutalGame.Ships;

/// <summary>
/// A decal: a game image (by asset id) projected onto the hull and its flair from a box placed on the surface, along its
/// normal. Drawn only: it changes no shape, mass or collision. Treated as immutable once in a plan.
/// </summary>
public sealed class HullDecal
{
    public const int MinSizeMm = 20;
    public const int MaxSizeMm = 32000;

    public uint Id;

    /// <summary>The centre, 1/1024 m in ship space (on the hull).</summary>
    public int X, Y, Z;

    /// <summary>Out of the hull, ×32767 per axis; the image projects back along it.</summary>
    public short NX, NY, NZ;

    /// <summary>Turn of the image about the normal, in 15 degree steps (0-23).</summary>
    public byte Roll;

    /// <summary>The image's asset id (<c>DefId</c> value of a decal in game data).</summary>
    public uint Asset;

    public ushort WidthMm = 2000;
    public ushort HeightMm = 2000;

    /// <summary>How deep the projection reaches, half out of the hull and half in.</summary>
    public ushort DepthMm = 1000;

    /// <summary>Opacity, 0-255.</summary>
    public byte Opacity = 255;

    public Vector3d Position => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    public Vector3d Normal => HullSurface.Normalise(new Vector3d(NX, NY, NZ), new Vector3d(0, 1, 0));

    public void SetNormal(Vector3d n)
    {
        n = HullSurface.Normalise(n, new Vector3d(0, 1, 0));
        (NX, NY, NZ) = ((short)Math.Round(n.X * 32767), (short)Math.Round(n.Y * 32767), (short)Math.Round(n.Z * 32767));
    }

    /// <summary>The image's right and up on the hull: up leans to ship up (or forward on a level face), turned by <see cref="Roll"/>.</summary>
    public (Vector3d Right, Vector3d Up) Axes()
    {
        var n = Normal;
        var reference = Math.Abs(n.Y) < 0.95 ? new Vector3d(0, 1, 0) : new Vector3d(0, 0, 1);
        var up = HullSurface.Normalise(HullSurface.Sub(reference, HullSurface.Scale(n, HullSurface.Dot(reference, n))), new Vector3d(0, 0, 1));
        var right = HullSurface.Cross(up, n);
        var a = Roll * SolidRotation.DegreesPerStep * Math.PI / 180.0;
        var (c, s) = (Math.Cos(a), Math.Sin(a));
        return (HullSurface.Add(HullSurface.Scale(right, c), HullSurface.Scale(up, s)), HullSurface.Sub(HullSurface.Scale(up, c), HullSurface.Scale(right, s)));
    }

    /// <summary>The projector this decal draws as; <paramref name="layer"/> is its image's texture layer.</summary>
    public HullProjector Projector(int layer)
    {
        var (right, up) = Axes();
        return new HullProjector
        {
            Kind = ProjectorKind.Decal,
            Centre = Position,
            U = right,
            V = up,
            N = Normal,
            HalfU = WidthMm * 0.0005,
            HalfV = HeightMm * 0.0005,
            HalfN = DepthMm * 0.0005,
            Colour = 0x00FFFFFFu | ((uint)Opacity << 24),
            Layer = layer,
        };
    }

    public HullDecal Clone() => (HullDecal)MemberwiseClone();
}

/// <summary>The plan's decals (BODY format 10).</summary>
public sealed class HullDecalSet
{
    public const int MaxDecals = 256;
    private const int Bytes = 34;

    public readonly List<HullDecal> Decals = new();

    public bool IsEmpty => Decals.Count == 0;

    public int IndexOf(uint id) => Decals.FindIndex(d => d.Id == id);

    public HullDecalSet Clone()
    {
        var copy = new HullDecalSet();

        foreach (var d in Decals)
            copy.Decals.Add(d.Clone());

        return copy;
    }

    /// <summary>
    /// <c>u16 count</c>, then per decal <c>u32 id, i32 X, Y, Z, i16 NX, NY, NZ, u8 roll, u32 asset, u16 width, height,
    /// depth (mm), u8 opacity</c> (34 B).
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new byte[2 + Decals.Count * Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Decals.Count);
        var o = 2;

        foreach (var d in Decals)
        {
            var s = b.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, d.Id);
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], d.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], d.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[12..], d.Z);
            BinaryPrimitives.WriteInt16LittleEndian(s[16..], d.NX);
            BinaryPrimitives.WriteInt16LittleEndian(s[18..], d.NY);
            BinaryPrimitives.WriteInt16LittleEndian(s[20..], d.NZ);
            s[22] = d.Roll;
            BinaryPrimitives.WriteUInt32LittleEndian(s[23..], d.Asset);
            BinaryPrimitives.WriteUInt16LittleEndian(s[27..], d.WidthMm);
            BinaryPrimitives.WriteUInt16LittleEndian(s[29..], d.HeightMm);
            BinaryPrimitives.WriteUInt16LittleEndian(s[31..], d.DepthMm);
            s[33] = d.Opacity;
            o += Bytes;
        }

        return b;
    }

    public static HullDecalSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 2)
            return null;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(s);

        if (count > MaxDecals || s.Length < 2 + count * Bytes)
            return null;

        var set = new HullDecalSet();

        for (var i = 0; i < count; i++)
        {
            var r = s.Slice(2 + i * Bytes, Bytes);
            set.Decals.Add(new HullDecal
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(r),
                X = BinaryPrimitives.ReadInt32LittleEndian(r[4..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(r[8..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
                NX = BinaryPrimitives.ReadInt16LittleEndian(r[16..]),
                NY = BinaryPrimitives.ReadInt16LittleEndian(r[18..]),
                NZ = BinaryPrimitives.ReadInt16LittleEndian(r[20..]),
                Roll = r[22],
                Asset = BinaryPrimitives.ReadUInt32LittleEndian(r[23..]),
                WidthMm = BinaryPrimitives.ReadUInt16LittleEndian(r[27..]),
                HeightMm = BinaryPrimitives.ReadUInt16LittleEndian(r[29..]),
                DepthMm = BinaryPrimitives.ReadUInt16LittleEndian(r[31..]),
                Opacity = r[33],
            });
        }

        consumed = 2 + count * Bytes;
        return set;
    }
}
