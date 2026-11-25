using System.Runtime.InteropServices;

namespace BrutalGame.Ships;

public enum BrushShape : byte { Box, Wedge, Cylinder, Capsule, Sphere, Cone, Prism, Convex, Mesh }

public enum BrushOp : byte { Add, Subtract, Paint, Intersect }

[Flags]
public enum BrushFlags : byte { None = 0, Interior = 1, NoCollide = 2, Damage = 4, Mirrored = 8, Inset = 16, BlowOut = 32 }

/// <summary>
/// Wire and save brush, blittable, little endian, 34 B (docs/pivot/ship-hulls.md). Order in the list is evaluation order.
/// Positions are ship-local 1/1024 m, half extents 1/256 m. <see cref="Rotation"/> is a 15 degree snap index
/// (<see cref="BrushRotation"/>, review X2). Box and Wedge use all three half extents; Cylinder runs along local Y with
/// radius <see cref="HalfX"/> and half height <see cref="HalfY"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HullBrush : IEquatable<HullBrush>
{
    public const int SizeInBytes = 34;
    public const int PositionUnitsPerMetre = 1024;
    public const int HalfUnitsPerMetre = 256;
    public const double MetresPerPositionUnit = 1.0 / PositionUnitsPerMetre;
    public const double MetresPerHalfUnit = 1.0 / HalfUnitsPerMetre;

    public uint Id;
    public BrushShape Shape;
    public BrushOp Op;
    public BrushFlags Flags;
    public byte Material;
    public int PosX, PosY, PosZ;
    public uint Rotation;
    public ushort HalfX, HalfY, HalfZ;
    public ushort Bevel;
    public ushort Param;

    public readonly double PositionX => PosX * MetresPerPositionUnit;

    public readonly double PositionY => PosY * MetresPerPositionUnit;

    public readonly double PositionZ => PosZ * MetresPerPositionUnit;

    public readonly bool Equals(HullBrush other) =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<HullBrush>(in this)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<HullBrush>(in other)));

    public override readonly bool Equals(object? obj) => obj is HullBrush other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Id, PosX, PosY, PosZ, Rotation, HalfX, HalfY, HalfZ);

    public static bool operator ==(HullBrush a, HullBrush b) => a.Equals(b);

    public static bool operator !=(HullBrush a, HullBrush b) => !a.Equals(b);
}
