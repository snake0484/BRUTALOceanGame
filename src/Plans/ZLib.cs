using System.Runtime.InteropServices;

namespace BrutalGame.Plans;

/// <summary>
/// zlib (RFC 1950) through the native library <c>Ahwoo.BRUTAL.ZLib</c> ships (it has no C# interop of its own).
/// One fixed level, so the same input always packs to the same bytes with that library version.
/// </summary>
public static unsafe partial class ZLib
{
    public const int Level = 9;
    private const string Library = "zlib";
    private const int Ok = 0;

    public static int Bound(int length) => (int)CompressBound(new CULong((uint)length)).Value;

    /// <summary>Packs <paramref name="source"/> into <paramref name="destination"/> (at least <see cref="Bound"/> long); returns the packed length.</summary>
    public static int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var length = new CULong((uint)destination.Length);

        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            var result = Compress2(dst, &length, src, new CULong((uint)source.Length), Level);

            if (result != Ok)
                throw new InvalidOperationException($"zlib compress2 failed: {result}");
        }

        return (int)length.Value;
    }

    /// <summary>Unpacks into exactly <paramref name="destination"/>; false on corrupt data or a length mismatch.</summary>
    public static bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        var length = new CULong((uint)destination.Length);
        var sourceLength = new CULong((uint)source.Length);

        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            var result = Uncompress2(dst, &length, src, &sourceLength);
            return result == Ok && (int)length.Value == destination.Length && (int)sourceLength.Value == source.Length;
        }
    }

    [LibraryImport(Library, EntryPoint = "compressBound")]
    private static partial CULong CompressBound(CULong sourceLength);

    [LibraryImport(Library, EntryPoint = "compress2")]
    private static partial int Compress2(byte* dest, CULong* destLength, byte* source, CULong sourceLength, int level);

    [LibraryImport(Library, EntryPoint = "uncompress2")]
    private static partial int Uncompress2(byte* dest, CULong* destLength, byte* source, CULong* sourceLength);
}
