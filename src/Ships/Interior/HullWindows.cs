using System.Buffers.Binary;

namespace BrutalGame.Ships.Interior;

public enum WindowShape : byte { Rectangle, Rounded, Circle, Arch }

/// <summary>
/// A window: an outline placed on the hull. Each rebuild it sticks to the hull along its normal; the hull behind it is cut
/// away to the interior (a room cut, <see cref="Depth"/> deep), and a pane of its material fills the opening, shaped to
/// the hull's surface (it follows the hull's curves, not a flat plate). Treated as immutable once in a plan.
/// </summary>
public sealed class HullWindow
{
    public const int MinSizeMm = 100;
    public const int MaxSizeMm = 16000;
    public const int MinThicknessMm = 16;
    public const int MaxDepthMm = 8000;

    public uint Id;

    /// <summary>The centre, 1/1024 m in ship space (on or near the hull).</summary>
    public int X, Y, Z;

    /// <summary>Outward direction, ×32767 per axis.</summary>
    public short NX, NY, NZ;

    /// <summary>Turn of the outline about the normal, in 15 degree steps (0-23).</summary>
    public byte Roll;

    public WindowShape Shape = WindowShape.Rounded;
    public ushort WidthMm = 1000;
    public ushort HeightMm = 600;

    /// <summary>How far in from the hull surface the opening is cut.</summary>
    public ushort DepthMm = 1000;

    /// <summary>The pane, measured in from the hull surface.</summary>
    public ushort ThicknessMm = 40;

    /// <summary>Plan material index of the pane.</summary>
    public byte Material;

    public Vector3d Position => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    public Vector3d Normal => HullSurface.Normalise(new Vector3d(NX, NY, NZ), new Vector3d(0, 1, 0));

    public void SetNormal(Vector3d n)
    {
        n = HullSurface.Normalise(n, new Vector3d(0, 1, 0));
        (NX, NY, NZ) = ((short)Math.Round(n.X * 32767), (short)Math.Round(n.Y * 32767), (short)Math.Round(n.Z * 32767));
    }

    public HullWindow Clone() => (HullWindow)MemberwiseClone();
}

/// <summary>The plan's windows.</summary>
public sealed class HullWindowSet
{
    public const int MaxWindows = 1024;
    private const int Bytes = 33;

    public readonly List<HullWindow> Windows = new();

    public bool IsEmpty => Windows.Count == 0;

    public int IndexOf(uint id) => Windows.FindIndex(w => w.Id == id);

    public HullWindowSet Clone()
    {
        var copy = new HullWindowSet();

        foreach (var w in Windows)
            copy.Windows.Add(w.Clone());

        return copy;
    }

    /// <summary>
    /// <c>u16 count</c>, then per window <c>u32 id, i32 X, Y, Z, i16 NX, NY, NZ, u8 roll, u8 shape, u16 width, height,
    /// depth, thickness (mm), u8 material</c> (33 B).
    /// </summary>
    public byte[] ToBytes()
    {
        var b = new byte[2 + Windows.Count * Bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)Windows.Count);
        var o = 2;

        foreach (var w in Windows)
        {
            var s = b.AsSpan(o);
            BinaryPrimitives.WriteUInt32LittleEndian(s, w.Id);
            BinaryPrimitives.WriteInt32LittleEndian(s[4..], w.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[8..], w.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[12..], w.Z);
            BinaryPrimitives.WriteInt16LittleEndian(s[16..], w.NX);
            BinaryPrimitives.WriteInt16LittleEndian(s[18..], w.NY);
            BinaryPrimitives.WriteInt16LittleEndian(s[20..], w.NZ);
            s[22] = w.Roll;
            s[23] = (byte)w.Shape;
            BinaryPrimitives.WriteUInt16LittleEndian(s[24..], w.WidthMm);
            BinaryPrimitives.WriteUInt16LittleEndian(s[26..], w.HeightMm);
            BinaryPrimitives.WriteUInt16LittleEndian(s[28..], w.DepthMm);
            BinaryPrimitives.WriteUInt16LittleEndian(s[30..], w.ThicknessMm);
            s[32] = w.Material;
            o += Bytes;
        }

        return b;
    }

    public static HullWindowSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 2)
            return null;

        int count = BinaryPrimitives.ReadUInt16LittleEndian(s);

        if (count > MaxWindows || s.Length < 2 + count * Bytes)
            return null;

        var set = new HullWindowSet();

        for (var i = 0; i < count; i++)
        {
            var r = s.Slice(2 + i * Bytes, Bytes);
            set.Windows.Add(new HullWindow
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(r),
                X = BinaryPrimitives.ReadInt32LittleEndian(r[4..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(r[8..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(r[12..]),
                NX = BinaryPrimitives.ReadInt16LittleEndian(r[16..]),
                NY = BinaryPrimitives.ReadInt16LittleEndian(r[18..]),
                NZ = BinaryPrimitives.ReadInt16LittleEndian(r[20..]),
                Roll = r[22],
                Shape = (WindowShape)r[23],
                WidthMm = BinaryPrimitives.ReadUInt16LittleEndian(r[24..]),
                HeightMm = BinaryPrimitives.ReadUInt16LittleEndian(r[26..]),
                DepthMm = BinaryPrimitives.ReadUInt16LittleEndian(r[28..]),
                ThicknessMm = BinaryPrimitives.ReadUInt16LittleEndian(r[30..]),
                Material = r[32],
            });
        }

        consumed = 2 + count * Bytes;
        return set;
    }
}
