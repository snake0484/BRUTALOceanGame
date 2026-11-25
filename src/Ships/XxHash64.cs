using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Ships;

/// <summary>
/// xxHash64 (Yann Collet's reference algorithm), seed 0 unless given. Content hash of plan bodies and the hull brick
/// cache key. Not collision resistant: stores compare bytes on insert.
/// </summary>
public static class XxHash64
{
    private const ulong Prime1 = 11400714785074694791UL;
    private const ulong Prime2 = 14029467366897019727UL;
    private const ulong Prime3 = 1609587929392839161UL;
    private const ulong Prime4 = 9650029242287828579UL;
    private const ulong Prime5 = 2870177450012600261UL;

    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var length = data.Length;
        var p = 0;
        ulong h;

        if (length >= 32)
        {
            var v1 = seed + Prime1 + Prime2;
            var v2 = seed + Prime2;
            var v3 = seed;
            var v4 = seed - Prime1;

            do
            {
                v1 = Round(v1, Read64(data, p));
                v2 = Round(v2, Read64(data, p + 8));
                v3 = Round(v3, Read64(data, p + 16));
                v4 = Round(v4, Read64(data, p + 24));
                p += 32;
            }
            while (p <= length - 32);

            h = BitOperations.RotateLeft(v1, 1) + BitOperations.RotateLeft(v2, 7) + BitOperations.RotateLeft(v3, 12) + BitOperations.RotateLeft(v4, 18);
            h = Merge(h, v1);
            h = Merge(h, v2);
            h = Merge(h, v3);
            h = Merge(h, v4);
        }
        else
        {
            h = seed + Prime5;
        }

        h += (ulong)length;

        while (p + 8 <= length)
        {
            h ^= Round(0, Read64(data, p));
            h = BitOperations.RotateLeft(h, 27) * Prime1 + Prime4;
            p += 8;
        }

        if (p + 4 <= length)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(data[p..]) * Prime1;
            h = BitOperations.RotateLeft(h, 23) * Prime2 + Prime3;
            p += 4;
        }

        while (p < length)
        {
            h ^= data[p] * Prime5;
            h = BitOperations.RotateLeft(h, 11) * Prime1;
            p++;
        }

        h ^= h >> 33;
        h *= Prime2;
        h ^= h >> 29;
        h *= Prime3;
        h ^= h >> 32;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Read64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input)
    {
        acc += input * Prime2;
        acc = BitOperations.RotateLeft(acc, 31);
        return acc * Prime1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Merge(ulong acc, ulong val)
    {
        acc ^= Round(0, val);
        return acc * Prime1 + Prime4;
    }
}
